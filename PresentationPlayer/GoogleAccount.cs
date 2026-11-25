using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PresentationPlayer;

/// <summary>
/// The optional Google sign-in, used to open decks that are shared with a person rather than
/// "anyone with the link". The user signs in to Google in a normal WebView2 window that uses its
/// own browser profile; the live player runs in the same profile, so it sees the deck too, and the
/// PDF/PPTX downloads borrow that profile's Google cookies. Nothing is stored by the app itself:
/// the session lives in the WebView2 profile, whose cookies Chromium encrypts with Windows DPAPI.
/// All public members are thread-safe.
/// </summary>
public sealed partial class GoogleAccount(AppSettings settings) : PresentationLoader.IGoogleSession
{
    private const string ServiceUrl = "https://docs.google.com/presentation/";
    private const string SignInUrl =
        "https://accounts.google.com/ServiceLogin?service=wise&passive=1209600&continue=" + ServiceUrl + "u/0/";

    private Control? _ui;
    private CoreWebView2Environment? _env;
    private CoreWebView2Controller? _cookieHost; // hidden, only used for its cookie manager
    private SignInForm? _signIn;
    private SignInForm? _page; // the "request access" window
    private volatile string? _email;
    private volatile bool _signedIn;
    private volatile bool _expired;
    private Task? _check;
    private System.Windows.Forms.Timer? _timer;

    /// <summary>
    /// The WebView2 profile holding the Google session. Each settings file gets its own, so a second
    /// copy of the app (started with --settings) can use another account, or none.
    /// </summary>
    public static string ProfileName { get; } = MakeProfileName();

    private static string MakeProfileName()
    {
        if (AppSettings.FilePath == AppSettings.DefaultFilePath) return "Google";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(AppSettings.FilePath.ToLowerInvariant()));
        return "Google-" + Convert.ToHexString(hash, 0, 6);
    }

    public bool SignedIn => _signedIn;

    /// <summary>Google has ended the session (they last a limited time), so the account has to sign in again.</summary>
    public bool Expired => _expired;

    /// <summary>The address of the account that's signed in, or was until Google signed it out.</summary>
    public string? Email => _email;

    /// <summary>Raised (on any thread) when the account signs in or out.</summary>
    public event Action? Changed;

    /// <summary>Options that put a WebView in the Google profile. Call on the UI thread.</summary>
    public static CoreWebView2ControllerOptions ControllerOptions(CoreWebView2Environment env)
    {
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = ProfileName;
        return options;
    }

    /// <summary>Reads the saved session (if any). Call once on the UI thread.</summary>
    public async Task InitializeAsync(Control ui, CoreWebView2Environment env)
    {
        _ui = ui;
        _env = env;
        _cookieHost = await env.CreateCoreWebView2ControllerAsync(ui.Handle, ControllerOptions(env));
        _cookieHost.IsVisible = false;
        // Normally well under a second; don't hold up opening the last deck if Google is slow.
        await Task.WhenAny(RefreshStatusAsync(), Task.Delay(5000));
        _timer = new System.Windows.Forms.Timer { Interval = 10 * 60 * 1000 };
        _timer.Tick += (_, _) => _ = RefreshStatusAsync();
        _timer.Start();
    }

    /// <summary>Checks whether Google still accepts the session.</summary>
    public Task CheckAsync() => OnUiAsync(async () =>
    {
        await RefreshStatusAsync();
        return true;
    });

    /// <summary>
    /// Opens the sign-in window (or brings it forward) and completes when it closes.
    /// Returns an error message, or null when the account is signed in.
    /// </summary>
    public Task<string?> SignInAsync() => OnUiAsync(async () =>
    {
        if (_env == null) return "The sign-in window needs the WebView2 runtime.";
        if (_signIn != null)
        {
            _signIn.Activate();
            return await _signIn.Result;
        }

        // One account at a time: start from a clean slate so Google doesn't pick another one.
        await ClearSessionAsync();
        _signIn = new SignInForm(_env, _ui as Form);
        try
        {
            _signIn.Show(_ui as Form);
            var error = await _signIn.Result;
            await RefreshStatusAsync();
            return error ?? (_signedIn ? null : "The sign-in window was closed before you finished signing in.");
        }
        finally
        {
            _signIn.Dispose();
            _signIn = null;
        }
    });

    /// <summary>
    /// Opens Google's page for a presentation or Drive file in a window on the signed-in account, so
    /// the user can press Google's own "Request access" button there (the request then comes from
    /// the account the app uses, not whichever one their browser is signed in to). Returns an error
    /// message, or null once the window is open.
    /// </summary>
    public Task<string?> ShowRequestAccessAsync(string url) => OnUiAsync(() =>
    {
        if (_env == null) return Task.FromResult<string?>("This needs the WebView2 runtime.");
        try
        {
            if (_page is { IsDisposed: false })
            {
                _page.Open(url);
                _page.Activate();
            }
            else
            {
                var page = new SignInForm(_env, _ui as Form, url, closeOnceSignedIn: false);
                _page = page;
                // Signing in on that page counts too.
                page.FormClosed += async (_, _) => { _page = null; await RefreshStatusAsync(); };
                page.Show(_ui as Form);
            }
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex)
        {
            _page = null;
            return Task.FromResult<string?>("Couldn't open Google's page: " + ex.Message);
        }
    });

    public Task SignOutAsync() => OnUiAsync(async () =>
    {
        settings.GoogleEmail = "";
        settings.Save();
        PictureStore.Clear();
        await ClearSessionAsync();
        await RefreshStatusAsync();
        return (string?)null;
    });

    private async Task ClearSessionAsync()
    {
        if (_cookieHost == null) return;
        var profile = _cookieHost.CoreWebView2.Profile;
        profile.CookieManager.DeleteAllCookies();
        await profile.ClearBrowsingDataAsync(
            CoreWebView2BrowsingDataKinds.AllDomStorage | CoreWebView2BrowsingDataKinds.Cookies |
            CoreWebView2BrowsingDataKinds.CacheStorage | CoreWebView2BrowsingDataKinds.DiskCache);
    }

    /// <summary>The Google cookies of the signed-in session (empty when signed out).</summary>
    public Task<IReadOnlyList<Cookie>> GetCookiesAsync() => OnUiAsync(GetCookiesOnUiAsync);

    private async Task<IReadOnlyList<Cookie>> GetCookiesOnUiAsync()
    {
        if (_cookieHost == null) return [];
        var all = await _cookieHost.CoreWebView2.CookieManager.GetCookiesAsync("");
        return all.Where(c => IsGoogleDomain(c.Domain)).Select(c => c.ToSystemNetCookie()).ToList();
    }

    private static bool IsGoogleDomain(string domain)
    {
        domain = domain.TrimStart('.');
        return domain is "google.com" || domain.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase) ||
               domain.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private Task RefreshStatusAsync()
    {
        // One check at a time; whoever asks meanwhile waits for the same one.
        if (_check is { IsCompleted: false }) return _check;
        return _check = RefreshStatusOnUiAsync();
    }

    private async Task RefreshStatusOnUiAsync()
    {
        var cookies = await GetCookiesOnUiAsync();
        var before = (_signedIn, _expired, _email);
        var hasSession = cookies.Any(c => SessionCookies.Contains(c.Name));
        if (!hasSession)
        {
            _signedIn = false;
            _expired = settings.GoogleEmail.Length > 0; // it was signed in, and nobody signed it out here
            _email = _expired ? settings.GoogleEmail : null;
        }
        else if (await IsSessionAcceptedAsync() != false)
        {
            // The check may have renewed the cookies. (No answer, e.g. offline: assume it's still good.)
            _signedIn = true;
            _expired = false;
            cookies = await GetCookiesOnUiAsync();
            _email = await Task.Run(() => FetchEmailAsync(cookies)) ?? (settings.GoogleEmail.Length > 0 ? settings.GoogleEmail : null);
            if (_email != null && settings.GoogleEmail != _email)
            {
                settings.GoogleEmail = _email;
                settings.Save();
            }
        }
        else
        {
            // The cookies are still here but Google no longer accepts them. Remembered as signed out by
            // Google, and dropped: with a dead session Google refuses even public videos.
            _email = settings.GoogleEmail.Length > 0 ? settings.GoogleEmail : await Task.Run(() => FetchEmailAsync(cookies));
            settings.GoogleEmail = _email ?? "a Google account";
            settings.Save();
            await DropSessionCookiesAsync();
            _signedIn = false;
            _expired = true;
        }
        if (before != (_signedIn, _expired, _email)) Changed?.Invoke();
    }

    // Google's session cookies; they're only there while an account is signed in.
    private static readonly HashSet<string> SessionCookies = ["SID", "__Secure-1PSID", "__Secure-3PSID"];
    private static readonly HashSet<string> DeadCookies =
    [
        .. SessionCookies, "HSID", "SSID", "APISID", "SAPISID", "__Secure-1PAPISID", "__Secure-3PAPISID",
        "SIDCC", "__Secure-1PSIDCC", "__Secure-3PSIDCC", "__Secure-1PSIDTS", "__Secure-3PSIDTS", "OSID", "__Secure-OSID",
        "LSID", "__Host-1PLSID", "__Host-3PLSID",
    ];

    private async Task DropSessionCookiesAsync()
    {
        if (_cookieHost == null) return;
        var manager = _cookieHost.CoreWebView2.CookieManager;
        foreach (var c in await manager.GetCookiesAsync(""))
            if (IsGoogleDomain(c.Domain) && DeadCookies.Contains(c.Name)) manager.DeleteCookie(c);
    }

    /// <summary>
    /// Opens Slides' home page in the hidden browser: Google sends it to the sign-in page when the
    /// session has ended. Null when there's no telling (offline, say).
    /// </summary>
    private async Task<bool?> IsSessionAcceptedAsync()
    {
        if (_cookieHost == null) return null;
        var core = _cookieHost.CoreWebView2;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += Completed;
        try
        {
            core.Navigate(ServiceUrl + "u/0/");
            if (await Task.WhenAny(done.Task, Task.Delay(20000)) != done.Task || !await done.Task) return null;
            return Uri.TryCreate(core.Source, UriKind.Absolute, out var uri)
                ? uri.Host == "accounts.google.com" ? false : uri.Host == "docs.google.com" ? true : null
                : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            core.NavigationCompleted -= Completed;
            try { core.Navigate("about:blank"); } catch { /* closing */ }
        }
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+'-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    /// <summary>Asks Google which account the cookies belong to (the same request Chrome uses).</summary>
    private static async Task<string?> FetchEmailAsync(IReadOnlyList<Cookie> cookies)
    {
        try
        {
            var jar = new CookieContainer();
            foreach (var c in cookies) jar.Add(c);
            using var http = new HttpClient(new HttpClientHandler { CookieContainer = jar }) { Timeout = TimeSpan.FromSeconds(15) };
            using var content = new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded");
            using var resp = await http.PostAsync("https://accounts.google.com/ListAccounts?gpsia=1&source=ChromiumBrowser&json=standard", content);
            if (!resp.IsSuccessStatusCode) return null;
            var m = EmailRegex().Match(await resp.Content.ReadAsStringAsync());
            return m.Success ? m.Value : null;
        }
        catch
        {
            return null;
        }
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var ui = _ui;
        if (ui == null || ui.IsDisposed) return Task.FromResult(default(T)!);
        if (!ui.InvokeRequired) return action();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.BeginInvoke(async () =>
        {
            try { tcs.SetResult(await action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>
    /// A plain window showing a Google page in the app's Google profile: the sign-in page (closing
    /// itself once the user is signed in), or a deck or file's page for requesting access.
    /// </summary>
    private sealed class SignInForm : Form
    {
        private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
        private readonly CoreWebView2Environment _env;
        private readonly bool _closeOnceSignedIn;
        private string _url;

        public Task<string?> Result => _result.Task;

        public SignInForm(CoreWebView2Environment env, Form? owner, string url = SignInUrl, bool closeOnceSignedIn = true)
        {
            _env = env;
            _url = url;
            _closeOnceSignedIn = closeOnceSignedIn;
            Text = closeOnceSignedIn ? "Sign in to Google – Presentation Player" : "Request access – Presentation Player";
            Icon = owner?.Icon;
            StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
            ClientSize = closeOnceSignedIn ? new Size(520, 720) : new Size(960, 720);
            MinimumSize = new Size(420, 560);
            Controls.Add(_web);
            Shown += async (_, _) => await StartAsync();
            FormClosed += (_, _) => _result.TrySetResult(null);
        }

        /// <summary>
        /// Google's "You need access" page defaults the requested role to Editor; the app only needs
        /// to view, so pick Viewer (the first role) once the page has drawn it. Best effort: the user
        /// can still change it, and nothing happens if the page looks different.
        /// </summary>
        private const string PickViewerScript = """
            (() => {
              let tries = 0;
              const pick = () => {
                const radios = [...document.querySelectorAll('input[type=radio], [role=radio]')];
                if (radios.length >= 2) {
                  const viewer = radios.find(r => /viewer/i.test((r.closest('label') || r.parentElement || r).textContent || r.getAttribute('aria-label') || '')) || radios[0];
                  viewer.click();
                } else if (++tries < 40) setTimeout(pick, 250);
              };
              pick();
            })();
            """;

        /// <summary>Shows another page in this window.</summary>
        public void Open(string url)
        {
            _url = url;
            _web.CoreWebView2?.Navigate(url);
        }

        private async Task StartAsync()
        {
            try
            {
                await _web.EnsureCoreWebView2Async(_env, ControllerOptions(_env));
                var core = _web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                if (_closeOnceSignedIn)
                    core.DocumentTitleChanged += (_, _) => Text = core.DocumentTitle is { Length: > 0 } t ? t : Text;
                else
                    core.NavigationCompleted += (_, _) => _ = core.ExecuteScriptAsync(PickViewerScript);
                // Help and privacy links open in the default browser rather than in a popup.
                core.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    if (e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
                        catch { /* ignore */ }
                    }
                };
                // Google sends you on to Slides once you're in; that's the cue to close.
                core.SourceChanged += (_, _) =>
                {
                    if (_closeOnceSignedIn && core.Source.StartsWith(ServiceUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        _result.TrySetResult(null);
                        Close();
                    }
                };
                core.Navigate(_url);
            }
            catch (Exception ex)
            {
                _result.TrySetResult("The sign-in window couldn't open: " + ex.Message);
                Close();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _web.Dispose();
            base.Dispose(disposing);
        }
    }
}

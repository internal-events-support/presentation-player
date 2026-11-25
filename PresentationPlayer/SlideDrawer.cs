using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PresentationPlayer;

/// <summary>
/// Turns the drawings (SVG) of slides in the player's page into pictures, in a hidden browser (see draw.html).
/// It's in the Google profile, on an address of Google's that's answered here, so it can get the images
/// in them, which the live player has often fetched already.
/// </summary>
public sealed class SlideDrawer(Control ui) : IDisposable
{
    private const string Page = "https://docs.google.com/presentation-player/draw";
    private const int MaxSvg = 20_000_000; // characters
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleFor = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<int, TaskCompletionSource<string?>> _waiting = new();
    private int _lastId;
    // UI thread only.
    private Task<CoreWebView2Controller>? _browser;
    private System.Windows.Forms.Timer? _idle;
    private bool _disposed;

    /// <summary>Set once the app window is up.</summary>
    public static SlideDrawer? Current { get; set; }

    /// <summary>The picture (JPEG), or null if it couldn't be drawn.</summary>
    public async Task<byte[]?> DrawAsync(string svg, int width, CancellationToken ct)
    {
        if (svg.Length > MaxSvg) return null;
        var id = Interlocked.Increment(ref _lastId);
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = done;
        try
        {
            var message = JsonSerializer.Serialize(new { id, svg, width });
            ui.BeginInvoke(async () =>
            {
                try
                {
                    var browser = await BrowserAsync();
                    browser.CoreWebView2.PostWebMessageAsJson(message);
                }
                catch (Exception)
                {
                    done.TrySetResult(null);
                }
            });
            var data = await done.Task.WaitAsync(Timeout, ct);
            return data == null ? null : Convert.FromBase64String(data);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or FormatException)
        {
            return null; // too slow, or the app is closing
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    private Task<CoreWebView2Controller> BrowserAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_idle == null)
        {
            // It's only needed while a deck's pictures are being made.
            _idle = new System.Windows.Forms.Timer { Interval = (int)IdleFor.TotalMilliseconds };
            _idle.Tick += (_, _) => { if (_waiting.IsEmpty) Close(); };
        }
        _idle.Stop();
        _idle.Start();
        if (_browser is { IsFaulted: true }) _browser = null;
        return _browser ??= CreateAsync();
    }

    private async Task<CoreWebView2Controller> CreateAsync()
    {
        var env = await WebViewEnvironment.GetAsync();
        var browser = await env.CreateCoreWebView2ControllerAsync(ui.Handle, GoogleAccount.ControllerOptions(env));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            browser.IsVisible = false;
            var core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.AddWebResourceRequestedFilter(Page, CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (e.Request.Uri != Page) return;
                using var page = Assembly.GetExecutingAssembly().GetManifestResourceStream("wwwroot/draw.html")!;
                var copy = new MemoryStream();
                page.CopyTo(copy);
                copy.Position = 0;
                // Nothing in a drawing can run or be fetched, other than the images the page's script gets.
                e.Response = env.CreateWebResourceResponse(copy, 200, "OK",
                    "Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; " +
                    "connect-src https://docs.google.com https://ssl.gstatic.com https://*.googleusercontent.com; img-src blob: data:");
            };
            core.NavigationStarting += (_, e) => { if (e.Uri != Page) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.WebMessageReceived += (_, e) =>
            {
                if (e.Source != Page) return;
                try
                {
                    using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                    var id = doc.RootElement.GetProperty("id").GetInt32();
                    if (id == 0) ready.TrySetResult();
                    else if (_waiting.TryGetValue(id, out var done)) done.TrySetResult(doc.RootElement.GetProperty("jpeg").GetString());
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { /* not one of ours */ }
            };
            core.ProcessFailed += (_, _) => ui.BeginInvoke(Close);
            core.Navigate(Page);
            await ready.Task.WaitAsync(Timeout);
            return browser;
        }
        catch
        {
            browser.Close();
            throw;
        }
    }

    private void Close()
    {
        _idle?.Stop();
        if (_browser is { IsCompletedSuccessfully: true } b)
        {
            try { b.Result.Close(); } catch (Exception) { /* it has gone already */ }
        }
        if (_browser is { IsCompleted: true }) _browser = null;
        foreach (var done in _waiting.Values) done.TrySetResult(null);
    }

    public void Dispose()
    {
        _disposed = true;
        Close();
        _idle?.Dispose();
    }

    // ---- The drawings in the player's page ----

    /// <summary>
    /// The slides' drawings by their ids. The page has, for each slide,
    ///   SK_svgData = '…'; … SK_viewerApp.setPageData('id', SK_svgData, …)
    /// Like the rest of the page this isn't documented; whatever can't be found is left out.
    /// </summary>
    public static ConcurrentDictionary<string, string> ParseDrawings(string html)
    {
        const string data = "SK_svgData = '", set = "setPageData('";
        var drawings = new ConcurrentDictionary<string, string>();
        var at = 0;
        while ((at = html.IndexOf(data, at, StringComparison.Ordinal)) >= 0)
        {
            var start = at + data.Length;
            var end = EndOfString(html, start);
            if (end < 0) break;
            at = end;
            var idAt = html.IndexOf(set, end, Math.Min(600, html.Length - end), StringComparison.Ordinal);
            if (idAt < 0) continue;
            idAt += set.Length;
            var idEnd = EndOfString(html, idAt);
            if (idEnd < 0) break;
            if (Unescape(html.AsSpan(start, end - start)) is { } svg && svg.StartsWith("<svg", StringComparison.Ordinal) &&
                Unescape(html.AsSpan(idAt, idEnd - idAt)) is { } id)
                drawings[id] = svg;
        }
        return drawings;
    }

    private static int EndOfString(string s, int from)
    {
        for (var i = from; i < s.Length; i++)
        {
            if (s[i] == '\\') i++;
            else if (s[i] == '\'') return i;
        }
        return -1;
    }

    /// <summary>A JavaScript string's value, or null if it isn't one.</summary>
    private static string? Unescape(ReadOnlySpan<char> s)
    {
        var text = new StringBuilder(s.Length);
        while (s.Length > 0)
        {
            var plain = s.IndexOf('\\');
            if (plain < 0) plain = s.Length;
            text.Append(s[..plain]);
            s = s[plain..];
            if (s.Length == 0) break;
            if (s.Length == 1) return null;
            var digits = s[1] switch { 'x' => 2, 'u' => 4, _ => 0 };
            if (digits > 0)
            {
                if (s.Length < 2 + digits || !ushort.TryParse(s.Slice(2, digits), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var c)) return null;
                text.Append((char)c);
            }
            else
            {
                text.Append(s[1] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f', 'v' => '\v', '0' => '\0', var c => c });
            }
            s = s[(2 + digits)..];
        }
        return text.ToString();
    }
}

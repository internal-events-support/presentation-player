using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace PresentationPlayer;

/// <summary>
/// Keeps an installed copy of the app up to date from the GitHub releases. New versions are downloaded
/// in the background, and installed when the app closes or when the user asks, never while it's running.
/// </summary>
public sealed class Updater : IDisposable
{
    private const string Repo = "https://github.com/internal-events-support/presentation-player";
    private const string AfterUpdate = "--after-update"; // then "open" or "close", then the app's own arguments
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private readonly PresentationController _controller;
    private readonly AppSettings _settings;
    private readonly UpdateManager? _manager;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private volatile VelopackAsset? _ready;
    private volatile string _activity = "";
    private volatile int _percent;
    private volatile string? _error;
    private DateTime? _lastChecked;

    public Updater(PresentationController controller, AppSettings settings)
    {
        _controller = controller;
        _settings = settings;
        try
        {
            // Another source, such as a folder of packages, for testing updates.
            var source = Environment.GetEnvironmentVariable("PRESENTATIONPLAYER_UPDATES");
            var manager = string.IsNullOrWhiteSpace(source)
                ? new UpdateManager(new GithubSource(Repo, null, false))
                : new UpdateManager(source);
            if (!manager.IsInstalled) return; // built from source, or the single exe of older releases
            _manager = manager;
            _ready = manager.UpdatePendingRestart; // downloaded in an earlier run
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    /// <summary>A version is downloaded and can be installed.</summary>
    public bool Ready => _ready != null;

    public object Status() => new
    {
        supported = _manager != null,
        automatic = _settings.AutoUpdate,
        version = Version,
        activity = _activity, // "checking", "downloading" or nothing
        percent = _percent,
        ready = _ready?.Version.ToString(),
        lastChecked = _lastChecked,
        lastError = _error,
        releasesUrl = Repo + "/releases",
    };

    /// <summary>Checks now, and then every few hours while no presentation is open.</summary>
    public void Start()
    {
        if (_manager == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token); // let the outputs and the last presentation start first
                if (_settings.AutoUpdate) await CheckAsync();
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), _stop.Token);
                    if (!_settings.AutoUpdate || DateTime.UtcNow - _lastChecked < CheckEvery) continue;
                    // A download could get in the way of a show.
                    if (string.IsNullOrEmpty(_controller.GetState().PresentationId)) await CheckAsync();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Looks for a newer version and downloads it. Problems are in the status.</summary>
    public async Task CheckAsync()
    {
        if (_manager == null || !await _oneAtATime.WaitAsync(0)) return;
        try
        {
            _error = null;
            _activity = "checking";
            var found = await _manager.CheckForUpdatesAsync();
            if (found == null || found.TargetFullRelease.Version == _ready?.Version) return;
            _percent = 0;
            _activity = "downloading";
            await _manager.DownloadUpdatesAsync(found, p => _percent = p, _stop.Token);
            _ready = found.TargetFullRelease;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _error = (_activity == "downloading" ? "The update couldn't be downloaded. " : "Couldn't check for updates. ") + ex.Message;
        }
        finally
        {
            _lastChecked = DateTime.UtcNow;
            _activity = "";
            _oneAtATime.Release();
        }
    }

    /// <summary>Why the update can't be installed now, or null if it can.</summary>
    public string? CantInstall() =>
        _ready == null ? "There's no update to install."
        : AnotherCopyRunning() ? "Close the other copy of Presentation Player first."
        : null;

    /// <summary>
    /// Has the update installed as soon as this process ends, which should be straight away.
    /// Returns false if it couldn't be started.
    /// </summary>
    /// <param name="reopen">Open the app again afterwards.</param>
    public bool InstallAfterExit(bool reopen, string[] args)
    {
        if (_manager == null || _ready is not { } update || CantInstall() != null) return false;
        try
        {
            Directory.CreateDirectory(AppFolders.Local);
            File.Delete(ReopenFile);
            File.WriteAllText(UpdatingFile, update.Version.ToString());
            // Always started again afterwards, to open the app if it was asked for while it was being updated.
            _manager.WaitExitThenApplyUpdates(update, silent: !reopen, restart: true, [AfterUpdate, reopen ? "open" : "close", .. args]);
            return true;
        }
        catch
        {
            return false; // it's still there for next time
        }
    }

    // ---- Starting up around an update ----

    private static string UpdatingFile => Path.Combine(AppFolders.Local, "updating");
    private static string ReopenFile => Path.Combine(AppFolders.Local, "reopen.json");

    /// <summary>
    /// Call first. Returns the arguments to start the app with, or null to exit: either an update has just
    /// been installed after closing, or one is still being installed (the installer ends any copy that's
    /// running, so the app opens once it has finished instead).
    /// </summary>
    public static string[]? StartupArgs(string[] args)
    {
        var own = args is [AfterUpdate, _, .. var rest] ? rest : args;
        try
        {
            if (args is [AfterUpdate, var then, ..])
            {
                // Opening the app right up until the installer ends counts as during the update.
                for (var i = 0; i < 50 && Installing(); i++) Thread.Sleep(100);
                var reopen = File.Exists(ReopenFile) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(ReopenFile)) : null;
                ClearFiles();
                return then == "open" ? own : reopen;
            }
            if (File.Exists(UpdatingFile) && Installing())
            {
                File.WriteAllText(ReopenFile, JsonSerializer.Serialize(args));
                return null;
            }
            ClearFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Opening the app matters more.
        }
        return own;
    }

    private static void ClearFiles()
    {
        File.Delete(UpdatingFile);
        File.Delete(ReopenFile);
    }

    private static bool Installing() => Running("Update", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Update.exe")));

    /// <summary>Another copy (with its own --settings) would be closed by the update.</summary>
    private static bool AnotherCopyRunning() =>
        Environment.ProcessPath is { } exe && Running(Path.GetFileNameWithoutExtension(exe), exe);

    private static bool Running(string name, string path)
    {
        var found = false;
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try
                {
                    found |= p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Someone else's, or it has just ended.
                }
            }
        }
        return found;
    }

    public void Dispose() => _stop.Cancel();
}

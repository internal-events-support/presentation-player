using System.Diagnostics;
using Velopack;

namespace PresentationPlayer;

internal static class Program
{
    private static volatile bool _restartRequested;
    private static volatile bool _updateRequested;

    [STAThread]
    private static void Main(string[] args)
    {
        // The installer runs the app to let it know about installs and updates. Updates that are ready
        // aren't installed here, so reopening the app during a show doesn't take longer or change it.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        if (Updater.StartupArgs(args) is not { } own) return;
        args = own;

        ApplicationConfiguration.Initialize();

        // --settings <file> runs with a separate settings file (e.g. a second instance with its
        // own API port and NDI source names).
        var i = Array.FindIndex(args, a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length)
            AppSettings.FilePath = Path.GetFullPath(args[i + 1]);

        var settings = AppSettings.Load();
        var google = new GoogleAccount(settings);
        PresentationLoader.Google = google;
        var controller = new PresentationController(settings);
        google.Changed += () => controller.SetGoogleExpired(google.Expired);
        var displays = new DisplayOutputs(controller, settings);
        var audio = new AudioRouter();
        audio.Configure(settings);
        var updater = new Updater(controller, settings);
        MainForm? form = null;
        ApiServer api;
        try
        {
            api = ApiServer.StartAsync(controller, settings, audio, google, displays, many => form?.BrowseAsync(many) ?? Task.FromResult<string[]>([]), () =>
            {
                _restartRequested = true;
                form?.BeginInvoke(form.Close);
            }, updater, () =>
            {
                _updateRequested = true;
                form?.BeginInvoke(form.Close);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show("The app couldn't start its control server.\n\n" + ex.Message,
                "Presentation Player", MessageBoxButtons.OK, MessageBoxIcon.Error);
            displays.Dispose();
            controller.Dispose();
            audio.Dispose();
            return;
        }

        updater.Start();
        Application.Run(form = new MainForm(controller, settings, api, audio, google, displays));

        displays.Dispose();

        settings.Save();
        api.DisposeAsync().AsTask().GetAwaiter().GetResult();
        controller.Dispose(); // frees the NDI source names and port before a restart
        audio.Dispose();
        updater.Dispose();

        var closed = !_restartRequested && !_updateRequested;
        var install = _updateRequested || (closed && settings.AutoUpdate && updater.Ready && !form.WindowsEnding);
        if (install && updater.InstallAfterExit(_updateRequested, args)) return;

        if (!closed && Environment.ProcessPath is { } exe)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
    }
}

using System.Diagnostics;

namespace PresentationPlayer;

internal static class Program
{
    private static volatile bool _restartRequested;

    [STAThread]
    private static void Main(string[] args)
    {
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
        MainForm? form = null;
        ApiServer api;
        try
        {
            api = ApiServer.StartAsync(controller, settings, audio, google, displays, many => form?.BrowseAsync(many) ?? Task.FromResult<string[]>([]), () =>
            {
                _restartRequested = true;
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

        Application.Run(form = new MainForm(controller, settings, api, audio, google, displays));

        displays.Dispose();

        settings.Save();
        api.DisposeAsync().AsTask().GetAwaiter().GetResult();
        controller.Dispose(); // frees the NDI source names and port before a restart
        audio.Dispose();

        if (_restartRequested && Environment.ProcessPath is { } exe)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
    }
}

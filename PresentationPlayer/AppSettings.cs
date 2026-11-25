using System.Text.Json;

namespace PresentationPlayer;

public sealed class RecentPresentation
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int SlideCount { get; set; }
    public DateTime LastOpened { get; set; }
}

public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Persisted user settings, stored in %APPDATA%\PresentationPlayer\settings.json.</summary>
public sealed class AppSettings
{
    public string LastUrl { get; set; } = "";
    public float NotesFontSize { get; set; } = 48;
    public bool AutoFitNotes { get; set; }
    public int ApiPort { get; set; } = 8080;
    public string SlidesNdiName { get; set; } = "Presentation Slides";
    public string NotesNdiName { get; set; } = "Presentation Notes";
    /// <summary>NDI discovery server(s), comma separated ("host" or "host:port"). Empty = automatic discovery.</summary>
    public string NdiDiscoveryServer { get; set; } = "";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int FrameRate { get; set; } = 30;
    /// <summary>Where slide audio plays: "default" (system default device), "none", or a device id ("asio:name" for an ASIO driver).</summary>
    public string AudioOutputDevice { get; set; } = AudioRouter.DefaultDevice;
    public int AudioOutputVolume { get; set; } = 100;
    /// <summary>Optional second device for monitoring (e.g. headphones): "none", "default" or a device id.</summary>
    public string AudioMonitorDevice { get; set; } = AudioRouter.NoDevice;
    public int AudioMonitorVolume { get; set; } = 100;
    /// <summary>First output of the stereo pair used on an ASIO driver (0 = outputs 1 and 2, 2 = outputs 3 and 4).</summary>
    public int AudioAsioChannel { get; set; }
    /// <summary>Captured audio kept queued for an ASIO driver. Lower is less delay, but more likely to drop out.</summary>
    public int AudioAsioBufferMs { get; set; } = 60;
    /// <summary>Allow the REST API / web remote to be reached from other devices (otherwise localhost only).</summary>
    public bool RemoteAccess { get; set; } = true;
    /// <summary>Width of the next slide in the presenter view: "full" (same as the current slide) or "half".</summary>
    public string NextPreview { get; set; } = "full";
    /// <summary>The Google account last signed in, to notice when Google has signed it out. Empty after signing out here.</summary>
    public string GoogleEmail { get; set; } = "";
    /// <summary>Where the outputs are shown on this computer, besides NDI.</summary>
    public OutputPlacement SlidesOutput { get; set; } = new();
    public OutputPlacement NotesOutput { get; set; } = new();
    /// <summary>Download new versions in the background and install them when the app closes.</summary>
    public bool AutoUpdate { get; set; } = true;
    public List<RecentPresentation> Recent { get; set; } = [];
    public List<MasterDeck> MasterDecks { get; set; } = [];
    public WindowPlacement? Window { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static readonly string DefaultFilePath = Path.Combine(AppFolders.Roaming, "settings.json");

    /// <summary>The settings file in use; overridden with the --settings command-line option.</summary>
    public static string FilePath { get; set; } = DefaultFilePath;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings file: fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            lock (JsonOptions) // saved from several threads
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            }
        }
        catch
        {
            // Non-fatal.
        }
    }
}

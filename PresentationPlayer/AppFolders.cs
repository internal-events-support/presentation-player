namespace PresentationPlayer;

/// <summary>
/// Where the app keeps its files. The app has had other names; the first run under this one brings
/// the settings and the built-in browser's data (with the Google sign-in) along.
/// </summary>
internal static class AppFolders
{
    private const string Name = "PresentationPlayer";
    private static readonly string[] OldNames = ["GoogleSlidesNdiPresenter", "GSlideNdi"];

    public static string Roaming { get; } = BringAlongSettings(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    public static string Local { get; } = BringAlongData(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Copies the settings file, so an older version still has its own.</summary>
    internal static string BringAlongSettings(string root)
    {
        var folder = Path.Combine(root, Name);
        var file = Path.Combine(folder, "settings.json");
        try
        {
            var old = OldNames.Select(n => Path.Combine(root, n, "settings.json")).FirstOrDefault(File.Exists);
            if (!File.Exists(file) && old != null)
            {
                Directory.CreateDirectory(folder);
                File.Copy(old, file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starts with default settings.
        }
        return folder;
    }

    /// <summary>Moves the data folder. If it's in use by an older version that's running, it's shared for this run.</summary>
    internal static string BringAlongData(string root)
    {
        var folder = Path.Combine(root, Name);
        var old = Path.Combine(root, OldNames[0]);
        if (Directory.Exists(folder) || !Directory.Exists(old)) return folder;
        try
        {
            Directory.Move(old, folder);
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return old;
        }
    }
}

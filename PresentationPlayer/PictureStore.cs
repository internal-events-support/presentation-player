using System.Security.Cryptography;
using System.Text;

namespace PresentationPlayer;

/// <summary>
/// Keeps the pictures of recently opened Google decks on disk, so opening one again doesn't have to
/// wait for them. They're only used once Google has let the deck be opened, and go when signing out.
/// </summary>
internal static class PictureStore
{
    private const int KeptDecks = 12;
    private static int _epoch;

    private static string Root => Path.Combine(AppFolders.Local, "Pictures");
    private static string DeckFolder(string deckId) => Path.Combine(Root, Name(deckId));
    private static string RevisionFolder(string deckId, long revision) => Path.Combine(DeckFolder(deckId), revision.ToString());

    // Google's ids depend on upper and lower case, which file names don't.
    private static string Name(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)), 0, 12);

    /// <summary>Changes when everything is cleared. Pictures of decks opened before that aren't kept.</summary>
    public static int Epoch => Volatile.Read(ref _epoch);

    /// <summary>Gives the slides the pictures there are. Those of another revision stand in until new ones arrive.</summary>
    public static void Load(string deckId, long? revision, IReadOnlyList<DeckSlide> slides)
    {
        try
        {
            var folder = new DirectoryInfo(DeckFolder(deckId));
            if (!folder.Exists) return;
            Directory.SetLastWriteTimeUtc(folder.FullName, DateTime.UtcNow); // the least recently opened go first
            foreach (var kept in folder.GetDirectories().OrderByDescending(d => d.Name == revision?.ToString()).ThenByDescending(d => d.LastWriteTimeUtc))
            {
                var quality = kept.Name == revision?.ToString() ? PictureQuality.Preview : PictureQuality.Old;
                foreach (var s in slides)
                {
                    if (s.PageId == null || s.Picture.Quality >= quality) continue;
                    var file = Path.Combine(kept.FullName, Name(s.PageId) + ".jpg");
                    if (File.Exists(file)) s.Picture.Offer(File.ReadAllBytes(file), quality);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // They'll be fetched.
        }
    }

    public static void Save(int epoch, string deckId, long? revision, string pageId, byte[] picture)
    {
        if (revision is not { } r || epoch != Epoch) return;
        try
        {
            var folder = RevisionFolder(deckId, r);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, Name(pageId) + ".jpg");
            var part = $"{file}.{Environment.ProcessId}.part";
            File.WriteAllBytes(part, picture);
            File.Move(part, file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not kept this time.
        }
    }

    /// <summary>Tidies up, once a deck has all its pictures.</summary>
    public static void Finish(string deckId, long? revision)
    {
        if (revision is not { } r) return;
        try
        {
            if (Directory.Exists(RevisionFolder(deckId, r)))
                foreach (var d in new DirectoryInfo(DeckFolder(deckId)).GetDirectories())
                    if (d.Name != r.ToString()) d.Delete(true);
            if (Directory.Exists(Root))
                foreach (var d in new DirectoryInfo(Root).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Skip(KeptDecks))
                    d.Delete(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Next time.
        }
    }

    public static void Clear()
    {
        Interlocked.Increment(ref _epoch);
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use by another copy.
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PresentationPlayer;

/// <summary>
/// Draws a PowerPoint file's slides, with PowerPoint if it's installed, otherwise LibreOffice. The
/// result is kept, so a file only has to be converted again after it's been changed.
/// </summary>
internal static class PptxConverter
{
    private const int Kept = 30; // conversions
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static string Root => Path.Combine(AppFolders.Local, "Converted");

    /// <summary>Returns a PDF, or a folder with a picture per slide (1.png, 2.png…).</summary>
    /// <param name="key">Identifies the file and its version.</param>
    public static async Task<string> ConvertAsync(string pptx, string key, int width, int height, CancellationToken ct)
    {
        await OneAtATime.WaitAsync(ct);
        try
        {
            var folder = Path.Combine(Root, $"{key}-{width}x{height}");
            var done = Path.Combine(folder, "done.txt");
            if (File.Exists(done) && File.ReadAllText(done) is { Length: > 0 } result && Path.Exists(Path.Combine(folder, result)))
            {
                Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
                return Path.Combine(folder, result);
            }

            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            // A copy, so it doesn't matter if the file is open somewhere, and nothing can change the original.
            var copy = Path.Combine(folder, "slides" + Path.GetExtension(pptx));
            await using (var from = LocalDecks.OpenShared(pptx))
            await using (var to = File.Create(copy))
                await from.CopyToAsync(to, ct);

            string? error = null;
            try
            {
                foreach (var engine in new Func<string, string, int, int, CancellationToken, Task<string?>>[] { PowerPointAsync, LibreOfficeAsync })
                {
                    try
                    {
                        if (await engine(copy, folder, width, height, ct) is not { } made) continue; // not installed
                        File.Delete(copy);
                        File.WriteAllText(done, Path.GetFileName(made));
                        Prune();
                        return made;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        error = ex.Message;
                    }
                }
                throw new InvalidOperationException(error != null
                    ? "This PowerPoint file couldn't be converted. " + error
                    : "Opening PowerPoint files needs PowerPoint or LibreOffice on this computer, to draw the slides. LibreOffice is free, from libreoffice.org. Or save the presentation as a PDF and open that.");
            }
            catch
            {
                // Nothing to keep, least of all the copy.
                try { Directory.Delete(folder, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next time */ }
                throw;
            }
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static Task<string?> PowerPointAsync(string pptx, string folder, int width, int height, CancellationToken ct)
    {
        if (Type.GetTypeFromProgID("PowerPoint.Application") is not { } type) return Task.FromResult<string?>(null);
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            dynamic? app = null, presentation = null;
            var alreadyOpen = false;
            try
            {
                app = Activator.CreateInstance(type) ?? throw new InvalidOperationException("PowerPoint couldn't start.");
                // There's only ever one PowerPoint: if someone is using it, it's left running.
                alreadyOpen = (int)app.Visible != 0 || (int)app.Presentations.Count > 0;
                app.AutomationSecurity = 3; // no macros
                app.DisplayAlerts = 1; // no messages
                presentation = app.Presentations.Open(pptx, -1, 0, 0); // read-only, as itself, no window
                var (w, h) = ((double)presentation.PageSetup.SlideWidth, (double)presentation.PageSetup.SlideHeight);
                var scale = Math.Min(width / w, height / h);
                var pictures = Directory.CreateDirectory(Path.Combine(folder, "pictures")).FullName;
                int count = presentation.Slides.Count;
                for (var i = 1; i <= count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    presentation.Slides[i].Export(Path.Combine(pictures, i + ".png"), "PNG", (int)Math.Round(w * scale), (int)Math.Round(h * scale));
                }
                result.TrySetResult(pictures);
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
            finally
            {
                try { presentation?.Close(); } catch { /* already gone */ }
                try { if (!alreadyOpen) app?.Quit(); } catch { /* already gone */ }
                if (presentation != null) Marshal.FinalReleaseComObject(presentation);
                if (app != null) Marshal.FinalReleaseComObject(app);
            }
        }) { IsBackground = true, Name = "PowerPoint" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static async Task<string?> LibreOfficeAsync(string pptx, string folder, int width, int height, CancellationToken ct)
    {
        if (FindLibreOffice() is not { } exe) return null;
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        // Its own profile, so it works while LibreOffice is open.
        info.ArgumentList.Add("-env:UserInstallation=" + new Uri(Path.Combine(AppFolders.Local, "LibreOffice")).AbsoluteUri);
        foreach (var a in new[] { "--headless", "--norestore", "--convert-to", """pdf:impress_pdf_Export:{"ExportHiddenSlides":{"type":"boolean","value":"true"}}""", "--outdir", folder, pptx })
            info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("LibreOffice couldn't start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested) throw;
            throw new InvalidOperationException("LibreOffice took too long.");
        }
        var pdf = Path.Combine(folder, Path.GetFileNameWithoutExtension(pptx) + ".pdf");
        return File.Exists(pdf) ? pdf : throw new InvalidOperationException("LibreOffice couldn't read it.");
    }

    private static string? FindLibreOffice()
    {
        // For trying out another converter.
        if (Environment.GetEnvironmentVariable("PRESENTATION_PLAYER_SOFFICE") is { Length: > 0 } given && File.Exists(given)) return given;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\LibreOffice\UNO\InstallPath");
            if (key?.GetValue(null) is string path && Find(path) is { } found) return found;
        }
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(f => Find(Path.Combine(Environment.GetFolderPath(f), "LibreOffice", "program"))).FirstOrDefault(f => f != null);

        // soffice.com waits until it's done; soffice.exe doesn't always.
        static string? Find(string folder) => new[] { "soffice.com", "soffice.exe" }.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
    }

    private static void Prune()
    {
        try
        {
            foreach (var old in new DirectoryInfo(Root).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Skip(Kept))
                old.Delete(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Next time.
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Ffmpeg.Filters;

/// <summary>
/// Materializes burned-in text as a file for <c>drawtext</c>'s <c>textfile=</c> option.
/// <para>
/// Named by a hash of its own contents, which makes the whole thing free of coordination:
/// parallel extractions of the same text share a path, a re-run reuses the file, and different
/// captions cannot collide. Writes go via a unique temporary name and are then moved into place,
/// so a reader can never observe a half-written file. <see cref="Sweep"/> bounds the growth.
/// </para>
/// <para>
/// Files live under <see cref="ScratchPaths.Text"/>, which is per-user and owner-only. The name
/// is deliberately predictable — reuse between runs depends on it — and the fast path below
/// trusts an existing file by name and length alone, so the directory's permissions are the only
/// thing stopping somebody else choosing what your credit line says.
/// </para>
/// </summary>
public static class TextResources
{
    /// <summary>How long an unused text file survives a <see cref="Sweep"/>.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>Guards <see cref="SweepOnce"/>; 1 once a sweep has been started this process.</summary>
    private static int _swept;

    /// <summary>Writes <paramref name="text"/> to a content-addressed file and returns its path.</summary>
    public static string Materialize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var directory = ScratchPaths.EnsureDirectory(DirectoryPath());
        var path = Path.Combine(directory, NameFor(text) + ".txt");

        SweepOnce();

        // UTF-8 without a BOM: drawtext renders the byte order mark as a visible glyph.
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

        // Length is the cheap half of a content check, and catches what a bare File.Exists
        // misses: a truncated file from a killed write, reused silently for ever.
        if (IsIntact(path, bytes.Length))
        {
            // Reuse counts as use: without the touch the timestamp records first write, so a
            // caption used in every render would still be swept -- possibly out from under a
            // render using it. Touching makes the age mean "unused for MaxAge", which is what
            // makes Sweep safe to run concurrently with a render.
            TryTouch(path);
            return path;
        }

        var staging = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";

        try
        {
            File.WriteAllBytes(staging, bytes);
            File.Move(staging, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);

            // Losing the race is benign: the name is a hash of the content, so the winner wrote
            // identical bytes. Anything else — full disk, read-only temp, denied write — leaves
            // no file, and returning the path regardless hands drawtext a textfile= that does
            // not exist, surfacing much later as an opaque mid-render FFmpeg failure.
            if (!IsIntact(path, bytes.Length))
            {
                throw new IOException(
                    $"Could not write attribution text to '{path}': {ex.Message}",
                    ex);
            }
        }

        return path;
    }

    /// <summary>
    /// Deletes text files not touched for <see cref="MaxAge"/>. Called from
    /// <see cref="SweepOnce"/> and from <c>rcc doctor --clear-cache</c>.
    /// <para>
    /// Unlike the pipeline's working directory these outlive the render on purpose, so the next
    /// one reuses them, and nothing else ever removes them. Age is the only safe criterion: a
    /// file in use by a concurrent render looks exactly like one left behind by a finished one.
    /// </para>
    /// </summary>
    public static int Sweep()
    {
        var directory = DirectoryPath();

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - MaxAge;
        var removed = 0;

        foreach (var file in SafeEnumerate(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Being unable to tidy up is never worth surfacing, let alone failing over.
            }
        }

        return removed;
    }

    /// <summary>
    /// Sweeps in the background, at most once per process. Called from
    /// <see cref="Materialize"/> so tidying happens without anybody having to remember it.
    /// <para>
    /// Fire-and-forget, and failure is ignored: housekeeping must not delay a render, and a
    /// render must not fail because housekeeping could not. Safe alongside the extraction that
    /// triggered it because <see cref="Materialize"/> touches every file it reuses, so nothing
    /// in use is old enough to be swept.
    /// </para>
    /// </summary>
    private static void SweepOnce()
    {
        if (Interlocked.Exchange(ref _swept, 1) != 0)
        {
            return;
        }

        _ = Task.Run(static () =>
        {
            try
            {
                Sweep();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Already swallowed per file inside Sweep; this is only for enumeration itself.
            }
        });
    }

    /// <summary>Where the text files live. Per-user and owner-only; see <see cref="ScratchPaths"/>.</summary>
    public static string DirectoryPath() => ScratchPaths.Text;

    /// <summary>The content-addressed file name, without extension.</summary>
    internal static string NameFor(string text)
    {
        // Fully qualified: this project has its own Encoding namespace, which shadows System.Text.
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static bool IsIntact(string path, int expectedBytes)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == expectedBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Marks a file as used. Best-effort: a filesystem that refuses the update costs only an
    /// early sweep, and the file is rewritten next time it is needed.
    /// </summary>
    private static void TryTouch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temp file in the temp directory is not worth failing a render over.
        }
    }
}

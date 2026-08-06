using System.Security.Cryptography;
using System.Text;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Ffmpeg.Filters;

/// <summary>
/// Materializes burned-in text as a file for <c>drawtext</c>'s <c>textfile=</c> option.
/// <para>
/// Named by a hash of its own contents, which makes the whole thing free of coordination: every
/// parallel extraction asks for the same text and gets the same path, a re-run reuses the file
/// rather than rewriting it, and two renders with different captions cannot collide. Writes go
/// via a unique temporary name and are then moved into place, so a reader can never observe a
/// half-written file.
/// </para>
/// <para>
/// Files live under <see cref="ScratchPaths.Text"/>, which is per-user and owner-only. On a
/// shared machine a directory anybody can write to is a path anybody can plant a file in, and
/// since the fast path below trusts an existing file by name and length alone, that would let
/// somebody else choose what your credit line says. Note the name is deliberately predictable —
/// reuse between runs depends on it — so the protection is the directory's permissions, not its
/// name.
/// </para>
/// <para>
/// <see cref="Sweep"/> is what keeps the directory from growing without bound. It runs after any
/// render that burned in text, and from <c>rcc doctor --clear-cache</c>. Both matter: the first
/// is what makes it automatic, and the second is what reaches a directory left behind by a
/// version that did not do the first.
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

        // Length is the cheap half of the content check, and it catches the case the bare
        // File.Exists misses: a run killed mid-write, or a disk that filled, leaving a
        // truncated file that this would otherwise reuse silently for ever.
        if (IsIntact(path, bytes.Length))
        {
            // Reuse counts as use. Without this the timestamp records when the caption was first
            // written, so a credit line used in every render for a year would still be swept
            // seven days in -- and, worse, could be swept out from under a render that is using
            // it right now. Touching it makes the age mean "unused for MaxAge", which is both
            // what Sweep documents and what makes it safe to run concurrently with a render.
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

            // Losing the race to another process is benign: the name is a hash of the content,
            // so identical bytes are the only thing the winner could have written. Anything
            // else — a full disk, a read-only temp directory, a denied write — leaves no file
            // at all, and returning the path regardless would hand drawtext a textfile= that
            // does not exist. That surfaces much later as an opaque mid-render FFmpeg failure,
            // so the cause is reported here instead.
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
    /// Deletes text files not touched for <see cref="MaxAge"/>.
    /// <para>
    /// These are tiny, but nothing else ever removes them: unlike the pipeline's working
    /// directory they outlive the render on purpose, so that the next one reuses them. Age is
    /// the only safe criterion, since a file in use by a concurrent render looks exactly like
    /// one left behind by a finished one.
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
    /// Sweeps in the background, at most once per process.
    /// <para>
    /// Called from <see cref="Materialize"/> so tidying happens without anybody having to
    /// remember it: these files outlive the render on purpose, nothing else removes them, and
    /// leaving it to <c>doctor --clear-cache</c> meant in practice that it never ran at all.
    /// </para>
    /// <para>
    /// Fire-and-forget, and failure is ignored. Housekeeping must not delay a render, and a
    /// render must not fail because housekeeping could not. Safe to run alongside the extraction
    /// that triggered it because <see cref="Materialize"/> touches every file it reuses, so
    /// nothing in use is old enough to be swept.
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
    /// Marks a file as used. Best-effort: a filesystem that refuses the update costs nothing
    /// worse than an early sweep, which rewrites the file next time it is needed.
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

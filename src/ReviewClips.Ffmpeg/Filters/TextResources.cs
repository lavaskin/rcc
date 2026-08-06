using System.Security.Cryptography;
using System.Text;
using ReviewClips.Core.Primitives;

namespace ReviewClips.Ffmpeg.Filters;

/// <summary>
/// Materialises burned-in text as a file for <c>drawtext</c>'s <c>textfile=</c> option.
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
/// name. <see cref="Sweep"/> keeps it from growing without bound.
/// </para>
/// </summary>
public static class TextResources
{
    /// <summary>How long an unused text file survives a <see cref="Sweep"/>.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>Writes <paramref name="text"/> to a content-addressed file and returns its path.</summary>
    public static string Materialise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var directory = ScratchPaths.EnsureDirectory(DirectoryPath());
        var path = Path.Combine(directory, NameFor(text) + ".txt");

        // UTF-8 without a BOM: drawtext renders the byte order mark as a visible glyph.
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

        // Length is the cheap half of the content check, and it catches the case the bare
        // File.Exists misses: a run killed mid-write, or a disk that filled, leaving a
        // truncated file that this would otherwise reuse silently for ever.
        if (IsIntact(path, bytes.Length))
        {
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

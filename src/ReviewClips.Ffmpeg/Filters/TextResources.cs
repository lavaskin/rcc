using System.Security.Cryptography;
using System.Text;

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
/// </summary>
public static class TextResources
{
    /// <summary>Writes <paramref name="text"/> to a content-addressed file and returns its path.</summary>
    public static string Materialise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var directory = Path.Combine(Path.GetTempPath(), "reviewclips", "text");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, NameFor(text) + ".txt");

        if (File.Exists(path))
        {
            return path;
        }

        // UTF-8 without a BOM: drawtext renders the byte order mark as a visible glyph.
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        var staging = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";

        try
        {
            File.WriteAllBytes(staging, bytes);
            File.Move(staging, path, overwrite: true);
        }
        catch (IOException)
        {
            // Another process won the race and wrote identical content, which is the only thing
            // it could have written given the name is a hash of it.
            TryDelete(staging);
        }

        return path;
    }

    /// <summary>The content-addressed file name, without extension.</summary>
    internal static string NameFor(string text)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A stray temp file in the temp directory is not worth failing a render over.
        }
    }
}

namespace ReviewClips.Core.Primitives;

/// <summary>
/// Every location rcc writes to outside the output directory: extracted segments, cached
/// analysis, and burned-in text. Centralized so the ownership and permission rules on
/// <see cref="Root"/> apply to all of them by construction.
/// </summary>
public static class ScratchPaths
{
    /// <summary>
    /// rcc's private scratch root for the current user.
    /// <para>
    /// Per-user because anything trusted by name must live where no other user can write:
    /// attribution text is content-addressed and reused whenever a file of the expected name and
    /// length is present, so whoever can create files here decides what a caption says. The names
    /// are deliberately predictable, so protection comes entirely from
    /// <see cref="EnsureDirectory"/>'s permissions. A shared root would also belong to whoever
    /// ran rcc first.
    /// </para>
    /// </summary>
    public static string Root =>
        Path.Combine(Path.GetTempPath(), $"reviewclips-{SafeUserName()}");

    /// <summary>Working directories for in-progress renders. One subdirectory per render.</summary>
    public static string Work => Path.Combine(Root, "work");

    /// <summary>Materialized <c>drawtext</c> text files, which outlive a render on purpose.</summary>
    public static string Text => Path.Combine(Root, "text");

    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Creates a directory under <see cref="Root"/>, owner-only on Unix, and returns its path.
    /// <para>
    /// Every level from <see cref="Root"/> down is tightened, not just the leaf: a private leaf
    /// inside a writable parent can be renamed aside and replaced. The mode is reapplied on every
    /// call, not only at creation, so a pre-created directory is closed before anything inside it
    /// is trusted; if another user owns it, tightening throws rather than being swallowed.
    /// </para>
    /// </summary>
    public static string EnsureDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Directory.CreateDirectory(path).FullName;

        if (OperatingSystem.IsWindows())
        {
            // Windows resolves the temp path inside the user's own profile, which is already
            // private, so there is no shared parent to close.
            return full;
        }

        var root = Path.GetFullPath(Root);
        Restrict(Directory.CreateDirectory(root).FullName);

        // Walk down from the root, so a parent is closed before its child is created inside it.
        if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            var relative = full[(root.Length + 1)..];
            var current = root;

            foreach (var part in relative.Split(
                Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                Restrict(current);
            }
        }

        return full;
    }

    /// <summary>Applies <see cref="OwnerOnly"/> to one directory. Unix only; callers guard the platform.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Restrict(string directory)
    {
        try
        {
            File.SetUnixFileMode(directory, OwnerOnly);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"'{directory}' exists but belongs to another user, so it cannot be used as a "
                + "private scratch directory. Remove it, or point TMPDIR somewhere writable.",
                ex);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // Some filesystems carry no Unix modes at all: nothing to tighten, so proceed.
        }
    }

    /// <summary>
    /// The user name, reduced to characters that are safe in a path.
    /// <para>
    /// <see cref="Environment.UserName"/> can be empty in a container, and a Windows domain
    /// account can contain a backslash; either would collapse the per-user directory back into a
    /// shared one, so both fall back to a fixed placeholder.
    /// </para>
    /// </summary>
    private static string SafeUserName()
    {
        var name = Environment.UserName;

        if (string.IsNullOrWhiteSpace(name))
        {
            return "default";
        }

        var safe = new string([.. name.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]);

        return safe.Trim('.', '_') is { Length: > 0 } trimmed ? trimmed : "default";
    }
}

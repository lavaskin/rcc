namespace ReviewClips.Core.Primitives;

/// <summary>
/// Every location rcc writes to outside the output directory: extracted segments, cached
/// analysis, and burned-in text.
/// <para>
/// All three are owned here so there is one answer to where rcc puts things, and so the
/// ownership and permission rules described on <see cref="Root"/> apply to all of them by
/// construction rather than being restated at each call site.
/// </para>
/// </summary>
public static class ScratchPaths
{
    /// <summary>
    /// rcc's private scratch root for the current user.
    /// <para>
    /// Scoped per user for two reasons. The lesser is that a single shared directory belongs to
    /// whoever runs rcc first, and everybody else then gets permission errors inside it.
    /// </para>
    /// <para>
    /// The greater is that anything trusted by name has to live somewhere no other user can write
    /// to. Attribution text is content-addressed and reused whenever a file of the expected name
    /// and length is already present, so whoever can create files here decides what a caption
    /// says. The name is deliberately predictable — reuse between runs depends on it — so the
    /// protection comes entirely from <see cref="EnsureDirectory"/>'s permissions.
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
    /// Every level from <see cref="Root"/> down is tightened, not just the leaf. A private leaf
    /// inside a world-writable parent is not private: anyone able to write to the parent can
    /// rename the leaf aside and put their own directory in its place, and the permissions on the
    /// thing they replaced it with are their choice.
    /// </para>
    /// <para>
    /// The mode is reapplied on every call rather than only at creation, so a directory that was
    /// pre-created with loose permissions is closed before anything inside it is trusted. If
    /// another user owns it, tightening fails and that is reported rather than swallowed — it
    /// means the path is not ours and nothing found in it can be relied on.
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

    /// <summary>
    /// Applies <see cref="OwnerOnly"/> to one directory. Unix only; callers guard the platform,
    /// and the annotation keeps that contract checkable rather than a matter of trust.
    /// </summary>
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
            // Some filesystems carry no Unix modes at all. There is nothing to tighten, and
            // failing a render over it would be worse than proceeding.
        }
    }

    /// <summary>
    /// The user name, reduced to characters that are safe in a path.
    /// <para>
    /// <see cref="Environment.UserName"/> can be empty in a container, and on Windows a domain
    /// account can contain a backslash. Either would collapse the per-user directory back into
    /// a shared one, so both are normalised to a fixed placeholder instead.
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

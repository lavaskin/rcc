namespace ReviewClips.Core.Sources;

/// <summary>
/// Expands user-supplied inputs into a concrete list of media files. Accepts a file, a
/// directory, or a glob.
/// <para>
/// Being source-agnostic is deliberate: the same pipeline runs over a feature film, a folder
/// of trailers, public-domain footage, or licensed stock. That means you can change where the
/// footage comes from — including for copyright reasons — without changing anything else.
/// </para>
/// </summary>
public sealed class SourceResolver
{
    private static readonly string[] DefaultExtensions =
    [
        ".mp4", ".mkv", ".mov", ".m4v", ".avi", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg", ".wmv", ".flv",
    ];

    private readonly HashSet<string> _extensions;

    public SourceResolver(IEnumerable<string>? extensions = null) =>
        _extensions = (extensions ?? DefaultExtensions)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> KnownExtensions => _extensions;

    /// <summary>Resolves every input, de-duplicating while preserving discovery order.</summary>
    /// <exception cref="SourceResolutionException">When an input matches nothing.</exception>
    public IReadOnlyList<string> Resolve(IEnumerable<string> inputs, bool recursive = true)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            var matches = ResolveSingle(input.Trim(), recursive);
            if (matches.Count == 0)
            {
                throw new SourceResolutionException($"Input '{input}' did not match any media files.");
            }

            foreach (var match in matches)
            {
                if (seen.Add(match))
                {
                    results.Add(match);
                }
            }
        }

        if (results.Count == 0)
        {
            throw new SourceResolutionException("No media files were found for the supplied inputs.");
        }

        return results;
    }

    private List<string> ResolveSingle(string input, bool recursive)
    {
        if (File.Exists(input))
        {
            return [Path.GetFullPath(input)];
        }

        if (Directory.Exists(input))
        {
            return EnumerateMedia(input, "*", recursive);
        }

        // Treat anything else as a glob: split the fixed directory part from the pattern.
        var directory = Path.GetDirectoryName(input);
        var pattern = Path.GetFileName(input);

        if (string.IsNullOrEmpty(pattern))
        {
            return [];
        }

        directory = string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory;

        return Directory.Exists(directory)
            ? EnumerateMedia(directory, pattern, recursive: false)
            : [];
    }

    private List<string> EnumerateMedia(string directory, string pattern, bool recursive)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        return
        [
            .. Directory
                .EnumerateFiles(directory, pattern, options)
                .Where(f => _extensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(Path.GetFullPath),
        ];
    }
}

public sealed class SourceResolutionException : Exception
{
    public SourceResolutionException(string message) : base(message)
    {
    }

    public SourceResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SourceResolutionException()
    {
    }
}

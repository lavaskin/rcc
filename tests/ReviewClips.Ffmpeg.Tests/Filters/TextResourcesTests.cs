using ReviewClips.Ffmpeg.Filters;

namespace ReviewClips.Ffmpeg.Tests.Filters;

/// <summary>
/// Content-addressed materialisation of attribution text. The design rests on three claims:
/// identical text always resolves to the same path, parallel extractions therefore need no
/// coordination, and what lands on disk is exactly the bytes drawtext should read.
/// </summary>
public class TextResourcesTests
{
    [Fact]
    public void IdenticalTextResolvesToTheSamePath()
    {
        const string Text = "Clip from Heat (1995), dir. Michael Mann";

        TextResources.Materialize(Text).ShouldBe(TextResources.Materialize(Text));
    }

    [Fact]
    public void DifferentTextResolvesToDifferentPaths() =>
        TextResources.Materialize("Clip from Heat (1995)")
            .ShouldNotBe(TextResources.Materialize("Clip from Ronin (1998)"));

    [Fact]
    public void TheFileHoldsExactlyTheTextGiven()
    {
        const string Text = "Clip from Amelie (2001)";

        var bytes = File.ReadAllBytes(TextResources.Materialize(Text));

        // Fully qualified: this project has its own Encoding namespace, shadowing System.Text.
        System.Text.Encoding.UTF8.GetString(bytes).ShouldBe(Text);
    }

    [Fact]
    public void NonAsciiTextIsWrittenWithoutAByteOrderMark()
    {
        // drawtext renders a BOM as a visible glyph, so a caption would silently gain a box in
        // front of it. Only a non-ASCII title makes a careless encoder choice show up at all.
        var bytes = File.ReadAllBytes(TextResources.Materialize("Le Fabuleux Destin d'Amélie"));

        bytes.Length.ShouldBeGreaterThan(3);
        (bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF).ShouldBeFalse();
    }

    [Fact]
    public void TextWithReservedCharactersRoundTripsUntouched()
    {
        // Reading from a file is why these need no escaping, and why nothing may be escaped on
        // the way in either.
        const string Text = "100% [practical] effects: no CGI, 'honest', back\\slash {here}";

        File.ReadAllText(TextResources.Materialize(Text)).ShouldBe(Text);
    }

    [Fact]
    public void EmptyTextIsAllowedRatherThanThrowing() =>
        File.Exists(TextResources.Materialize(string.Empty)).ShouldBeTrue();

    [Fact]
    public void NullTextIsRejected() =>
        Should.Throw<ArgumentNullException>(() => TextResources.Materialize(null!));

    [Fact]
    public void ATruncatedFileIsRewrittenRatherThanReused()
    {
        // A run killed mid-write leaves a short file. The bare existence check this replaced
        // would have reused it silently, for ever.
        const string Text = "Clip from The Insider (1999), dir. Michael Mann";

        var path = TextResources.Materialize(Text);
        File.WriteAllText(path, "trunc");

        var recovered = TextResources.Materialize(Text);

        recovered.ShouldBe(path);
        File.ReadAllText(recovered).ShouldBe(Text);
    }

    [Fact]
    public void AFailedWriteThrowsRatherThanReturningAPathToNothing()
    {
        // Nothing downstream checks the file exists, so swallowing the write failure and
        // returning the path turns a full disk into an opaque FFmpeg error minutes into a render.
        var text = $"Unwritable caption {Guid.NewGuid():N}";

        // A directory sitting where the file belongs fails the move for the same reasons a
        // full or read-only disk would, without needing either.
        var path = Path.Combine(TextResources.DirectoryPath(), TextResources.NameFor(text) + ".txt");
        Directory.CreateDirectory(path);

        try
        {
            var ex = Should.Throw<IOException>(() => TextResources.Materialize(text));

            ex.Message.ShouldContain(path);
            ex.InnerException.ShouldNotBeNull();
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void AStrayTemporaryFileIsNotLeftBehindByAFailedWrite()
    {
        var text = $"Unwritable caption {Guid.NewGuid():N}";
        var path = Path.Combine(TextResources.DirectoryPath(), TextResources.NameFor(text) + ".txt");
        Directory.CreateDirectory(path);

        try
        {
            Should.Throw<IOException>(() => TextResources.Materialize(text));

            Directory.GetFiles(TextResources.DirectoryPath(), Path.GetFileName(path) + ".*.tmp")
                .ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void ConcurrentCallersAllGetTheSameIntactFile()
    {
        // A staging-then-move that was not atomic would show up here as a throw or a short read.
        var text = $"Clip from Collateral (2004) {Guid.NewGuid():N}";

        var paths = new string[32];
        Parallel.For(0, paths.Length, i => paths[i] = TextResources.Materialize(text));

        paths.Distinct(StringComparer.Ordinal).ShouldHaveSingleItem();
        File.ReadAllText(paths[0]).ShouldBe(text);
    }

    [Fact]
    public void ConcurrentCallersWithDifferentTextDoNotCollide()
    {
        var texts = Enumerable.Range(0, 32).Select(i => $"Caption number {i}").ToArray();
        var paths = new string[texts.Length];

        Parallel.For(0, texts.Length, i => paths[i] = TextResources.Materialize(texts[i]));

        paths.Distinct(StringComparer.Ordinal).Count().ShouldBe(texts.Length);

        for (var i = 0; i < texts.Length; i++)
        {
            File.ReadAllText(paths[i]).ShouldBe(texts[i]);
        }
    }

    [Fact]
    public void TheNameIsDerivedFromTheContent()
    {
        TextResources.NameFor("a").ShouldBe(TextResources.NameFor("a"));
        TextResources.NameFor("a").ShouldNotBe(TextResources.NameFor("b"));

        // 16 hex characters of SHA-256. Short enough to keep the path readable, long enough
        // that a collision between two captions is not a thing that happens.
        TextResources.NameFor("a").Length.ShouldBe(16);
        TextResources.NameFor("a").ShouldAllBe(c => Uri.IsHexDigit(c) && !char.IsUpper(c));
    }

    [Fact]
    public void FilesLiveSomewhereNotSharedWithOtherUsers()
    {
        // A predictable shared path lets somebody else create the file first, and the fast path
        // trusts an existing file by name. Scoping the directory by user removes the shared name.
        var directory = TextResources.DirectoryPath();

        directory.ShouldContain(Environment.UserName);
        Path.IsPathFullyQualified(directory).ShouldBeTrue();
    }

    [Fact]
    public void SweepLeavesFilesThatAreStillFresh()
    {
        var path = TextResources.Materialize($"Fresh caption {Guid.NewGuid():N}");

        TextResources.Sweep();

        // Age is the only safe criterion: a file in use by a concurrent render is
        // indistinguishable from one left behind by a finished one.
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void SweepRemovesFilesThatHaveGoneStale()
    {
        var path = TextResources.Materialize($"Stale caption {Guid.NewGuid():N}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(30));

        TextResources.Sweep().ShouldBeGreaterThan(0);

        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void SweepOnAnAbsentDirectoryIsHarmless()
    {
        // Nothing has necessarily ever been materialised when doctor --clear-cache runs.
        TextResources.Sweep();
        TextResources.Sweep().ShouldBeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// Reuse has to count as use, or the age records when a caption was first written rather than
    /// when it was last needed: a long-lived credit line could then be swept out from under the
    /// render using it, since reuse is the one path that does not rewrite the file.
    /// </summary>
    [Fact]
    public void ReusingAFileMarksItAsUsed()
    {
        var text = $"Reused caption {Guid.NewGuid():N}";
        var path = TextResources.Materialize(text);

        // Old enough that the next sweep would take it.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(30));

        TextResources.Materialize(text).ShouldBe(path);

        File.GetLastWriteTimeUtc(path)
            .ShouldBeGreaterThan(DateTime.UtcNow - TimeSpan.FromMinutes(1));

        TextResources.Sweep();
        File.Exists(path).ShouldBeTrue("a file just reused must survive the sweep");
    }
}

using System.CommandLine;
using ReviewClips.Cli.Cli;
using ReviewClips.Cli.Profiles;
using ReviewClips.Core.Options;
using ReviewClips.Core.Sources;

namespace ReviewClips.Cli.Tests;

/// <summary>
/// Parses a command line the way <c>rcc generate</c> does and hands back the resolved request.
/// <para>
/// Goes through the real <see cref="GenerateOptions"/> and the real parser rather than
/// constructing a <see cref="ClipRequest"/> directly, so tests cover option binding, then profile
/// layering, then validation. The interesting behavior is in the joins between those three, and
/// a hand-built request has no joins.
/// </para>
/// </summary>
internal sealed class RequestBuilderHarness : IDisposable
{
    private readonly string _directory;

    public RequestBuilderHarness(params RenderProfile[] profiles)
    {
        _directory = Path.Combine(Path.GetTempPath(), "rcc_cli_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);

        // SourceResolver only checks existence and extension, so an empty file is a valid source
        // as far as the builder is concerned. Nothing here reaches FFmpeg.
        SourcePath = Path.Combine(_directory, "source.mp4");
        File.WriteAllBytes(SourcePath, []);

        AudioPath = Path.Combine(_directory, "bed.wav");
        File.WriteAllBytes(AudioPath, []);

        Profiles = new ProfileLibrary(profiles);
    }

    public string SourcePath { get; }

    public string AudioPath { get; }

    private ProfileLibrary Profiles { get; }

    /// <summary>A path inside the harness's scratch directory, for tests that need one.</summary>
    public string PathFor(string name) => Path.Combine(_directory, name);

    /// <summary>Builds a request from the given arguments, with <c>-i</c> supplied automatically.</summary>
    public ClipRequest Build(params string[] arguments)
    {
        var options = new GenerateOptions();
        var command = new Command("generate");
        options.AddTo(command);

        var parse = command.Parse(["-i", SourcePath, .. arguments]);

        // A binder error is not a validation error: without this, a test asserting "this is
        // rejected" would pass just as happily when the option name had a typo in it.
        if (parse.Errors.Count > 0)
        {
            throw new ParseFailedException(string.Join("; ", parse.Errors.Select(e => e.Message)));
        }

        var builder = new ClipRequestBuilder(options, Profiles, new SourceResolver());
        return builder.Build(parse);
    }

    /// <summary>The message from the <see cref="CliUsageException"/> the arguments provoke.</summary>
    public string Rejection(params string[] arguments) =>
        Should.Throw<CliUsageException>(() => Build(arguments)).Message;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}

/// <summary>Raised when the argument parser itself rejected the input, before validation ran.</summary>
internal sealed class ParseFailedException : Exception
{
    public ParseFailedException(string message) : base(message)
    {
    }
}

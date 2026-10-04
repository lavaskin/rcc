using ReviewClips.Cli.Presentation;
using ReviewClips.Core.Pipeline;
using Spectre.Console;

namespace ReviewClips.Cli.Tests;

/// <summary>
/// The console's progress output. The bars are live displays running on their own thread, so
/// what matters is that each one is closed before the next thing is printed, and that nothing
/// reopens one after a failure has closed it.
/// </summary>
public class ConsoleRenderObserverTests
{
    private const string HideCursor = "\u001b[?25l";
    private const string ShowCursor = "\u001b[?25h";

    private static (IAnsiConsole Console, StringWriter Output) CreateConsole(bool interactive)
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = interactive ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = interactive ? ColorSystemSupport.Standard : ColorSystemSupport.NoColors,
            Interactive = interactive ? InteractionSupport.Yes : InteractionSupport.No,
            Out = new AnsiConsoleOutput(output),

            // Spectre's CI enrichers override the settings above: on GitHub Actions they force
            // Ansi on and Interactive off, turning each case here into the other.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });

        console.Profile.Width = 100;
        return (console, output);
    }

    private static void Render(ConsoleRenderObserver observer, int clips)
    {
        observer.OnPhaseStarted(RenderPhase.Extracting, $"{clips} clip(s)");
        observer.OnExtractionProgress(0, clips, 0d);

        for (var done = 1; done <= clips; done++)
        {
            observer.OnExtractionProgress(done, clips, (double)done / clips);
        }

        observer.OnPhaseStarted(RenderPhase.Stitching, "DipToBlack");

        for (var percent = 1; percent <= 100; percent++)
        {
            observer.OnStitchProgress(percent / 100d);
        }

        observer.OnPhaseStarted(RenderPhase.Finalizing, "out.mp4");
    }

    [Fact]
    public void Each_bar_is_closed_before_the_next_phase_is_announced()
    {
        var (console, output) = CreateConsole(interactive: true);

        using (var observer = new ConsoleRenderObserver(console))
        {
            Render(observer, clips: 31);
        }

        var text = output.ToString();
        var extracting = text.IndexOf("> Extracting", StringComparison.Ordinal);
        var lastClips = text.LastIndexOf("encoded 31/31", StringComparison.Ordinal);
        var stitching = text.IndexOf("> Stitching", StringComparison.Ordinal);
        var lastStitch = text.LastIndexOf("stitching", StringComparison.Ordinal);
        var finalizing = text.IndexOf("> Finalizing", StringComparison.Ordinal);

        extracting.ShouldBeGreaterThanOrEqualTo(0);
        lastClips.ShouldBeGreaterThan(extracting);
        stitching.ShouldBeGreaterThan(lastClips);
        lastStitch.ShouldBeGreaterThan(stitching);
        finalizing.ShouldBeGreaterThan(lastStitch);
        text.ShouldContain("100%");
    }

    [Fact]
    public void A_redirected_console_gets_plain_text_rather_than_a_line_per_clip()
    {
        var (console, output) = CreateConsole(interactive: false);

        using (var observer = new ConsoleRenderObserver(console))
        {
            Render(observer, clips: 31);
        }

        var text = output.ToString();
        text.ShouldNotContain("\u001b");
        text.ShouldNotContain("\r");
        text.Split('\n').Count(line => line.Contains("encoded", StringComparison.Ordinal)).ShouldBeLessThan(31);
    }

    [Fact]
    public void Disposing_mid_bar_closes_it_and_restores_the_cursor()
    {
        var (console, output) = CreateConsole(interactive: true);

        var observer = new ConsoleRenderObserver(console);
        observer.OnPhaseStarted(RenderPhase.Stitching, "DipToBlack");
        observer.OnStitchProgress(0.4);
        observer.Dispose();

        var text = output.ToString();
        text.ShouldContain("40%");
        text.LastIndexOf(ShowCursor, StringComparison.Ordinal)
            .ShouldBeGreaterThan(text.LastIndexOf(HideCursor, StringComparison.Ordinal));
    }

    [Fact]
    public void A_report_arriving_after_dispose_does_not_reopen_a_bar()
    {
        var (console, output) = CreateConsole(interactive: true);

        var observer = new ConsoleRenderObserver(console);
        observer.OnPhaseStarted(RenderPhase.Stitching, "DipToBlack");
        observer.Dispose();
        var closed = output.ToString();

        // An FFmpeg killed by Ctrl+C can flush a last progress line after the pipeline unwound.
        observer.OnStitchProgress(0.5);
        observer.OnPhaseStarted(RenderPhase.Stitching, "DipToBlack");

        output.ToString()[closed.Length..].ShouldNotContain(HideCursor);
    }

    [Fact]
    public void A_warning_lands_below_the_bar_it_interrupts()
    {
        var (console, output) = CreateConsole(interactive: true);

        using (var observer = new ConsoleRenderObserver(console))
        {
            observer.OnPhaseStarted(RenderPhase.Analyzing, "1 source(s)");
            observer.OnAnalysisStarted("/movies/film.mkv", fromCache: false);
            observer.OnAnalysisProgress("/movies/film.mkv", 0.43);
            observer.OnWarning("Analysis failed");

            // The interrupted step's bar is gone; its stragglers must not draw a new one.
            observer.OnAnalysisProgress("/movies/film.mkv", 0.44);
        }

        var text = output.ToString();
        var warning = text.IndexOf("Analysis failed", StringComparison.Ordinal);
        text.LastIndexOf("43%", StringComparison.Ordinal).ShouldBeLessThan(warning);
        text.IndexOf("44%", StringComparison.Ordinal).ShouldBe(-1);
    }
}

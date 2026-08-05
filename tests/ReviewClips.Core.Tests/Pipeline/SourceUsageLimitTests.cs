using ReviewClips.Core.Pipeline;

namespace ReviewClips.Core.Tests.Pipeline;

/// <summary>
/// The guardrail as the pipeline actually applies it: warn by default, refuse on request.
/// </summary>
public class SourceUsageLimitTests
{
    /// <summary>A 60s render from a 180s source: 33%, comfortably over the 10% guideline.</summary>
    private static (PipelineHarness Harness, string Source) Heavy()
    {
        var harness = new PipelineHarness();
        return (harness, harness.AddSource("film.mkv", 180));
    }

    /// <summary>A 60s render from a 3600s feature: 1.7%, comfortably inside it.</summary>
    private static (PipelineHarness Harness, string Source) Light()
    {
        var harness = new PipelineHarness();
        return (harness, harness.AddSource("feature.mkv", 3600));
    }

    [Fact]
    public async Task WarnsByDefaultRatherThanFailing()
    {
        var (harness, source) = Heavy();

        // pi hard-fails here. That refuses an entirely ordinary short render, so rcc warns and
        // leaves the refusal to --strict-source-limit.
        var plan = await harness.PlanAsync(PipelineHarness.Request(source));

        plan.Segments.ShouldNotBeEmpty();
        plan.Warnings.ShouldContain(w => w.Contains("of the supplied footage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StaysQuietWhenInsideTheLimit()
    {
        var (harness, source) = Light();

        var plan = await harness.PlanAsync(PipelineHarness.Request(source));

        plan.SourceUsage.ExceedsLimit.ShouldBeFalse();
        plan.Warnings.ShouldNotContain(w => w.Contains("of the supplied footage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StrictModeRefusesTheRender()
    {
        var (harness, source) = Heavy();
        var request = PipelineHarness.Request(source) with { EnforceMaxSourceFraction = true };

        var ex = await Should.ThrowAsync<SourceUsageLimitException>(
            () => harness.PlanAsync(request));

        ex.Message.ShouldContain("--max-clips");
        ex.Report.ShouldNotBeNull();
        ex.Report.ExceedsLimit.ShouldBeTrue();
    }

    [Fact]
    public async Task StrictModeAllowsARenderInsideTheLimit()
    {
        var (harness, source) = Light();
        var request = PipelineHarness.Request(source) with { EnforceMaxSourceFraction = true };

        var plan = await harness.PlanAsync(request);

        plan.Segments.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ARaisedLimitSuppressesTheWarning()
    {
        var (harness, source) = Heavy();

        var plan = await harness.PlanAsync(PipelineHarness.Request(source) with { MaxSourceFraction = 0.50d });

        plan.SourceUsage.ExceedsLimit.ShouldBeFalse();
        plan.Warnings.ShouldNotContain(w => w.Contains("of the supplied footage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AZeroLimitDisablesTheCheckEntirelyEvenInStrictMode()
    {
        var (harness, source) = Heavy();
        var request = PipelineHarness.Request(source) with
        {
            MaxSourceFraction = 0d,
            EnforceMaxSourceFraction = true,
        };

        var plan = await harness.PlanAsync(request);

        plan.Segments.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task MaxClipsIsTheWayOutOfTheLimit()
    {
        var harness = new PipelineHarness();
        var source = harness.AddSource("film.mkv", 600);

        // 300s of runtime at 5s a clip is 60 slots, i.e. 300s of footage from a 600s film: 50%.
        var unbounded = await harness.PlanAsync(PipelineHarness.Request(source, durationSeconds: 300));
        unbounded.SourceUsage.ExceedsLimit.ShouldBeTrue();

        // Capping the pool at 10 clips fills the same runtime from 50s of film: 8.3%.
        var capped = await harness.PlanAsync(
            PipelineHarness.Request(source, durationSeconds: 300, maxClips: 10));

        capped.EffectiveDuration.ShouldBe(unbounded.EffectiveDuration, TimeSpan.FromSeconds(1));
        capped.SourceUsage.Used.ShouldBeLessThan(unbounded.SourceUsage.Used);
        capped.SourceUsage.ExceedsLimit.ShouldBeFalse();

        // And this is the property that makes the guardrail worth having: the render is just as
        // long, but a measurement based on slot count would have reported both the same.
        capped.Segments.Count.ShouldBeGreaterThan(capped.DistinctClipCount);
    }

    [Fact]
    public async Task MeasuresAgainstEverySourceCombined()
    {
        var harness = new PipelineHarness();
        var a = harness.AddSource("a.mkv", 180);
        var b = harness.AddSource("b.mkv", 180);

        var request = PipelineHarness.Request(a) with { Sources = [a, b] };
        var plan = await harness.PlanAsync(request);

        plan.SourceUsage.Available.ShouldBe(TimeSpan.FromSeconds(360));

        // Adding a second source of the same length halves the share consumed, which is the
        // other way out of the limit.
        var single = await harness.PlanAsync(PipelineHarness.Request(a));
        plan.SourceUsage.Fraction.ShouldBeLessThan(single.SourceUsage.Fraction);
    }
}

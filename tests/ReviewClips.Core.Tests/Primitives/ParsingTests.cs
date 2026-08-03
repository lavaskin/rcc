using ReviewClips.Core.Primitives;

namespace ReviewClips.Core.Tests.Primitives;

public class DurationSpecTests
{
    [Theory]
    [InlineData("90", 90)]
    [InlineData("90s", 90)]
    [InlineData("2m", 120)]
    [InlineData("1m30s", 90)]
    [InlineData("1.5m", 90)]
    [InlineData("1h", 3600)]
    [InlineData("1h2m3s", 3723)]
    [InlineData("1:30", 90)]
    [InlineData("00:01:30", 90)]
    [InlineData("01:00:00", 3600)]
    [InlineData("00:00:01.5", 1.5)]
    [InlineData("  45s  ", 45)]
    public void Parse_AcceptsSupportedForms(string input, double expectedSeconds) =>
        DurationSpec.Parse(input).TotalSeconds.ShouldBe(expectedSeconds, 0.001);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("5x")]
    [InlineData("1:2:3:4")]
    public void TryParse_RejectsInvalidInput(string input)
    {
        DurationSpec.TryParse(input, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }
}

public class OffsetTests
{
    [Fact]
    public void Percent_ResolvesRelativeToTotalRuntime() =>
        Offset.Parse("5%")
            .Resolve(TimeSpan.FromMinutes(100))
            .ShouldBe(TimeSpan.FromMinutes(5));

    [Fact]
    public void AbsoluteValue_IgnoresTotalRuntime() =>
        Offset.Parse("30s")
            .Resolve(TimeSpan.FromHours(2))
            .ShouldBe(TimeSpan.FromSeconds(30));

    [Fact]
    public void Percent_IsFlaggedAsRelative()
    {
        Offset.Parse("8%").IsRelative.ShouldBeTrue();
        Offset.Parse("8s").IsRelative.ShouldBeFalse();
    }

    [Theory]
    [InlineData("120%")]
    [InlineData("-5%")]
    [InlineData("abc%")]
    [InlineData("")]
    public void TryParse_RejectsInvalidInput(string input) =>
        Offset.TryParse(input, out _, out _).ShouldBeFalse();
}

public class RatioTests
{
    [Theory]
    [InlineData("16:9", 16, 9)]
    [InlineData("9:16", 9, 16)]
    [InlineData("16/9", 16, 9)]
    [InlineData("1:1", 1, 1)]
    public void TryParse_AcceptsColonAndSlashForms(string input, int n, int d)
    {
        Ratio.TryParse(input, out var ratio, out _).ShouldBeTrue();
        ratio.Numerator.ShouldBe(n);
        ratio.Denominator.ShouldBe(d);
    }

    [Theory]
    [InlineData("0:1")]
    [InlineData("16:0")]
    [InlineData("-16:9")]
    [InlineData("169")]
    [InlineData("")]
    public void TryParse_RejectsInvalidOrDegenerateRatios(string input) =>
        Ratio.TryParse(input, out _, out _).ShouldBeFalse();
}

using System.CommandLine;
using System.Text.RegularExpressions;
using ReviewClips.Cli.Cli;
using ReviewClips.Cli.Profiles;
using ReviewClips.Ffmpeg.Diagnostics;

namespace ReviewClips.Cli.Tests;

/// <summary>
/// The flag names <c>doctor</c> prints, checked against the flags that actually exist.
/// <para>
/// <c>doctor</c>'s optional-filter lines exist to name the option that stopped working. That makes
/// the strings load-bearing, and nothing else verifies them: they are plain text sitting in a
/// different assembly from the option definitions, so they drift silently. They had drifted —
/// <c>--tone-map</c> for an option called <c>--tonemap</c>, and <c>--preset shorts</c> for what is
/// <c>--profile shorts</c> — and both were being reproduced verbatim in the README.
/// </para>
/// </summary>
public class DoctorFeatureTextTests
{
    /// <summary>Every long option the generate command actually defines.</summary>
    private static HashSet<string> RealFlags()
    {
        var command = new Command("generate");
        new GenerateOptions().AddTo(command);

        return
        [
            .. command.Options
                .SelectMany(o => new[] { o.Name }.Concat(o.Aliases))
                .Where(n => n.StartsWith("--", StringComparison.Ordinal)),
        ];
    }

    public static TheoryData<string, string> Features()
    {
        var data = new TheoryData<string, string>();

        foreach (var feature in EnvironmentInspector.OptionalFeatures)
        {
            data.Add(feature.Filter, feature.Feature);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Features))]
    public void EveryFlagNamedInAFeatureLineExists(string filter, string feature)
    {
        var real = RealFlags();

        // Long options only: the text never refers to short forms.
        var mentioned = Regex.Matches(feature, @"--[a-z0-9][a-z0-9-]*")
            .Select(m => m.Value)
            .ToList();

        mentioned.ShouldNotBeEmpty($"'{filter}' names no flag, so its line cannot be acted on");

        foreach (var flag in mentioned)
        {
            real.ShouldContain(
                flag,
                customMessage: $"doctor says '{filter}' gates '{feature}', but {flag} is not an option");
        }
    }

    /// <summary>
    /// A flag can exist and still be quoted with a value it does not accept, which is how
    /// "--preset shorts" survived: --preset is real, but shorts is a profile, not a preset.
    /// </summary>
    [Fact]
    public void ProfileNamesAreQuotedAgainstTheProfileFlag()
    {
        var profiles = new ProfileLibrary().Names.ToList();

        foreach (var feature in EnvironmentInspector.OptionalFeatures)
        {
            foreach (var profile in profiles)
            {
                var wrong = $"--preset {profile}";

                feature.Feature.ShouldNotContain(
                    wrong,
                    customMessage: $"'{profile}' is a profile, so this should say --profile, not --preset");
            }
        }
    }

    /// <summary>The specific pair that was wrong, pinned so a revert is caught by name.</summary>
    [Fact]
    public void ToneMappingNamesTheTonemapOption()
    {
        var toneMapping = EnvironmentInspector.OptionalFeatures
            .Where(f => f.Feature.Contains("tone mapping", StringComparison.OrdinalIgnoreCase))
            .ToList();

        toneMapping.ShouldNotBeEmpty();

        foreach (var feature in toneMapping)
        {
            feature.Feature.ShouldContain("--tonemap");
            feature.Feature.ShouldNotContain("--tone-map");
        }
    }
}

using ReviewClips.Core.Options;

namespace ReviewClips.Cli.Profiles;

/// <summary>The built-in presets, plus any added or overridden in <c>appsettings.json</c>.</summary>
public sealed class ProfileLibrary
{
    private readonly Dictionary<string, RenderProfile> _profiles;

    public ProfileLibrary(IEnumerable<RenderProfile>? overrides = null)
    {
        _profiles = BuiltIn().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var profile in overrides ?? [])
        {
            if (!string.IsNullOrWhiteSpace(profile.Name))
            {
                _profiles[profile.Name] = profile;
            }
        }
    }

    public IReadOnlyCollection<RenderProfile> All =>
        _profiles.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

    public bool TryGet(string name, out RenderProfile profile) =>
        _profiles.TryGetValue(name, out profile!);

    public IEnumerable<string> Names => _profiles.Keys.OrderBy(n => n, StringComparer.Ordinal);

    private static IEnumerable<RenderProfile> BuiltIn()
    {
        yield return new RenderProfile
        {
            Name = "youtube",
            Description = "1080p 16:9 for standard videos. Darkened so it sits behind narration.",
            Width = 1920,
            Height = 1080,
            Fit = FitMode.Crop,
        };

        yield return new RenderProfile
        {
            Name = "shorts",
            Description = "1080x1920 vertical for Shorts, with the blurred-backdrop fill.",
            Width = 1080,
            Height = 1920,
            Fit = FitMode.BlurPad,

            // Most films are 2.35:1, which at 1.0 would fill only a quarter of a 9:16 frame.
            ForegroundScale = 1.5,
        };

        yield return new RenderProfile
        {
            Name = "square",
            Description = "1080x1080 for feed posts.",
            Width = 1080,
            Height = 1080,
            Fit = FitMode.Crop,
        };

        yield return new RenderProfile
        {
            Name = "subtle",
            Description = "Barely treated: light darkening, gentle cross-dissolves.",
            Darken = 0.20,
            Saturation = 0.92,
            Transition = TransitionKind.Fade,
            TransitionSeconds = 0.6,
        };

        yield return new RenderProfile
        {
            Name = "moody",
            Description = "Heavily suppressed: dark, desaturated, vignetted, grainy. Recedes furthest behind a voice-over.",
            Darken = 0.50,
            Saturation = 0.55,
            Contrast = 1.05,
            Blur = 1.5,
            Grain = 6,
            Vignette = true,
            Transition = TransitionKind.DipToBlack,
            TransitionSeconds = 0.5,
        };

        yield return new RenderProfile
        {
            Name = "kenburns",
            Description = "Slow drift on every clip, longer splices. Good over slower commentary.",
            Zoom = 1.10,
            SpliceSeconds = 7,
            SpliceJitterSeconds = 1.5,
            Transition = TransitionKind.Fade,
            TransitionSeconds = 0.8,
        };

        yield return new RenderProfile
        {
            Name = "raw",
            Description = "No treatment and hard cuts, which lets the render finish by stream copy. Fastest.",
            Darken = 0,
            Saturation = 1,
            Contrast = 1,
            Blur = 0,
            Grain = 0,
            Vignette = false,
            Transition = TransitionKind.None,

            // Fades would force a re-encode and lose the stream-copy fast path.
            FadeInSeconds = 0,
            FadeOutSeconds = 0,
        };
    }
}

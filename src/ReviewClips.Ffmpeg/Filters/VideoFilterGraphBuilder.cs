namespace ReviewClips.Ffmpeg.Filters;

/// <summary>
/// Composes the applicable <see cref="IVideoFilterStage"/> instances into a single
/// <c>-filter_complex</c> graph.
/// </summary>
public sealed class VideoFilterGraphBuilder
{
    private readonly IReadOnlyList<IVideoFilterStage> _stages;

    public VideoFilterGraphBuilder(IEnumerable<IVideoFilterStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        _stages = stages.OrderBy(s => s.Order).ToList();
    }

    public static VideoFilterGraphBuilder CreateDefault() =>
        new(
        [
            new ToneMapStage(),
            new SpeedStage(),
            new SquarePixelStage(),
            new FitStage(),
            new FrameRateStage(),
            new ZoomStage(),
            new MirrorStage(),
            new LutStage(),
            new LookStage(),
            new SharpenStage(),
            new BlurStage(),
            new PixelateStage(),
            new VignetteStage(),
            new FadeEdgesStage(),
            new GrainStage(),
            new AttributionStage(),
            new OutputFormatStage(),
        ]);

    public IReadOnlyList<IVideoFilterStage> Stages => _stages;

    /// <summary>Names of the stages that would run, for diagnostics and dry-run output.</summary>
    public IReadOnlyList<string> ActiveStageNames(FilterContext context) =>
        _stages.Where(s => s.AppliesTo(context)).Select(s => s.Name).ToList();

    /// <summary>
    /// Builds the graph.
    /// </summary>
    /// <param name="context">Source, target format and look.</param>
    /// <param name="inputLabel">Label of the incoming stream, typically <c>0:v</c>.</param>
    /// <param name="outputLabel">Label the caller will <c>-map</c>.</param>
    public string Build(FilterContext context, string inputLabel = "0:v", string outputLabel = "vout")
    {
        ArgumentNullException.ThrowIfNull(context);

        var applicable = _stages.Where(s => s.AppliesTo(context)).ToList();
        var writer = new FilterGraphWriter();

        if (applicable.Count == 0)
        {
            writer.AddChain(inputLabel, outputLabel, "null");
            return writer.Build();
        }

        var current = inputLabel;
        for (var i = 0; i < applicable.Count; i++)
        {
            var isLast = i == applicable.Count - 1;
            var next = isLast ? outputLabel : writer.NewLabel();
            applicable[i].Emit(writer, context, current, next);
            current = next;
        }

        return writer.Build();
    }
}

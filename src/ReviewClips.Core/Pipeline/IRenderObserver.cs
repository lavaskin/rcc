namespace ReviewClips.Core.Pipeline;

public enum RenderPhase
{
    Probing,
    Analyzing,
    Selecting,
    Extracting,
    Stitching,
    Finalizing,
}

/// <summary>
/// Progress and diagnostics sink. Keeps Core free of any console dependency so the
/// pipeline stays testable and the CLI owns all presentation.
/// </summary>
public interface IRenderObserver
{
    void OnPhaseStarted(RenderPhase phase, string detail);

    void OnProbed(string path, int completed, int total);

    void OnAnalysisStarted(string path, bool fromCache);

    void OnAnalysisProgress(string path, double fraction);

    void OnSegmentsSelected(int count, int requested);

    void OnSegmentCompleted(int completed, int total);

    void OnStitchProgress(double fraction);

    void OnWarning(string message);
}

public sealed class NullRenderObserver : IRenderObserver
{
    public static NullRenderObserver Instance { get; } = new();

    public void OnPhaseStarted(RenderPhase phase, string detail)
    {
    }

    public void OnProbed(string path, int completed, int total)
    {
    }

    public void OnAnalysisStarted(string path, bool fromCache)
    {
    }

    public void OnAnalysisProgress(string path, double fraction)
    {
    }

    public void OnSegmentsSelected(int count, int requested)
    {
    }

    public void OnSegmentCompleted(int completed, int total)
    {
    }

    public void OnStitchProgress(double fraction)
    {
    }

    public void OnWarning(string message)
    {
    }
}

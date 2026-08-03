namespace ReviewClips.Core.Pipeline;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its callback synchronously on the reporting
/// thread.
/// <para>
/// The framework's <see cref="Progress{T}"/> marshals callbacks through the synchronization
/// context, which in a console application means the thread pool. Reports then arrive out of
/// order and interleave, so a monotonic percentage renders as "50% 40% 30% 60%". Since progress
/// here is already produced sequentially by a single reader thread, invoking inline preserves
/// ordering and avoids the queueing entirely.
/// </para>
/// </summary>
public sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public InlineProgress(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public void Report(T value) => _handler(value);
}

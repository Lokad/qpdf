namespace Lokad.Qpdf.Runtime;

/// <summary>A fixed receipt for one independently escaping outcome carrier.</summary>
// A returned carrier keeps its own receipt alive, without retaining the job.
// Collection publishes two atomic values; only the job thread spends reserved
// retirement work or changes its memory counters.
internal sealed class ExecutionResultMemory(WeakReference<ResourceGovernor> owner, long bytes)
{
    private readonly WeakReference<ResourceGovernor> _owner = owner;
    private long _bytes = bytes;

    internal void Cancel()
    {
        Interlocked.Exchange(ref _bytes, 0L);
        GC.SuppressFinalize(this);
    }

    ~ExecutionResultMemory()
    {
        long bytes = Interlocked.Exchange(ref _bytes, 0L);
        if (bytes != 0L && _owner.TryGetTarget(out ResourceGovernor? owner))
        { owner.RetireExecutionResultMemory(bytes); }
    }
}

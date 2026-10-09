namespace Lokad.Qpdf.Runtime;

/// <summary>A prepaid weak receipt for one library-created version.</summary>
internal sealed class VersionMemory(WeakReference<ResourceGovernor> owner, long bytes)
{
    internal const long StorageBytes = 32L;
    private readonly WeakReference<ResourceGovernor> _owner = owner;
    private long _bytes = bytes;

    internal void Cancel()
    {
        Interlocked.Exchange(ref _bytes, 0L);
        GC.SuppressFinalize(this);
    }

    ~VersionMemory()
    {
        long bytes = Interlocked.Exchange(ref _bytes, 0L);
        if (bytes != 0L && _owner.TryGetTarget(out ResourceGovernor? owner))
        { owner.RetireVersionMemory(bytes); }
    }
}

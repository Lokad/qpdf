namespace Lokad.Qpdf.Runtime;

/// <summary>Owns reservations made by one parser attempt, independently of resolver loads.</summary>
internal sealed class ParserMemoryScope : IDisposable
{
    private const long OwnerBytes = 64L;
    private readonly ResourceGovernor _governor;
    private long _reservedBytes;
    private bool _disposed;

    internal long ReservedBytes => _reservedBytes;

    private ParserMemoryScope(ResourceGovernor governor) { _governor = governor; }

    internal static ParserMemoryScope Create(ResourceGovernor governor)
    {
        governor.CheckCancellation();
        governor.ReserveMemory(OwnerBytes);
        try { return new ParserMemoryScope(governor); }
        catch { governor.ReleaseMemory(OwnerBytes); throw; }
    }

    internal void Reserve(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long next = checked(_reservedBytes + bytes);
        _governor.ReserveMemory(bytes);
        _reservedBytes = next;
    }

    internal void Release(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes < 0L || bytes > _reservedBytes)
        {
            throw new InvalidOperationException("Parser release exceeds its attempt's ownership.");
        }
        _reservedBytes -= bytes;
        _governor.ReleaseMemory(bytes);
    }

    // Successful values retain their admission for the document/job lifetime.
    // Only this attempt's temporary wrapper is released on disposal.
    internal void Retain() { _reservedBytes = 0L; }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _governor.ReleaseMemory(checked(_reservedBytes + OwnerBytes));
        _reservedBytes = 0L;
    }
}

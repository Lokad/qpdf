namespace Lokad.Qpdf.Runtime;

/// <summary>Owns an admitted immutable string until disposal or transfer to diagnostics.</summary>
internal sealed class GovernedText : IDisposable
{
    private const long OwnerBytes = 64L;
    private readonly ResourceGovernor _governor;
    private string? _value;
    private long _reservedBytes;

    private GovernedText(ResourceGovernor governor, string value, long reservedBytes, long utf8Bytes)
    {
        _governor = governor;
        _value = value;
        _reservedBytes = reservedBytes;
        Utf8Bytes = utf8Bytes;
    }

    internal string Value => _value ?? throw new ObjectDisposedException(nameof(GovernedText));

    internal long Utf8Bytes { get; }

    internal static GovernedText Create(ResourceGovernor governor, TextChunk? head, int length, long utf8Bytes)
    {
        governor.CheckCancellation();
        long reserved = checked(OwnerBytes + 32L + 2L * length);
        governor.ReserveMemory(reserved);
        try
        {
            string value = string.Create(length, (governor, head), static (target, state) =>
            {
                int position = 0;
                for (TextChunk? chunk = state.head; chunk is not null; chunk = chunk.Next)
                {
                    for (int offset = 0; offset < chunk.Used; offset += Math.Min(256, chunk.Used - offset))
                    {
                        int count = Math.Min(256, chunk.Used - offset);
                        state.governor.ChargeWork(count);
                        state.governor.CheckCancellation();
                        chunk.Buffer.AsSpan(offset, count).CopyTo(target.Slice(position, count));
                        position += count;
                    }
                }
            });
            return new GovernedText(governor, value, reserved, utf8Bytes);
        }
        catch
        {
            governor.ReleaseMemory(reserved);
            throw;
        }
    }

    internal void CheckOwner(ResourceGovernor governor)
    {
        _ = Value;
        if (!ReferenceEquals(governor, _governor))
        {
            throw new InvalidOperationException("Display text belongs to another job.");
        }
    }

    // The diagnostic list retains the very same string for the remaining job
    // lifetime. Release only this temporary wrapper; keep the string admitted.
    internal void TransferToDiagnostic(ResourceGovernor governor)
    {
        CheckOwner(governor);
        _governor.ReleaseMemory(OwnerBytes);
        _reservedBytes = 0L;
        _value = null;
    }

    public void Dispose()
    {
        _value = null;
        _governor.ReleaseMemory(_reservedBytes);
        _reservedBytes = 0L;
    }
}

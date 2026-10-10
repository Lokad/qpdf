using System.Text;

namespace Lokad.Qpdf.Runtime;

/// <summary>Owns an admitted immutable string until disposal or transfer to retained storage.</summary>
internal sealed class GovernedText : IDisposable
{
    private const long OwnerBytes = 64L;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
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

    internal long StringReservation => checked(_reservedBytes - OwnerBytes);

    // Measured .NET 10 strings contain two pointer-sized header words, an
    // Int32 length and a UTF-16 terminator, aligned to the pointer size. Empty
    // String.Create results borrow String.Empty instead of allocating storage.
    internal static long StringBytes(int length)
    {
        if (length == 0) { return 0L; }
        long alignment = IntPtr.Size;
        return checked((2L * alignment + 6L + 2L * length + alignment - 1L) & ~(alignment - 1L));
    }

    internal static string CopySubstring(ResourceGovernor governor, ParserMemoryScope memory, string source, int start, int length)
    {
        governor.CheckCancellation();
        governor.ChargeWork(checked(length / 256L + 1L));
        if (length == 0) { return string.Empty; }
        long bytes = StringBytes(length);
        memory.Reserve(bytes);
        string value = source.Substring(start, length);
        memory.RetainAllocation(value, bytes);
        return value;
    }

    internal static GovernedText DecodeUtf8(ResourceGovernor governor, ReadOnlySpan<byte> bytes)
    {
        governor.CheckCancellation();
        // Each bounded work unit covers at most 256 input bytes in each pass.
        governor.ChargeWork(checked(2L * (bytes.Length / 256L + 1L)));
        int length = StrictUtf8.GetCharCount(bytes);
        governor.CheckCancellation();
        long reserved = checked(OwnerBytes + StringBytes(length));
        governor.ReserveMemory(reserved);
        try
        {
            string value = StrictUtf8.GetString(bytes);
            return new GovernedText(governor, value, reserved, bytes.Length);
        }
        catch { governor.ReleaseMemory(reserved); throw; }
    }

    internal static GovernedText Create(ResourceGovernor governor, TextChunk? head, int length, long utf8Bytes)
    {
        governor.CheckCancellation();
        long reserved = checked(OwnerBytes + StringBytes(length));
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
                        chunk.CopyTo(offset, target.Slice(position, count));
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

    internal static GovernedText Create(ResourceGovernor governor, DiagnosticStage stage, DiagnosticStage.Entry entry)
    {
        governor.CheckCancellation();
        long reserved = checked(OwnerBytes + StringBytes(entry.Length));
        governor.ReserveMemory(reserved);
        try
        {
            string value = string.Create(entry.Length, (stage, entry), static (target, state) => state.stage.CopyTo(state.entry, target));
            return new GovernedText(governor, value, reserved, entry.Utf8Bytes);
        }
        catch { governor.ReleaseMemory(reserved); throw; }
    }

    private readonly ref struct FormattingState(ResourceGovernor governor, ReadOnlySpan<DiagnosticPart> parts)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal readonly ReadOnlySpan<DiagnosticPart> Parts = parts;
    }

    internal static GovernedText Create(ResourceGovernor governor, ReadOnlySpan<DiagnosticPart> parts, DiagnosticTextInfo info)
    {
        governor.CheckCancellation();
        long reserved = checked(OwnerBytes + StringBytes(info.Length));
        governor.ReserveMemory(reserved);
        try
        {
            string value = string.Create(info.Length, new FormattingState(governor, parts),
                static (target, state) => DiagnosticMessage.Copy(state.Governor, state.Parts, target));
            return new GovernedText(governor, value, reserved, info.Utf8Bytes);
        }
        catch { governor.ReleaseMemory(reserved); throw; }
    }

    internal void CheckOwner(ResourceGovernor governor)
    {
        _ = Value;
        if (!ReferenceEquals(governor, _governor))
        {
            throw new InvalidOperationException("Display text belongs to another job.");
        }
    }

    // The receiver adopts the string's receipt before this handoff.
    // Release only the temporary wrapper; retained storage owns the string.
    internal void TransferToRetainedStorage(ResourceGovernor governor)
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

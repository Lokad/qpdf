using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns rebuilt value or stream-header metadata while borrowing immutable payloads.</summary>
internal sealed class WriterValue : IDisposable
{
    private ParserMemoryScope? _memory;
    private QpdfObject? _value;

    private WriterValue(ParserMemoryScope memory, QpdfObject value) { _memory = memory; _value = value; }

    internal static WriterValue Remap(QpdfObject value, WriterIndex<int> renumber, ResourceGovernor governor, CancellationToken token)
        => Create(value, null, renumber, governor, token);

    internal static WriterValue StreamHeader(PdfDictionary header, int lengthNumber, WriterIndex<int> renumber,
        ResourceGovernor governor, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lengthNumber);
        return Create(header, lengthNumber, renumber, governor, token);
    }

    private static WriterValue Create(QpdfObject value, int? lengthNumber, WriterIndex<int> renumber,
        ResourceGovernor governor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(renumber); ArgumentNullException.ThrowIfNull(governor);
        renumber.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L); governor.ChargeWork(1L);
            QpdfObject mapped = lengthNumber.HasValue
                ? ObjectRemap.RemapStreamHeader(value as PdfDictionary ?? throw new ArgumentException("Stream header must be a dictionary.", nameof(value)),
                    lengthNumber.Value, renumber, memory, governor, token)
                : ObjectRemap.RemapIds(value, renumber, memory, governor, token);
            return new WriterValue(memory, mapped);
        }
        catch { memory.Dispose(); throw; }
    }

    internal QpdfObject Value => _value ?? throw new ObjectDisposedException(nameof(WriterValue));

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory; _value = null; _memory = null; memory?.Dispose();
    }
}

using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns classic QDF emission rows and rebuilt graph metadata, borrowing stream bytes.</summary>
internal sealed class QdfEmissionPlan : IDisposable
{
    private ParserMemoryScope? _memory;
    private WriterOrder<QdfEmission>? _rows;
    private WriterIndex<int>? _renumber;

    private QdfEmissionPlan(ParserMemoryScope memory, WriterOrder<QdfEmission> rows, WriterIndex<int> renumber, int lastLengthNumber)
    { _memory = memory; _rows = rows; _renumber = renumber; LastLengthNumber = lastLengthNumber; }

    internal static QdfEmissionPlan Create(WriterOrder<NumberedObject> source, IReadOnlyDictionary<int, int>? originalIds,
        ResourceGovernor governor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(governor);
        source.CheckOwner(governor); token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        WriterOrder<QdfEmission>? rows = null; WriterIndex<int>? renumber = null;
        try
        {
            memory.Reserve(64L);
            renumber = WriterIndex<int>.Create(sizeof(int), governor, token);
            long next = 1L; int lastLengthNumber = 0;
            foreach (NumberedObject numbered in source)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                if (next > Array.MaxLength) { throw new QpdfException("QDF emission plan exceeds the supported capacity."); }
                renumber[numbered.Number] = (int)next++;
                if (numbered.Value is PdfStream)
                {
                    if (next > Array.MaxLength) { throw new QpdfException("QDF emission plan exceeds the supported capacity."); }
                    lastLengthNumber = (int)next++;
                }
            }
            rows = WriterOrder<QdfEmission>.Create((int)(next - 1L), 32L, governor, token);
            int number = 1;
            foreach (NumberedObject numbered in source)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                int? original = null;
                if (originalIds is not null)
                {
                    governor.NoteOperation();
                    if (originalIds.TryGetValue(numbered.Number, out int found)) { original = found; }
                }
                QpdfObject value;
                if (numbered.Value is PdfStream stream)
                {
                    PdfDictionary header = ObjectRemap.RemapStreamHeader(stream.Dictionary, number + 1,
                        renumber, memory, governor, token);
                    memory.Reserve(32L);
                    value = stream.WithDictionary(header);
                }
                else { value = ObjectRemap.RemapIds(numbered.Value, renumber, memory, governor, token); }
                rows.Add(new QdfEmission(number++, value, original, null));
                if (numbered.Value is PdfStream) { rows.Add(new QdfEmission(number++, null, null, numbered.Number)); }
            }
            return new QdfEmissionPlan(memory, rows, renumber, lastLengthNumber);
        }
        catch { rows?.Dispose(); renumber?.Dispose(); memory.Dispose(); throw; }
    }

    private WriterOrder<QdfEmission> Rows => _rows ?? throw new ObjectDisposedException(nameof(QdfEmissionPlan));
    internal WriterIndex<int> Renumber => _renumber ?? throw new ObjectDisposedException(nameof(QdfEmissionPlan));
    internal int LastLengthNumber { get; }
    internal int Count => Rows.Count;
    internal QdfEmission this[int index] => Rows[index];
    public WriterOrder<QdfEmission>.Enumerator GetEnumerator() => Rows.GetEnumerator();

    internal long StreamLength(QdfEmission lengthEmission)
    {
        if (lengthEmission.LengthFor.HasValue && Renumber.TryGetValue(lengthEmission.LengthFor.Value, out int target) &&
            target > 0 && target <= Count && Rows[target - 1].Value is PdfStream stream)
        { return stream.RawMemory.Length; }
        throw new QpdfException("QDF publication lost a length object stream.");
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory; WriterOrder<QdfEmission>? rows = _rows; WriterIndex<int>? renumber = _renumber;
        _memory = null; _rows = null; _renumber = null;
        rows?.Dispose(); renumber?.Dispose(); memory?.Dispose();
    }
}

internal readonly record struct QdfEmission(int Number, QpdfObject? Value, int? OriginalSource, int? LengthFor);

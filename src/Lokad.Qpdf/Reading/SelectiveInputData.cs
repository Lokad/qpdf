using System.Numerics;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Reading;

// A bounded strict prefetch for private selections. Consumers stay synchronous:
// missing ranges or uncertain parsing retry the complete reader with the job's
// requested recovery policy, on the same lease and before publication.
internal sealed class SelectiveInputData : IDisposable
{
    private const int MaximumMetadata = 65536;
    private const int MaximumPrefetch = 262144;
    private const int LargeExtent = 1048576;
    private readonly record struct Plan(int Start, int Length);
    private readonly record struct Range(int Start, byte[] Bytes);
    private readonly DiagnosticBag _diagnostics;
    private readonly ResourceGovernor _governor;
    private ParserMemoryScope? _memory;
    private XRefTable? _table;
    private Range[] _ranges;

    private SelectiveInputData(Range[] ranges, XRefTable table, ParserMemoryScope memory,
        DiagnosticBag diagnostics, ResourceGovernor governor)
    { _ranges = ranges; _table = table; _memory = memory; _diagnostics = diagnostics; _governor = governor; }

    internal static async ValueTask<SelectiveInputData?> TryCreateAsync(IQpdfInput input,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        if (input.Length < 8L * 1048576L || governor.RemainingMemoryBytes < MaximumMetadata * 4L
            || governor.RemainingWorkUnits < 65536L
            || limits.MaxHostOperations - governor.Snapshot().HostOperations < 32L)
        { return null; }
        if (diagnostics.ReadingPartialInput) { throw new InvalidOperationException("Input prefetch cannot be nested."); }
        diagnostics.ReadingPartialInput = true;
        XRefTable? table = null;
        ParserMemoryScope? memory = null;
        bool transferred = false;
        try
        {
            using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
            // Short/non-array host reads can overlap their transferred chunk
            // with the helper's assembly buffer. Admit that overlap up front.
            scratch.Reserve(576L);
            byte[] tail = await QpdfInputReader.ReadExactlyAsync(input, input.Length - 256L, 256, token).ConfigureAwait(false);
            long xref = XRefTable.ReadTailOffset(tail);
            long metadataLength = input.Length - xref;
            if (xref < 8L || metadataLength <= 0L || metadataLength > MaximumMetadata) { return null; }
            scratch.Reserve(checked(2L * (metadataLength + 32L)));
            byte[] metadata = await QpdfInputReader.ReadExactlyAsync(input, xref, (int)metadataLength, token).ConfigureAwait(false);
            table = XRefTable.ReadClassicWindow(metadata, xref, (int)input.Length, limits, governor, diagnostics);

            List<int> offsets = ParserContainers.CreateList<int>(scratch);
            ParserContainers.AddItem(scratch, offsets, 0, sizeof(int));
            foreach (XrefEntry entry in table.Entries.Values)
            {
                governor.ChargeWork(1L); governor.CheckCancellation();
                if (entry.Kind == XrefEntryKind.Compressed) { return null; }
                if (entry.Kind != XrefEntryKind.Uncompressed) { continue; }
                if (entry.First >= xref) { return null; }
                ParserContainers.AddItem(scratch, offsets, checked((int)entry.First), sizeof(int));
            }
            ParserContainers.AddItem(scratch, offsets, (int)xref, sizeof(int));
            governor.ChargeWork(checked((long)offsets.Count * (BitOperations.Log2((uint)offsets.Count) + 1L)));
            offsets.Sort(); governor.CheckCancellation();
            List<Plan> plans = ParserContainers.CreateList<Plan>(scratch);
            long total = 0L;
            bool omitted = false;
            int allowance = (int)Math.Min(MaximumPrefetch, governor.RemainingMemoryBytes / 4L);
            for (int index = 0; index + 1 < offsets.Count; index++)
            {
                governor.ChargeWork(1L); governor.CheckCancellation();
                int start = offsets[index], length = offsets[index + 1] - start;
                if (length == 0) { continue; }
                if (length > LargeExtent)
                {
                    omitted = true;
                    if (start != 0) { continue; }
                    length = 8;
                }
                total = checked(total + length);
                if (total > allowance) { return null; }
                if (plans.Count > 0 && plans[^1].Start + plans[^1].Length == start)
                { plans[^1] = plans[^1] with { Length = checked(plans[^1].Length + length) }; }
                else { ParserContainers.AddItem(scratch, plans, new Plan(start, length), 8L); }
            }
            if (!omitted || plans.Count == 0
                || limits.MaxHostOperations - governor.Snapshot().HostOperations < plans.Count + 16L)
            { return null; }
            memory = ParserMemoryScope.Create(governor);
            memory.Reserve(checked(64L + 32L + plans.Count * 16L + 2L * (total + plans.Count * 32L)));
            Range[] ranges = new Range[plans.Count];
            for (int index = 0; index < ranges.Length; index++)
            {
                Plan plan = plans[index];
                byte[] bytes = await QpdfInputReader.ReadExactlyAsync(input, plan.Start, plan.Length, token).ConfigureAwait(false);
                ranges[index] = new Range(plan.Start, bytes);
                memory.Release(checked(plan.Length + 32L));
            }
            SelectiveInputData result = new(ranges, table, memory, diagnostics, governor);
            transferred = true;
            return result;
        }
        catch (QpdfParseException) { throw new CompleteInputRequiredException(); }
        finally
        {
            if (!transferred)
            { diagnostics.ReadingPartialInput = false; table?.Dispose(); memory?.Dispose(); }
        }
    }

    internal XRefTable DetachTable()
    {
        XRefTable table = _table ?? throw new InvalidOperationException("Input table was already transferred.");
        _table = null;
        return table;
    }

    internal ReadOnlyMemory<byte> At(long offset)
    {
        int first = 0, last = _ranges.Length - 1;
        while (first <= last)
        {
            _governor.ChargeWork(1L); _governor.CheckCancellation();
            int middle = first + (last - first) / 2;
            Range range = _ranges[middle];
            if (offset < range.Start) { last = middle - 1; }
            else if (offset >= (long)range.Start + range.Bytes.Length) { first = middle + 1; }
            else { return range.Bytes.AsMemory(checked((int)(offset - range.Start))); }
        }
        throw new CompleteInputRequiredException();
    }

    internal void Accept() => _diagnostics.ReadingPartialInput = false;

    public void Dispose()
    {
        _diagnostics.ReadingPartialInput = false;
        _ranges = Array.Empty<Range>();
        _table?.Dispose(); _table = null;
        _memory?.Dispose(); _memory = null;
    }
}

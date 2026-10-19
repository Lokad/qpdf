using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Reading;

/// <summary>Checks hint claims against independently parsed input boundaries and graph membership.</summary>
internal sealed class LinearizationPhysical : IDisposable
{
    private readonly DocumentEngine _engine;
    private readonly LinearizationData _data;
    private readonly QpdfLimits _limits;
    private readonly ResourceGovernor _governor;
    private readonly CancellationToken _token;
    private readonly ParserMemoryScope _memory;
    private GovernedIntDictionary<PdfObjectExtent>? _extents;

    private LinearizationPhysical(DocumentEngine engine, LinearizationData data, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken token, ParserMemoryScope memory)
    {
        _engine = engine; _data = data; _limits = limits; _governor = governor;
        _token = token; _memory = memory;
        _extents = GovernedIntDictionary<PdfObjectExtent>.Create(memory, governor, 24L, token);
    }

    internal static void Validate(DocumentEngine engine, LinearizationData data, ClassifiedGraph graph,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(80L);
        using LinearizationPhysical check = new(engine, data, limits, governor, cancellationToken, memory);
        check.Check(graph);
    }

    public void Dispose() { _extents = null; }

    private void Step()
    {
        _token.ThrowIfCancellationRequested();
        _governor.CheckCancellation();
    }

    private PdfObjectExtent Extent(int number)
    {
        Step();
        GovernedIntDictionary<PdfObjectExtent> extents = _extents ?? throw new ObjectDisposedException(nameof(LinearizationPhysical));
        if (extents.TryGetValue(number, out PdfObjectExtent value)) { return value; }
        if (!_engine.XRefEntries.TryGetValue(number, out XrefEntry entry)
            || entry.Kind != XrefEntryKind.Uncompressed || entry.Second != 0)
        {
            throw new QpdfParseException("Linearization hint names no generation-zero physical object.", null, number);
        }
        value = _engine.ReadPhysicalExtent(number, _limits, _governor, _token);
        extents.TryAdd(_memory, number, value);
        return value;
    }

    private long Adjusted(long offset)
        => offset >= _data.HintOffset ? checked(offset + _data.HintLength) : offset;

    private void Check(ClassifiedGraph graph)
    {
        Step();
        if (graph.Pages.Count != _data.PageCount || graph.Pages.Count == 0
            || graph.Pages[0] != _data.FirstPageObject)
        {
            throw new QpdfParseException("Linearization dictionary /O does not name the first page.", null, null);
        }
        foreach (int page in graph.Pages) { _ = Extent(page); }
        CheckHintSpans();
        CheckXrefZero();
        long minimumEnd = 0L;
        long maximumEnd = 0L;
        foreach (int number in graph.Part6)
        {
            PdfObjectExtent extent = Extent(number);
            minimumEnd = Math.Max(minimumEnd, extent.End);
            maximumEnd = Math.Max(maximumEnd, extent.WhitespaceEnd);
        }
        if (_data.FirstPageEnd < minimumEnd || _data.FirstPageEnd > maximumEnd)
        {
            throw new QpdfParseException("Linearization dictionary /E disagrees with the first-page physical end.", null, null);
        }

        GovernedIntDictionary<int> memberGroups;
        using (ParserMemoryScope groupsMemory = ParserMemoryScope.Create(_governor))
        {
            GovernedIntSet firstPage = Set(graph.Part6, groupsMemory);
            GovernedIntSet otherShared = Set(graph.Part8, groupsMemory);
            memberGroups = CheckSharedGroups(firstPage, otherShared, graph.Pages[0]);
        }
        CheckPages(graph, memberGroups);
        CheckOutlines(graph);
        // The pinned checker deliberately ignores integer /P (Acrobat does too).
        // Parsing/reporting retain it; it must not change physical page identity.
    }

    private GovernedIntSet Set(IReadOnlyCollection<int> numbers, ParserMemoryScope memory)
    {
        GovernedIntSet result = GovernedIntSet.Create(memory, _governor, _token);
        foreach (int number in numbers) { Step(); result.Add(memory, number); }
        return result;
    }

    private void CheckHintSpans()
    {
        PdfObjectExtent primary = Extent(_data.HintObject);
        CheckSpan(primary, _data.HintOffset, _data.HintLength);
        if (_engine.Load(_data.LindictObject) is PdfDictionary dictionary
            && PdfDictionaryLookup.TryGet(dictionary, "H"u8, _governor, _token, out QpdfObject raw)
            && raw is PdfArray { Count: 4 } spans && spans[2] is PdfInteger offset && spans[3] is PdfInteger length)
        {
            int number = ObjectAt(offset.Value);
            CheckSpan(Extent(number), offset.Value, length.Value);
            if (offset.Value < primary.WhitespaceEnd && checked(offset.Value + length.Value) > primary.Start)
            {
                throw new QpdfParseException("Primary and overflow hint spans overlap.", null, null);
            }
        }
    }

    private static void CheckSpan(PdfObjectExtent extent, long offset, long length)
    {
        if (extent.Start != offset || length < extent.End - offset || length > extent.WhitespaceEnd - offset)
        {
            throw new QpdfParseException("Declared hint span disagrees with its physical object boundary.", offset, null);
        }
    }

    private int ObjectAt(long offset)
    {
        foreach (KeyValuePair<int, XrefEntry> entry in _engine.XRefEntries)
        {
            Step();
            if (entry.Value.Kind == XrefEntryKind.Uncompressed && entry.Value.First == offset) { return entry.Key; }
        }
        throw new QpdfParseException("No physical object starts at the declared hint offset.", offset, null);
    }

    private void CheckXrefZero()
    {
        if (!PdfDictionaryLookup.TryGet(_engine.Trailer, "Prev"u8, _governor, _token, out QpdfObject raw)
            || raw is not PdfInteger previous || previous.Value < 0L || previous.Value >= _engine.Data.Length)
        {
            throw new QpdfParseException("Linearization needs the physical main xref section.", null, null);
        }
        _memory.Reserve(32L);
        try
        {
            PdfTokenizer tokenizer = new(_engine.Data, _token);
            tokenizer.SetPosition(previous.Value);
            if (!tokenizer.TryReadToken(out PdfToken first, false, _governor))
            {
                throw new QpdfParseException("Missing main xref section.", previous.Value, null);
            }
            long expected;
            if (first.Kind == PdfTokenKind.XRef)
            {
                if (!tokenizer.TryReadToken(out PdfToken start, false, _governor) || start.Kind != PdfTokenKind.Integer
                    || !start.Data.Span.SequenceEqual("0"u8)
                    || !tokenizer.TryReadToken(out PdfToken count, false, _governor) || count.Kind != PdfTokenKind.Integer
                    || !tokenizer.TryReadToken(out PdfToken item, false, _governor) || item.Kind != PdfTokenKind.Integer)
                {
                    throw new QpdfParseException("Main xref does not start with object zero.", previous.Value, null);
                }
                expected = item.Offset;
            }
            else
            {
                int number = ObjectAt(previous.Value);
                if (_engine.Load(number) is not PdfStream stream
                    || !PdfDictionaryLookup.TryGet(stream.Dictionary, "Type"u8, _governor, _token, out QpdfObject type)
                    || type is not PdfName name || !name.RawSpan.SequenceEqual("XRef"u8))
                {
                    throw new QpdfParseException("Main xref is not an xref table or stream.", previous.Value, null);
                }
                expected = previous.Value;
            }
            long actual = _data.XrefZeroOffset;
            while (actual < _engine.Data.Length && _engine.Data.Span[(int)actual] is 10 or 13 or 32) { Step(); actual++; }
            if (actual != expected)
            {
                throw new QpdfParseException("Linearization dictionary /T disagrees with the main xref boundary.", actual, null);
            }
        }
        finally { _memory.Release(32L); }
    }

    private GovernedIntDictionary<int> CheckSharedGroups(GovernedIntSet firstPage, GovernedIntSet otherShared, int firstObject)
    {
        SharedObjectTable table = _data.SharedTable;
        GovernedIntDictionary<int> groups = GovernedIntDictionary<int>.Create(_memory, _governor, sizeof(int), _token);
        int current = firstObject;
        int firstMembers = 0;
        int otherMembers = 0;
        if (table.TotalCount > table.FirstPageCount)
        {
            if (!otherShared.Contains(table.FirstSharedObject) || Extent(table.FirstSharedObject).Start != Adjusted(table.FirstSharedOffset))
            {
                throw new QpdfParseException("First shared object identity/offset disagrees with physical shared objects.", null, null);
            }
        }
        for (int group = 0; group < table.Entries.Count; group++)
        {
            Step();
            if (group == table.FirstPageCount) { current = table.FirstSharedObject; }
            SharedObjectEntry entry = table.Entries[group];
            GovernedIntSet expected = group < table.FirstPageCount ? firstPage : otherShared;
            if (entry.MemberCount <= 0L || entry.MemberCount > expected.Count)
            {
                throw new QpdfParseException("Shared group has an impossible physical member count.", null, null);
            }
            PdfObjectExtent extent = Range(current, (int)entry.MemberCount);
            if (extent.WhitespaceEnd - extent.Start != entry.GroupLength)
            {
                throw new QpdfParseException("Shared group length disagrees with physical object boundaries.", extent.Start, current);
            }
            for (int member = 0; member < entry.MemberCount; member++)
            {
                Step();
                int number = checked(current + member);
                if (!expected.Contains(number) || !groups.TryAdd(_memory, number, group))
                {
                    throw new QpdfParseException("Shared hint group assigns the wrong physical object.", null, number);
                }
            }
            if (group < table.FirstPageCount) { firstMembers = checked(firstMembers + (int)entry.MemberCount); }
            else { otherMembers = checked(otherMembers + (int)entry.MemberCount); }
            current = checked(current + (int)entry.MemberCount);
        }
        if (firstMembers != firstPage.Count || otherMembers != otherShared.Count)
        {
            throw new QpdfParseException("Shared hint groups do not cover the computed physical assignments.", null, null);
        }
        return groups;
    }

    private PdfObjectExtent Range(int first, int count)
    {
        if (count <= 0 || count > _engine.XRefEntries.Count || count - 1L > int.MaxValue - (long)first)
        {
            throw new QpdfParseException("Hint object run exceeds supported physical objects.", null, first);
        }
        PdfObjectExtent start = Extent(first);
        PdfObjectExtent last = start;
        for (int index = 1; index < count; index++)
        {
            PdfObjectExtent next = Extent(first + index);
            if (next.Start != last.WhitespaceEnd)
            {
                throw new QpdfParseException("Hint object run is not physically consecutive.", next.Start, first + index);
            }
            last = next;
        }
        return new(start.Start, last.End, last.WhitespaceEnd);
    }

    private void CheckPages(ClassifiedGraph graph, GovernedIntDictionary<int> groups)
    {
        if (_data.PageTable.Entries.Count != graph.Pages.Count
            || Adjusted(_data.PageTable.FirstPageOffset) != Extent(graph.Pages[0]).Start)
        {
            throw new QpdfParseException("First page hint offset disagrees with its physical object.", null, null);
        }
        for (int page = 0; page < graph.Pages.Count; page++)
        {
            Step();
            using ParserMemoryScope pageMemory = ParserMemoryScope.Create(_governor);
            PageOffsetEntry entry = _data.PageTable.Entries[page];
            IReadOnlyList<int> expected = page == 0 ? graph.Part6 : graph.Part7[page - 1];
            if (entry.ObjectCount != expected.Count)
            {
                throw new QpdfParseException("Page hint object count disagrees with physical graph assignments.", null, graph.Pages[page]);
            }
            PdfObjectExtent run = Range(graph.Pages[page], expected.Count);
            if (run.WhitespaceEnd - run.Start != entry.PageLength)
            {
                throw new QpdfParseException("Page hint length disagrees with physical object boundaries.", run.Start, graph.Pages[page]);
            }
            GovernedIntSet actualMembers = Set(expected, pageMemory);
            for (int index = 0; index < expected.Count; index++)
            {
                Step();
                if (!actualMembers.Contains(checked(graph.Pages[page] + index)))
                {
                    throw new QpdfParseException("Page hint run assigns the wrong physical object.", null, graph.Pages[page]);
                }
            }
            GovernedIntSet hinted = Set(entry.SharedIdentifiers, pageMemory);
            GovernedIntSet needed = GovernedIntSet.Create(pageMemory, _governor, _token);
            if (page == 0 && hinted.Count != 0)
            {
                throw new QpdfParseException("First page hint must not have shared identifiers.", null, graph.Pages[page]);
            }
            if (page > 0)
            {
                foreach (int number in graph.PageUsers[page])
                {
                    Step();
                    if (groups.TryGetValue(number, out int group)) { needed.Add(pageMemory, group); }
                }
            }
            if (hinted.Count != needed.Count)
            {
                throw new QpdfParseException("Page shared identifiers disagree with physical graph users.", null, graph.Pages[page]);
            }
            foreach (int group in needed)
            {
                Step();
                if (!hinted.Contains(group))
                {
                    throw new QpdfParseException("Page shared identifier names the wrong physical group.", null, graph.Pages[page]);
                }
            }
        }
    }

    private void CheckOutlines(ClassifiedGraph graph)
    {
        if (_data.Outlines is not OutlineTable table) { return; }
        if (table.ObjectCount != graph.Outlines.Count)
        {
            throw new QpdfParseException("Outline hint count disagrees with physical graph assignments.", null, null);
        }
        if (table.ObjectCount == 0) { return; }
        if (table.FirstObject != graph.Outlines[0])
        {
            throw new QpdfParseException("Incorrect offset in outlines table. Outline hint names the wrong physical first object.", null, null);
        }
        long end = 0L;
        foreach (int number in graph.Outlines) { end = Math.Max(end, Extent(number).WhitespaceEnd); }
        long start = Extent(table.FirstObject).Start;
        if (Adjusted(table.FirstOffset) != start || table.GroupLength != end - start)
        {
            throw new QpdfParseException("Incorrect offset in outlines table. Outline hint offset/length disagrees with physical boundaries.", start, table.FirstObject);
        }
    }
}

using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Reading;

/// <summary>Writes the linearization layout directly into admitted diagnostic storage.</summary>
internal static class LinearizationReport
{
    internal static ClassifiedGraph ClassifyDocument(DocumentEngine engine, int lindictObject, int hintObject,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken cancellationToken)
        => LinearizationGraph.Classify(engine, lindictObject, hintObject, memory, governor, cancellationToken);

    internal static void Write(string inputName, LinearizationData data, ClassifiedGraph parts,
        ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputName);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(governor);
        ArgumentNullException.ThrowIfNull(diagnostics);
        cancellationToken.ThrowIfCancellationRequested();
        Line(diagnostics, [inputName, ": linearization data:"]);
        Line(diagnostics, ["file_size: ", DiagnosticPart.Invariant(data.FileSize)]);
        Line(diagnostics, ["first_page_object: ", DiagnosticPart.Invariant(data.FirstPageObject)]);
        Line(diagnostics, ["first_page_end: ", DiagnosticPart.Invariant(data.FirstPageEnd)]);
        Line(diagnostics, ["npages: ", DiagnosticPart.Invariant(data.PageCount)]);
        Line(diagnostics, ["xref_zero_offset: ", DiagnosticPart.Invariant(data.XrefZeroOffset)]);
        Line(diagnostics, ["first_page: ", DiagnosticPart.Invariant(data.FirstPage)]);
        Line(diagnostics, ["H_offset: ", DiagnosticPart.Invariant(data.HintOffset)]);
        Line(diagnostics, ["H_length: ", DiagnosticPart.Invariant(data.HintLength)]);
        Line(diagnostics, ["Page Offsets Hint Table"]);
        PageOffsetTable table = data.PageTable;
        Line(diagnostics, ["min_nobjects: ", DiagnosticPart.Invariant(table.MinObjects)]);
        Line(diagnostics, ["first_page_offset: ", DiagnosticPart.Invariant(Adjusted(data, table.FirstPageOffset))]);
        Line(diagnostics, ["nbits_delta_nobjects: ", DiagnosticPart.Invariant(table.DeltaObjectsBits)]);
        Line(diagnostics, ["min_page_length: ", DiagnosticPart.Invariant(table.MinPageLength)]);
        Line(diagnostics, ["nbits_delta_page_length: ", DiagnosticPart.Invariant(table.DeltaLengthBits)]);
        Line(diagnostics, ["min_content_offset: ", DiagnosticPart.Invariant(table.MinContentOffset)]);
        Line(diagnostics, ["nbits_delta_content_offset: ", DiagnosticPart.Invariant(table.DeltaContentOffsetBits)]);
        Line(diagnostics, ["min_content_length: ", DiagnosticPart.Invariant(table.MinContentLength)]);
        Line(diagnostics, ["nbits_delta_content_length: ", DiagnosticPart.Invariant(table.DeltaContentLengthBits)]);
        Line(diagnostics, ["nbits_nshared_objects: ", DiagnosticPart.Invariant(table.SharedCountBits)]);
        Line(diagnostics, ["nbits_shared_identifier: ", DiagnosticPart.Invariant(table.IdentifierBits)]);
        Line(diagnostics, ["nbits_shared_numerator: ", DiagnosticPart.Invariant(table.NumeratorBits)]);
        Line(diagnostics, ["shared_denominator: ", DiagnosticPart.Invariant(table.SharedDenominator)]);
        for (int page = 0; page < table.Entries.Count; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageOffsetEntry entry = table.Entries[page];
            Line(diagnostics, ["Page ", DiagnosticPart.Invariant(page), ":"]);
            Line(diagnostics, ["  nobjects: ", DiagnosticPart.Invariant(entry.ObjectCount)]);
            Line(diagnostics, ["  length: ", DiagnosticPart.Invariant(entry.PageLength)]);
            Line(diagnostics, ["  content_offset: ", DiagnosticPart.Invariant(entry.ContentOffset)]);
            Line(diagnostics, ["  content_length: ", DiagnosticPart.Invariant(entry.ContentLength)]);
            Line(diagnostics, ["  nshared_objects: ", DiagnosticPart.Invariant(entry.SharedIdentifiers.Count)]);
            for (int index = 0; index < entry.SharedIdentifiers.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Line(diagnostics, ["    identifier ", DiagnosticPart.Invariant(index), ": ", DiagnosticPart.Invariant(entry.SharedIdentifiers[index])]);
                Line(diagnostics, ["    numerator ", DiagnosticPart.Invariant(index), ": ", DiagnosticPart.Invariant(entry.SharedNumerators[index])]);
            }
        }
        SharedObjectTable shared = data.SharedTable;
        Line(diagnostics, ["Shared Objects Hint Table"]);
        Line(diagnostics, ["first_shared_obj: ", DiagnosticPart.Invariant(shared.FirstSharedObject)]);
        Line(diagnostics, ["first_shared_offset: ", DiagnosticPart.Invariant(Adjusted(data, shared.FirstSharedOffset))]);
        Line(diagnostics, ["nshared_first_page: ", DiagnosticPart.Invariant(shared.FirstPageCount)]);
        Line(diagnostics, ["nshared_total: ", DiagnosticPart.Invariant(shared.TotalCount)]);
        Line(diagnostics, ["nbits_nobjects: ", DiagnosticPart.Invariant(shared.ObjectCountBits)]);
        Line(diagnostics, ["min_group_length: ", DiagnosticPart.Invariant(shared.MinGroupLength)]);
        Line(diagnostics, ["nbits_delta_group_length: ", DiagnosticPart.Invariant(shared.DeltaGroupBits)]);
        for (int group = 0; group < shared.Entries.Count; group++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SharedObjectEntry entry = shared.Entries[group];
            Line(diagnostics, ["Shared Object ", DiagnosticPart.Invariant(group), ":"]);
            Line(diagnostics, ["  group length: ", DiagnosticPart.Invariant(entry.GroupLength)]);
            if (entry.SignaturePresent) { Line(diagnostics, ["  signature present"]); }
            if (entry.MemberCount != 1L) { Line(diagnostics, ["  nobjects: ", DiagnosticPart.Invariant(entry.MemberCount)]); }
        }
        if (data.Outlines is { ObjectCount: > 0 } outlines)
        {
            Line(diagnostics, ["Outlines Hint Table"]);
            Line(diagnostics, ["first_object: ", DiagnosticPart.Invariant(outlines.FirstObject)]);
            Line(diagnostics, ["first_object_offset: ", DiagnosticPart.Invariant(Adjusted(data, outlines.FirstOffset))]);
            Line(diagnostics, ["nobjects: ", DiagnosticPart.Invariant(outlines.ObjectCount)]);
            Line(diagnostics, ["group_length: ", DiagnosticPart.Invariant(outlines.GroupLength)]);
        }
        Line(diagnostics, ["Computed Linearization Part Assignments"]);
        DumpPart(diagnostics, governor, "4 (root, document open)", new PartCursor(parts.Part4), cancellationToken);
        DumpPart(diagnostics, governor, "6 (first page)", new PartCursor(parts.Part6), cancellationToken);
        DumpPart(diagnostics, governor, "7 (other pages, private)", new PartCursor(parts.Part7), cancellationToken);
        DumpPart(diagnostics, governor, "8 (other pages, shared)", new PartCursor(parts.Part8), cancellationToken);
        DumpPart(diagnostics, governor, "9 (everything else)", new PartCursor(parts.Part9), cancellationToken);
    }

    private static void Line(DiagnosticBag diagnostics, ReadOnlySpan<DiagnosticPart> parts)
        => diagnostics.AddParts(QpdfDiagnosticCode.LinearizationReport, QpdfDiagnosticSeverity.Info, parts);

    private static long Adjusted(LinearizationData data, long offset)
        => offset >= data.HintOffset ? checked(offset + data.HintLength) : offset;

    // Flatten part-7 runs without an intermediate list, preserving adjacency
    // across run boundaries and charging empty-run transitions as well.
    private ref struct PartCursor
    {
        private readonly IReadOnlyList<IReadOnlyList<int>>? _runs;
        private IReadOnlyList<int> _current;
        private int _run;
        private int _position;

        internal PartCursor(IReadOnlyList<int> items) { _runs = null; _current = items; _run = 0; _position = 0; }
        internal PartCursor(IReadOnlyList<IReadOnlyList<int>> runs) { _runs = runs; _current = Array.Empty<int>(); _run = 0; _position = 0; }

        internal bool MoveNext(ResourceGovernor governor, CancellationToken cancellationToken, out int value)
        {
            while (_position == _current.Count)
            {
                if (_runs is null || _run == _runs.Count) { value = 0; return false; }
                cancellationToken.ThrowIfCancellationRequested();
                governor.NoteOperation();
                _current = _runs[_run++];
                _position = 0;
            }
            cancellationToken.ThrowIfCancellationRequested();
            governor.NoteOperation();
            value = _current[_position++];
            return true;
        }
    }

    private static void DumpPart(DiagnosticBag diagnostics, ResourceGovernor governor, string label,
        PartCursor cursor, CancellationToken cancellationToken)
    {
        if (!cursor.MoveNext(governor, cancellationToken, out int first))
        {
            Line(diagnostics, ["part ", label, ": none"]);
            return;
        }
        Line(diagnostics, ["part ", label, ":"]);
        int start = first;
        int last = first;
        int ranges = 0;
        DisplayBuffer? line = null;
        try
        {
            while (cursor.MoveNext(governor, cancellationToken, out int next))
            {
                if ((long)next == last + 1L) { last = next; continue; }
                AddRange(diagnostics, governor, ref line, ref ranges, start, last);
                start = last = next;
            }
            AddRange(diagnostics, governor, ref line, ref ranges, start, last);
            if (line is not null) { Emit(diagnostics, line); }
        }
        finally { line?.Dispose(); }
    }

    private static void AddRange(DiagnosticBag diagnostics, ResourceGovernor governor,
        ref DisplayBuffer? line, ref int ranges, int start, int last)
    {
        if (line is null)
        {
            line = DisplayBuffer.Create(governor, true);
            line.AppendParts([" "]);
        }
        if (ranges > 0) { line.AppendParts([", "]); }
        if (start == last) { line.AppendParts([DiagnosticPart.Invariant(start)]); }
        else { line.AppendParts([DiagnosticPart.Invariant(start), "-", DiagnosticPart.Invariant(last)]); }
        if (++ranges == 10)
        {
            Emit(diagnostics, line);
            line.Dispose();
            line = null;
            ranges = 0;
        }
    }

    private static void Emit(DiagnosticBag diagnostics, DisplayBuffer line)
    {
        using GovernedText text = line.Finish();
        diagnostics.AddOwned(QpdfDiagnosticCode.LinearizationReport, QpdfDiagnosticSeverity.Info, text);
    }
}

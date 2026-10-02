using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Reading;

/// <summary>
/// Human-readable linearization dump mirroring the upstream show layout:
/// parameter data, page-offset and shared-object hint tables, the outline
/// table when present, and recomputed part assignments. Reference blank
/// separators are omitted; one diagnostic carries one line.
/// </summary>
internal static class LinearizationReport
{
    internal static ClassifiedGraph ClassifyDocument(
        DocumentEngine engine,
        System.Collections.Generic.IReadOnlySet<int> excluded,
        ResourceGovernor governor,
        CancellationToken cancellationToken)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        if (excluded is null)
        {
            throw new ArgumentNullException(nameof(excluded));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        Dictionary<int, QpdfObject> lookup = new(engine.XRefEntries.Count);
        foreach (System.Collections.Generic.KeyValuePair<int, XrefEntry> entry in engine.XRefEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            governor.ChargeWork(1L);
            if (entry.Key <= 0 || entry.Value.Kind == XrefEntryKind.Free || excluded.Contains(entry.Key))
            {
                continue;
            }

            QpdfObject value;
            try
            {
                value = engine.Load(entry.Key);
            }
            catch (QpdfParseException)
            {
                continue;
            }

            if (value is null || value is PdfNull)
            {
                continue;
            }

            lookup[entry.Key] = value;
        }

        if (!engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject? rootRaw)
            || rootRaw is not PdfReference rootReference)
        {
            throw new QpdfParseException("Linearized display needs an indirect catalog reference.", null, null);
        }

        if (!lookup.TryGetValue(rootReference.ObjectNumber, out QpdfObject? catalog) || catalog is not PdfDictionary)
        {
            throw new QpdfParseException("Linearized display needs a reachable catalog dictionary.", null, rootReference.ObjectNumber);
        }

        return LinearizedParts.ClassifyGraph(lookup, rootReference.ObjectNumber, governor, cancellationToken);
    }

    internal static List<string> Build(string inputName, LinearizationData data, ClassifiedGraph parts)
    {
        if (inputName is null)
        {
            throw new ArgumentNullException(nameof(inputName));
        }

        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (parts is null)
        {
            throw new ArgumentNullException(nameof(parts));
        }

        List<string> lines = new()
        {
            $"{inputName}: linearization data:",
            $"file_size: {data.FileSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"first_page_object: {data.FirstPageObject.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"first_page_end: {data.FirstPageEnd.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"npages: {data.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"xref_zero_offset: {data.XrefZeroOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"first_page: {data.FirstPage.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"H_offset: {data.HintOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"H_length: {data.HintLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            "Page Offsets Hint Table",
        };
        PageOffsetTable table = data.PageTable;
        lines.Add($"min_nobjects: {table.MinObjects.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"first_page_offset: {Adjusted(data, table.FirstPageOffset).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"nbits_delta_nobjects: {table.DeltaObjectsBits.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"min_page_length: {table.MinPageLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"nbits_delta_page_length: {table.DeltaLengthBits.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"min_content_offset: {table.MinContentOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"nbits_delta_content_offset: {table.DeltaContentOffsetBits.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"min_content_length: {table.MinContentLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"nbits_delta_content_length: {table.DeltaContentLengthBits.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"nbits_nshared_objects: {table.SharedCountBits.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"nbits_shared_identifier: {table.IdentifierBits.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"nbits_shared_numerator: {table.NumeratorBits.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"shared_denominator: {table.SharedDenominator.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        for (int page = 0; page < table.Entries.Count; page++)
        {
            PageOffsetEntry entry = table.Entries[page];
            lines.Add($"Page {page.ToString(System.Globalization.CultureInfo.InvariantCulture)}:");
            lines.Add($"  nobjects: {entry.ObjectCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            lines.Add($"  length: {entry.PageLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            lines.Add($"  content_offset: {entry.ContentOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            lines.Add($"  content_length: {entry.ContentLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            lines.Add($"  nshared_objects: {entry.SharedIdentifiers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            for (int k = 0; k < entry.SharedIdentifiers.Count; k++)
            {
                lines.Add($"    identifier {k.ToString(System.Globalization.CultureInfo.InvariantCulture)}: {entry.SharedIdentifiers[k].ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                lines.Add($"    numerator {k.ToString(System.Globalization.CultureInfo.InvariantCulture)}: {entry.SharedNumerators[k].ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
        }

        SharedObjectTable shared = data.SharedTable;
        lines.Add("Shared Objects Hint Table");
        lines.Add($"first_shared_obj: {shared.FirstSharedObject.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"first_shared_offset: {Adjusted(data, shared.FirstSharedOffset).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"nshared_first_page: {shared.FirstPageCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"nshared_total: {shared.TotalCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"nbits_nobjects: {shared.ObjectCountBits.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"min_group_length: {shared.MinGroupLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        lines.Add($"nbits_delta_group_length: {shared.DeltaGroupBits.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        for (int group = 0; group < shared.Entries.Count; group++)
        {
            SharedObjectEntry entry = shared.Entries[group];
            lines.Add($"Shared Object {group.ToString(System.Globalization.CultureInfo.InvariantCulture)}:");
            lines.Add($"  group length: {entry.GroupLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            if (entry.SignaturePresent)
            {
                lines.Add("  signature present");
            }

            if (entry.MemberCount != 1L)
            {
                lines.Add($"  nobjects: {entry.MemberCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
        }

        if (data.Outlines is not null && data.Outlines.ObjectCount > 0)
        {
            lines.Add("Outlines Hint Table");
            lines.Add($"first_object: {data.Outlines.FirstObject.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            lines.Add($"first_object_offset: {Adjusted(data, data.Outlines.FirstOffset).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            lines.Add($"nobjects: {data.Outlines.ObjectCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            lines.Add($"group_length: {data.Outlines.GroupLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        lines.Add("Computed Linearization Part Assignments");
        DumpPart(lines, "4 (root, document open)", parts.Part4);
        DumpPart(lines, "6 (first page)", parts.Part6);
        List<int> part7 = new();
        foreach (IReadOnlyList<int> run in parts.Part7)
        {
            part7.AddRange(run);
        }

        DumpPart(lines, "7 (other pages, private)", part7);
        DumpPart(lines, "8 (other pages, shared)", parts.Part8);
        DumpPart(lines, "9 (everything else)", parts.Part9);
        return lines;
    }

    private static long Adjusted(LinearizationData data, long offset)
    {
        if (offset >= data.HintOffset)
        {
            return checked(offset + data.HintLength);
        }

        return offset;
    }

    private static void DumpPart(List<string> lines, string label, IReadOnlyList<int> part)
    {
        if (part.Count == 0)
        {
            lines.Add($"part {label}: none");
            return;
        }

        lines.Add($"part {label}:");
        int start = part[0];
        int last = part[0];
        List<string> ranges = new();
        for (int i = 1; i < part.Count; i++)
        {
            if (part[i] == checked(last + 1))
            {
                last = part[i];
                continue;
            }

            ranges.Add(start == last
                ? start.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{start.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{last.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            start = part[i];
            last = part[i];
        }

        ranges.Add(start == last
            ? start.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"{start.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{last.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        for (int i = 0; i < ranges.Count; i += 10)
        {
            System.Text.StringBuilder line = new(" ");
            for (int k = i; k < ranges.Count && k < i + 10; k++)
            {
                if (k > i)
                {
                    line.Append(", ");
                }

                line.Append(ranges[k]);
            }

            lines.Add(line.ToString());
        }
    }
}
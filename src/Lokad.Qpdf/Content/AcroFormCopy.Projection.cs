using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

internal static partial class AcroFormCopy
{
    // Plan edits against the immutable registration tree. A private edge into
    // the original tree does not make its target an active calculation field.
    internal static Dictionary<int, PdfArray>? ProjectFieldKids(PdfDictionary form, IIndirectResolver resolver,
        HashSet<int> survivors, QpdfLimits limits, ParserMemoryScope memory, ResourceGovernor governor,
        CancellationToken token, out PdfArray? calculationOrder)
    {
        calculationOrder = null;
        if (!PdfDictionaryLookup.TryGet(form, "Fields"u8, governor, token, out QpdfObject raw)
            || CatalogCopy.ResolveValue(raw, resolver, governor, token) is not PdfArray fields) { return null; }

        using ParserMemoryScope walk = ParserMemoryScope.Create(governor);
        FieldMembership membership = FieldMembership.Create(resolver, survivors, limits, governor, walk, token);
        Dictionary<int, PdfArray>? edits = null;
        HashSet<(int Number, int Generation)> visited = ParserContainers.CreateSet<(int, int)>(walk);
        List<QpdfObject> pending = ParserContainers.CreateList(walk);
        AddChildren(fields);
        const MemberStatus mixed = MemberStatus.HasSurvivor | MemberStatus.HasRemovedWidget;
        while (pending.Count != 0)
        {
            token.ThrowIfCancellationRequested(); governor.NoteOperation();
            QpdfObject next = pending[^1]; pending.RemoveAt(pending.Count - 1);
            if (next is not PdfReference reference
                || !ParserContainers.AddSetItem(walk, visited, (reference.ObjectNumber, reference.Generation), 8L)
                || CatalogCopy.ResolveValue(reference, resolver, governor, token) is not PdfDictionary field
                || membership.CompleteStatus(reference, field) is not MemberStatus status
                || (status & mixed) != mixed
                || ReadFieldKids(field, resolver) is not PdfArray children) { continue; }

            PdfArray? projected = RetainFieldEntries(children, (membership, walk, pending), static (child, state) =>
            {
                if (state.membership.CompleteStatus(child) is not MemberStatus childStatus) { return true; }
                if ((childStatus & mixed) == mixed) { ParserContainers.AddItem(state.walk, state.pending, child); }
                return (childStatus & mixed) != MemberStatus.HasRemovedWidget;
            }, memory, governor, token);
            if (projected is null) { continue; }
            edits ??= ParserContainers.CreateDictionary<int, PdfArray>(memory);
            ParserContainers.EnsureEntryCapacity(memory, edits, IntPtr.Size);
            edits.Add(reference.ObjectNumber, projected);
        }

        if (edits is null || !PdfDictionaryLookup.TryGet(form, "CO"u8, governor, token, out QpdfObject co)
            || CatalogCopy.ResolveValue(co, resolver, governor, token) is not PdfArray order) { return edits; }
        visited.Clear();
        AddChildren(fields);
        while (pending.Count != 0)
        {
            token.ThrowIfCancellationRequested(); governor.NoteOperation();
            QpdfObject next = pending[^1]; pending.RemoveAt(pending.Count - 1);
            if (next is not PdfReference reference
                || !ParserContainers.AddSetItem(walk, visited, (reference.ObjectNumber, reference.Generation), 8L)) { continue; }
            if (edits.TryGetValue(reference.ObjectNumber, out PdfArray? replacement)) { AddChildren(replacement); }
            else if (CatalogCopy.ResolveValue(reference, resolver, governor, token) is PdfDictionary field
                && ReadFieldKids(field, resolver) is PdfArray children) { AddChildren(children); }
        }
        calculationOrder = RetainFieldEntries(order, visited, static (reference, reached) =>
            reached.Contains((reference.ObjectNumber, reference.Generation)), memory, governor, token);
        return edits;

        void AddChildren(PdfArray children)
        {
            for (int index = 0; index < children.Count; index++)
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                ParserContainers.AddItem(walk, pending, children[index]);
            }
        }
    }

    private static PdfArray? RetainFieldEntries<T>(PdfArray items, T state, Func<PdfReference, T, bool> keep,
        ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        List<QpdfObject>? kept = null;
        for (int index = 0; index < items.Count; index++)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            QpdfObject item = items[index];
            if (item is PdfReference reference && !keep(reference, state))
            {
                if (kept is not null) { continue; }
                kept = ParserContainers.CreateList(memory);
                for (int prefix = 0; prefix < index; prefix++)
                {
                    token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                    ParserContainers.AddItem(memory, kept, items[prefix]);
                }
            }
            else if (kept is not null) { ParserContainers.AddItem(memory, kept, item); }
        }
        return kept is null ? null : ParserContainers.CopyArray(memory, kept);
    }
}

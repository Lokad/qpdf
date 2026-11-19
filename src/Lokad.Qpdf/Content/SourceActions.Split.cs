using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

internal static partial class SourceActions
{
    internal static (PdfDictionary Catalog, Dictionary<int, QpdfObject>? Edits) ProjectSplitNavigation(
        PdfDictionary catalog, IIndirectResolver resolver, ReadOnlySpan<PdfReference> pages, IReadOnlySet<int> group,
        int maximum, QpdfLimits limits, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        using Binding binding = new(resolver, catalog, group, governor, memory, token);
        foreach (PdfReference page in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if (resolver.TryResolve(page, out QpdfObject? value) && value is PdfDictionary dictionary)
            { binding.AddPage(page.ObjectNumber, dictionary); }
        }
        binding.Add(catalog, Role.Catalog, null);
        Dictionary<int, QpdfObject>? edits = binding.ApplyProjection(maximum, limits);
        return ((PdfDictionary?)binding.Map(catalog, Role.Catalog) ?? catalog, edits);
    }

    private sealed partial class Binding
    {
        private readonly IReadOnlySet<int>? _splitPages;

        internal Binding(IIndirectResolver resolver, PdfDictionary catalog, IReadOnlySet<int> group,
            ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
            : this(resolver, governor, memory, token)
        {
            _catalog = catalog; _splitPages = group;
        }

        private bool IsStaleDestination(PdfArray destination)
            => destination.Count > 0 && (destination[0] is PdfNull
                || destination[0] is PdfReference target && _splitPages?.Contains(target.ObjectNumber) == false);

        private bool HasStaleDestination(PdfDictionary dictionary, ReadOnlySpan<byte> key)
        {
            QpdfObject? destination = Resolve(Read(dictionary, key, _governor, _token));
            if (destination is PdfName or PdfByteString && _catalog is PdfDictionary catalog)
            {
                _destinations ??= NamedDestinations.Create(Resolver, catalog, _scratch, _governor, _token);
                destination = _destinations.Find(destination);
            }
            return destination is PdfArray explicitDestination && IsStaleDestination(explicitDestination);
        }

        private void ProjectNavigation(Node node, PdfDictionary dictionary)
        {
            bool hasDestination = (node.Roles & Role.Outline) != 0
                || (node.Roles & Role.Annotation) != 0 && IsSubtype(node, "Link"u8);
            if (hasDestination && HasStaleDestination(dictionary, "Dest"u8))
            {
                node.Edited = PdfDictionaryRebuild.RemoveEntry(Graph, dictionary, "Dest"u8);
            }
            if ((node.Roles & Role.Action) != 0
                && PdfDictionaryLookup.HasName(dictionary, "S"u8, "GoTo"u8, Resolver, _governor, _token)
                && HasStaleDestination(dictionary, "D"u8)
                && Resolve(Read(dictionary, "Next"u8, _governor, _token)) is null or PdfNull or PdfArray { Count: 0 })
            {
                node.Edited = PdfNull.Instance;
            }
        }

        private QpdfObject BuildActionArray(PdfArray array, int depth)
        {
            List<QpdfObject> items = ParserContainers.CreateList<QpdfObject>(Graph.Memory, array.Count, IntPtr.Size);
            for (int index = 0; index < array.Count; index++)
            {
                Check();
                QpdfObject raw = array[index];
                Node? child = Find(raw, Role.Action);
                if (child is { Edited: PdfNull }) { continue; }
                items.Add(child is { Changed: true } ? child.Reference ?? Build(child, depth + 1) : raw);
            }
            PdfArray projected = ParserContainers.CopyArray(Graph.Memory, items);
            Graph.Add(checked(64L + IntPtr.Size * (long)projected.Count));
            return projected;
        }
    }
}

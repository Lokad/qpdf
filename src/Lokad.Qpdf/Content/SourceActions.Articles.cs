using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

internal static partial class SourceActions
{
    internal static (PdfDictionary Catalog, Dictionary<int, QpdfObject>? Edits) ProjectArticleActions(
        PdfDictionary catalog, IIndirectResolver resolver, ReadOnlySpan<PdfReference> pages, ArticleThreads? source,
        IReadOnlyDictionary<PdfReference, PdfReference>? replacements, IReadOnlyDictionary<int, QpdfObject>? pageEdits,
        int maximum, QpdfLimits limits, ParserMemoryScope memory, ResourceGovernor governor, CancellationToken token)
    {
        using Binding binding = new(resolver, source, replacements, limits, governor, memory, token);
        foreach (PdfReference page in pages)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation();
            if ((pageEdits?.TryGetValue(page.ObjectNumber, out QpdfObject? value) == true || resolver.TryResolve(page, out value))
                && value is PdfDictionary dictionary)
            { binding.AddPage(page.ObjectNumber, dictionary); }
        }
        binding.Add(catalog, Role.Catalog, null);
        Dictionary<int, QpdfObject>? edits = binding.ApplyProjection(maximum, limits);
        return ((PdfDictionary?)binding.Map(catalog, Role.Catalog) ?? catalog, edits);
    }

    private sealed partial class Binding
    {
        private readonly bool _projectArticleActions;
        private readonly ArticleThreads? _articleSource;
        private readonly IReadOnlyDictionary<PdfReference, PdfReference>? _articleReplacements;

        internal Binding(IIndirectResolver resolver, ArticleThreads? source,
            IReadOnlyDictionary<PdfReference, PdfReference>? replacements, QpdfLimits limits,
            ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
            : this(resolver, governor, memory, token)
        {
            _projectArticleActions = true;
            _articleSource = source; _articleReplacements = replacements; _maxActionContainerSize = limits.MaxContainerSize;
        }

        private void ProjectThreadAction(Node node, PdfDictionary dictionary)
        {
            if (node.Roles != Role.Action
                || !PdfDictionaryLookup.HasName(dictionary, "S"u8, "Thread"u8, Resolver, _governor, _token))
            { return; }
            if (Read(dictionary, "F"u8, _governor, _token) is QpdfObject file)
            {
                if (Resolve(file) is not (PdfDictionary or PdfByteString)) { SkipAction(node); }
                return;
            }
            if (_articleSource is null || _articleReplacements is null) { SkipAction(node); return; }

            QpdfObject? destination = Read(dictionary, "D"u8, _governor, _token);
            QpdfObject? bead = Read(dictionary, "B"u8, _governor, _token);
            (PdfReference? thread, PdfReference? projectedBead) = _articleSource.ResolveAction(destination, bead,
                Resolver, _articleReplacements, _governor, _token);
            if (thread is null) { SkipAction(node); return; }

            PdfDictionary projected = PdfDictionaryRebuild.ReplaceEntry(Graph, dictionary, "D"u8, thread);
            if (projectedBead is not null)
            { projected = PdfDictionaryRebuild.ReplaceEntry(Graph, projected, "B"u8, projectedBead); }
            node.Edited = projected;
        }
    }
}

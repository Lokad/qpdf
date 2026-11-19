using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Content;

internal static class PageResources
{
    internal static readonly PdfName Key = PdfName.FromAscii("Resources");
    private static readonly PdfDictionary Empty = new(new Dictionary<PdfName, QpdfObject>());

    private static bool IsDictionary(IIndirectResolver resolver, QpdfObject? resources,
        QpdfLimits limits, ResourceGovernor governor)
        => resources is not null && PageInheritance.Resolve(resolver, resources, limits, governor) is PdfDictionary;

    internal static QpdfObject? Check(PageLocation page, int pageNumber, IIndirectResolver resolver,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        QpdfObject? resources = PageInheritance.Find(resolver, page, Key, limits, governor, token);
        if (!IsDictionary(resolver, resources, limits, governor))
        {
            diagnostics.AddParts(QpdfDiagnosticCode.PageStructureWarning, QpdfDiagnosticSeverity.Warning,
                ["Page ", pageNumber, " has missing or invalid /Resources."]);
        }
        return resources;
    }

    internal static QpdfObject? PrepareSelection(PdfDictionary page, QpdfObject? inherited, IIndirectResolver resolver,
        QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics, CancellationToken token)
    {
        QpdfObject? effective = inherited;
        if (effective is null && PdfDictionaryLookup.TryGet(page, "Resources"u8, governor, token, out QpdfObject local))
        { effective = local; }
        if (IsDictionary(resolver, effective, limits, governor)) { return inherited; }

        if (IsResourceFree(page, resolver, limits, governor, token))
        {
            diagnostics.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning,
                "Page /Resources is missing or invalid. Installed an empty dictionary for resource-free content.");
            return Empty;
        }

        diagnostics.Add(QpdfDiagnosticCode.PageStructureWarning, QpdfDiagnosticSeverity.Warning,
            "Page /Resources is missing or invalid. The page may depend on resources that cannot be resolved.");
        return inherited;
    }

    private static bool IsResourceFree(PdfDictionary page, IIndirectResolver resolver, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken token)
    {
        if (AppearanceResources.PageNeedsResources(page, resolver, governor, token)) { return false; }
        if (!PdfDictionaryLookup.TryGet(page, "Contents"u8, governor, token, out QpdfObject contents)
            || contents is PdfNull) { return true; }
        using AnalysisChunks? chunks = ContentTransforms.CollectProgramChunks(contents, resolver, null, false, limits, governor, token);
        if (chunks is null) { return false; }
        if (chunks.Parts.Count == 0) { return true; }
        try
        {
            if (chunks.Parts.Count == 1) { return IsResourceFreeProgram(chunks.Parts[0], governor, token); }
            using JoinedContent program = chunks.Join();
            return IsResourceFreeProgram(program.Bytes, governor, token);
        }
        catch (QpdfParseException) { return false; }
    }

    private static bool IsResourceFreeProgram(ReadOnlyMemory<byte> program, ResourceGovernor governor, CancellationToken token)
    {
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        memory.Reserve(64L);
        ContentTokenizer tokenizer = new(program, governor, token);
        while (tokenizer.TryReadToken(out ContentToken item))
        {
            governor.NoteOperation();
            if (item.Kind == ContentTokenKind.Operator && !IsResourceFreeOperator(item.Data.Span)) { return false; }
        }
        return true;
    }

    // Graphics state, path construction and painting with the initial color space.
    private static bool IsResourceFreeOperator(ReadOnlySpan<byte> op)
        => op.Length == 1 ? "qQwJjMdimlcvyhSsfFBbnW"u8.IndexOf(op[0]) >= 0
            : op.SequenceEqual("cm"u8) || op.SequenceEqual("ri"u8) || op.SequenceEqual("re"u8)
                || op.SequenceEqual("f*"u8) || op.SequenceEqual("B*"u8) || op.SequenceEqual("b*"u8) || op.SequenceEqual("W*"u8);
}

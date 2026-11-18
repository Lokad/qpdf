using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Json;

/// <summary>Borrowed source for JSON projections. Resolution uses internal identities; output uses declared pairs.</summary>
internal readonly struct JsonDocumentView
{
    private readonly ImportedModel? _model;
    private readonly IReadOnlyDictionary<int, (int Number, int Generation)>? _originalIds;

    internal JsonDocumentView(QpdfDocument document)
    {
        Physical = document.Engine;
        Resolver = Physical;
        Pages = document.PageReferences;
        HeaderVersion = Physical.InspectionVersionParts ?? document.HeaderVersionParts;
        Security = Physical.Security;
    }

    internal JsonDocumentView(ImportedModel model, IIndirectResolver resolver, IReadOnlyList<PageLocation> pages,
        IReadOnlyDictionary<int, (int Number, int Generation)>? originalIds, DocumentSecurity? security)
    {
        _model = model;
        _originalIds = originalIds;
        Resolver = resolver;
        Pages = pages;
        HeaderVersion = new(model.HeaderVersion.Major, model.HeaderVersion.Minor);
        Security = security;
    }

    internal DocumentEngine? Physical { get; }
    internal IIndirectResolver Resolver { get; }
    internal IReadOnlyList<PageLocation> Pages { get; }
    internal QpdfDocument.VersionParts HeaderVersion { get; }
    internal DocumentSecurity? Security { get; }
    internal IReadOnlyDictionary<int, QpdfObject>? ImportedObjects => _model?.Objects;
    internal PdfDictionary Trailer => _model?.Trailer ?? PhysicalReader.Trailer;
    internal bool CalledAllPages => _model?.CalledAllPages
        ?? (PhysicalReader.InspectionCalledAllPages ?? PhysicalReader.HasInspectionObjects);
    internal bool PushedInheritedResources => _model?.PushedInheritedResources
        ?? (PhysicalReader.InspectionPushedInheritedResources ?? PhysicalReader.HasInspectionObjects);

    private DocumentEngine PhysicalReader => Physical ?? throw new InvalidOperationException("Missing physical reader.");

    internal QpdfObject Load(int number)
        => _model is not null ? _model.Objects[number] : PhysicalReader.Load(number);

    internal (int Number, int Generation) ObjectPair(int number)
        => Physical is DocumentEngine reader ? (number, reader.InspectionObjectGeneration(number))
            : ReferencePair(number, 0);

    internal (int Number, int Generation) ReferencePair(int number, int generation)
        => OriginalObjectIds.DisplayPair(number, generation, _originalIds);

    internal bool HasLiveReference(PdfReference reference)
        => Physical is DocumentEngine reader ? reader.HasLiveReference(reference)
            : reference.Generation == 0 && Resolver.TryResolve(reference, out _);

    internal bool IsNullReference(PdfReference reference)
        => Physical is DocumentEngine reader ? reader.IsNullReference(reference)
            : reference.Generation != 0 || !Resolver.TryResolve(reference, out QpdfObject? value) || value is null or PdfNull;

    internal QpdfObject? ResolveArrayOrNull(PdfReference reference)
        => Physical is DocumentEngine reader ? reader.ResolveArrayOrNull(reference)
            : Resolver.TryResolve(reference, out QpdfObject? value) && value is PdfArray or PdfNull ? value : null;
}

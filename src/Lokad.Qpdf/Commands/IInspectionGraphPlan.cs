namespace Lokad.Qpdf.Commands;

/// <summary>Page assembly shared by structural and JSON inspections.</summary>
internal interface IInspectionGraphPlan
{
    QpdfPath Input { get; }
    QpdfRecoveryMode Recovery { get; }
    IReadOnlyList<PdfPageSource> Sources { get; }
    PdfPageCollation? Collation { get; }
    bool AssembleCatalog { get; }
    IReadOnlyList<OutputRotation> Rotations { get; }
    IReadOnlyList<OverlaySpec> Overlays { get; }
    IReadOnlyList<OverlaySpec> Underlays { get; }
    bool CoalesceContents { get; }
    bool ExternalizeInlineImages { get; }
    long IiMinBytes { get; }
}

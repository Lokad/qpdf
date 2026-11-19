namespace Lokad.Qpdf.Commands;

/// <summary>Page assembly shared by structural and JSON inspections.</summary>
internal interface IInspectionGraphPlan
{
    QpdfPath Input { get; }
    QpdfRecoveryMode Recovery { get; }
    IReadOnlyList<PdfPageSource> Sources { get; }
    PdfPageCollation? Collation { get; }
    bool AssembleCatalog { get; }
    Writing.DocumentPublicationPolicy PublicationPolicy { get; }
    IReadOnlyList<PageLabelSpec> Labels { get; }
    IReadOnlyList<OutputRotation> Rotations { get; }
    IReadOnlyList<OverlaySpec> Overlays { get; }
    IReadOnlyList<OverlaySpec> Underlays { get; }
    bool CoalesceContents { get; }
    bool ExternalizeInlineImages { get; }
    long IiMinBytes { get; }
    string? RemoveUnreferencedResources { get; }
    bool OptimizeImages { get; }
    long OiMinWidth { get; }
    long OiMinHeight { get; }
    long OiMinArea { get; }
    bool KeepInlineImages { get; }
    int? JpegQuality { get; }
    IReadOnlyList<AttachmentAdd> AttachmentsToAdd { get; }
    IReadOnlyList<string> AttachmentsToRemove { get; }
    IReadOnlyList<AttachmentCopy> AttachmentsToCopy { get; }
    bool HasAttachmentEdits { get; }
}

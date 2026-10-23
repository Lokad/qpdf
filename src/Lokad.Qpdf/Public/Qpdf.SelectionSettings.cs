using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    // Borrows configuration through the selection call. This value owns no
    // graph, input lease, reservation or output transaction.
    private readonly struct SelectionSettings
    {
        internal required IReadOnlyList<OutputRotation> Rotations { get; init; }
        internal required IReadOnlyList<PageLabelSpec> Labels { get; init; }
        internal bool RemoveLabels { get; init; }
        internal string? MinVersion { get; init; }
        internal string? ForceVersion { get; init; }
        internal bool DeterministicId { get; init; }
        internal bool StaticId { get; init; }
        internal bool NewlineBeforeEndstream { get; init; }
        internal string? ObjectStreams { get; init; }
        internal bool PreserveUnreferenced { get; init; }
        internal required CompressionRequest Compression { get; init; }
        internal bool RemoveInfo { get; init; }
        internal bool RemoveAcroform { get; init; }
        internal bool RemoveStructure { get; init; }
        internal bool RemoveMetadata { get; init; }
        internal required AttachmentEdits Edits { get; init; }
        internal bool CoalesceContents { get; init; }
        internal bool NormalizeContent { get; init; }
        internal bool ExternalizeInlineImages { get; init; }
        internal long IiMinBytes { get; init; }
        internal required IReadOnlyList<OverlaySpec> Underlays { get; init; }
        internal required IReadOnlyList<OverlaySpec> Overlays { get; init; }
        internal bool FlattenRotation { get; init; }
        internal string? FlattenAnnotations { get; init; }
        internal bool GenerateAppearances { get; init; }
        internal required ImageOptimizeRequest Optimize { get; init; }
        internal required QdfOptions Qdf { get; init; }
        internal required ContentPrunePolicy Prune { get; init; }
        internal bool PreserveCatalog { get; init; }
        internal bool PreserveAttachments { get; init; }
        internal bool Linearize { get; init; }
        internal QpdfPath? LinearizePass1 { get; init; }

        // Typed selection preserves opaque streams and omits command-only
        // catalog/resource defaults. All references here are process-lifetime values.
        internal static SelectionSettings Typed { get; } = new()
        {
            Rotations = Array.Empty<OutputRotation>(),
            Labels = Array.Empty<PageLabelSpec>(),
            Compression = CompressionRequest.Default,
            Edits = AttachmentEdits.Empty,
            IiMinBytes = 1024L,
            Underlays = Array.Empty<OverlaySpec>(),
            Overlays = Array.Empty<OverlaySpec>(),
            Optimize = ImageOptimizeRequest.Disabled,
            Qdf = QdfOptions.Disabled,
            Prune = new(false, null),
        };

        internal static SelectionSettings FromPlan(JobPlan plan, bool pruneEnabled) => new()
        {
            Rotations = plan.Rotations,
            Labels = plan.Labels,
            RemoveLabels = plan.RemoveLabels,
            MinVersion = plan.MinVersion,
            ForceVersion = plan.ForceVersion,
            DeterministicId = plan.DeterministicId,
            StaticId = plan.StaticId,
            NewlineBeforeEndstream = plan.NewlineBeforeEndstream,
            ObjectStreams = plan.ObjectStreams,
            PreserveUnreferenced = plan.PreserveUnreferenced,
            Compression = plan.Compression(),
            RemoveInfo = plan.RemoveInfo,
            RemoveAcroform = plan.RemoveAcroform,
            RemoveStructure = plan.RemoveStructure,
            RemoveMetadata = plan.RemoveMetadata,
            Edits = plan.Edits(),
            CoalesceContents = plan.CoalesceContents,
            NormalizeContent = plan.NormalizeContent,
            ExternalizeInlineImages = plan.ExternalizeInlineImages,
            IiMinBytes = plan.IiMinBytes,
            Underlays = plan.Underlays,
            Overlays = plan.Overlays,
            FlattenRotation = plan.FlattenRotation,
            FlattenAnnotations = plan.FlattenAnnotations,
            GenerateAppearances = plan.GenerateAppearances,
            Optimize = new(plan.OptimizeImages, plan.OiMinWidth, plan.OiMinHeight,
                plan.OiMinArea, plan.KeepInlineImages, plan.JpegQuality),
            Qdf = new(plan.Qdf, plan.NoOriginalObjectIds),
            Prune = new(pruneEnabled, plan.RemoveUnreferencedResources),
            PreserveCatalog = true,
            Linearize = plan.Linearize,
            LinearizePass1 = plan.LinearizePass1,
        };
    }
}

namespace Lokad.Qpdf;

/// <summary>Embedded-file metadata, reader warnings and counters after the input lease closes.</summary>
public sealed record QpdfAttachmentListResult
{
    internal const long StorageBytes = 40L;

    internal QpdfAttachmentListResult(IReadOnlyList<QpdfAttachmentInfo> attachments,
        IReadOnlyList<QpdfDiagnostic> warnings, QpdfExecutionCounters counters)
    { Attachments = attachments; Warnings = warnings; Counters = counters; }

    /// <summary>Gets the immutable attachment metadata collection.</summary>
    public IReadOnlyList<QpdfAttachmentInfo> Attachments { get; }

    /// <summary>Gets immutable reader and recovery warnings.</summary>
    public IReadOnlyList<QpdfDiagnostic> Warnings { get; }

    /// <summary>Gets the final counters, including closed input handles.</summary>
    public QpdfExecutionCounters Counters { get; }
}

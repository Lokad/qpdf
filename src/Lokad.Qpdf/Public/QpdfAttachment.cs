namespace Lokad.Qpdf;

/// <summary>An embedded file listed from a document.</summary>
public sealed record QpdfAttachmentInfo
{
    /// <summary>Creates attachment information.</summary>
    /// <exception cref="ArgumentNullException">A text field is null.</exception>
    public QpdfAttachmentInfo(
        string key,
        string fileName,
        string description,
        string mimeType,
        string creationDate,
        string modDate,
        string checksum,
        int streamObjectNumber,
        long size)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        MimeType = mimeType ?? throw new ArgumentNullException(nameof(mimeType));
        CreationDate = creationDate ?? throw new ArgumentNullException(nameof(creationDate));
        ModDate = modDate ?? throw new ArgumentNullException(nameof(modDate));
        Checksum = checksum ?? throw new ArgumentNullException(nameof(checksum));
        StreamObjectNumber = streamObjectNumber;
        Size = size;
    }

    /// <summary>Gets the name-tree key identifying the attachment.</summary>
    public string Key { get; }

    /// <summary>Gets the preferred file name.</summary>
    public string FileName { get; }

    /// <summary>Gets the description.</summary>
    public string Description { get; }

    /// <summary>Gets the MIME type.</summary>
    public string MimeType { get; }

    /// <summary>Gets the creation date in PDF timestamp syntax.</summary>
    public string CreationDate { get; }

    /// <summary>Gets the modification date in PDF timestamp syntax.</summary>
    public string ModDate { get; }

    /// <summary>Gets the stored checksum as lowercase hexadecimal.</summary>
    public string Checksum { get; }

    /// <summary>Gets the embedded stream object number, or -1 for direct streams.</summary>
    public int StreamObjectNumber { get; }

    /// <summary>Gets the declared byte size, or -1 when absent.</summary>
    public long Size { get; }
}

/// <summary>Outcome of an attachment extraction.</summary>
public sealed record QpdfAttachmentExtraction
{
    /// <summary>Creates an extraction outcome.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public QpdfAttachmentExtraction(
        string key,
        long byteCount,
        IReadOnlyList<QpdfDiagnostic> warnings,
        QpdfExecutionCounters counters)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        ByteCount = byteCount;
        Warnings = warnings is null ? throw new ArgumentNullException(nameof(warnings)) : [.. warnings];
        Counters = counters ?? throw new ArgumentNullException(nameof(counters));
    }

    /// <summary>Gets the extracted attachment key.</summary>
    public string Key { get; }

    /// <summary>Gets the extracted byte count.</summary>
    public long ByteCount { get; }

    /// <summary>Gets the warnings recorded by the job.</summary>
    public IReadOnlyList<QpdfDiagnostic> Warnings { get; }

    /// <summary>Gets the final job counters.</summary>
    public QpdfExecutionCounters Counters { get; }
}

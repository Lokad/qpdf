using Lokad.Qpdf.Runtime;

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
    internal const long StorageBytes = 56L;

    /// <summary>Creates an extraction outcome.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public QpdfAttachmentExtraction(
        string key,
        long byteCount,
        IReadOnlyList<QpdfDiagnostic> warnings,
        QpdfExecutionCounters counters)
        : this(key, byteCount, warnings, counters, false)
    { }

    internal QpdfAttachmentExtraction(
        string key,
        long byteCount,
        IReadOnlyList<QpdfDiagnostic> warnings,
        QpdfExecutionCounters counters,
        bool adoptBacking)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        ByteCount = byteCount;
        Warnings = warnings is null ? throw new ArgumentNullException(nameof(warnings)) : adoptBacking ? warnings : [.. warnings];
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

/// <summary>An attachment to embed from a host file.</summary>
public sealed record QpdfAttachmentSpec
{
    /// <summary>Creates an attachment specification.</summary>
    /// <exception cref="ArgumentNullException">The file is null.</exception>
    public QpdfAttachmentSpec(
        string file,
        string? key,
        string? fileName,
        string? description,
        string? mimeType,
        string? creationDate,
        string? modDate,
        bool replace)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        Key = key;
        FileName = fileName;
        Description = description;
        MimeType = mimeType;
        CreationDate = creationDate;
        ModDate = modDate;
        Replace = replace;
    }

    /// <summary>Gets the host file to embed.</summary>
    public string File { get; }

    /// <summary>Gets the attachment key, defaulting to the file basename.</summary>
    public string? Key { get; }

    /// <summary>Gets the displayed filename, defaulting to the file basename.</summary>
    public string? FileName { get; }

    /// <summary>Gets the description.</summary>
    public string? Description { get; }

    /// <summary>Gets the MIME type.</summary>
    public string? MimeType { get; }

    /// <summary>Gets the creation date in PDF timestamp syntax.</summary>
    public string? CreationDate { get; }

    /// <summary>Gets the modification date in PDF timestamp syntax.</summary>
    public string? ModDate { get; }

    /// <summary>Gets whether a same-key attachment is replaced.</summary>
    public bool Replace { get; }
}

/// <summary>An attachment source to copy: a document plus a key prefix.</summary>
public sealed record QpdfAttachmentCopy
{
    /// <summary>Creates an attachment copy source.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public QpdfAttachmentCopy(QpdfPath input, string? prefix)
    {
        Input = input;
        Prefix = prefix;
    }

    /// <summary>Gets the source document path.</summary>
    public QpdfPath Input { get; }

    /// <summary>Gets the key prefix.</summary>
    public string? Prefix { get; }
}

/// <summary>Outcome of an attachment edit.</summary>
public sealed record QpdfAttachmentEditResult
{
    internal const long StorageBytes = 48L;

    /// <summary>Creates an edit outcome.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public QpdfAttachmentEditResult(
        int attachmentCount,
        IReadOnlyList<QpdfDiagnostic> warnings,
        QpdfExecutionCounters counters)
        : this(attachmentCount, warnings, counters, false)
    { }

    internal QpdfAttachmentEditResult(
        int attachmentCount,
        IReadOnlyList<QpdfDiagnostic> warnings,
        QpdfExecutionCounters counters,
        bool adoptBacking)
    {
        AttachmentCount = attachmentCount;
        Warnings = warnings is null ? throw new ArgumentNullException(nameof(warnings)) : adoptBacking ? warnings : [.. warnings];
        Counters = counters ?? throw new ArgumentNullException(nameof(counters));
    }

    /// <summary>Gets the final attachment count.</summary>
    public int AttachmentCount { get; }

    /// <summary>Gets the warnings recorded by the job.</summary>
    public IReadOnlyList<QpdfDiagnostic> Warnings { get; }

    /// <summary>Gets the final job counters.</summary>
    public QpdfExecutionCounters Counters { get; }
}

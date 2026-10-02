namespace Lokad.Qpdf.Commands;

/// <summary>Validated job plan shared by every entry path.</summary>
internal abstract record JobPlan(QpdfPath Input)
{
    public bool WarningExitZero { get; init; }

    public bool NoWarn { get; init; }

    public IReadOnlyList<OutputRotation> Rotations { get; init; } = [];

    public IReadOnlyList<PageLabelSpec> Labels { get; init; } = [];

    public int? SplitPages { get; init; }

    public bool ReplaceInput { get; init; }

    public string? MinVersion { get; init; }

    public string? ForceVersion { get; init; }

    public bool DeterministicId { get; init; }

    public bool StaticId { get; init; }

    public bool NewlineBeforeEndstream { get; init; }

    public string? ObjectStreams { get; init; }

    public bool PreserveUnreferenced { get; init; }

    public string? StreamData { get; init; }

    public string? CompressStreams { get; init; }

    public string? DecodeLevel { get; init; }

    public long? MaxStreamFilters { get; init; }

    public long? ParserMaxNesting { get; init; }

    public bool RecompressFlate { get; init; }

    public string? CompressionLevel { get; init; }

    public bool RemoveInfo { get; init; }

    public bool RemoveMetadata { get; init; }

    public bool CoalesceContents { get; init; }

    public bool NormalizeContent { get; init; }

    public bool ExternalizeInlineImages { get; init; }

    public long IiMinBytes { get; init; } = 1024L;

    public string? RemoveUnreferencedResources { get; init; }

    public IReadOnlyList<AttachmentAdd> AttachmentsToAdd { get; init; } = [];

    public IReadOnlyList<string> AttachmentsToRemove { get; init; } = [];

    public IReadOnlyList<AttachmentCopy> AttachmentsToCopy { get; init; } = [];

    public IReadOnlyList<OverlaySpec> Overlays { get; init; } = [];

    public IReadOnlyList<OverlaySpec> Underlays { get; init; } = [];

    public bool FlattenRotation { get; init; }

    public string? FlattenAnnotations { get; init; }

    public bool GenerateAppearances { get; init; }

    public bool OptimizeImages { get; init; }

    public long OiMinWidth { get; init; } = 128L;

    public long OiMinHeight { get; init; } = 128L;

    public long OiMinArea { get; init; } = 16384L;

    public bool KeepInlineImages { get; init; }

    public int? JpegQuality { get; init; }

    public bool Qdf { get; init; }

    public bool NoOriginalObjectIds { get; init; }

    public bool Linearize { get; init; }
    public QpdfPath? LinearizePass1 { get; init; }


    internal CompressionRequest Compression()
    {
        return new CompressionRequest(StreamData, CompressStreams, DecodeLevel, RecompressFlate, CompressionLevel);
    }

    internal AttachmentEdits Edits()
    {
        return new AttachmentEdits(AttachmentsToAdd, AttachmentsToRemove, AttachmentsToCopy);
    }
}

/// <summary>Validated single-page selection.</summary>
internal sealed record SelectionPlan(QpdfPath Input, QpdfPath Output, int Page, QpdfRecoveryMode Recovery) : JobPlan(Input)
{
    internal static int ParsePageNumber(string range)
    {
        if (range is null || range.Length == 0 || range.Length > 10)
        {
            throw new QpdfParseException($"Invalid page range '{range}'.", 0L, null);
        }

        foreach (char c in range)
        {
            if (c < '0' || c > '9')
            {
                throw new QpdfParseException($"Invalid page range '{range}'. Only a single page number is supported.", 0L, null);
            }
        }

        long value = 0L;
        foreach (char c in range)
        {
            value = (value * 10L) + (c - '0');
        }

        if (value < 1L || value > int.MaxValue)
        {
            throw new QpdfParseException($"Invalid page range '{range}'.", 0L, null);
        }

        return (int)value;
    }

    internal static (bool Trailer, int Number, int Generation) ParseObjectSelector(string value)
    {
        if (value is null || value.Length == 0 || value.Length > 32)
        {
            throw new QpdfParseException("Object selector must be trailer or an object number with optional generation.", 0L, null);
        }

        if (string.Equals(value, "trailer", StringComparison.Ordinal))
        {
            return (true, 0, 0);
        }

        int comma = value.IndexOf(',');
        string head = comma < 0 ? value : value.Substring(0, comma);
        string tail = comma < 0 ? "0" : value.Substring(comma + 1);
        if (!TryParseObjectNumber(head, out int number) || !TryParseObjectNumber(tail, out int generation))
        {
            throw new QpdfParseException($"Invalid object selector '{value}'. Enumerate trailer or an object number with optional generation.", 0L, null);
        }

        if (number < 1)
        {
            throw new QpdfParseException($"Invalid object selector '{value}'. Object numbers start at 1.", 0L, null);
        }

        return (false, number, generation);
    }

    private static bool TryParseObjectNumber(string text, out int number)
    {
        number = 0;
        if (text is null || text.Length == 0 || text.Length > 10)
        {
            return false;
        }

        long value = 0L;
        foreach (char c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }

            value = checked((value * 10L) + (c - '0'));
            if (value > int.MaxValue)
            {
                return false;
            }
        }

        number = (int)value;
        return true;
    }

    internal static QpdfPath CheckedPath(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new QpdfParseException("Empty file path.", 0L, null);
        }

        if (value.Length > QpdfPath.MaxLength || value.Contains('\0'))
        {
            throw new QpdfParseException("File path is not a valid logical path.", 0L, null);
        }

        return QpdfPath.From(value);
    }
}

/// <summary>Validated page-count inspection.</summary>
internal sealed record ShowNpagesPlan(QpdfPath Input, QpdfRecoveryMode Recovery) : JobPlan(Input);

/// <summary>Validated page-dictionary inspection.</summary>
internal sealed record ShowPagesPlan(QpdfPath Input, QpdfRecoveryMode Recovery) : JobPlan(Input);

/// <summary>Validated object inspection.</summary>
internal sealed record ShowObjectPlan(QpdfPath Input, QpdfRecoveryMode Recovery, string Selector) : JobPlan(Input);

/// <summary>Validated cross-reference inspection.</summary>
internal sealed record ShowXrefPlan(QpdfPath Input, QpdfRecoveryMode Recovery) : JobPlan(Input);

/// <summary>Validated structural inspection.</summary>
internal sealed record CheckPlan(QpdfPath Input, QpdfRecoveryMode Recovery) : JobPlan(Input)
{
    public bool CheckLinearization { get; init; }

    public bool ShowLinearization { get; init; }
}

/// <summary>Validated linearization-only inspection.</summary>
internal sealed record LinearizationPlan(QpdfPath Input, QpdfRecoveryMode Recovery) : JobPlan(Input)
{
    public bool Check { get; init; }

    public bool Show { get; init; }
}

/// <summary>Validated whole-file transformation.</summary>
internal sealed record WholeFilePlan(QpdfPath Input, QpdfPath Output, QpdfRecoveryMode Recovery) : JobPlan(Input);

/// <summary>Validated attachment inspection and extraction.</summary>
internal sealed record AttachmentPlan(QpdfPath Input, QpdfPath? Output, string? Key, bool List, QpdfRecoveryMode Recovery) : JobPlan(Input);

/// <summary>Validated JSON inspection.</summary>
internal sealed record JsonPlan(QpdfPath Input, QpdfPath Output, int Version, IReadOnlyList<string> Keys, QpdfRecoveryMode Recovery) : JobPlan(Input)
{
    public string StreamDataMode { get; init; } = "none";

    public string? StreamPrefix { get; init; }

    public IReadOnlyList<string> Objects { get; init; } = [];

    public bool MinimalOutput { get; init; }
}

/// <summary>Validated JSON inspection bundle shared by every entry path.</summary>
internal sealed record JsonInspection(
    int Version,
    IReadOnlyList<string> Keys,
    string? DecodeLevel,
    string StreamData,
    string? StreamPrefix,
    IReadOnlyList<string> Objects,
    bool MinimalOutput);

/// <summary>Validated JSON import: a qpdf JSON file converted to PDF.</summary>
internal sealed record JsonInputPlan(QpdfPath Input, QpdfPath Output, QpdfRecoveryMode Recovery) : JobPlan(Input);

/// <summary>Validated PDF update from qpdf JSON.</summary>
internal sealed record JsonUpdatePlan(QpdfPath Input, QpdfPath JsonPath, QpdfPath Output, QpdfRecoveryMode Recovery) : JobPlan(Input);

/// <summary>One multi-source group: a raw file token plus its range text.</summary>
internal sealed record PageGroup(string File, string Range);

/// <summary>One attachment to embed: a host file plus display metadata.</summary>
internal sealed record AttachmentAdd(
    string File,
    string? Key,
    string? FileName,
    string? CreationDate,
    string? ModDate,
    string? MimeType,
    string? Description,
    bool Replace);

/// <summary>One attachment source to copy: a document plus a key prefix.</summary>
internal sealed record AttachmentCopy(string File, string? Prefix);

/// <summary>Attachment edits applied to the output model.</summary>
internal sealed record AttachmentEdits(
    IReadOnlyList<AttachmentAdd> Adds,
    IReadOnlyList<string> Removes,
    IReadOnlyList<AttachmentCopy> Copies)
{
    internal static AttachmentEdits Empty { get; } = new([], [], []);

    internal bool HasEdits => Adds.Count > 0 || Removes.Count > 0 || Copies.Count > 0;
}

/// <summary>Validated multi-source merge.</summary>
internal sealed record MergePlan(
    QpdfPath Input,
    QpdfPath Output,
    IReadOnlyList<PdfPageSource> Sources,
    PdfPageCollation? Collation,
    QpdfRecoveryMode Recovery) : JobPlan(Input);

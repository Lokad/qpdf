namespace Lokad.Qpdf.Commands;

/// <summary>Validated job plan shared by every entry path.</summary>
internal abstract record JobPlan(QpdfPath Input)
{
    internal CollationPreparation? CollationStorage { get; init; }
    internal Security.CommandPasswordFiles? PasswordFiles { get; init; }
    internal QpdfPassword? TransformationPassword { get; init; }
    internal QpdfOutputEncryption? OutputEncryption { get; init; }
    /// <summary>Input token standing in for the synthetic zero-page document.</summary>
    internal const string EmptyDocumentInput = "--empty";

    public bool EmptyDocument { get; init; }

    public bool WarningExitZero { get; init; }

    public bool NoWarn { get; init; }

    public IReadOnlyList<OutputRotation> Rotations { get; init; } = [];

    public IReadOnlyList<PageLabelSpec> Labels { get; init; } = [];

    public bool RemoveLabels { get; init; }

    public int? SplitPages { get; init; }

    public bool ReplaceInput { get; init; }

    public string? MinVersion { get; init; }

    public string? ForceVersion { get; init; }

    public bool StaticId { get; init; }

    public bool NewlineBeforeEndstream { get; init; }

    public string? ObjectStreams { get; init; }

    public bool PreserveUnreferenced { get; init; }

    public string? StreamData { get; init; }

    public string? CompressStreams { get; init; }

    public string? DecodeLevel { get; init; }

    internal byte CompressionState { get; init; }

    public long? MaxStreamFilters { get; init; }

    public bool NoDefaultLimits { get; init; }

    public long? ParserMaxErrors { get; init; }

    public long? ParserMaxNesting { get; init; }

    public long? MaxContainerSize { get; init; }

    public long? MaxContainerSizeDamaged { get; init; }

    public bool WithImages { get; init; }

    public bool IgnoreXrefStreams { get; init; }

    public bool ReportMemoryUsage { get; init; }

    public bool TestJsonSchema { get; init; }

    public bool Verbose { get; init; }

    public bool Progress { get; init; }

    public bool RawStreamData { get; init; }

    public bool FilteredStreamData { get; init; }

    public bool RecompressFlate { get; init; }

    public string? CompressionLevel { get; init; }

    public bool RemoveInfo { get; init; }

    public bool RemoveAcroform { get; init; }

    public bool RemoveStructure { get; init; }

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
        CompressionSettings settings = CompressionSettings.FromState(CompressionState);
        return new CompressionRequest(StreamData,
            settings.IsInitialized ? settings.CompressStreams : CompressStreams,
            settings.IsInitialized ? settings.DecodeLevel : DecodeLevel,
            RecompressFlate, CompressionLevel);
    }

    internal AttachmentEdits Edits()
    {
        return new AttachmentEdits(AttachmentsToAdd, AttachmentsToRemove, AttachmentsToCopy);
    }
}

/// <summary>Validated single-page selection.</summary>
internal sealed record SelectionPlan(QpdfPath Input, QpdfPath Output, int Page, QpdfRecoveryMode Recovery) : JobPlan(Input)
{
    internal static bool IsPlainPageNumber(string? range)
    {
        if (string.IsNullOrEmpty(range) || range.Length > 10)
        {
            return false;
        }

        foreach (char c in range)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }

    internal static int ParsePageNumber(string? range)
    {
        if (range is null || range.Length == 0 || range.Length > 10)
        {
            throw new QpdfParseException($"Invalid page range '{ArgvParser.ErrorPreview(range)}'.", 0L, null);
        }

        foreach (char c in range)
        {
            if (c < '0' || c > '9')
            {
                throw new QpdfParseException($"Invalid page range '{ArgvParser.ErrorPreview(range)}'. Only a single page number is supported.", 0L, null);
            }
        }

        long value = 0L;
        foreach (char c in range)
        {
            value = (value * 10L) + (c - '0');
        }

        if (value < 1L || value > int.MaxValue)
        {
            throw new QpdfParseException($"Invalid page range '{ArgvParser.ErrorPreview(range)}'.", 0L, null);
        }

        return (int)value;
    }

    internal static (bool Trailer, int Number, int Generation) ParseInspectionSelector(string value, int previousGeneration)
    {
        if (value.Length > 32) { throw new QpdfParseException("Object selector exceeds its supported length.", 0L, null); }
        if (value == "trailer") { return (true, 0, previousGeneration); }
        int comma = value.IndexOf(',');
        int generation = comma >= 0 && comma + 1 < value.Length
            ? ReadIntegerPrefix(value.AsSpan(comma + 1)) : previousGeneration;
        return (false, ReadIntegerPrefix(value.AsSpan()), generation);
    }

    private static int ReadIntegerPrefix(ReadOnlySpan<char> text)
    {
        int cursor = 0;
        while (cursor < text.Length && text[cursor] is ' ' or '\t' or '\n' or '\r' or '\f' or '\v') { cursor++; }
        bool negative = cursor < text.Length && text[cursor] == '-';
        if (cursor < text.Length && text[cursor] is '+' or '-') { cursor++; }
        long number = 0L;
        while (cursor < text.Length && text[cursor] is >= '0' and <= '9')
        {
            number = number * 10L + text[cursor++] - '0';
            if (number > int.MaxValue + (negative ? 1L : 0L))
            {
                throw new QpdfParseException("Object selector integer is outside the supported range.", 0L, null);
            }
        }
        return (int)(negative ? -number : number);
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

/// <summary>Validated object inspection.</summary>
internal sealed record ShowObjectPlan(QpdfPath Input, QpdfRecoveryMode Recovery, string Selector) : JobPlan(Input)
{
    public bool RejectStreamAfterInfo { get; init; }

    public QpdfPath? Output { get; init; }
}

/// <summary>Validated encryption-status predicate.</summary>
internal sealed record SecurityPlan(QpdfPath Input, QpdfRecoveryMode Recovery, bool RequiresPassword,
    QpdfPassword? Password) : JobPlan(Input);

/// <summary>Validated structural inspection.</summary>
internal sealed record CheckPlan(QpdfPath Input, QpdfRecoveryMode Recovery) : JobPlan(Input)
{
    public QpdfPassword? Password { get; init; }

    public bool ShowEncryption { get; init; }

    public bool NeedsDocument => RunCheck || ShowNpages || ShowPages || ShowXref || ObjectSelector is not null
        || ListAttachments || AttachmentKey is not null || CheckLinearization || ShowLinearization;

    public IReadOnlyList<PdfPageSource> Sources { get; init; } = [];

    public PdfPageCollation? Collation { get; init; }

    public bool RunCheck { get; init; } = true;

    public string? AttachmentKey { get; init; }

    public bool ShowNpages { get; init; }

    public bool ShowPages { get; init; }

    public bool ShowXref { get; init; }

    public string? ObjectSelector { get; init; }

    public bool ListAttachments { get; init; }

    public bool CheckLinearization { get; init; }

    public bool ShowLinearization { get; init; }
}

/// <summary>Validated whole-file transformation.</summary>
internal sealed record WholeFilePlan(QpdfPath Input, QpdfPath Output, QpdfRecoveryMode Recovery) : JobPlan(Input);

/// <summary>Validated attachment inspection and extraction.</summary>
internal sealed record AttachmentPlan(QpdfPath Input, QpdfPath? Output, string? Key, bool List, QpdfRecoveryMode Recovery) : JobPlan(Input);

/// <summary>Validated JSON inspection.</summary>
internal sealed record JsonPlan(QpdfPath Input, QpdfPath? Output, int Version, IReadOnlyList<string> Keys, QpdfRecoveryMode Recovery) : JobPlan(Input)
{
    public QpdfPassword? Password { get; init; }

    public string StreamDataMode { get; init; } = "none";

    public string? StreamPrefix { get; init; }

    public HashSet<(bool Trailer, int Number, int Generation)>? Objects { get; init; }

    public bool MinimalOutput { get; init; }
}

/// <summary>Validated JSON inspection bundle shared by every entry path.</summary>
internal sealed record JsonInspection(
    int Version,
    IReadOnlyList<string> Keys,
    string? DecodeLevel,
    string StreamData,
    string? StreamPrefix,
    HashSet<(bool Trailer, int Number, int Generation)>? Objects,
    bool MinimalOutput)
{
    internal QpdfPassword? Password { get; init; }
}

/// <summary>Validated JSON import: a qpdf JSON file converted to PDF.</summary>
internal sealed record JsonInputPlan(QpdfPath Input, QpdfPath Output) : JobPlan(Input);

/// <summary>Validated PDF update from qpdf JSON.</summary>
internal sealed record JsonUpdatePlan(QpdfPath Input, QpdfPath JsonPath, QpdfPath Output, QpdfRecoveryMode Recovery) : JobPlan(Input);

/// <summary>One multi-source group: a raw file token plus its range text.</summary>
internal sealed record PageGroup(string File, string? Range)
{
    internal string? Password { get; init; }
    public override string ToString() => "PageGroup [credential redacted]";
}

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
    QpdfRecoveryMode Recovery) : JobPlan(Input)
{
    public bool EmptyInput { get; init; }
}

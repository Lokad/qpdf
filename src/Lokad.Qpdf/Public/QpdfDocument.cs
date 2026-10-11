using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

/// <summary>
/// Strict inspection handle over one input PDF, covering classic tables, cross-reference
/// streams, and hybrid/incremental chains with bounded recovery. Holds its input lease until
/// disposed; always dispose (leases close and budgets release even on failure paths).
/// Handles are single-owner and not thread-safe; concurrent calls need separate handles.
/// </summary>
public sealed class QpdfDocument : IAsyncDisposable
{
    private IQpdfInput? _input;
    private DocumentEngine? _engine;
    private readonly ResourceGovernor _governor;
    private readonly DiagnosticBag _diagnostics;
    private List<PageLocation>? _pages;
    private ParserMemoryScope? _pageStorage;
    private readonly int _pageCount;
    private IReadOnlyList<PageLocation>? _inspectionPages;
    private bool _disposed;
    private readonly PdfVersion? _version;
    private readonly PdfVersion? _headerVersion;
    private readonly VersionParts _effectiveVersionParts;
    internal readonly record struct VersionParts(int Major, int Minor);
    internal VersionParts HeaderVersionParts { get; }

    private QpdfDocument(
        IQpdfInput input,
        DocumentEngine engine,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        List<PageLocation> pages,
        ParserMemoryScope pageStorage,
        QpdfPath path,
        QpdfLimits limits,
        PdfVersion? version,
        PdfVersion? headerVersion,
        VersionParts headerVersionParts,
        VersionParts effectiveVersionParts)
    {
        _input = input;
        _engine = engine;
        _governor = governor;
        _diagnostics = diagnostics;
        _pages = pages;
        _pageStorage = pageStorage;
        _pageCount = pages.Count;
        Path = path;
        Limits = limits;
        _version = version;
        _headerVersion = headerVersion;
        HeaderVersionParts = headerVersionParts;
        _effectiveVersionParts = effectiveVersionParts;
    }

    /// <summary>Gets the logical input path.</summary>
    public QpdfPath Path { get; }

    /// <summary>Gets the effective budgets.</summary>
    public QpdfLimits Limits { get; }

    /// <summary>Gets the effective version: catalog /Version overrides the header.</summary>
    public PdfVersion Version => _version ?? throw new InvalidOperationException("Exclusive JSON reader has no public version carrier.");

    /// <summary>Gets the version from the file header.</summary>
    public PdfVersion HeaderVersion => _headerVersion ?? throw new InvalidOperationException("Exclusive JSON reader has no public version carrier.");

    /// <summary>Gets the page count from page-tree traversal.</summary>
    public int PageCount => _inspectionPages?.Count ?? _pageCount;

    /// <summary>Gets an immutable snapshot of the job counters and memory estimates.</summary>
    /// <remarks>The current implementation charges snapshot creation to the document's
    /// estimated storage and work budgets. Snapshots remain valid after disposal;
    /// their values do not measure the heap or require independent physical-memory billing.</remarks>
    /// <exception cref="QpdfLimitException">Snapshot storage or work exceeds the job budget.</exception>
    /// <exception cref="OperationCanceledException">The job cancellation token is canceled.</exception>
    public QpdfExecutionCounters Counters => _governor.OwnedSnapshot();

    /// <summary>Gets an immutable snapshot of the recorded warnings.</summary>
    /// <remarks>Fresh collection storage estimates and work are charged to the document budgets.
    /// Snapshots preserve diagnostic identities and remain valid after disposal.
    /// Empty snapshots borrow shared storage.</remarks>
    /// <exception cref="QpdfLimitException">Snapshot storage or work exceeds the job budget.</exception>
    /// <exception cref="OperationCanceledException">The job cancellation token is canceled.</exception>
    public IReadOnlyList<QpdfDiagnostic> Warnings
    {
        get
        {
            _governor.CheckCancellation();
            IReadOnlyList<QpdfDiagnostic> warnings = _diagnostics.Entries;
            if (warnings.Count == 0) { return Array.Empty<QpdfDiagnostic>(); }
            using OwnedAllocationBatch storage = OwnedAllocationBatch.Create(_governor, 2, _governor.CancellationToken);
            IReadOnlyList<QpdfDiagnostic> snapshot = ResultCollectionStorage.Copy(storage, warnings, IntPtr.Size);
            storage.RetainWithPrepaidRetirement();
            return snapshot;
        }
    }

    internal DocumentEngine Engine => _engine ?? throw new ObjectDisposedException(nameof(QpdfDocument));

    // Complete jobs keep the parsed graph exclusive, but their negotiated
    // version may outlive a private source. Retain only that immutable carrier.
    internal PdfVersion OwnVersion() => _version ?? _governor.OwnVersion(_effectiveVersionParts.Major, _effectiveVersionParts.Minor);

    internal IReadOnlyList<PageLocation> PageReferences => _inspectionPages ?? _pages ?? throw new ObjectDisposedException(nameof(QpdfDocument));

    internal void SetInspectionPages(IReadOnlyList<PageLocation>? pages) => _inspectionPages = pages;

    internal void PrepareInlinePageResources(CancellationToken cancellationToken)
    {
        List<PageLocation> pages = _pages ?? throw new ObjectDisposedException(nameof(QpdfDocument));
        DocumentEngine engine = Engine;
        using ParserMemoryScope memory = ParserMemoryScope.Create(_governor);
        HashSet<int> repaired = ParserContainers.CreateSet<int>(memory);
        for (int index = 0; index < pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); _governor.CheckCancellation(); _governor.ChargeWork(1L);
            PageLocation location = pages[index];
            if (PdfDictionaryLookup.TryGet(location.Page, "Resources"u8, _governor, cancellationToken, out QpdfObject raw)
                && PageInheritance.Resolve(engine, raw, Limits, _governor) is PdfDictionary) { continue; }
            if (PageInheritance.HasDictionaryAncestor(engine, location, "Resources"u8, Limits, _governor, cancellationToken)) { continue; }
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(_governor, 6, cancellationToken);
            PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out int allocation);
            PdfDictionary resources = PdfDictionaryRebuild.Complete(graph, entries, allocation);
            PdfDictionary page = PdfDictionaryRebuild.ReplaceEntry(graph, location.Page, "Resources"u8, resources);
            graph.Retain();
            if (location.Source is not PdfReference source || ParserContainers.AddSetItem(memory, repaired, source.ObjectNumber, sizeof(int)))
            { _diagnostics.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Page Resources is missing or invalid; repairing."); }
            pages[index] = new(location.Source, page);
        }
    }

    /// <summary>Opens a document with default budgets.</summary>
    public static ValueTask<QpdfDocument> OpenAsync(IQpdfHost host, QpdfPath path, CancellationToken cancellationToken)
    {
        return OpenAsync(host, path, QpdfLimits.Default, cancellationToken);
    }

    /// <summary>Opens a document with explicit budgets.</summary>
    /// <exception cref="ArgumentNullException">Host or limits are null.</exception>
    public static async ValueTask<QpdfDocument> OpenAsync(
        IQpdfHost host,
        QpdfPath path,
        QpdfLimits limits,
        CancellationToken cancellationToken)
    {
        if (host is null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken) { OwnsDocumentCounters = true };
        DiagnosticBag diagnostics = new(governor);
        return await OpenCore(host, path, limits, governor, diagnostics, QpdfRecoveryMode.Strict, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synthetic zero-page document backing <c>--empty</c> inspections and rewrites.
    /// The bytes are a minimal classic-xref file parsed by the same strict pipeline
    /// as host inputs, so the empty document carries real xref entries, a catalog,
    /// and an empty page tree with no host lease involved.
    /// </summary>
    private static readonly byte[] EmptyPdfBytes = System.Text.Encoding.ASCII.GetBytes(
        "%PDF-1.3\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [ ] /Count 0 >>\nendobj\nxref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \ntrailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n111\n%%EOF\n");

    private sealed class EmptyPdfInput : IQpdfInput
    {
        internal EmptyPdfInput(QpdfPath path)
        {
            Path = path;
        }

        public QpdfPath Path { get; }

        public string Identity => "empty";

        public long Length => EmptyPdfBytes.Length;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            if (offset < 0 || count < 0 || offset + count > EmptyPdfBytes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Read range exceeds the stable input length.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ReadOnlyMemory<byte>>(EmptyPdfBytes.AsMemory((int)offset, count));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    internal static ValueTask<QpdfDocument> OpenEmptyDocument(
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery,
        CancellationToken cancellationToken)
        => OpenEmptyDocument(path, limits, governor, diagnostics, recovery, cancellationToken, true);

    internal static async ValueTask<QpdfDocument> OpenEmptyDocument(
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery,
        CancellationToken cancellationToken,
        bool valuesEscape)
    {
        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        limits.Validate();
        IQpdfInput input = new EmptyPdfInput(path);
        return await OpenInputDocument(input, path, limits, governor, diagnostics, recovery, cancellationToken, valuesEscape).ConfigureAwait(false);
    }

    internal static ValueTask<QpdfDocument> OpenCore(
        IQpdfHost host,
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery,
        CancellationToken cancellationToken)
        => OpenCore(host, path, limits, governor, diagnostics, recovery, cancellationToken, true);

    internal static async ValueTask<QpdfDocument> OpenCore(
        IQpdfHost host,
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery,
        CancellationToken cancellationToken,
        bool valuesEscape)
    {
        IQpdfInput input = await GovernedHost.OpenInputAsync(host, path, governor, cancellationToken).ConfigureAwait(false);
        return await OpenInputDocument(input, path, limits, governor, diagnostics, recovery, cancellationToken, valuesEscape).ConfigureAwait(false);
    }

    private static async ValueTask<QpdfDocument> OpenInputDocument(
        IQpdfInput input,
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery,
        CancellationToken cancellationToken,
        bool valuesEscape)
    {
        DocumentEngine? engine = null;
        XRefTable? table = null;
        ParserMemoryScope? pageStorage = null;
        List<PageLocation>? pages = null;
        byte[]? bytes = null;
        ReadOnlyMemory<byte> data = default;
        long reservedInput = 0L;
        try
        {
            if (input.Length > limits.MaxInputBytes)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, limits.MaxInputBytes);
            }

            if (input.Length > int.MaxValue)
            {
                throw new QpdfLimitException(nameof(QpdfLimits.MaxInputBytes), input.Length, int.MaxValue);
            }

            if (input.Length == 0L)
            {
                throw new QpdfParseException("Input is empty.", 0L, null);
            }

            long inputBytes = checked(input.Length + GovernedBufferStream.ArrayOverheadBytes);
            governor.ReserveMemory(inputBytes);
            reservedInput = inputBytes;
            bytes = await QpdfInputReader.ReadExactlyAsync(input, 0L, (int)input.Length, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            data = bytes;
            VersionParts headerParts = ParseHeader(data);
            cancellationToken.ThrowIfCancellationRequested();
            table = XRefTable.Parse(data, limits, governor, diagnostics, recovery, valuesEscape);
            engine = DocumentEngine.CreateOwned(data, table, limits, governor, diagnostics, recovery, reservedInput, valuesEscape);
            table = null;
            reservedInput = 0L;
            cancellationToken.ThrowIfCancellationRequested();
            RejectEncrypted(path, engine);
            VersionParts? effectiveParts = ResolveEffectiveVersion(engine);
            pageStorage = ParserMemoryScope.Create(governor);
            pages = ListPages(engine, diagnostics, governor, limits, pageStorage, cancellationToken);
            // Exclusive JSON emits components as text and never exports this
            // document or a version alias. Public/shared readers own carriers.
            PdfVersion? headerVersion = valuesEscape ? governor.OwnVersion(headerParts.Major, headerParts.Minor) : null;
            PdfVersion? version = valuesEscape && effectiveParts.HasValue
                ? governor.OwnVersion(effectiveParts.Value.Major, effectiveParts.Value.Minor) : headerVersion;
            QpdfDocument document = new(input, engine, governor, diagnostics, pages, pageStorage, path, limits, version, headerVersion, headerParts, effectiveParts ?? headerParts);
            pageStorage = null;
            return document;
        }
        catch
        {
            bytes = null;
            data = default;
            pages = null;
            pageStorage?.Dispose();
            engine?.Dispose();
            table?.Dispose();
            governor.ReleaseMemory(reservedInput);
            await input.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Returns the object, resolving references. Free or deleted entries yield
    /// <see cref="PdfNull" />; unknown numbers throw
    /// <see cref="QpdfParseException" />.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The number is negative.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public QpdfObject GetObject(int objectNumber)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(QpdfDocument));
        }

        if (objectNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(objectNumber), "Object numbers must not be negative.");
        }

        return Engine.Load(objectNumber);
    }

    /// <summary>Releases the input and reader cache storage; safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            IQpdfInput? input = _input;
            DocumentEngine? engine = _engine;
            ParserMemoryScope? pageStorage = _pageStorage;
            _input = null;
            _engine = null;
            _pages = null;
            _inspectionPages = null;
            _pageStorage = null;
            try
            {
                if (input is not null) { await input.DisposeAsync().ConfigureAwait(false); }
            }
            finally
            {
                try { engine?.Dispose(); }
                finally
                {
                    try { pageStorage?.Dispose(); }
                    finally { _governor.FinishDocumentCounters(); }
                }
            }
        }
    }
    private static VersionParts ParseHeader(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> prefix = [(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-'];
        if (data.Length < 8 || !data.Span.Slice(0, 5).SequenceEqual(prefix))
        {
            throw new QpdfParseException("Input does not start with '%PDF-'.", 0L, null);
        }

        ReadOnlySpan<byte> span = data.Span;
        if (!IsDigit(span[5]) || span[6] != (byte)'.' || !IsDigit(span[7]))
        {
            throw new QpdfParseException("Malformed PDF header version.", 0L, null);
        }

        int major = span[5] - (byte)'0';
        int minor = span[7] - (byte)'0';
        if ((major == 1 && minor > 7) || (major == 2 && minor != 0) || (major != 1 && major != 2))
        {
            throw new QpdfParseException($"Unsupported PDF version {major}.{minor}.", 0L, null);
        }

        return new(major, minor);
    }

    private static bool IsDigit(byte b)
    {
        return b >= (byte)'0' && b <= (byte)'9';
    }

    private static VersionParts? ResolveEffectiveVersion(DocumentEngine engine)
    {
        PdfName rootKey = new([(byte)'R', (byte)'o', (byte)'o', (byte)'t']);
        if (!engine.Trailer.TryGet(rootKey, out QpdfObject rootRaw))
        {
            return null;
        }

        PdfDictionary root;
        if (rootRaw is PdfDictionary direct)
        {
            root = direct;
        }
        else if (rootRaw is PdfReference reference)
        {
            if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? resolved) || resolved is not PdfDictionary resolvedRoot)
            {
                return null;
            }

            root = resolvedRoot;
        }
        else
        {
            return null;
        }

        if (ReadType(root).Length != 0 && ReadType(root) != "Catalog")
        {
            return null;
        }

        PdfName versionKey = new([(byte)'V', (byte)'e', (byte)'r', (byte)'s', (byte)'i', (byte)'o', (byte)'n']);
        if (!root.TryGet(versionKey, out QpdfObject raw) || raw is PdfNull)
        {
            return null;
        }

        if (raw is PdfReference versionReference)
        {
            if (!((IIndirectResolver)engine).TryResolve(versionReference, out QpdfObject? versionResolved) || versionResolved is null)
            {
                throw new QpdfParseException("Unresolvable catalog /Version reference.", null, versionReference.ObjectNumber);
            }

            raw = versionResolved;
            if (raw is PdfNull)
            {
                return null;
            }
        }

        if (raw is not PdfName versionName)
        {
            throw new QpdfParseException("Catalog /Version must be a name.", null, null);
        }

        return ParseVersionName(versionName.DisplayText);
    }

    private static VersionParts ParseVersionName(string text)
    {
        if (text is null || text.Length != 3 || text[1] != (char)46)
        {
            throw new QpdfParseException($"Malformed catalog /Version /{text}.", null, null);
        }

        if (text[0] < (char)48 || text[0] > (char)57 || text[2] < (char)48 || text[2] > (char)57)
        {
            throw new QpdfParseException($"Malformed catalog /Version /{text}.", null, null);
        }

        int major = text[0] - (char)48;
        int minor = text[2] - (char)48;
        if ((major == 1 && minor > 7) || (major == 2 && minor != 0) || (major != 1 && major != 2))
        {
            throw new QpdfParseException($"Unsupported PDF version {major}.{minor}.", null, null);
        }

        return new(major, minor);
    }

    private static void RejectEncrypted(QpdfPath path, DocumentEngine engine)
    {
        foreach (PdfDictionary trailer in engine.Trailers)
        {
            if (TrailerDeclaresEncryption(trailer, engine))
            {
                throw new QpdfEncryptedInputException(path);
            }
        }
    }

    private static bool TrailerDeclaresEncryption(PdfDictionary trailer, DocumentEngine engine)
    {
        PdfName encryptKey = new([(byte)'E', (byte)'n', (byte)'c', (byte)'r', (byte)'y', (byte)'p', (byte)'t']);
        if (!trailer.TryGet(encryptKey, out QpdfObject raw) || raw is PdfNull)
        {
            return false;
        }

        if (raw is PdfReference reference)
        {
            if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? unresolved))
            {
                throw new QpdfParseException("Unresolvable trailer /Encrypt reference.", null, reference.ObjectNumber);
            }

            if (unresolved is null)
            {
                throw new QpdfParseException("Unresolvable trailer /Encrypt reference.", null, reference.ObjectNumber);
            }

            QpdfObject resolved = unresolved;
            if (resolved is PdfNull)
            {
                return false;
            }
        }

        return true;
    }

    private static List<PageLocation> ListPages(
        DocumentEngine engine,
        DiagnosticBag diagnostics,
        ResourceGovernor governor,
        QpdfLimits limits,
        ParserMemoryScope pageStorage,
        CancellationToken cancellationToken)
    {
        PdfDictionary pages = ReadPageRoot(engine);
        List<PageLocation> collector = ParserContainers.CreateList<PageLocation>(pageStorage);
        using ParserMemoryScope traversal = ParserMemoryScope.Create(governor);
        HashSet<int> path = ParserContainers.CreateSet<int>(traversal);
        WalkPages(engine, diagnostics, governor, limits, pages, null, path, collector, pageStorage, traversal, 0, cancellationToken);
        return collector;
    }

    internal long ReadDeclaredPageCount()
    {
        _governor.CheckCancellation();
        _governor.ChargeWork(4L);
        PdfDictionary pages = ReadPageRoot(Engine);
        if (pages.TryGet(PdfName.FromAscii("Count"), out QpdfObject count) && count is PdfInteger integer)
        {
            return integer.Value;
        }
        _diagnostics.Add(QpdfDiagnosticCode.PageCountMismatch, QpdfDiagnosticSeverity.Warning, "/Pages /Count is not an integer; reporting zero.");
        return 0L;
    }

    private static PdfDictionary ReadPageRoot(DocumentEngine engine)
    {
        PdfName rootKey = new([(byte)'R', (byte)'o', (byte)'o', (byte)'t']);
        if (!engine.Trailer.TryGet(rootKey, out QpdfObject rootRaw))
        {
            throw new QpdfParseException("Trailer must declare /Root.", null, null);
        }

        PdfDictionary root = ResolveDictionary(engine, rootRaw, "catalog");
        string rootType = ReadType(root);
        PdfDictionary pages;
        if (rootType == "Pages")
        {
            pages = root;
        }
        else
        {
            if (rootType.Length != 0 && rootType != "Catalog")
            {
                throw new QpdfParseException($"Catalog has unexpected /Type /{rootType}.", null, null);
            }

            PdfName pagesKey = new([(byte)'P', (byte)'a', (byte)'g', (byte)'e', (byte)'s']);
            if (!root.TryGet(pagesKey, out QpdfObject pagesRaw))
            {
                throw new QpdfParseException("Catalog must declare /Pages.", null, null);
            }

            pages = ResolveDictionary(engine, pagesRaw, "page tree");
        }

        return pages;
    }

    private static PdfDictionary ResolveDictionary(DocumentEngine engine, QpdfObject raw, string role)
    {
        if (raw is PdfDictionary direct)
        {
            return direct;
        }

        if (raw is PdfReference reference)
        {
            if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? unresolved))
            {
                throw new QpdfParseException($"Unresolvable {role} reference.", null, reference.ObjectNumber);
            }

            if (unresolved is null)
            {
                throw new QpdfParseException($"Unresolvable {role} reference.", null, reference.ObjectNumber);
            }

            QpdfObject resolved = unresolved;
            if (resolved is PdfDictionary dictionary)
            {
                return dictionary;
            }

            throw new QpdfParseException($"The {role} reference does not resolve to a dictionary.", null, reference.ObjectNumber);
        }

        throw new QpdfParseException($"The {role} must be a dictionary or a reference.", null, null);
    }

    private static string ReadType(PdfDictionary dictionary)
    {
        PdfName typeKey = new([(byte)'T', (byte)'y', (byte)'p', (byte)'e']);
        if (!dictionary.TryGet(typeKey, out QpdfObject raw) || raw is PdfNull)
        {
            return string.Empty;
        }

        if (raw is not PdfName name)
        {
            throw new QpdfParseException("The /Type entry must be a name.", null, null);
        }

        return name.DisplayText;
    }

    private static void WalkPages(
        DocumentEngine engine,
        DiagnosticBag diagnostics,
        ResourceGovernor governor,
        QpdfLimits limits,
        PdfDictionary node,
        PdfReference? incoming,
        HashSet<int> path,
        List<PageLocation> collector,
        ParserMemoryScope pageStorage,
        ParserMemoryScope traversal,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > limits.MaxNestingDepth)
        {
            throw new QpdfParseException($"Page tree depth exceeds {limits.MaxNestingDepth}.", null, null);
        }

        governor.ChargeWork(1L);
        string type = ReadType(node);
        if (type == "Page")
        {
            ParserContainers.AddItem(pageStorage, collector, new PageLocation(incoming, node), 2L * IntPtr.Size);
            return;
        }

        if (type != "Pages")
        {
            throw new QpdfParseException("Page tree nodes must have /Type /Page or /Pages.", null, null);
        }

        PdfName kidsKey = new([(byte)'K', (byte)'i', (byte)'d', (byte)'s']);
        if (!node.TryGet(kidsKey, out QpdfObject kidsRaw) || kidsRaw is not PdfArray kids)
        {
            throw new QpdfParseException("Pages node must declare a /Kids array.", null, null);
        }

        long before = collector.Count;
        for (int i = 0; i < kids.Count; i++)
        {
            QpdfObject kid = kids[i];
            if (kid is PdfReference reference)
            {
                if (!ParserContainers.AddSetItem(traversal, path, reference.ObjectNumber, sizeof(int)))
                {
                    throw new QpdfParseException($"Page tree cycle at object {reference.ObjectNumber}.", null, reference.ObjectNumber);
                }

                if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? kidResolved) || kidResolved is not PdfDictionary child)
                {
                    throw new QpdfParseException($"Page kid does not resolve to a dictionary.", null, reference.ObjectNumber);
                }

                WalkPages(engine, diagnostics, governor, limits, child, reference, path, collector, pageStorage, traversal, depth + 1, cancellationToken);
                path.Remove(reference.ObjectNumber);
                continue;
            }

            if (kid is PdfDictionary direct)
            {
                WalkPages(engine, diagnostics, governor, limits, direct, null, path, collector, pageStorage, traversal, depth + 1, cancellationToken);
                continue;
            }

            throw new QpdfParseException("Page /Kids elements must be dictionaries or references.", null, null);
        }

        PdfName countKey = new([(byte)'C', (byte)'o', (byte)'u', (byte)'n', (byte)'t']);
        if (node.TryGet(countKey, out QpdfObject countRaw) && countRaw is PdfInteger declared)
        {
            long total = collector.Count - before;
            if (declared.Value != total && diagnostics.ReportPageCountMismatch)
            {
                diagnostics.AddParts(
                    QpdfDiagnosticCode.PageCountMismatch,
                    QpdfDiagnosticSeverity.Warning,
                    ["A /Pages /Count of ", declared.Value, " disagrees with traversal (", total, "); traversal wins."]);
            }
        }
    }
}

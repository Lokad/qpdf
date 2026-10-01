using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

/// <summary>
/// Strict inspection handle over a single-section classic-xref PDF. Holds its
/// input lease until disposed. Xref streams, hybrid and incremental structures
/// arrive in M2; recovery arrives in M4.
/// </summary>
public sealed class QpdfDocument : IAsyncDisposable
{
    private readonly IQpdfInput _input;
    private readonly DocumentEngine _engine;
    private readonly ResourceGovernor _governor;
    private readonly DiagnosticBag _diagnostics;
    private readonly List<PageLocation> _pages;
    private bool _disposed;

    private QpdfDocument(
        IQpdfInput input,
        DocumentEngine engine,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        List<PageLocation> pages,
        QpdfPath path,
        QpdfLimits limits,
        PdfVersion version,
        PdfVersion headerVersion)
    {
        _input = input;
        _engine = engine;
        _governor = governor;
        _diagnostics = diagnostics;
        _pages = pages;
        Path = path;
        Limits = limits;
        Version = version;
        HeaderVersion = headerVersion;
    }

    /// <summary>Gets the logical input path.</summary>
    public QpdfPath Path { get; }

    /// <summary>Gets the effective budgets.</summary>
    public QpdfLimits Limits { get; }

    /// <summary>Gets the effective version: catalog /Version overrides the header.</summary>
    public PdfVersion Version { get; }

    /// <summary>Gets the version from the file header.</summary>
    public PdfVersion HeaderVersion { get; }

    /// <summary>Gets the page count from page-tree traversal.</summary>
    public int PageCount => _pages.Count;

    /// <summary>Gets a live snapshot of the job counters.</summary>
    public QpdfExecutionCounters Counters => _governor.Snapshot();

    /// <summary>Gets the warnings recorded while opening (duplicate keys, count mismatches).</summary>
    public IReadOnlyList<QpdfDiagnostic> Warnings => [.. _diagnostics.Entries];

    internal DocumentEngine Engine => _engine;

    internal IReadOnlyList<PageLocation> PageReferences => _pages;

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
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        return await OpenCore(host, path, limits, governor, diagnostics, QpdfRecoveryMode.Strict, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<QpdfDocument> OpenCore(
        IQpdfHost host,
        QpdfPath path,
        QpdfLimits limits,
        ResourceGovernor governor,
        DiagnosticBag diagnostics,
        QpdfRecoveryMode recovery,
        CancellationToken cancellationToken)
    {
        IQpdfInput input = await host.OpenInputAsync(path, cancellationToken).ConfigureAwait(false);
        governor.NoteHostOperation();
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

            governor.ReserveMemory(input.Length);
            byte[] bytes = await QpdfInputReader.ReadExactlyAsync(input, 0L, (int)input.Length, cancellationToken).ConfigureAwait(false);
            governor.NoteHostOperation();
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlyMemory<byte> data = bytes;
            PdfVersion headerVersion = ParseHeader(data);
            cancellationToken.ThrowIfCancellationRequested();
            XRefTable table = XRefTable.Parse(data, limits, governor, diagnostics, recovery);
            DocumentEngine engine = new(data, table, limits, governor, diagnostics);
            cancellationToken.ThrowIfCancellationRequested();
            RejectEncrypted(path, engine);
            PdfVersion version = ResolveEffectiveVersion(headerVersion, engine);
            List<PageLocation> pages = ListPages(engine, diagnostics, governor, limits, cancellationToken);
            return new QpdfDocument(input, engine, governor, diagnostics, pages, path, limits, version, headerVersion);
        }
        catch
        {
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

        return _engine.Load(objectNumber);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _input.DisposeAsync().ConfigureAwait(false);
        }
    }
    private static PdfVersion ParseHeader(ReadOnlyMemory<byte> data)
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

        return new PdfVersion(major, minor);
    }

    private static bool IsDigit(byte b)
    {
        return b >= (byte)'0' && b <= (byte)'9';
    }

    private static PdfVersion ResolveEffectiveVersion(PdfVersion header, DocumentEngine engine)
    {
        PdfName rootKey = new([(byte)'R', (byte)'o', (byte)'o', (byte)'t']);
        if (!engine.Trailer.TryGet(rootKey, out QpdfObject rootRaw))
        {
            return header;
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
                return header;
            }

            root = resolvedRoot;
        }
        else
        {
            return header;
        }

        if (ReadType(root).Length != 0 && ReadType(root) != "Catalog")
        {
            return header;
        }

        PdfName versionKey = new([(byte)'V', (byte)'e', (byte)'r', (byte)'s', (byte)'i', (byte)'o', (byte)'n']);
        if (!root.TryGet(versionKey, out QpdfObject raw) || raw is PdfNull)
        {
            return header;
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
                return header;
            }
        }

        if (raw is not PdfName versionName)
        {
            throw new QpdfParseException("Catalog /Version must be a name.", null, null);
        }

        return ParseVersionName(versionName.DisplayText);
    }

    private static PdfVersion ParseVersionName(string text)
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

        return new PdfVersion(major, minor);
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
        CancellationToken cancellationToken)
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

        List<PageLocation> collector = new();
        WalkPages(engine, diagnostics, governor, limits, pages, null, new HashSet<int>(), collector, 0, cancellationToken);
        return collector;
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
            collector.Add(new PageLocation(incoming, node));
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
                if (!path.Add(reference.ObjectNumber))
                {
                    throw new QpdfParseException($"Page tree cycle at object {reference.ObjectNumber}.", null, reference.ObjectNumber);
                }

                if (!((IIndirectResolver)engine).TryResolve(reference, out QpdfObject? kidResolved) || kidResolved is not PdfDictionary child)
                {
                    throw new QpdfParseException($"Page kid does not resolve to a dictionary.", null, reference.ObjectNumber);
                }

                WalkPages(engine, diagnostics, governor, limits, child, reference, path, collector, depth + 1, cancellationToken);
                path.Remove(reference.ObjectNumber);
                continue;
            }

            if (kid is PdfDictionary direct)
            {
                WalkPages(engine, diagnostics, governor, limits, direct, null, path, collector, depth + 1, cancellationToken);
                continue;
            }

            throw new QpdfParseException("Page /Kids elements must be dictionaries or references.", null, null);
        }

        PdfName countKey = new([(byte)'C', (byte)'o', (byte)'u', (byte)'n', (byte)'t']);
        if (node.TryGet(countKey, out QpdfObject countRaw) && countRaw is PdfInteger declared)
        {
            long total = collector.Count - before;
            if (declared.Value != total)
            {
                diagnostics.Add(
                    QpdfDiagnosticCode.PageCountMismatch,
                    QpdfDiagnosticSeverity.Warning,
                    $"A /Pages /Count of {declared.Value} disagrees with traversal ({total}); traversal wins.");
            }
        }
    }
}


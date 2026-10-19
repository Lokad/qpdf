namespace Lokad.Qpdf.Runtime;

/// <summary>Stores admitted diagnostic records under one private or caller owner.</summary>
internal sealed class DiagnosticBag
{
    private readonly ResourceGovernor _governor;
    private List<QpdfDiagnostic>? _entries;
    private RetainedStorage.Receipt? _storage;
    private bool _suppressWarnings;

    internal DiagnosticBag(ResourceGovernor governor)
    { _governor = governor ?? throw new ArgumentNullException(nameof(governor)); }

    internal bool ReportPageCountMismatch { get; set; } = true;
    internal bool HasNonWarningEntries { get; private set; }
    internal bool HasAcceptedWarnings { get; private set; }
    internal bool ReadingPartialInput { get; set; }
    internal bool SuppressWarningStorage
    {
        set
        {
            _suppressWarnings = value;
            if (value) { _entries?.RemoveAll(static item => item.Severity == QpdfDiagnosticSeverity.Warning); }
        }
    }

    private bool Suppress(QpdfDiagnosticSeverity severity)
    {
        if (severity != QpdfDiagnosticSeverity.Warning || !_suppressWarnings) { return false; }
        HasAcceptedWarnings = true;
        return true;
    }


    internal IReadOnlyList<QpdfDiagnostic> Entries
    {
        get
        {
            if (_storage is not null && (!_governor.OwnsDocumentCounters || _governor.DocumentAccepted))
            { _governor.PromoteAllocation(_storage); }
            return _entries is null ? Array.Empty<QpdfDiagnostic>() : _entries;
        }
    }

    private RetainedStorage.Receipt Storage
        => _storage ?? throw new InvalidOperationException("Diagnostic storage is unavailable.");
    private static long ArrayBytes(int capacity) => capacity == 0 ? 0L : checked(IntPtr.Size * (3L + capacity));

    private void EnsureStorage()
    {
        if (_entries is not null) { return; }
        _governor.ReserveMemory(32L);
        try
        {
            List<QpdfDiagnostic> entries = new();
            RetainedStorage.Receipt storage = _governor.RetainAllocation(32L);
            _entries = entries; _storage = storage;
        }
        catch { _governor.ReleaseMemory(32L); throw; }
    }

    private void EnsureCapacity()
    {
        List<QpdfDiagnostic> entries = _entries ?? throw new InvalidOperationException("Missing diagnostic storage.");
        if (entries.Count < entries.Capacity) { return; }
        int capacity = (int)Math.Min(Array.MaxLength, Math.Max(1L, entries.Capacity * 2L));
        if (capacity <= entries.Count) { throw new QpdfParseException("Diagnostic storage exceeds supported capacity.", null, null); }
        long previous = ArrayBytes(entries.Capacity), next = ArrayBytes(capacity);
        _governor.ReserveMemory(next);
        try
        {
            entries.Capacity = capacity;
            _governor.GrowAllocation(Storage, next - previous);
            _governor.ReleaseMemory(previous);
        }
        catch { _governor.ReleaseMemory(next); throw; }
    }

    internal void Add(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        AddParts(code, severity, [message]);
    }

    internal void AddParts(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, ReadOnlySpan<DiagnosticPart> parts)
    {
        if (ReadingPartialInput) { throw new CompleteInputRequiredException(); }
        _governor.CheckCancellation();
        if (Suppress(severity)) { return; }
        DiagnosticTextInfo info = DiagnosticMessage.Check(_governor, parts);
        using GovernedText message = GovernedText.Create(_governor, parts, info);
        Accept(code, severity, message, false);
    }

    internal void AddOwned(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, GovernedText message)
    {
        message.CheckOwner(_governor);
        if (ReadingPartialInput) { throw new CompleteInputRequiredException(); }
        _governor.CheckCancellation();
        if (Suppress(severity)) { return; }
        Accept(code, severity, message, true);
    }

    private void Accept(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, GovernedText message, bool exposed)
    {
        if (ReadingPartialInput) { throw new CompleteInputRequiredException(); }
        message.CheckOwner(_governor); _governor.CheckCancellation();
        _governor.CheckDiagnostic(message.Utf8Bytes);
        bool existed = _entries is not null;
        bool reserved = false;
        try
        {
            EnsureStorage(); EnsureCapacity();
            _governor.ReserveMemory(32L); reserved = true;
            QpdfDiagnostic diagnostic = new(code, severity, message.Value);
            _governor.GrowAllocation(Storage, checked(32L + message.StringReservation));
            _governor.ReserveDiagnostic(message.Utf8Bytes);
            _entries?.Add(diagnostic);
            // AddOwned may receive a string whose Value was already borrowed.
            if (exposed) { _governor.PromoteAllocation(Storage); }
            message.TransferToRetainedStorage(_governor); reserved = false;
            HasNonWarningEntries |= severity != QpdfDiagnosticSeverity.Warning;
            HasAcceptedWarnings |= severity == QpdfDiagnosticSeverity.Warning;
        }
        catch
        {
            if (reserved) { _governor.ReleaseMemory(32L); }
            if (!existed && _storage is not null)
            {
                _governor.DropAllocation(_storage); _storage = null; _entries = null;
            }
            throw;
        }
    }
}

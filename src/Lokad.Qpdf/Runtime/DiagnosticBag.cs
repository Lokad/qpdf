namespace Lokad.Qpdf.Runtime;

/// <summary>Admits diagnostic storage and preserves independently escaping messages.</summary>
internal sealed class DiagnosticBag
{
    private readonly ResourceGovernor _governor;
    private List<QpdfDiagnostic>? _entries;
    private RetainedStorage.Receipt? _storage;
    private DiagnosticStage? _stage;

    internal DiagnosticBag(ResourceGovernor governor)
    {
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
    }

    internal bool ReportPageCountMismatch { get; set; } = true;
    internal bool HasNonWarningEntries { get; private set; }
    internal bool SuppressWarningStorage { get; set; }
    internal bool HasAcceptedWarnings { get; private set; }
    internal bool ReadingPartialInput { get; set; }

    internal IReadOnlyList<QpdfDiagnostic> Entries
    {
        get
        {
            _stage?.Flush();
            if (_storage is not null && (!_governor.OwnsDocumentCounters || _governor.DocumentAccepted))
            { _governor.PromoteAllocation(_storage); }
            return _entries is null ? Array.Empty<QpdfDiagnostic>() : _entries;
        }
    }

    // Exclusive JSON has no diagnostic aliases until it exports its result.
    // An already exposed list keeps its live append semantics instead.
    internal DiagnosticStage? BeginStaging()
    {
        if (_stage is not null) { throw new InvalidOperationException("Diagnostic staging cannot be nested."); }
        if (_entries is not null) { return null; }
        return _stage = DiagnosticStage.Create(this, _governor);
    }

    internal void EndStaging(DiagnosticStage stage)
    {
        if (ReferenceEquals(_stage, stage)) { _stage = null; }
    }

    internal int EntryCapacity => _entries?.Capacity ?? 0;
    internal IReadOnlyList<QpdfDiagnostic> PromotedEntries
        => _entries is null ? Array.Empty<QpdfDiagnostic>() : _entries;

    private RetainedStorage.Receipt Storage
        => _storage ?? throw new InvalidOperationException("Diagnostic storage is unavailable.");

    // Reference arrays have two header words and an aligned length word;
    // the empty list borrows shared storage. Direct BCL allocation probes
    // cover one-slot lists and subsequent capacity growth.
    private static long ArrayBytes(int capacity) => capacity == 0 ? 0L : checked(IntPtr.Size * (3L + capacity));

    private void EnsureStorage()
    {
        if (_entries is not null) { return; }
        _governor.ReserveMemory(32L);
        try
        {
            List<QpdfDiagnostic> entries = new();
            RetainedStorage.Receipt storage = _governor.RetainAllocation(entries, 32L);
            _entries = entries;
            _storage = storage;
        }
        catch { _governor.ReleaseMemory(32L); throw; }
    }

    private void EnsureCapacity()
    {
        List<QpdfDiagnostic> entries = _entries ?? throw new InvalidOperationException("Diagnostic storage is unavailable.");
        if (entries.Count < entries.Capacity) { return; }
        // Many documents keep only one or two warnings. Avoid four-slot slack
        // on every retained document; larger bags still grow geometrically.
        int capacity = (int)Math.Min(Array.MaxLength, Math.Max(1L, entries.Capacity * 2L));
        if (capacity <= entries.Count) { throw new QpdfLimitException(nameof(QpdfLimits.MaxDiagnostics), entries.Count + 1L, entries.Count); }
        long previousBytes = ArrayBytes(entries.Capacity);
        long nextBytes = ArrayBytes(capacity);
        _governor.ChargeWork(checked(entries.Count + (long)capacity));
        _governor.CheckCancellation();
        _governor.ReserveMemory(nextBytes);
        try { entries.Capacity = capacity; }
        catch { _governor.ReleaseMemory(nextBytes); throw; }
        _governor.GrowAllocation(Storage, nextBytes - previousBytes);
        _governor.ReleaseMemory(previousBytes);
    }

    private void AbandonNewStorage(bool existed)
    {
        if (existed || _entries is null) { return; }
        List<QpdfDiagnostic> entries = _entries;
        RetainedStorage.Receipt storage = Storage;
        _entries = null;
        _storage = null;
        _governor.DropAllocation(entries, storage);
    }

    internal void Add(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, string message)
    {
        if (ReadingPartialInput) { throw new CompleteInputRequiredException(); }
        ArgumentNullException.ThrowIfNull(message);
        long utf8Bytes = CheckMessage(message, out int messageHash, out bool ascii);
        if (_stage is DiagnosticStage stage)
        {
            stage.Append(code, severity, message, messageHash, ascii, utf8Bytes);
            _governor.ReserveDiagnostic(utf8Bytes);
            HasAcceptedWarnings |= severity == QpdfDiagnosticSeverity.Warning;
            return;
        }
        long messageBytes = GovernedText.StringBytes(message.Length);
        string? shared = message.Length == 0 ? string.Empty : _governor.FindDiagnosticMessage(message, messageHash);
        long payloadBytes = checked(32L + (shared is null ? messageBytes : 0L));
        bool existed = _entries is not null;
        bool reserved = false;
        try
        {
            EnsureStorage();
            EnsureCapacity();
            _governor.ReserveMemory(payloadBytes);
            reserved = true;
            string owned = shared ?? string.Create(message.Length, (message, _governor), static (target, state) =>
            {
                for (int offset = 0; offset < target.Length; offset += Math.Min(256, target.Length - offset))
                {
                    int count = Math.Min(256, target.Length - offset);
                    state._governor.ChargeWork(count);
                    state._governor.CheckCancellation();
                    state.message.AsSpan(offset, count).CopyTo(target.Slice(offset, count));
                }
            });
            QpdfDiagnostic diagnostic = new(code, severity, owned);
            Accept(diagnostic, payloadBytes, utf8Bytes, shared is null, messageHash, true);
            reserved = false;
        }
        catch
        {
            if (reserved) { _governor.ReleaseMemory(payloadBytes); }
            AbandonNewStorage(existed);
            throw;
        }
    }

    internal void AddOwned(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, GovernedText message)
    {
        message.CheckOwner(_governor);
        _governor.CheckDiagnostic(message.Utf8Bytes);
        _stage?.Flush();
        bool existed = _entries is not null;
        bool reserved = false;
        try
        {
            EnsureStorage();
            EnsureCapacity();
            _governor.ReserveMemory(32L);
            reserved = true;
            long payloadBytes = checked(32L + message.StringReservation);
            QpdfDiagnostic diagnostic = new(code, severity, message.Value);
            // A caller may already hold GovernedText.Value independently of
            // this bag. Accept both estimates into the caller's diagnostic batch.
            Accept(diagnostic, payloadBytes, message.Utf8Bytes, false, 0, true);
            message.TransferToRetainedStorage(_governor);
            reserved = false;
        }
        catch
        {
            if (reserved) { _governor.ReleaseMemory(32L); }
            AbandonNewStorage(existed);
            throw;
        }
    }

    internal void AddParts(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, ReadOnlySpan<DiagnosticPart> parts)
    {
        if (ReadingPartialInput) { throw new CompleteInputRequiredException(); }
        DiagnosticTextInfo info = DiagnosticMessage.Check(_governor, parts);
        if (_stage is DiagnosticStage stage)
        {
            stage.AppendParts(code, severity, parts, info);
            _governor.ReserveDiagnostic(info.Utf8Bytes);
            HasAcceptedWarnings |= severity == QpdfDiagnosticSeverity.Warning;
            return;
        }
        string? shared = info.Length == 0 ? string.Empty : _governor.FindDiagnosticMessage(parts, info);
        if (shared is not null)
        {
            AddSharedText(code, severity, shared, info.Utf8Bytes);
            return;
        }
        using GovernedText text = GovernedText.Create(_governor, parts, info);
        AddOrdinaryText(code, severity, text, info.Hash, true);
    }

    private void AddSharedText(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, string shared, long utf8Bytes)
    {
        bool existed = _entries is not null;
        bool reserved = false;
        try
        {
            EnsureStorage();
            EnsureCapacity();
            _governor.ReserveMemory(32L);
            reserved = true;
            QpdfDiagnostic diagnostic = new(code, severity, shared);
            Accept(diagnostic, 32L, utf8Bytes, false, 0, true);
            reserved = false;
        }
        catch
        {
            if (reserved) { _governor.ReleaseMemory(32L); }
            AbandonNewStorage(existed);
            throw;
        }
    }

    internal void AddStaged(DiagnosticStage.Entry entry, GovernedText message)
        => AddOrdinaryText(entry.Code, entry.Severity, message, entry.Hash, false);

    private void AddOrdinaryText(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, GovernedText message, int messageHash, bool countDiagnostic)
    {
        message.CheckOwner(_governor);
        string? shared = message.Value.Length == 0 ? string.Empty : _governor.FindDiagnosticMessage(message.Value, messageHash);
        bool existed = _entries is not null;
        bool reserved = false;
        try
        {
            EnsureStorage();
            EnsureCapacity();
            _governor.ReserveMemory(32L);
            reserved = true;
            long payloadBytes = checked(32L + (shared is null ? message.StringReservation : 0L));
            QpdfDiagnostic diagnostic = new(code, severity, shared ?? message.Value);
            // A staged entry already committed its count/bytes. New formatted
            // ordinary text follows the same ownership path and charges once.
            Accept(diagnostic, payloadBytes, countDiagnostic ? message.Utf8Bytes : 0L, shared is null, messageHash, countDiagnostic);
            if (shared is null) { message.TransferToRetainedStorage(_governor); }
            reserved = false;
        }
        catch
        {
            if (reserved) { _governor.ReleaseMemory(32L); }
            AbandonNewStorage(existed);
            throw;
        }
    }

    private void Accept(QpdfDiagnostic diagnostic, long payloadBytes, long utf8Bytes, bool cacheMessage, int messageHash, bool countDiagnostic)
    {
        List<QpdfDiagnostic>? discardedWarnings = SuppressWarningStorage && !HasNonWarningEntries
            && diagnostic.Severity != QpdfDiagnosticSeverity.Warning ? _entries : null;
        if (discardedWarnings is not null && discardedWarnings.Count != 0)
        { _governor.ChargeWork(discardedWarnings.Count); _governor.CheckCancellation(); }
        long retained = cacheMessage
            ? _governor.RetainCachedDiagnostic(diagnostic, payloadBytes, messageHash)
            : _governor.RetainDiagnostic(diagnostic, payloadBytes);
        // Before the first visible record, suppression can borrow the empty
        // failure history. Once a visible record arrives, sever the private
        // warning references while keeping the caller's coarse batch charged.
        discardedWarnings?.Clear();
        // Count/byte preflight already passed and no intervening diagnostic can
        // enter this single-owner bag. The remaining handoff cannot allocate.
        if (countDiagnostic) { _governor.ReserveDiagnostic(utf8Bytes); }
        if (!SuppressWarningStorage || !HasNonWarningEntries || diagnostic.Severity != QpdfDiagnosticSeverity.Warning)
        { _entries?.Add(diagnostic); }
        HasNonWarningEntries |= diagnostic.Severity != QpdfDiagnosticSeverity.Warning;
        HasAcceptedWarnings |= diagnostic.Severity == QpdfDiagnosticSeverity.Warning;
        _governor.ReleaseMemory(payloadBytes - retained);
    }

    private long CheckMessage(string message, out int messageHash, out bool ascii)
    {
        DiagnosticTextInfo info = DiagnosticMessage.Check(_governor, [message]);
        messageHash = info.Hash;
        ascii = info.Ascii;
        return info.Utf8Bytes;
    }
}

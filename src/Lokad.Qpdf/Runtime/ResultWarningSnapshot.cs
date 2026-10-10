namespace Lokad.Qpdf.Runtime;

/// <summary>Admits immutable warning carriers at the shared publication boundary.</summary>
internal sealed class ResultWarningSnapshot : IDisposable
{
    private ResourceGovernor? _governor;
    private DiagnosticBag? _diagnostics;
    private ParserMemoryScope? _memory;
    private IReadOnlyList<QpdfDiagnostic>? _warnings;
    private readonly CancellationToken _token;

    private ResultWarningSnapshot(ResourceGovernor governor, DiagnosticBag diagnostics,
        ParserMemoryScope memory, CancellationToken token)
    { _governor = governor; _diagnostics = diagnostics; _memory = memory; _token = token; }

    internal static ResultWarningSnapshot? Create(ResourceGovernor governor, DiagnosticBag diagnostics,
        CancellationToken token)
    {
        // Command outcomes consume the already governed diagnostic history via
        // ExecutionPresentation; they never expose a typed Warnings collection.
        if (!governor.HasTypedResult) { return null; }
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(56L);
            ResultWarningSnapshot snapshot = new(governor, diagnostics, memory, token);
            governor.AttachResultWarnings(snapshot);
            return snapshot;
        }
        catch { memory.Dispose(); throw; }
    }

    internal IReadOnlyList<QpdfDiagnostic> Warnings => _warnings
        ?? throw new InvalidOperationException("Result warnings have not been prepared.");

    internal static IReadOnlyList<QpdfDiagnostic> Read(ResultWarningSnapshot? snapshot, DiagnosticBag diagnostics)
        => snapshot?.Warnings ?? diagnostics.Entries;

    internal void Prepare()
    {
        ResourceGovernor governor = _governor ?? throw new ObjectDisposedException(nameof(ResultWarningSnapshot));
        IReadOnlyList<QpdfDiagnostic> source = (_diagnostics
            ?? throw new ObjectDisposedException(nameof(ResultWarningSnapshot))).Entries;
        if (_warnings is not null && _warnings.Count == source.Count) { return; }
        if (source.Count == 0) { _warnings = Array.Empty<QpdfDiagnostic>(); return; }
        using OwnedAllocationBatch storage = OwnedAllocationBatch.Create(governor, 2, _token);
        IReadOnlyList<QpdfDiagnostic> warnings = ResultCollectionStorage.Copy(storage, source, IntPtr.Size);
        storage.RetainWithPrepaidRetirement();
        _warnings = warnings;
    }

    public void Dispose()
    {
        ResourceGovernor? governor = _governor;
        if (governor is null) { return; }
        governor.DetachResultWarnings(this);
        _governor = null; _diagnostics = null; _warnings = null;
        ParserMemoryScope? memory = _memory; _memory = null; memory?.Dispose();
    }
}

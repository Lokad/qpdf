using System.Collections.ObjectModel;

namespace Lokad.Qpdf.Runtime;

/// <summary>Owns a prepared projection and every fresh record/text allocation.</summary>
internal sealed class DiagnosticProjection : IDisposable
{
    private readonly record struct Owned(object Carrier, RetainedValueOwners.Receipt Receipt);
    private ResourceGovernor? _governor;
    private ParserMemoryScope? _memory;
    private Owned[]? _owned;
    private ReadOnlyCollection<QpdfDiagnostic>? _diagnostics;
    private int _ownedCount;
    internal readonly int SourceCount;
    internal readonly long OutputBytes, DecodedBytes, PeakBytes;
    internal readonly bool HasWarning, HasError;

    private DiagnosticProjection(ResourceGovernor governor, ParserMemoryScope memory, Owned[] owned,
        int sourceCount, long output, long decoded, long peak, bool warning, bool error)
    { _governor = governor; _memory = memory; _owned = owned; SourceCount = sourceCount; OutputBytes = output; DecodedBytes = decoded; PeakBytes = peak; HasWarning = warning; HasError = error; }

    internal static DiagnosticProjection Create(ResourceGovernor governor, IReadOnlyList<QpdfDiagnostic> source,
        bool noWarn, bool verbose, bool memoryUsage, bool progress, long output, long decoded, long peak)
        => CreateCore(governor, source, noWarn, false, verbose, memoryUsage, progress, output, decoded, peak);

    internal static DiagnosticProjection CreateWarningHistory(ResourceGovernor governor, IReadOnlyList<QpdfDiagnostic> source)
        => CreateCore(governor, source, false, true, false, false, false, 0L, 0L, 0L);

    private static DiagnosticProjection CreateCore(ResourceGovernor governor, IReadOnlyList<QpdfDiagnostic> source,
        bool noWarn, bool warningsOnly, bool verbose, bool memoryUsage, bool progress, long output, long decoded, long peak)
    {
        int summaries = (verbose ? 1 : 0) + (memoryUsage ? 1 : 0) + (progress && output > 0L ? 4 : 0);
        int count = 0;
        bool warning = false, error = false;
        foreach (QpdfDiagnostic diagnostic in source)
        {
            governor.ChargeWork(1L); governor.CheckCancellation();
            warning |= diagnostic.Severity == QpdfDiagnosticSeverity.Warning;
            error |= diagnostic.Severity == QpdfDiagnosticSeverity.Error;
            if ((!noWarn || diagnostic.Severity != QpdfDiagnosticSeverity.Warning)
                && (!warningsOnly || diagnostic.Severity == QpdfDiagnosticSeverity.Warning)) { count++; }
        }
        count = checked(count + summaries);
        if (count > Array.MaxLength) { throw new QpdfParseException("Diagnostic projection exceeds supported capacity.", null, null); }
        int capacity = checked(2 + 2 * summaries);
        governor.ChargeWork(checked(1L + capacity + count));
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        DiagnosticProjection? projection = null;
        try
        {
            memory.Reserve(88L + 24L + 16L * capacity);
            projection = new(governor, memory, new Owned[capacity], source.Count, output, decoded, peak, warning, error);
            memory.Reserve(count == 0 ? 0L : checked(24L + IntPtr.Size * (long)count));
            QpdfDiagnostic[] entries = count == 0 ? Array.Empty<QpdfDiagnostic>() : new QpdfDiagnostic[count];
            if (count != 0) { projection.OwnScoped(entries, checked(24L + IntPtr.Size * (long)count)); }
            int index = 0;
            foreach (QpdfDiagnostic diagnostic in source)
            {
                governor.ChargeWork(1L); governor.CheckCancellation();
                if ((!noWarn || diagnostic.Severity != QpdfDiagnosticSeverity.Warning)
                    && (!warningsOnly || diagnostic.Severity == QpdfDiagnosticSeverity.Warning)) { entries[index++] = diagnostic; }
            }
            if (verbose)
            {
                entries[index++] = projection.Format(QpdfDiagnosticCode.VerboseSummary,
                    ["Verbose summary: ", output, " output bytes, ", decoded, " decoded bytes, peak ", peak, " bytes."]);
            }
            if (memoryUsage)
            { entries[index++] = projection.Format(QpdfDiagnosticCode.MemoryUsage, ["Peak memory usage: ", peak, " bytes."]); }
            if (progress && output > 0L)
            {
                for (int step = 1; step <= 4; step++)
                {
                    governor.ChargeWork(1L); governor.CheckCancellation();
                    entries[index++] = projection.Format(QpdfDiagnosticCode.ProgressReport, ["Write progress: ", step * 25, "% of ", output, " bytes."]);
                }
            }
            memory.Reserve(24L);
            ReadOnlyCollection<QpdfDiagnostic> view = Array.AsReadOnly(entries);
            projection.OwnScoped(view, 24L);
            projection._diagnostics = view;
            return projection;
        }
        catch { if (projection is null) { memory.Dispose(); } else { projection.Dispose(); } throw; }
    }

    private ResourceGovernor Governor => _governor ?? throw new ObjectDisposedException(nameof(DiagnosticProjection));
    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(DiagnosticProjection));

    private void OwnScoped(object carrier, long bytes)
    {
        Governor.ChargeWork(1L);
        RetainedValueOwners.Receipt receipt = Memory.RetainAllocationReceipt(carrier, bytes);
        (_owned ?? throw new ObjectDisposedException(nameof(DiagnosticProjection)))[_ownedCount++] = new(carrier, receipt);
    }

    private QpdfDiagnostic Format(QpdfDiagnosticCode code, ReadOnlySpan<DiagnosticPart> parts)
    {
        DiagnosticTextInfo info = DiagnosticMessage.Measure(Governor, parts);
        using GovernedText text = GovernedText.Create(Governor, parts, info);
        Memory.Reserve(32L);
        QpdfDiagnostic diagnostic = new(code, QpdfDiagnosticSeverity.Info, text.Value);
        Governor.ChargeWork(1L);
        RetainedValueOwners.Receipt receipt = Governor.RetainAllocation(text.Value, text.StringReservation);
        (_owned ?? throw new ObjectDisposedException(nameof(DiagnosticProjection)))[_ownedCount++] = new(text.Value, receipt);
        text.TransferToRetainedStorage(Governor);
        OwnScoped(diagnostic, 32L);
        return diagnostic;
    }

    internal IReadOnlyList<QpdfDiagnostic> Transfer()
    {
        ReadOnlyCollection<QpdfDiagnostic> result = _diagnostics ?? throw new ObjectDisposedException(nameof(DiagnosticProjection));
        _diagnostics = null;
        _governor = null;
        Owned[]? owned = _owned; _owned = null; _ownedCount = 0;
        if (owned is not null) { Array.Clear(owned); }
        ParserMemoryScope? memory = _memory; _memory = null; memory?.Dispose();
        return result;
    }

    public void Dispose()
    {
        ResourceGovernor? governor = _governor;
        Owned[]? owned = _owned;
        _diagnostics = null; _owned = null; _governor = null;
        if (governor is not null && owned is not null)
        { for (int i = _ownedCount - 1; i >= 0; i--) { governor.DropAllocation(owned[i].Carrier, owned[i].Receipt); } }
        if (owned is not null) { Array.Clear(owned); }
        _ownedCount = 0;
        ParserMemoryScope? memory = _memory; _memory = null; memory?.Dispose();
    }
}

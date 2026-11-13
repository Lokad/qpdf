using Lokad.Qpdf.Operations;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    /// <summary>Counts pages with strict parsing and default budgets, using bounded input when supported.</summary>
    public static ValueTask<QpdfPageCountResult> CountPagesAsync(IQpdfHost host, QpdfPath inputPath, CancellationToken cancellationToken)
        => CountPagesAsync(host, inputPath, QpdfLimits.Default, cancellationToken);

    /// <summary>Counts pages with strict parsing and explicit budgets, using bounded input when supported.</summary>
    public static ValueTask<QpdfPageCountResult> CountPagesAsync(IQpdfHost host, QpdfPath inputPath, QpdfLimits limits, CancellationToken cancellationToken)
        => CountPagesAsync(host, inputPath, null, QpdfRecoveryMode.Strict, limits, cancellationToken);

    /// <summary>Counts validated pages with an input credential, explicit recovery and budgets.</summary>
    /// <remarks>Compact classic-xref inputs can omit large unrelated objects. Unsupported input layouts,
    /// encrypted inputs and uncertain parsing use the complete reader on the same lease. Memory refusal
    /// fails without changing storage strategy. Public document opening remains a separate operation.</remarks>
    public static async ValueTask<QpdfPageCountResult> CountPagesAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPassword? password, QpdfRecoveryMode recovery, QpdfLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(limits);
        if (recovery is not (QpdfRecoveryMode.Strict or QpdfRecoveryMode.BoundedRepair))
        { throw new ArgumentOutOfRangeException(nameof(recovery)); }
        limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(QpdfPageCountResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            int count = checked((int)await PageCountInspection.ReadAsync(host, inputPath, password, recovery, limits,
                governor, diagnostics, false, cancellationToken).ConfigureAwait(false));
            IReadOnlyList<QpdfDiagnostic> warnings;
            using (OwnedAllocationBatch storage = OwnedAllocationBatch.Create(governor, cancellationToken))
            {
                warnings = ResultCollectionStorage.Copy(storage, diagnostics.Entries, IntPtr.Size);
                storage.RetainResult();
            }
            QpdfPageCountResult result = new(count, warnings, governor.TypedCounters);
            governor.AcceptTypedResult(); governor.FinishTypedCounters(); return result;
        }
        finally { governor.AbandonTypedCounters(); }
    }
}

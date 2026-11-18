using Lokad.Qpdf.Operations;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    /// <summary>Creates a PDF from plaintext qpdf JSON with explicit layout, protection and budgets.</summary>
    /// <remarks>Preserve requires JSON without an encryption declaration. Other policies protect plaintext values;
    /// template credentials are borrowed until completion. Physical layout uses the shared PDF writers.</remarks>
    public static async ValueTask<QpdfJsonImportResult> CreatePdfFromJsonAsync(IQpdfHost host, QpdfPath jsonPath,
        QpdfPath outputPath, QpdfJsonImportOptions options, QpdfOutputEncryption outputEncryption,
        QpdfLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(outputEncryption); ArgumentNullException.ThrowIfNull(limits); limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(QpdfJsonImportResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            JsonImportOutcome result = await JsonExecution.ImportCreateCoreAsync(host, jsonPath, outputPath,
                null, null, false, false, options.ObjectStreams, options.Linearize, outputEncryption, new(false, false), QpdfRecoveryMode.Strict,
                limits, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            QpdfJsonImportResult presented = result.Present(governor);
            governor.FinishTypedCounters(); return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }

    /// <summary>Updates an authenticated PDF from qpdf JSON with explicit layout, protection and budgets.</summary>
    /// <remarks>Preserve authenticates the primary handler; other policies replace it. Credentials are borrowed
    /// until completion. Object-stream preservation applies only to matching source objects.</remarks>
    public static async ValueTask<QpdfJsonImportResult> UpdatePdfFromJsonAsync(IQpdfHost host, QpdfPath inputPath,
        QpdfPath jsonPath, QpdfPath outputPath, QpdfJsonImportOptions options, QpdfPassword? password,
        QpdfOutputEncryption outputEncryption, QpdfLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(outputEncryption); ArgumentNullException.ThrowIfNull(limits); limits.Validate();
        ResourceGovernor governor = new(limits, cancellationToken, host);
        governor.PrepareTypedResult(QpdfJsonImportResult.StorageBytes);
        try
        {
            DiagnosticBag diagnostics = new(governor);
            JsonImportOutcome result = await JsonExecution.ImportUpdateCoreAsync(host, inputPath, jsonPath, outputPath,
                null, null, false, false, options.ObjectStreams, options.Linearize, password, outputEncryption, new(false, false),
                limits, QpdfRecoveryMode.Strict, governor, diagnostics, cancellationToken).ConfigureAwait(false);
            QpdfJsonImportResult presented = result.Present(governor);
            governor.FinishTypedCounters(); return presented;
        }
        finally { governor.AbandonTypedCounters(); }
    }
}

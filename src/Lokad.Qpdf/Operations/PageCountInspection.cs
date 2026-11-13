using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Operations;

internal static class PageCountInspection
{
    // A page count never exposes the document or its synchronous object resolver.
    // Speculation and fallback finish on the same opened lease before it closes.
    internal static async ValueTask<long> ReadAsync(IQpdfHost host, QpdfPath path, QpdfPassword? password,
        QpdfRecoveryMode recovery, QpdfLimits limits, ResourceGovernor governor, DiagnosticBag diagnostics,
        bool declaredCount, CancellationToken token)
    {
        IQpdfInput input = await GovernedHost.OpenInputAsync(host, path, governor, token).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                bool retryParse = false;
                try
                {
                    await using QpdfDocument document = await QpdfDocument.OpenPageCountInputAsync(input, path, limits,
                        governor, diagnostics, recovery, attempt == 0, password, token).ConfigureAwait(false);
                    retryParse = document.Engine.HasSelectiveInput;
                    if (retryParse && declaredCount && !document.HasConsistentDeclaredPageCount())
                    { throw new CompleteInputRequiredException(); }
                    document.Engine.AcceptSelectiveInput(); retryParse = false;
                    return declaredCount ? document.ReadDeclaredPageCount() : document.PageCount;
                }
                catch (CompleteInputRequiredException) when (attempt == 0) { }
                catch (QpdfParseException) when (attempt == 0 && retryParse) { }
            }
        }
        finally
        {
            try { await input.DisposeAsync().ConfigureAwait(false); }
            finally { governor.FinishDocumentCounters(); }
        }
    }
}

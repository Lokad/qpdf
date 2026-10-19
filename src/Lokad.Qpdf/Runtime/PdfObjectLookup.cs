using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Runtime;

/// <summary>Bounds borrowed indirect resolution at both sides of the resolver call.</summary>
internal static class PdfObjectLookup
{
    internal static bool TryResolve(PdfReference reference, IReadOnlyDictionary<int, QpdfObject> pool,
        ResourceGovernor governor, CancellationToken token, out QpdfObject? value)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        bool found = pool.TryGetValue(reference.ObjectNumber, out QpdfObject? resolved);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        value = resolved; return found;
    }

    internal static bool TryResolve(PdfReference reference, IIndirectResolver resolver,
        ResourceGovernor governor, CancellationToken token, out QpdfObject? value)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        bool found = resolver.TryResolve(reference, out QpdfObject? resolved);
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        value = resolved; return found;
    }
}

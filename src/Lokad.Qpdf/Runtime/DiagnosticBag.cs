using System.Text;

namespace Lokad.Qpdf.Runtime;

/// <summary>
/// Bounded diagnostic sink. Every entry reserves count and message bytes from
/// the job governor before it is recorded; overflow fails explicitly.
/// </summary>
internal sealed class DiagnosticBag
{
    private readonly ResourceGovernor _governor;
    private readonly List<QpdfDiagnostic> _entries = new();

    internal DiagnosticBag(ResourceGovernor governor)
    {
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
    }

    internal void Add(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, string message)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        long bytes = Encoding.UTF8.GetByteCount(message);
        _governor.ReserveDiagnostic(bytes);
        _entries.Add(new QpdfDiagnostic(code, severity, message));
    }

    internal IReadOnlyList<QpdfDiagnostic> Entries => _entries;

    internal bool ReportPageCountMismatch { get; set; } = true;

    internal void AddOwned(QpdfDiagnosticCode code, QpdfDiagnosticSeverity severity, GovernedText message)
    {
        message.CheckOwner(_governor);
        _governor.ReserveDiagnostic(message.Utf8Bytes);
        _entries.Add(new QpdfDiagnostic(code, severity, message.Value));
        message.TransferToDiagnostic(_governor);
    }
}

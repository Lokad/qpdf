namespace Lokad.Qpdf.Probe;

internal sealed class ProbeException : Exception
{
    internal ProbeException(string message, int exitCode)
        : base(message)
    {
        ExitCode = exitCode;
    }

    internal int ExitCode { get; }
}

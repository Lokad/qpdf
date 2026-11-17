using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Probe;

internal readonly record struct ProbeOutput(string Logical, string Physical)
{
    internal static bool TryParse(string mapping, out ProbeOutput output)
    {
        int equals = mapping.IndexOf('=');
        if (equals <= 0 || equals + 1 >= mapping.Length) { output = default; return false; }
        output = new(mapping[..equals], mapping[(equals + 1)..]); return true;
    }

    internal static ProbeOutput ParseCompanion(string mapping)
        => TryParse(mapping, out ProbeOutput output) ? output
            : throw new ProbeException("Invalid --out mapping (" + mapping + "). Expected <logical>=<physical>.", 1);

    internal void Export(byte[] content, string label)
    {
        string? directory = Path.GetDirectoryName(Physical);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
        File.WriteAllBytes(Physical, content);
        Console.WriteLine($"{label}: output {Logical} {content.Length} bytes -> {Physical}");
    }

    internal int CompleteCompanion(InMemoryQpdfHost host, QpdfExecutionResult result, string label)
    {
        Console.WriteLine($"{label}: status={result.Status} exit={(int)result.ExitCode}");
        foreach (QpdfDiagnostic diagnostic in result.Diagnostics)
        { Console.WriteLine($"{label}: diagnostic {diagnostic.Code} {diagnostic.Severity} {diagnostic.Message}"); }
        if (!host.TryGetOutput(QpdfPath.From(Logical), out byte[] content))
        { throw new ProbeException("Library produced no output (" + Logical + "). status=" + result.Status + " exit=" + (int)result.ExitCode + ".", 2); }
        Export(content, label);
        Console.WriteLine($"{label}: counters decoded={result.Counters.TotalDecodedBytes} peakmem={result.Counters.PeakMemoryBytes}");
        return result.Status == QpdfExecutionStatus.Canceled ? 4 : result.Status == QpdfExecutionStatus.Completed ? 0 : 2;
    }
}

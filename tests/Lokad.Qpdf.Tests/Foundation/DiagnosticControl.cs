using System.Reflection;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

/// <summary>Gives a surviving-graph control the same observable warning effects.</summary>
internal static class DiagnosticControl
{
    internal static DiagnosticBag Read(QpdfDocument document)
        => typeof(QpdfDocument).GetField("_diagnostics", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(document)
            as DiagnosticBag ?? throw new InvalidOperationException("Expected document diagnostics.");

    internal static void Replay(DiagnosticBag control, IReadOnlyList<QpdfDiagnostic> recorded)
    {
        int previous = control.Entries.Count;
        Assert.InRange(previous, 0, recorded.Count);
        for (int index = 0; index < previous; index++)
        {
            Assert.Equal(recorded[index].Code, control.Entries[index].Code);
            Assert.Equal(recorded[index].Severity, control.Entries[index].Severity);
            Assert.Equal(recorded[index].Message, control.Entries[index].Message);
        }
        for (int index = previous; index < recorded.Count; index++)
        {
            QpdfDiagnostic diagnostic = recorded[index];
            control.Add(diagnostic.Code, diagnostic.Severity, diagnostic.Message);
        }
    }
}

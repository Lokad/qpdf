using System.Reflection;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

// Reader/parser controls observe their existing job independently. Reading the
// public snapshot property would allocate/charge memory and work under test;
// concurrent and canceled observation must also avoid mutating the governor.
internal static class CounterObservation
{
    internal static QpdfExecutionCounters Snapshot(QpdfDocument document)
        => ((ResourceGovernor)(typeof(QpdfDocument).GetField("_governor", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(document) ?? throw new InvalidOperationException("Missing reader governor."))).Snapshot();
}

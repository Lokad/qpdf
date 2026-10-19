using System.Globalization;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class LinearizationReportFormattingTests
{
    private static LinearizationData Data()
        => new(100L, 7L, 50L, 2, 90L, 11, 12, 0L, 10L, 5L,
            new PageOffsetTable(1L, 20L, 0, 3L, 0, 0L, 0, 0L, 0, 0, 0, 0, 4,
                [new(2L, 20L, 0L, 20L, [4], [0])]),
            new SharedObjectTable(6, 20L, 1, 1, 0, 10L, 0, [new(10L, true, 2L)]),
            new OutlineTable(9, 20L, 1, 10L));

    private static ClassifiedGraph Graph()
        => new([1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22], [],
            [new[] { 40, 41 }, Array.Empty<int>(), new[] { 42, 44 }],
            [int.MaxValue, 4], [], [], [], [], false);

    [Fact]
    public void ReportPreservesAdjustedOffsetsRangeGroupsAndRunBoundaryAdjacency()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        LinearizationReport.Write("input.pdf", Data(), Graph(), governor, diagnostics, CancellationToken.None);
        string[] lines = diagnostics.Entries.Select(entry => entry.Message).ToArray();
        Assert.Contains("first_page_offset: 25", lines);
        Assert.Contains("first_shared_offset: 25", lines);
        Assert.Contains("first_object_offset: 25", lines);
        Assert.Contains("  signature present", lines);
        Assert.Contains("  nobjects: 2", lines);
        int part4 = Array.IndexOf(lines, "part 4 (root, document open):");
        Assert.Equal(" 1-2, 4, 6, 8, 10, 12, 14, 16, 18, 20", lines[part4 + 1]);
        Assert.Equal(" 22", lines[part4 + 2]);
        int part7 = Array.IndexOf(lines, "part 7 (other pages, private):");
        Assert.Equal(" 40-42, 44", lines[part7 + 1]);
        int part8 = Array.IndexOf(lines, "part 8 (other pages, shared):");
        Assert.Equal(" 2147483647, 4", lines[part8 + 1]);
        Assert.All(diagnostics.Entries, entry => Assert.Equal(QpdfDiagnosticCode.LinearizationReport, entry.Code));
        Assert.Equal(diagnostics.Entries.Count, governor.Snapshot().DiagnosticCount);
    }

    [Fact]
    public void ReportNumbersStayInvariantWithAnUnusuallyLongCultureSign()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = new string('N', 1024);
        CultureInfo.CurrentCulture = culture;
        try
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            DiagnosticBag diagnostics = new(governor);
            LinearizationData data = Data() with { FileSize = long.MinValue };
            LinearizationReport.Write("input.pdf", data, Graph(), governor, diagnostics, CancellationToken.None);
            Assert.Contains(diagnostics.Entries, entry => entry.Message == "file_size: -9223372036854775808");
            Assert.DoesNotContain(diagnostics.Entries, entry => entry.Message.Contains(culture.NumberFormat.NegativeSign, StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void PreparedLongHeaderRejectsItsQuotaBeforeCreatingFormattedText()
    {
        string prepared = new('P', 10000);
        LinearizationData data = Data();
        ClassifiedGraph graph = Graph();
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnosticBytes = 128L };
        _ = Reject(prepared, data, graph, limits);
        long allocated = Reject(prepared, data, graph, limits);
        Assert.InRange(allocated, 0L, 4096L);
    }

    private static long Reject(string input, LinearizationData data, ClassifiedGraph graph, QpdfLimits limits)
    {
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() =>
            LinearizationReport.Write(input, data, graph, governor, diagnostics, CancellationToken.None));
        long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(nameof(QpdfLimits.MaxDiagnosticBytes), failure.LimitName);
        Assert.Empty(diagnostics.Entries);
        Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        return allocation;
    }

    [Fact]
    public void FailedHeaderAdmissionPreservesPriorAcceptedWarning()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnosticBytes = 128L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        diagnostics.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, new string('W', 10));
        QpdfDiagnostic prior = diagnostics.Entries[0];
        long live = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => LinearizationReport.Write(new string('P', 10000), Data(), Graph(),
            governor, diagnostics, CancellationToken.None));
        Assert.Same(prior, Assert.Single(diagnostics.Entries));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
    }

    [Fact]
    public void ExplicitCancellationStopsBeforeReportStorageIsCreated()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            LinearizationReport.Write("input.pdf", Data(), Graph(), governor, diagnostics, cancellation.Token));
        Assert.Empty(diagnostics.Entries);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

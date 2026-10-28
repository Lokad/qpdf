using System.Collections;
using System.Reflection;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class FailureDiagnosticAccessTests
{
    private sealed class ReadMeter
    {
        internal int Reads, CountReads, Limit;
        internal void Reset(int limit) { Reads = CountReads = 0; Limit = limit; }
        internal void Read()
        {
            if (++Reads > Limit) { throw new Xunit.Sdk.XunitException("Diagnostic access exceeded its linear read allowance."); }
        }
    }

    private sealed class CountedList(IReadOnlyList<QpdfDiagnostic> source, ReadMeter meter) : IReadOnlyList<QpdfDiagnostic>
    {
        public int Count { get { meter.CountReads++; return source.Count; } }
        public QpdfDiagnostic this[int index] { get { meter.Read(); return source[index]; } }
        public IEnumerator<QpdfDiagnostic> GetEnumerator()
        { for (int index = 0; index < source.Count; index++) { meter.Read(); yield return source[index]; } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Observe borrowed list reads without adding a production test hook. A
    // direct array result needs no instrumentation and satisfies the same tests.
    private static void CheckAccess(IReadOnlyList<QpdfDiagnostic> view, QpdfDiagnostic[] expected)
    {
        ReadMeter meter = new(); meter.Reset(int.MaxValue);
        List<(FieldInfo Field, IReadOnlyList<QpdfDiagnostic> Original)> fields = [];
        foreach (FieldInfo field in view.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (field.FieldType == typeof(IReadOnlyList<QpdfDiagnostic>)
                && field.GetValue(view) is IReadOnlyList<QpdfDiagnostic> source)
            { fields.Add((field, source)); field.SetValue(view, new CountedList(source, meter)); }
        }
        try
        {
            meter.Reset(0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long sum = 0;
            for (int repeat = 0; repeat < 1000; repeat++) { sum += view.Count; }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(expected.LongLength * 1000L, sum);
            Assert.Equal(0, meter.CountReads);
            Assert.InRange(allocated, 0L, 256L);

            meter.Reset(2);
            Assert.Same(expected[^1], view[expected.Length - 1]);

            meter.Reset(3 * expected.Length);
            before = GC.GetAllocatedBytesForCurrentThread();
            int count = 0;
            foreach (QpdfDiagnostic item in view)
            {
                if (!ReferenceEquals(expected[count++], item)) { throw new Xunit.Sdk.XunitException("Diagnostic identity/order changed."); }
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(expected.Length, count);
            Assert.InRange(allocated, 0L, 512L);

            meter.Reset(3 * expected.Length);
            for (int index = 0; index < view.Count; index++) { Assert.Same(expected[index], view[index]); }
        }
        finally { foreach (var field in fields) { field.Field.SetValue(view, field.Original); } }
        Assert.Throws<ArgumentOutOfRangeException>(() => view[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => view[view.Count]);
    }

    [Theory]
    [InlineData(false, false, 64)]
    [InlineData(false, true, 64)]
    [InlineData(true, false, 64)]
    [InlineData(true, true, 64)]
    [InlineData(false, false, 1024)]
    [InlineData(false, true, 1024)]
    [InlineData(true, false, 1024)]
    [InlineData(true, true, 1024)]
    public async Task PublicInspectionFailuresHaveConstantCountsAndLinearTraversal(bool job, bool noWarn, int extra)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnostics = 4096L, MaxDiagnosticBytes = 1048576L };
        List<(int, string, byte[]?)> objects = CheckTests.SinglePage("<< /Length 4 >>", "q Q\n"u8.ToArray());
        for (int index = 0; index < extra; index++) { objects.Add((6 + index, "null", null)); }
        QpdfExecutionResult result;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), CheckTests.Assemble(objects, 1), QpdfInputOwnership.Copy);
            result = job
                ? await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"check\":\"\",\"showXref\":\"\",\"showObject\":\"4\",\"rawStreamData\":\"\",\""
                    + (noWarn ? "noWarn" : "reportMemoryUsage") + "\":\"\"}", new QpdfExecutionOptions(limits), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--check", "--show-xref", "--show-object=4", "--raw-stream-data",
                    noWarn ? "--no-warn" : "--report-memory-usage"]), new QpdfExecutionOptions(limits), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(0, host.OpenLeaseCount);
            Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.Null(result.OutputBytes);
        }
        QpdfDiagnostic[] expected = result.Diagnostics.ToArray();
        Assert.Equal(extra + 6, expected.Length);
        Assert.All(expected[..^1], item => Assert.Equal(QpdfDiagnosticCode.XrefListing, item.Code));
        Assert.Equal("Binary stream output cannot follow informational output in this inspection.", expected[^1].Message);
        CheckAccess(result.Diagnostics, expected);
    }

    public static IEnumerable<object[]> HistoryShapes()
    {
        foreach (string flags in new[] { "plain", "summaries", "no-warn" })
        foreach (string shape in new[] { "terminal", "prefix", "filtered-prefix", "short-prefix", "canceled" })
        { yield return [flags, shape]; }
    }

    [Theory]
    [MemberData(nameof(HistoryShapes))]
    public void FailureShapesKeepIdentityOrderAndHistoryUnderExhaustion(string flags, string shape)
    {
        using CancellationTokenSource cancel = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 65536L };
        ResourceGovernor governor = new(limits, cancel.Token);
        DiagnosticBag bag = new(governor);
        bag.Add(QpdfDiagnosticCode.DuplicateKey, QpdfDiagnosticSeverity.Warning, "first warning");
        bag.Add(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, "information");
        bag.Add(QpdfDiagnosticCode.DuplicateKey, QpdfDiagnosticSeverity.Warning, "second warning");
        bag.Add(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, "same error text");
        bool noWarn = flags == "no-warn", warningOnly = flags == "plain";
        using ExecutionPresentation? presentation = ExecutionPresentation.Create(governor, bag, flags == "summaries",
            false, false, noWarn, false, CancellationToken.None);
        Assert.NotNull(presentation);
        QpdfDiagnostic terminal = new(QpdfDiagnosticCode.CheckFailed, QpdfDiagnosticSeverity.Error, "same error text");
        QpdfDiagnostic[] prefix = shape is "prefix" or "filtered-prefix" or "short-prefix"
            ? bag.Entries.Where(item => shape != "filtered-prefix" || !noWarn || item.Severity != QpdfDiagnosticSeverity.Warning).ToArray()
            : [];
        if (shape == "short-prefix")
        {
            bag.Add(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, "later information");
            bag.Add(QpdfDiagnosticCode.DuplicateKey, QpdfDiagnosticSeverity.Warning, "later warning");
        }
        QpdfDiagnostic[] tail = shape == "canceled" ? [] : [terminal];
        QpdfDiagnostic[] original = [.. prefix, .. tail];
        bool Visible(QpdfDiagnostic item) => !noWarn || item.Severity != QpdfDiagnosticSeverity.Warning;
        QpdfDiagnostic[] history = bag.Entries.Where(item => Visible(item) && (!warningOnly || item.Severity == QpdfDiagnosticSeverity.Warning)).ToArray();
        QpdfDiagnostic[] expected = [.. history, .. original.Where(item => Visible(item) && !history.Any(prior => ReferenceEquals(prior, item)))];
        using ParserMemoryScope blocker = ParserMemoryScope.Create(governor);
        blocker.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes);
        cancel.Cancel(); // Freezing a failure is bounded cleanup, including after refusal/cancellation.
        ExecutionOutcome outcome = shape == "canceled" ? ExecutionOutcome.Canceled(original) : ExecutionOutcome.Failed(original);
        IReadOnlyList<QpdfDiagnostic> view = presentation.Failure(outcome).Diagnostics;
        Assert.Equal(expected.Length, view.Count);
        Assert.Equal(expected.Length, view.Count());
        for (int index = 0; index < expected.Length; index++) { Assert.Same(expected[index], view[index]); }
        Assert.Equal(expected, view.ToArray());
        if (shape != "canceled") { Assert.Same(terminal, view[^1]); }
        Assert.Throws<ArgumentOutOfRangeException>(() => view[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => view[view.Count]);
        // Dispose the controller before re-reading the borrowed immutable result.
        blocker.Dispose(); presentation.Dispose();
        Assert.Equal(expected, view.ToArray());
    }
}

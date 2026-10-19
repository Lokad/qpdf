using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class TypedCounterOwnershipTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");
    private static readonly string[] Routes = ["typed-page", "typed-page-rotation", "typed-pages", "typed-pages-rotation",
        "typed-pages-options", "typed-check", "typed-json", "typed-import", "typed-update", "typed-extract",
        "typed-add", "typed-remove", "typed-copy"];

    private readonly record struct Values(long Live, long Peak, long Decoded, long OutputBytes,
        long Calls, long Handles, long Diagnostics);

    private static Values State(QpdfExecutionCounters c) => new(c.LiveMemoryBytes, c.PeakMemoryBytes,         c.TotalDecodedBytes, c.TotalOutputBytes, c.HostOperations, c.OpenHandles, c.DiagnosticCount);

    private sealed class Alias(ResourceGovernor governor, object? value, WeakReference host, WeakReference result,
        WeakReference counters, WeakReference prepared, Values? snapshot)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal object? Value = value;
        internal readonly WeakReference Host = host;
        internal readonly WeakReference Result = result;
        internal readonly WeakReference Counters = counters;
        internal readonly WeakReference Prepared = prepared;
        internal readonly Values? Snapshot = snapshot;
    }

    public static IEnumerable<object[]> PublicJobs()
    {
        foreach (object[] row in ExecutionCounterTests.Jobs())
        {
            if (!((string)row[0]).StartsWith("typed-", StringComparison.Ordinal)) { continue; }
            yield return [.. row];
        }
    }

    public static IEnumerable<object[]> SuccessAliases()
    {
        foreach (string route in Routes)
        foreach (bool compressed in new[] { false, true })
        foreach (string kind in new[] { "result", "counters", "copy" }) { yield return [route, compressed, kind]; }
    }

    public static IEnumerable<object[]> BudgetJobs()
    {
        foreach (string route in Routes)
        foreach (bool compressed in new[] { false, true })
        { yield return [route, compressed]; }
    }

    public static IEnumerable<object[]> MetadataJobs()
    {
        foreach (string route in Routes)
        foreach (bool compressed in new[] { false, true }) { yield return [route, compressed]; }
    }

    public static IEnumerable<object[]> PrecanceledJobs()
    {
        foreach (string route in Routes)
        { yield return [route]; }
    }

    private sealed class CountingHost(IQpdfHost inner) : IQpdfHost
    {
        internal int Calls;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        { Calls++; return inner.OpenInputAsync(path, token); }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Calls++; return inner.CreateOutputAsync(path, token); }
    }

    [Theory]
    [MemberData(nameof(PrecanceledJobs))]
    [Trait("Layer", "Contract")]
    public async Task PrecanceledTypedJobsRejectBeforeHostAccess(string route)
    {
        object fixture = await Prepare(route, false, "none", "success");
        CountingHost host = new((IQpdfHost)fixture);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        QpdfLimits limits = QpdfLimits.Default;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Start(host, route, limits, cancellation.Token));
        Assert.Equal(0, host.Calls);
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Read(fixture, "Inner");
        Assert.Equal(0L, inner.OpenLeaseCount);
        Assert.True(inner.TryGetOutput(Output, out byte[] prior));
        Assert.Equal(new byte[] { 17, 23, 41 }, prior);
    }

    private static object Read(object value, string property) => value.GetType().GetProperty(property, Members)?.GetValue(value)
        ?? throw new InvalidOperationException("Missing complete-job fixture property: " + property);

    private static async Task<object> Prepare(string route, bool compressed, string boundary, string outcome)
    {
        Task task = (Task)(typeof(ExecutionCounterTests).GetMethod("Prepare", Members)?.Invoke(null, [route, compressed, boundary, outcome])
            ?? throw new InvalidOperationException("Missing original complete-job fixture."));
        await task;
        return Read(task, "Result");
    }

    private static ResourceGovernor FindGovernor(Task task) => (ResourceGovernor)(typeof(ExecutionCounterTests)
        .GetMethod("FindGovernor", Members)?.Invoke(null, [task, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
        ?? throw new InvalidOperationException("Missing actual public governor."));

    private static QpdfAttachmentSpec Attachment() => new("attachment.bin", "payload", "payload.bin", null, null, null, null, false);

    private static async Task<object> Start(IQpdfHost host, string route, QpdfLimits limits, CancellationToken token)
    {
        PdfPageSource[] sources = [new(Input, new PdfPageSelectionOptions(1)), new(QpdfPath.From("second.pdf"), new PdfPageSelectionOptions(1))];
        if (route == "typed-page") { return await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), limits, token); }
        if (route == "typed-page-rotation") { return await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), [new PdfPageRotation(PdfRotationMode.Set, 90, [1])], limits, token); }
        if (route == "typed-pages") { return await Qpdf.SelectPagesAsync(host, sources, Output, limits, token); }
        if (route == "typed-pages-rotation") { return await Qpdf.SelectPagesAsync(host, sources, Output, [new PdfPageRotation(PdfRotationMode.Set, 90, [1])], limits, token); }
        if (route == "typed-pages-options") { return await Qpdf.SelectPagesAsync(host, sources, Output, new PdfPageMergeOptions(), limits, token); }
        if (route == "typed-check") { return await Qpdf.CheckAsync(host, Input, new QpdfCheckOptions(), limits, token); }
        if (route == "typed-json") { return await Qpdf.GetDocumentJsonAsync(host, Input, Output, new QpdfJsonOptions(2, []), limits, token); }
        if (route == "typed-import") { return await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("input.json"), Output, limits, token); }
        if (route == "typed-update") { return await Qpdf.UpdatePdfFromJsonAsync(host, Input, QpdfPath.From("input.json"), Output, limits, token); }
        if (route == "typed-extract") { return await Qpdf.ExtractAttachmentAsync(host, Input, "payload", Output, limits, token); }
        if (route == "typed-add") { return await Qpdf.AddAttachmentsAsync(host, Input, Output, [Attachment()], limits, token); }
        if (route == "typed-remove") { return await Qpdf.RemoveAttachmentsAsync(host, Input, Output, ["payload"], limits, token); }
        return await Qpdf.CopyAttachmentsAsync(host, Input, Output, [new QpdfAttachmentCopy(QpdfPath.From("second.pdf"), "copy-")], limits, token);
    }

    private static void Resume(object host) => host.GetType().GetMethod("Resume", Members)?.Invoke(host, []);

    private static async Task<Alias> Complete(string route, bool compressed, string boundary, string outcome, string kind, QpdfLimits limits)
    {
        using CancellationTokenSource cancellation = new();
        object host = await Prepare(route, compressed, boundary, outcome);
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Read(host, "Inner");
        Assert.True(inner.TryGetOutput(Output, out byte[] prior));
        Task<object> running = Start((IQpdfHost)host, route, limits, cancellation.Token);
        try
        {
            await ((Task)Read(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = FindGovernor(running);
            object? prepared = typeof(ResourceGovernor).GetField("_preparedTypedCounters", Members)?.GetValue(governor);
            WeakReference admitted = new(prepared);
            prepared = null;
            if (outcome == "cancel") { cancellation.Cancel(); } else { Resume(host); }
            object? result = null;
            if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
            else if (outcome == "fault") { await Assert.ThrowsAnyAsync<QpdfException>(() => running); }
            else if (outcome == "budget") { await Assert.ThrowsAsync<QpdfLimitException>(() => running); }
            else { result = await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            Assert.True((long)(host.GetType().GetField("ReadBytes", Members)?.GetValue(host) ?? 0L) > 0L);
            Assert.Equal(0L, inner.OpenLeaseCount);
            Assert.Equal(0L, governor.Snapshot().OpenHandles);
            Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            Assert.True(inner.TryGetOutput(Output, out byte[] current));
            if ((int)(host.GetType().GetField("Commits", Members)?.GetValue(host) ?? 0) == 0) { Assert.Equal(prior, current); }
            QpdfExecutionCounters? counters = result is null ? null : (QpdfExecutionCounters)Read(result, "Counters");
            if (counters is not null)
            {
                PropertyInfo property = result?.GetType().GetProperty("Counters") ?? throw new InvalidOperationException();
                Assert.False(property.CanWrite);
                Values final = State(governor.Snapshot());
                Assert.Equal(final with { Live = counters.LiveMemoryBytes }, State(counters));
            }
            object? value = kind == "result" ? result : kind == "copy" && counters is not null ? counters with { } : counters;
            if (kind == "copy" && counters is not null)
            {
                Assert.Equal(counters, value);
                Assert.Equal(counters.GetHashCode(), value?.GetHashCode());
            }
            return new(governor, value, new(host), new(result), new(counters), admitted, counters is null ? null : State(counters));
        }
        finally
        {
            Resume(host);
            try { await running; } catch (QpdfException) { } catch (OperationCanceledException) { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckSnapshot(Alias alias)
    {
        object value = alias.Value ?? throw new InvalidOperationException();
        QpdfExecutionCounters counters = value is QpdfExecutionCounters c ? c : (QpdfExecutionCounters)Read(value, "Counters");
        Assert.Equal(alias.Snapshot, State(counters));
    }

    [Theory]
    [MemberData(nameof(PublicJobs))]
    [Trait("Layer", "Contract")]
    public async Task PublicJobsExposeFinalCountersAndPreserveTargets(string route, bool compressed, string boundary, string outcome)
    {
        Alias alias = await Complete(route, compressed, boundary, outcome, "counters", QpdfLimits.Default);
        if (outcome == "success") { CheckSnapshot(alias); }
    }

    [Theory]
    [MemberData(nameof(SuccessAliases))]
    [Trait("Layer", "Contract")]
    public async Task EachTypedFamilyKeepsImmutableCountersAndCallerCopies(string route, bool compressed, string kind)
    {
        Alias alias = await Complete(route, compressed, "read", "success", kind, QpdfLimits.Default);
        CheckSnapshot(alias);
    }

    [Theory]
    [MemberData(nameof(BudgetJobs))]
    [Trait("Layer", "Contract")]
    public async Task RealPublicBudgetFailuresPreservePriorOutputs(string route, bool compressed)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2048L };
        Alias alias = await Complete(route, compressed, "read", "budget", "counters", limits);
    }

}

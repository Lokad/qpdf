using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class SelectionBackingOwnershipTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static IEnumerable<object[]> Aliases()
    {
        foreach (bool compressed in new[] { false, true })
        {
            yield return ["typed-page", compressed, "SourcePages", false];
            yield return ["typed-pages", compressed, "Mapping", false];
            yield return ["typed-pages", compressed, "Mapping", true];
            yield return ["typed-pages", compressed, "SourcePageCounts", false];
            yield return ["typed-pages", compressed, "OutputPaths", false];
        }
    }

    private static object Field(object value, string name) => value.GetType().GetField(name, Members)?.GetValue(value)
        ?? throw new InvalidOperationException(name);

    private static async Task<object> Complete(string route, bool compressed)
        => await Complete(route, compressed, "read", "success", QpdfLimits.Default);

    private static async Task<object> Complete(string route, bool compressed, string boundary, string outcome, QpdfLimits limits)
    {
        Task task = (Task)(typeof(TypedCounterOwnershipTests).GetMethod("Complete", Members)?.Invoke(null,
            [route, compressed, boundary, outcome, "result", limits]) ?? throw new InvalidOperationException());
        await task;
        return task.GetType().GetProperty("Result", Members)?.GetValue(task) ?? throw new InvalidOperationException();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Keep(object captured, string property, bool element)
    {
        object result = Field(captured, "Value");
        object value = result.GetType().GetProperty(property)?.GetValue(result) ?? throw new InvalidOperationException();
        if (element) { value = ((IReadOnlyList<PdfPageSelectionMapping>)value)[0]; }
        captured.GetType().GetField("Value", Members)?.SetValue(captured, value);
        return value;
    }

    private static QpdfExecutionCounters Snapshot(object captured) { ResourceGovernor governor = (ResourceGovernor)Field(captured, "Governor"); return governor.Snapshot(); }

    [Theory]
    [MemberData(nameof(Aliases))]
    [Trait("Layer", "Contract")]
    public async Task PublicMetadataAliasesRemainUsableAfterCleanup(string route, bool compressed, string property, bool element)
    {
        object captured = await Complete(route, compressed);
        await Task.Yield();
        object alias = Keep(captured, property, element);
        captured.GetType().GetField("Value", Members)?.SetValue(captured, null);
        if (element) { Assert.Equal(new PdfPageSelectionMapping(0, 1), Assert.IsType<PdfPageSelectionMapping>(alias)); }
        else if (property == "Mapping")
        {
            Assert.Equal(new[] { new PdfPageSelectionMapping(0, 1), new PdfPageSelectionMapping(1, 1) },
                Assert.IsAssignableFrom<IReadOnlyList<PdfPageSelectionMapping>>(alias));
        }
        else if (property == "OutputPaths")
        { Assert.Equal(new[] { QpdfPath.From("output.pdf") }, Assert.IsAssignableFrom<IReadOnlyList<QpdfPath>>(alias)); }
        else
        { Assert.Equal(property == "SourcePages" ? new[] { 1 } : new[] { 1, 1 }, Assert.IsAssignableFrom<IReadOnlyList<int>>(alias)); }
        Assert.Equal(0L, Snapshot(captured).OpenHandles);
        GC.KeepAlive(alias);
    }

    public static IEnumerable<object[]> Siblings()
    {
        foreach (string route in new[] { "typed-pages", "typed-pages-rotation", "typed-pages-options" })
        foreach (bool compressed in new[] { false, true })
        foreach (string kind in new[] { "element", "record-copy", "constructed", "result-copy" })
        { yield return [route, compressed, kind]; }
    }

    private sealed record MappingAliases(object Captured, PdfPageSelectionMapping Expected);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MappingAliases KeepMapping(object captured, string kind)
    {
        PdfMultiPageSelectionResult result = (PdfMultiPageSelectionResult)Field(captured, "Value");
        PdfPageSelectionMapping first = result.Mapping[0];
        object value = kind switch
        {
            "element" => first,
            "record-copy" => first with { },
            "constructed" => new PdfPageSelectionMapping(first.SourceIndex, first.SourcePage),
            _ => result with { },
        };
        if (value is PdfPageSelectionMapping copied && kind != "element")
        { Assert.Equal(first, copied); Assert.Equal(first.GetHashCode(), copied.GetHashCode()); Assert.NotSame(first, copied); }
        if (value is PdfMultiPageSelectionResult resultCopy)
        { Assert.Equal(result, resultCopy); Assert.Same(result.Mapping, resultCopy.Mapping); }
        MappingAliases aliases = new(captured, new(first.SourceIndex, first.SourcePage));
        captured.GetType().GetField("Value", Members)?.SetValue(captured, value);
        return aliases;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckMapping(MappingAliases aliases)
    {
        object value = Field(aliases.Captured, "Value");
        PdfPageSelectionMapping first = value is PdfMultiPageSelectionResult result ? result.Mapping[0] : (PdfPageSelectionMapping)value;
        Assert.Equal(aliases.Expected, first);
        Assert.False(typeof(PdfPageSelectionMapping).GetProperty(nameof(PdfPageSelectionMapping.SourcePage))?.CanWrite);
    }

    [Theory]
    [MemberData(nameof(Siblings))]
    [Trait("Layer", "Contract")]
    public async Task MappingAliasesPreserveValuesAndDistinguishCallerCopies(string route, bool compressed, string kind)
    {
        object captured = await Complete(route, compressed);
        await Task.Yield();
        MappingAliases aliases = KeepMapping(captured, kind);
        CheckMapping(aliases);
        GC.KeepAlive(aliases);
    }

    public static IEnumerable<object[]> PublicFailures()
        => TypedCounterOwnershipTests.PublicJobs().Where(row => ((string)row[0]).StartsWith("typed-page", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(PublicFailures))]
    [Trait("Layer", "Contract")]
    public async Task CompleteSelectionJobsKeepOriginalFaultCancellationAndPublicationControls(string route, bool compressed, string boundary, string outcome)
    {
        object captured = await Complete(route, compressed, boundary, outcome, QpdfLimits.Default);
        await Task.Yield();
        captured.GetType().GetField("Value", Members)?.SetValue(captured, null);
        QpdfExecutionCounters counters = Snapshot(captured);
        Assert.InRange(counters.PeakMemoryBytes, 1L, QpdfLimits.Default.MaxMemoryBytes);
        Assert.Equal(0L, counters.OpenHandles);
        GC.KeepAlive(captured);
    }

    private sealed class SplitCapture(ResourceGovernor governor, object? value)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal object? Value = value;
    }

    private static async Task<PdfMultiPageSelectionResult> StartSplit(IQpdfHost host, QpdfLimits limits, CancellationToken token)
        => await Qpdf.SelectPagesAsync(host,
            [new PdfPageSource(QpdfPath.From("input.pdf"), new PdfPageSelectionOptions(1)),
             new PdfPageSource(QpdfPath.From("second.pdf"), new PdfPageSelectionOptions(1))],
            QpdfPath.From("output.pdf"), new PdfPageMergeOptions { SplitPages = 1 }, limits, token);

    private static async Task<SplitCapture> CompleteSplit(bool compressed, string boundary, string outcome, bool second)
    {
        Task setup = (Task)(typeof(ExecutionCounterTests).GetMethod("Prepare", Members)?.Invoke(null,
            ["typed-pages", compressed, boundary, outcome]) ?? throw new InvalidOperationException());
        await setup;
        object host = setup.GetType().GetProperty("Result")?.GetValue(setup) ?? throw new InvalidOperationException();
        InMemoryQpdfHost inner = (InMemoryQpdfHost)(host.GetType().GetProperty("Inner", Members)?.GetValue(host)
            ?? throw new InvalidOperationException());
        List<byte[]> prior = [];
        foreach (string path in new[] { "output-1.pdf", "output-2.pdf" })
        {
            await using IQpdfOutputTransaction seed = await inner.CreateOutputAsync(QpdfPath.From(path), CancellationToken.None);
            await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
            Assert.True(inner.TryGetOutput(QpdfPath.From(path), out byte[] bytes)); prior.Add(bytes);
        }
        if (second) { host.GetType().GetField("PausePath", Members)?.SetValue(host, QpdfPath.From("output-2.pdf")); }
        using CancellationTokenSource cancellation = new();
        Task<PdfMultiPageSelectionResult> running = StartSplit((IQpdfHost)host, QpdfLimits.Default, cancellation.Token);
        try
        {
            await ((Task)(host.GetType().GetProperty("Entered", Members)?.GetValue(host) ?? throw new InvalidOperationException()))
                .WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = (ResourceGovernor)(typeof(ExecutionCounterTests).GetMethod("FindGovernor", Members)?.Invoke(null,
                [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)]) ?? throw new InvalidOperationException());
            if (outcome == "cancel") { cancellation.Cancel(); }
            else { host.GetType().GetMethod("Resume", Members)?.Invoke(host, []); }
            PdfMultiPageSelectionResult? result = null;
            if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
            else if (outcome == "fault") { await Assert.ThrowsAnyAsync<QpdfException>(() => running); }
            else { result = await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            int commits = outcome == "success" ? 2 : boundary == "output-close" ? second ? 2 : 1 : second ? 1 : 0;
            Assert.Equal(commits, (int)Field(host, "Commits"));
            for (int index = 0; index < prior.Count; index++)
            {
                Assert.Equal(new byte[] { 17, 23, 41 }, prior[index]);
                Assert.True(inner.TryGetOutput(QpdfPath.From($"output-{index + 1}.pdf"), out byte[] bytes));
                if (index < commits) { Assert.NotEqual(prior[index], bytes); }
                else { Assert.Equal(prior[index], bytes); }
            }
            Assert.Equal(0L, inner.OpenLeaseCount);
            Assert.Equal(0L, governor.Snapshot().OpenHandles);
            if (result is not null)
            {
                Assert.Equal(new[] { "output-1.pdf", "output-2.pdf" }, result.OutputPaths.Select(path => path.Value));
                Assert.Equal(new[] { 1, 1 }, result.SourcePageCounts);
                Assert.Equal(governor.Snapshot(), result.Counters);
            }
            return new(governor, result);
        }
        finally
        {
            host.GetType().GetMethod("Resume", Members)?.Invoke(host, []);
            { try { await running; } catch (QpdfException) { } catch (OperationCanceledException) { } }
        }
    }

    public static IEnumerable<object[]> SplitJobs()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (string boundary in new[] { "read", "input-close", "write", "commit", "output-close" })
        foreach (string outcome in new[] { "success", "fault", "cancel" })
        {
            yield return [compressed, boundary, outcome, false];
            if (boundary is "write" or "commit" or "output-close") { yield return [compressed, boundary, outcome, true]; }
        }
    }

    [Theory]
    [MemberData(nameof(SplitJobs))]
    [Trait("Layer", "Contract")]
    public async Task SplitJobsKeepPublishedAndPriorAliasesAcrossBothOutputs(bool compressed, string boundary, string outcome, bool second)
    {
        SplitCapture captured = await CompleteSplit(compressed, boundary, outcome, second);
        await Task.Yield();
        if (outcome == "success")
        {
            IReadOnlyList<QpdfPath> paths = Assert.IsType<PdfMultiPageSelectionResult>(captured.Value).OutputPaths;
            captured.Value = null;
            Assert.Equal(new[] { QpdfPath.From("output-1.pdf"), QpdfPath.From("output-2.pdf") }, paths);
            GC.KeepAlive(paths);
        }
        Assert.Equal(0L, captured.Governor.Snapshot().OpenHandles);
        GC.KeepAlive(captured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Layer", "Contract")]
    public async Task GeneratedSplitNamesRemainUsableAfterPublication(bool compressed)
    {
        SplitCapture captured = await CompleteSplit(compressed, "read", "success", false);
        await Task.Yield();
        string name = Assert.IsType<PdfMultiPageSelectionResult>(captured.Value).OutputPaths[0].Value;
        captured.Value = null;
        Assert.Equal("output-1.pdf", name);
        Assert.Equal(0L, captured.Governor.Snapshot().OpenHandles);
        GC.KeepAlive(name);
    }

    [Theory]
    [InlineData("typed-page", false)]
    [InlineData("typed-page", true)]
    [InlineData("typed-pages", false)]
    [InlineData("typed-pages", true)]
    [Trait("Layer", "Contract")]
    public async Task CallerConstructorsCopyValueBackingWithoutBorrowingTheOriginalView(string route, bool compressed)
    {
        object captured = await Complete(route, compressed);
        await Task.Yield();
        KeepCallerBacking(captured);
        Assert.Equal(route == "typed-page" ? new[] { 1 } : new[] { 1, 1 }, (IReadOnlyList<int>)Field(captured, "Value"));
        GC.KeepAlive(captured);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void KeepCallerBacking(object captured)
    {
        object result = Field(captured, "Value");
        IReadOnlyList<int> source, copy;
        if (result is PdfPageSelectionResult page)
        {
            source = page.SourcePages;
            copy = new PdfPageSelectionResult(page.SourcePageCount, page.SourcePages, page.PageCount, page.Warnings, page.Counters).SourcePages;
        }
        else
        {
            PdfMultiPageSelectionResult pages = (PdfMultiPageSelectionResult)result;
            source = pages.SourcePageCounts;
            copy = new PdfMultiPageSelectionResult(pages.PageCount, pages.Mapping, pages.SourcePageCounts, pages.OutputPaths, pages.Warnings, pages.Counters).SourcePageCounts;
        }
        Assert.NotSame(source, copy); Assert.Equal(source, copy);
        captured.GetType().GetField("Value", Members)?.SetValue(captured, copy);
    }

}

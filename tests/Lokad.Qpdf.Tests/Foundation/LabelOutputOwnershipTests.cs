using System.Reflection;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Writing;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class LabelOutputOwnershipTests(ITestOutputHelper log)
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");
    private static readonly byte[] Prior = [4, 8, 15, 16, 23, 42];
    private static readonly Dictionary<char, byte[]> SourceFixtures = [];

    private sealed class Host(string outcome) : IQpdfHost
    {
        internal readonly InMemoryQpdfHost Inner = new();
        internal readonly List<QpdfPath> Created = [];
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _paused;
        internal Task Entered => _entered.Task;
        internal void Resume() => _resume.TrySetResult();
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => Inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Created.Add(path);
            if (!_paused)
            {
                _paused = true; _entered.TrySetResult(); await _resume.Task.WaitAsync(token);
                if (outcome == "create-fault") { throw new QpdfHostException(path, QpdfHostFailureReason.WriteFailed); }
                if (outcome == "host-cancel") { throw new OperationCanceledException(new CancellationToken(true)); }
            }
            return new Transaction(await Inner.CreateOutputAsync(path, token), outcome);
        }
        private sealed class Transaction(IQpdfOutputTransaction inner, string outcome) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
                => outcome == "write-fault" ? throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed) : inner.WriteAsync(bytes, token);
            public ValueTask CommitAsync(CancellationToken token)
                => outcome == "commit-fault" ? throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync()
            {
                try { if (outcome == "close-fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); } }
                finally { await inner.DisposeAsync(); }
            }
        }
    }

    private static byte[] Fixture() => (byte[])(typeof(LabelTests).GetMethod("BuildDoc", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, [3])
        ?? throw new InvalidOperationException("Missing existing three-page label fixture."));

    private static async Task<byte[]> SourceFixture(char character)
    {
        if (SourceFixtures.TryGetValue(character, out byte[]? known)) { return known; }
        InMemoryQpdfHost host = new(); host.AddInput(Input, Fixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([Input.Value,
            "--set-page-labels", "1:D/17/" + new string(character, 512), "2:r/1/tail", "--", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        SourceFixtures.Add(character, bytes); return bytes;
    }

    private async Task<List<byte[]>> Complete(string route, string operation, string outcome, char character, bool inherited)
    {
        Host host = new(outcome); host.Inner.AddInput(Input, inherited ? await SourceFixture(character) : Fixture(), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(Output, CancellationToken.None))
        { await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        using CancellationTokenSource cancel = new();
        string prefix = new(character, 512); string[] labels = ["1:D/17/" + prefix, "2:r/1/tail"];
        Task running;
        if (route == "typed")
        {
            PdfPageSource[] sources = operation == "merge"
                ? [new(Input, PdfPageSelectionOptions.FromPages([1])), new(Input, PdfPageSelectionOptions.FromPages([2, 3]))]
                : [new(Input, PdfPageSelectionOptions.FromPages(operation == "whole" ? [1, 2, 3] : [3, 1, 2]))];
            running = Qpdf.SelectPagesAsync(host, sources, Output, new PdfPageMergeOptions
            { Labels = inherited ? [] : [new(1, PdfPageLabelStyle.Decimal, 17, prefix), new(2, PdfPageLabelStyle.LowerRoman, 1, "tail")],
                SplitPages = operation == "split" ? 1 : null }, QpdfLimits.Default, cancel.Token).AsTask();
        }
        else if (route == "job")
        {
            Dictionary<string, object> job = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value };
            if (!inherited) { job["setPageLabels"] = labels; }
            if (operation != "whole")
            { job["pages"] = operation == "merge" ? new[] { new { file = ".", range = "1" }, new { file = Input.Value, range = "2,3" } }
                : new[] { new { file = ".", range = "3,1,2" } }; }
            if (operation == "split") { job["splitPages"] = "1"; }
            running = Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), cancel.Token).AsTask();
        }
        else
        {
            List<string> argv = [Input.Value];
            if (operation != "whole")
            { argv.AddRange(operation == "merge" ? ["--pages", ".", "1", Input.Value, "2,3", "--"] : ["--pages", ".", "3,1,2", "--"]); }
            if (operation == "split") { argv.Add("--split-pages=1"); }
            if (!inherited) { argv.AddRange(["--set-page-labels", .. labels, "--"]); }
            argv.Add(Output.Value);
            running = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), cancel.Token).AsTask();
        }
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(15));
        if (outcome == "cancel") { cancel.Cancel(); } host.Resume();
        object? result = null;
        try
        {
            await running;
            result = running.GetType().GetProperty("Result", Fields)?.GetValue(running);
            if (route == "typed") { Assert.Equal("success", outcome); Assert.IsType<PdfMultiPageSelectionResult>(result); }
            else
            {
                QpdfExecutionResult command = Assert.IsType<QpdfExecutionResult>(result);
                Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed : outcome is "cancel" or "host-cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, command.Status);
            }
        }
        catch (OperationCanceledException) { Assert.Equal("typed", route); Assert.Contains(outcome, new[] { "cancel", "host-cancel" }); }
        catch (QpdfHostException) { Assert.Equal("typed", route); Assert.EndsWith("fault", outcome); }
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        if (operation != "split" && outcome != "success" && outcome != "close-fault")
        { Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior); }
        if (outcome == "success" && operation != "split")
        {
            Assert.True(host.Inner.TryGetOutput(Output, out byte[] bytes));
            InMemoryQpdfHost validation = new(); validation.AddInput(Input, bytes, QpdfInputOwnership.Copy);
            Assert.True((await Qpdf.CheckAsync(validation, Input, CancellationToken.None)).Passed);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(validation, Input, CancellationToken.None);
            Assert.Equal(3, document.PageCount);
        }
        List<byte[]> published = [];
        if (outcome == "success")
        {
            Assert.Equal(operation == "split" ? 3 : 1, host.Created.Count);
            int part = 0;
            foreach (QpdfPath path in host.Created)
            {
                Assert.True(host.Inner.TryGetOutput(path, out byte[] bytes)); published.Add(bytes);
                await ValidateLabels(bytes, prefix, operation, inherited, ++part);
            }
        }
        return published;
    }

    private static async Task ValidateLabels(byte[] bytes, string prefix, string operation, bool inherited, int part)
    {
        InMemoryQpdfHost host = new(); host.AddInput(Input, bytes, QpdfInputOwnership.Copy);
        Assert.True((await Qpdf.CheckAsync(host, Input, CancellationToken.None)).Passed);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        Assert.Equal(operation == "split" ? 1 : 3, document.PageCount);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        if (operation == "split" && !inherited)
        { Assert.False(catalog.TryGet(PdfName.FromAscii("PageLabels"), out _)); return; }
        Assert.True(catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject tree));
        Assert.True(Assert.IsType<PdfDictionary>(tree).TryGet(PdfName.FromAscii("Nums"), out QpdfObject raw));
        PdfArray numbers = Assert.IsType<PdfArray>(raw);
        Assert.Equal(operation == "split" ? 2 : inherited && operation == "selection" ? 6 : 4, numbers.Count);
        string expected = inherited && (operation == "selection" || operation == "split" && part != 2) ? "tail" : prefix;
        PdfDictionary label = Assert.IsType<PdfDictionary>(numbers[1]);
        Assert.Equal(0L, Assert.IsType<PdfInteger>(numbers[0]).Value);
        Assert.True(label.TryGet(PdfName.FromAscii("P"), out QpdfObject text));
        byte[] encoded = expected.Contains('\u03a9') ? Encoding.BigEndianUnicode.GetBytes("\ufeff" + expected) : Encoding.Latin1.GetBytes(expected);
        Assert.Equal(encoded, Assert.IsType<PdfByteString>(text).ValueSpan.ToArray());
        Assert.True(label.TryGet(PdfName.FromAscii("S"), out QpdfObject style));
        Assert.Equal(expected == "tail" ? "r" : "D", Assert.IsType<PdfName>(style).DisplayText);
        if (expected != "tail" || operation == "selection" || operation == "split")
        {
            Assert.True(label.TryGet(PdfName.FromAscii("St"), out QpdfObject start));
            Assert.Equal(expected == "tail" ? operation == "split" && part == 3 ? 1L : 2L : 17L, Assert.IsType<PdfInteger>(start).Value);
        }
    }

    [Fact]
    public async Task CompletePublicLabelJobsPreserveLabelsAndCleanup()
    {
        List<byte[]> outputs = [];
        foreach (bool inherited in new[] { false, true })
        foreach (string route in new[] { "argv", "job", "typed" })
        foreach (string operation in new[] { "whole", "selection", "merge", "split" })
        foreach (string outcome in new[] { "success", "create-fault", "write-fault", "commit-fault", "close-fault", "cancel", "host-cancel" })
        foreach (char encoding in outcome == "success" ? new[] { 'p', '\u00e9', '\u03a9' } : new[] { 'p' })
        { outputs.AddRange(await Complete(route, operation, outcome, encoding, inherited)); }
        // Caller-owned output snapshots remain valid after all jobs close.
        foreach (byte[] bytes in outputs)
        {
            using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(bytes);
            Assert.InRange(independent.NumberOfPages, 1, 3);
        }
    }

    [Fact]
    public async Task LargeTypedPrefixesRejectBeforeAllocatingUnadmittedEncoding()
    {
        string prefix = new('p', 4 * 1024 * 1024);
        List<string> defects = [];
        foreach (bool work in new[] { false, true })
        {
            InMemoryQpdfHost host = new(); host.AddInput(Input, Fixture(), QpdfInputOwnership.Copy);
            await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(Output, CancellationToken.None))
            { await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
            QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2L * 1024 * 1024, MaxWork = work ? 25000L : QpdfLimits.Default.MaxWork };
            PdfPageMergeOptions options = new() { Labels = [new(1, PdfPageLabelStyle.Decimal, 1, prefix)] };
            PdfPageSource[] sources = [new(Input, PdfPageSelectionOptions.FromPages([1]))];
            long allocated = GC.GetTotalAllocatedBytes(true);
            QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() => Qpdf.SelectPagesAsync(host, sources, Output, options, limits, CancellationToken.None).AsTask());
            long delta = GC.GetTotalAllocatedBytes(true) - allocated;
            log.WriteLine("LABEL BUDGET {0} allocation={1}", work ? "work" : "memory", delta);
            Assert.Equal(work ? nameof(QpdfLimits.MaxWork) : nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
            if (delta >= prefix.Length) { defects.Add((work ? "work" : "memory") + ":" + delta); }
            Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior);
        }
        Assert.True(defects.Count == 0, "Unadmitted prefix encoding allocated before public rejection: " + string.Join(',', defects));
    }

    [Fact]
    public async Task PrefixEncodingPreservesSurrogateBoundariesAndUndefinedPdfDocCharacters()
    {
        string[] prefixes = [new string('p', 255) + "\ud83d\ude00tail", new string('p', 255) + "\ud83dtail",
            new string('p', 256) + "\ude00tail", "\u007f\u00e9"];
        foreach (string prefix in prefixes)
        {
            InMemoryQpdfHost host = new(); host.AddInput(Input, Fixture(), QpdfInputOwnership.Copy);
            PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host, [new PdfPageSource(Input, PdfPageSelectionOptions.FromPages([1]))], Output,
                new PdfPageMergeOptions { Labels = [new(1, PdfPageLabelStyle.Decimal, 17, prefix)] }, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(1, result.PageCount); Assert.True(host.TryGetOutput(Output, out byte[] bytes));
            InMemoryQpdfHost reopened = new(); reopened.AddInput(Input, bytes, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(reopened, Input, CancellationToken.None);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            Assert.True(catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject tree));
            Assert.True(Assert.IsType<PdfDictionary>(tree).TryGet(PdfName.FromAscii("Nums"), out QpdfObject numbers));
            PdfDictionary label = Assert.IsType<PdfDictionary>(Assert.IsType<PdfArray>(numbers)[1]);
            Assert.True(label.TryGet(PdfName.FromAscii("P"), out QpdfObject text));
            Assert.Equal(Encoding.BigEndianUnicode.GetBytes("\ufeff" + prefix), Assert.IsType<PdfByteString>(text).ValueSpan.ToArray());
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }
}

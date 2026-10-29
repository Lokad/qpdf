using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class SelectiveAttachmentExtractionTests
{
    private static readonly string[] Routes = ["typed", "argv", "job", "argv-fileless", "job-fileless"];
    private static readonly QpdfLimits Tight = QpdfLimits.Default with { MaxMemoryBytes = 2L << 20 };

    private static byte[] Fixture(string kind, bool branch)
    {
        int count = kind switch { "errors" => 260, "shared" => 40, "default" => 128, _ => 0 };
        int length = kind is "selected" or "default" ? 524288 : kind == "shared" ? 65536 : 0;
        string other = "<< /F (other)" + (kind == "errors" ? "" : " /EF << /F 8 0 R >>")
            + (kind is "shared" or "default" ? " /Desc (" + new string('A', length) + ")" : "") + " >>";
        string target = "<< /F (target) /EF << /UF 6 0 R >>"
            + (kind == "selected" ? " /Desc (" + new string('B', length) + ")" : "") + " >>";
        string key = kind switch { "pdfdoc" => "<636166e9>", "utf16" => "<feff00630061006600e9>", "utf8" => "<efbbbf636166c3a9>", _ => "(target)" };
        string others = string.Concat(Enumerable.Range(0, count).Select(i => "(A" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ") 7 0 R "));
        string leaf = "<< /Names [" + others + key + " 5 0 R] >>";
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null),
            (4, branch ? "<< /Kids 9 0 R >>" : leaf, null), (5, target, null),
            (6, "<< /Length 1" + (kind == "params" ? " /Params 99 0 R" : "") + " >>", "X"u8.ToArray()),
            (7, other, null), (8, "<< /Length 1 >>", "Y"u8.ToArray()),
            (9, "[10 0 R]", null), (10, leaf, null)
        ];
        if (kind == "cycle") { objects[3] = (4, "<< /Kids [4 0 R] >>", null); }
        if (kind == "odd") { objects[3] = (4, "<< /Names [(target)] >>", null); }
        if (kind == "badkey") { objects[3] = (4, "<< /Names [/target 5 0 R] >>", null); }
        if (kind == "badkid") { objects[3] = (4, "<< /Kids [42] >>", null); }
        if (kind == "missing") { objects[3] = (4, "<< /Names [] >>", null); }
        if (kind == "badstream") { objects[4] = (5, "<< /F (target) >>", null); }
        if (kind == "depth")
        {
            objects[3] = (4, "<< /Kids [11 0 R] >>", null);
            for (int id = 11; id < 17; id++) { objects.Add((id, "<< /Kids [" + (id + 1) + " 0 R] >>", null)); }
            objects.Add((17, leaf, null));
        }
        return CheckTests.Assemble(objects, 1);
    }

    private static ValueTask<QpdfExecutionResult> Execute(IQpdfHost host, string route, string key, QpdfLimits limits, CancellationToken token)
    {
        bool fileless = route.EndsWith("fileless", StringComparison.Ordinal);
        if (route.StartsWith("argv", StringComparison.Ordinal))
        {
            List<string> argv = ["input", "--show-attachment=" + key];
            if (!fileless) { argv.Add("output"); }
            return Qpdf.ExecuteAsync(host, new(argv), new(limits), token);
        }
        Dictionary<string, object> job = new() { ["inputFile"] = "input", ["showAttachment"] = key };
        if (!fileless) { job["outputFile"] = "output"; }
        return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new(limits), token);
    }

    public static IEnumerable<object[]> SuccessCases()
    {
        foreach (string kind in new[] { "simple", "shared", "errors", "selected", "params", "pdfdoc", "utf16", "utf8" })
        foreach (bool branch in new[] { false, true })
        foreach (string route in Routes) { yield return [kind, branch, route]; }
    }

    [Theory]
    [MemberData(nameof(SuccessCases))]
    public async Task ExtractsOnlyRequestedPayload(string kind, bool branch, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(kind, branch), QpdfInputOwnership.Transfer);
        string key = kind is "pdfdoc" or "utf16" or "utf8" ? "café" : "target";
        ReadOnlyMemory<byte> payload;
        if (route == "typed")
        {
            QpdfAttachmentExtraction result = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), key, QpdfPath.From("output"), Tight, CancellationToken.None);
            Assert.Empty(result.Warnings);
            Assert.Equal(1, result.ByteCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
            payload = bytes;
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, route, key, Tight, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Empty(result.Diagnostics);
            if (route.EndsWith("fileless", StringComparison.Ordinal)) { payload = Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes); }
            else { Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); payload = bytes; }
        }
        Assert.Equal(0, host.OpenLeaseCount);
        host.Dispose();
        Assert.Equal("X"u8.ToArray(), payload.ToArray());
    }

    [Fact]
    public async Task SharedUnrelatedDescriptionDoesNotExhaustDefaultAllowance()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("default", false), QpdfInputOwnership.Transfer);
        QpdfAttachmentExtraction result = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), "target", QpdfPath.From("output"), CancellationToken.None);
        Assert.InRange(result.Counters.PeakMemoryBytes, 1L, 2L << 20);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.Equal("X"u8.ToArray(), bytes);
        // Listing still admits all metadata and retains its explicit limit.
        await Assert.ThrowsAsync<QpdfLimitException>(() => Qpdf.ListAttachmentsAsync(host, QpdfPath.From("input"), Tight, CancellationToken.None).AsTask());
        Assert.Equal(0, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> FailureCases()
    {
        foreach (string kind in new[] { "cycle", "depth", "odd", "badkey", "badkid", "missing", "badstream" })
        foreach (string route in Routes) { yield return [kind, route]; }
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task RelevantTreeAndStreamFailuresDoNotPublish(string kind, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(kind, false), QpdfInputOwnership.Transfer);
        QpdfLimits limits = Tight with { MaxNestingDepth = kind == "depth" ? 4 : Tight.MaxNestingDepth };
        if (route == "typed")
        { await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), "target", QpdfPath.From("output"), limits, CancellationToken.None).AsTask()); }
        else
        {
            QpdfExecutionResult result = await Execute(host, route, "target", limits, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Null(result.OutputBytes);
        }
        Assert.False(host.TryGetOutput(QpdfPath.From("output"), out _));
        Assert.Equal(0, host.OpenLeaseCount);
    }

    private sealed class CancelingObjects(Action cancel, int cancelAt) : Dictionary<int, QpdfObject>, IReadOnlyDictionary<int, QpdfObject>
    {
        internal int LastResolved { get; private set; }
        bool IReadOnlyDictionary<int, QpdfObject>.TryGetValue(int key, out QpdfObject value)
        {
            bool found = TryGetValue(key, out QpdfObject? resolved);
            LastResolved = key;
            if (key == cancelAt) { cancel(); }
            value = resolved ?? PdfNull.Instance;
            return found;
        }
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 5)]
    [InlineData(false, 6)]
    [InlineData(true, 6)]
    public async Task CancellationDuringSelectedStreamResolutionRefundsScratch(bool caller, int cancelAt)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("simple", true), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        CancelingObjects objects = new(cancellation.Cancel, cancelAt);
        foreach (int id in Enumerable.Range(1, 10)) { objects[id] = document.GetObject(id); }
        document.Engine.SetInspectionObjects(objects);
        ResourceGovernor governor = new(Tight, caller ? CancellationToken.None : cancellation.Token);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            Assert.ThrowsAny<OperationCanceledException>(() => EmbeddedFiles.ExtractAttachment(document.Engine, "target", Tight,
                governor, new(governor), memory, caller ? cancellation.Token : CancellationToken.None));
        }
        governor.CompletePrivateStorage();
        Assert.Equal(cancelAt, objects.LastResolved);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LookupAdmissionRefusalIsNotConvertedToRawFallback(bool shared)
    {
        using InMemoryQpdfHost input = new();
        input.AddInput(QpdfPath.From("input"), Fixture("simple", true), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(input, QpdfPath.From("input"), CancellationToken.None);
        RecordingMemoryBudget root = new(1L << 20) { RefuseOnGrowth = shared ? 2 : 0 };
        using IMemoryScope caller = root.CreateScope();
        using InMemoryQpdfHost host = new(Tight, null, caller);
        QpdfLimits limits = Tight with { MaxMemoryBytes = shared ? Tight.MaxMemoryBytes : 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            DiagnosticBag diagnostics = new(governor);
            memory.Reserve(1024L - governor.Snapshot().LiveMemoryBytes - 64L);
            Exception? error = Record.Exception(() => EmbeddedFiles.ExtractAttachment(document.Engine, "target", limits,
                governor, diagnostics, memory, CancellationToken.None));
            if (shared) { Assert.IsType<QpdfMemoryAdmissionException>(error); }
            else { Assert.IsType<QpdfLimitException>(error); }
        }
        governor.CompletePrivateStorage();
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    internal sealed class FaultHost(InMemoryQpdfHost inner, string fault, CancellationTokenSource cancellation) : IQpdfHost
    {
        internal int Created { get; private set; }
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created += 1; return new Transaction(await inner.CreateOutputAsync(path, token), fault, cancellation); }
        private sealed class Transaction(IQpdfOutputTransaction inner, string fault, CancellationTokenSource cancellation) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
            {
                await inner.WriteAsync(data, token);
                if (fault == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                if (fault == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
            }
            public ValueTask CommitAsync(CancellationToken token)
                => fault == "commit" ? throw new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("typed", "write")]
    [InlineData("argv", "write")]
    [InlineData("job", "write")]
    [InlineData("typed", "commit")]
    [InlineData("argv", "commit")]
    [InlineData("job", "commit")]
    [InlineData("typed", "cancel")]
    [InlineData("argv", "cancel")]
    [InlineData("job", "cancel")]
    public async Task PublicationFailurePreservesPriorTarget(string route, string fault)
    {
        using InMemoryQpdfHost inner = new();
        inner.AddInput(QpdfPath.From("input"), Fixture("shared", false), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await inner.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        using CancellationTokenSource cancellation = new();
        FaultHost host = new(inner, fault, cancellation);
        if (route == "typed")
        {
            Exception? error = await Record.ExceptionAsync(() => Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), "target", QpdfPath.From("output"), Tight, cancellation.Token).AsTask());
            if (fault == "cancel") { Assert.IsAssignableFrom<OperationCanceledException>(error); }
            else { Assert.IsAssignableFrom<QpdfException>(error); }
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, route, "target", Tight, cancellation.Token);
            Assert.Equal(fault == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        }
        Assert.Equal(1, host.Created);
        Assert.True(inner.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0, inner.OpenLeaseCount);
    }
}

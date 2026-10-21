using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationPublicationTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output");

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget, string fault, CancellationTokenSource cancellation) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        private void Fail(string phase)
        {
            if (fault == "cancel" && phase == "write") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            if (fault == phase) { throw new QpdfHostException(Output, QpdfHostFailureReason.WriteFailed); }
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Fail("create"); return new Transaction(this, await storage.CreateOutputAsync(path, token)); }
        private sealed class Transaction(Host host, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            { host.Fail("write"); return inner.WriteAsync(bytes, token); }
            public ValueTask CommitAsync(CancellationToken token)
            { host.Fail("commit"); return inner.CommitAsync(token); }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); host.Fail("close"); }
        }
    }

    private static async Task<InMemoryQpdfHost> Storage(string mode)
    {
        InMemoryQpdfHost host = new();
        byte[] input = mode.StartsWith("generate-text", StringComparison.Ordinal)
            ? AppearanceProgramTests.Fixture(new string(' ', 32768) + "/Tx BMC\n0 0 10 10 re f EMC", "", "/DA (/Helv 17 Tf)", "")
            : mode.StartsWith("generate", StringComparison.Ordinal) ? AppearanceGenerationRegressionTests.CheckboxFixture(64)
            : mode.StartsWith("catalog", StringComparison.Ordinal) ? OrdinaryFormStorageTests.Fixture(512)
            : mode == "merge" ? FormMergeStorageTests.Fixture(new string('F', 32768), false) : mode is "overlay" or "flatten" ? FlattenResourceStorageTests.Fixture(64, false)
            : ContentSafetyTests.Fixture(System.Text.Encoding.ASCII.GetBytes(new string(' ', 524288) + "BI /W 1 /H 1 /BPC 8 /CS /G ID X EI"), "", false, 1);
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("payload"), "abc"u8.ToArray(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("overlay"), mode == "merge" ? FormMergeStorageTests.Fixture(new string('F', 32768), true) : mode.StartsWith("overlay-form", StringComparison.Ordinal) ? OverlayFormStorageTests.Fixture() : FlattenResourceStorageTests.Fixture(4, false), QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction initial = await host.CreateOutputAsync(Output, CancellationToken.None);
        await initial.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await initial.CommitAsync(CancellationToken.None);
        return host;
    }

    private static ValueTask<QpdfExecutionResult> Run(Host host, string mode, CancellationToken token)
        => mode == "catalog-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1"}],"addAttachment":[{"file":"payload"}]}""", token)
            : mode == "catalog" ? Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1", "--", "--add-attachment", "payload", "--", "output"]), token)
            : mode == "overlay-form-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","overlay":{"file":"overlay"}}""", token)
            : mode is "generate-job" or "generate-text-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","generateAppearances":true}""", token)
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(mode == "merge" ? ["input", "--pages", ".", "1", "overlay", "1", "--", "output"] : mode is "overlay" or "overlay-form"
            ? ["input", "--overlay", "overlay", "--", "output"]
            : ["input", mode switch { "generate" or "generate-text" => "--generate-appearances", "flatten" => "--flatten-annotations=all", "coalesce" => "--coalesce-contents",
                "normalize" => "--normalize-content=y", _ => "--externalize-inline-images" }, "--ii-min-bytes=0", "--", "output"]), token);

    [Theory]
    [InlineData("catalog")]
    [InlineData("catalog-job")]
    [InlineData("flatten")]
    [InlineData("overlay")]
    [InlineData("overlay-form")]
    [InlineData("overlay-form-job")]
    [InlineData("coalesce")]
    [InlineData("normalize")]
    [InlineData("externalize")]
    [InlineData("merge")]
    [InlineData("generate")]
    [InlineData("generate-job")]
    [InlineData("generate-text")]
    [InlineData("generate-text-job")]
    public async Task EverySharedGrowthCanRefuseWithoutPublishing(string mode)
    {
        int growths;
        using (InMemoryQpdfHost storage = await Storage(mode))
        {
            RecordingMemoryBudget root = new(64L * 1024 * 1024);
            using (IMemoryScope owner = root.CreateScope())
            using (CancellationTokenSource cancellation = new())
            {
                QpdfExecutionResult result = await Run(new(storage, owner, "none", cancellation), mode, cancellation.Token);
                Assert.Equal(QpdfExecutionStatus.Completed, result.Status); growths = root.GrowthCalls;
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(0L, root.ReservedBytes);
        }
        Assert.InRange(growths, 1, 64);
        for (int growth = 1; growth <= growths; growth++)
        {
            using InMemoryQpdfHost storage = await Storage(mode);
            RecordingMemoryBudget root = new(64L * 1024 * 1024) { RefuseOnGrowth = growth };
            QpdfExecutionResult result;
            using (IMemoryScope owner = root.CreateScope())
            using (CancellationTokenSource cancellation = new())
            {
                result = await Run(new(storage, owner, "none", cancellation), mode, cancellation.Token);
                Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
                Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
                Assert.True(storage.TryGetOutput(Output, out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes);
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(0L, root.ReservedBytes); Assert.NotEmpty(result.Diagnostics);
        }
    }

    [Theory]
    [InlineData("create", "catalog")]
    [InlineData("write", "catalog")]
    [InlineData("commit", "catalog")]
    [InlineData("cancel", "catalog")]
    [InlineData("close", "catalog")]
    [InlineData("create", "catalog-job")]
    [InlineData("write", "catalog-job")]
    [InlineData("commit", "catalog-job")]
    [InlineData("cancel", "catalog-job")]
    [InlineData("close", "catalog-job")]
    [InlineData("create", "overlay-form")]
    [InlineData("write", "overlay-form")]
    [InlineData("commit", "overlay-form")]
    [InlineData("cancel", "overlay-form")]
    [InlineData("close", "overlay-form")]
    [InlineData("create", "overlay-form-job")]
    [InlineData("write", "overlay-form-job")]
    [InlineData("commit", "overlay-form-job")]
    [InlineData("cancel", "overlay-form-job")]
    [InlineData("close", "overlay-form-job")]
    [InlineData("create", "flatten")]
    [InlineData("create", "generate-text")]
    [InlineData("write", "generate-text")]
    [InlineData("commit", "generate-text")]
    [InlineData("cancel", "generate-text")]
    [InlineData("close", "generate-text")]
    [InlineData("create", "generate-text-job")]
    [InlineData("write", "generate-text-job")]
    [InlineData("commit", "generate-text-job")]
    [InlineData("cancel", "generate-text-job")]
    [InlineData("close", "generate-text-job")]
    [InlineData("create", "generate")]
    [InlineData("write", "generate")]
    [InlineData("commit", "generate")]
    [InlineData("cancel", "generate")]
    [InlineData("close", "generate")]
    [InlineData("create", "generate-job")]
    [InlineData("write", "generate-job")]
    [InlineData("commit", "generate-job")]
    [InlineData("cancel", "generate-job")]
    [InlineData("close", "generate-job")]
    [InlineData("write", "flatten")]
    [InlineData("commit", "flatten")]
    [InlineData("cancel", "flatten")]
    [InlineData("close", "flatten")]
    [InlineData("write", "overlay")]
    [InlineData("commit", "overlay")]
    [InlineData("cancel", "overlay")]
    [InlineData("close", "overlay")]
    [InlineData("create", "coalesce")]
    [InlineData("write", "coalesce")]
    [InlineData("commit", "coalesce")]
    [InlineData("cancel", "coalesce")]
    [InlineData("close", "coalesce")]
    [InlineData("create", "normalize")]
    [InlineData("write", "normalize")]
    [InlineData("commit", "normalize")]
    [InlineData("cancel", "normalize")]
    [InlineData("close", "normalize")]
    [InlineData("create", "externalize")]
    [InlineData("write", "externalize")]
    [InlineData("commit", "externalize")]
    [InlineData("cancel", "externalize")]
    [InlineData("close", "externalize")]
    [InlineData("create", "merge")]
    [InlineData("write", "merge")]
    [InlineData("commit", "merge")]
    [InlineData("cancel", "merge")]
    [InlineData("close", "merge")]
    public async Task OutputFaultsRefundOwnersAndRespectCommit(string phase, string mode)
    {
        using InMemoryQpdfHost storage = await Storage(mode);
        RecordingMemoryBudget root = new(64L * 1024 * 1024);
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        {
            QpdfExecutionResult result = await Run(new(storage, owner, phase, cancellation), mode, cancellation.Token);
            Assert.Equal(phase == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.True(storage.TryGetOutput(Output, out byte[] bytes));
            if (phase == "close") { Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8)); }
            else { Assert.Equal(new byte[] { 17, 23, 41 }, bytes); }
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}


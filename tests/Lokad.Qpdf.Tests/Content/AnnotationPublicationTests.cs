using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Writing;

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
        byte[] input = mode.StartsWith("attachment", StringComparison.Ordinal) ? File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf"))
            : mode.StartsWith("os-", StringComparison.Ordinal) ? ObjectStreamPlanningTests.Fixture(mode.Contains("qdf", StringComparison.Ordinal) ? 16 : 201)
            : mode.StartsWith("generate-text", StringComparison.Ordinal)
            ? AppearanceProgramTests.Fixture(new string(' ', 32768) + "/Tx BMC\n0 0 10 10 re f EMC", "", "/DA (/Helv 17 Tf)", "")
            : mode.StartsWith("generate", StringComparison.Ordinal) ? AppearanceGenerationRegressionTests.CheckboxFixture(64)
            : mode.StartsWith("catalog-extras", StringComparison.Ordinal) || mode.StartsWith("split-max", StringComparison.Ordinal) ? CatalogStorageTests.Fixture(512)
            : mode.StartsWith("catalog", StringComparison.Ordinal) ? OrdinaryFormStorageTests.Fixture(512)
            : mode == "merge" ? FormMergeStorageTests.Fixture(new string('F', 32768), false) : mode is "overlay" or "flatten" ? FlattenResourceStorageTests.Fixture(64, false)
            : ContentSafetyTests.Fixture(System.Text.Encoding.ASCII.GetBytes(new string(' ', 524288) + "BI /W 1 /H 1 /BPC 8 /CS /G ID X EI"), "", false, 1);
        if (mode.StartsWith("os-preserve", StringComparison.Ordinal))
        {
            using InMemoryQpdfHost seed = new();
            seed.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
            QpdfExecutionResult generated = await Qpdf.ExecuteAsync(seed, new(["input", "--object-streams=generate", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, generated.Status);
            Assert.True(seed.TryGetOutput(Output, out input));
        }
        if (mode.StartsWith("attachment-copy", StringComparison.Ordinal))
        {
            using InMemoryQpdfHost seed = new();
            seed.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
            seed.AddInput(QpdfPath.From("payload"), [17], QpdfInputOwnership.Transfer);
            await Qpdf.AddAttachmentsAsync(seed, QpdfPath.From("input"), QpdfPath.From("source"),
                Enumerable.Range(0, 4).Select(index => new QpdfAttachmentSpec("payload", "key" + index, "name", null, null, null, null, false)).ToArray(), CancellationToken.None);
            Assert.True(seed.TryGetOutput(QpdfPath.From("source"), out byte[] source));
            host.AddInput(QpdfPath.From("source"), source, QpdfInputOwnership.Transfer);
        }
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("payload"), "abc"u8.ToArray(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("overlay"), mode == "merge" ? FormMergeStorageTests.Fixture(new string('F', 32768), true) : mode.StartsWith("overlay-form", StringComparison.Ordinal) ? OverlayFormStorageTests.Fixture() : FlattenResourceStorageTests.Fixture(4, false), QpdfInputOwnership.Transfer);
        QpdfPath initialPath = mode.StartsWith("split-max", StringComparison.Ordinal) || mode.StartsWith("os-split", StringComparison.Ordinal) ? QpdfPath.From("output-1") : Output;
        await using IQpdfOutputTransaction initial = await host.CreateOutputAsync(initialPath, CancellationToken.None);
        await initial.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await initial.CommitAsync(CancellationToken.None);
        return host;
    }

    private static ValueTask<QpdfExecutionResult> Run(Host host, string mode, CancellationToken token)
        => mode == "attachment-copy-job" ? Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","outputFile":"output","copyAttachmentsFrom":[{"file":"source","prefix":"copy-"}]}""", token)
            : mode == "attachment-copy" ? Qpdf.ExecuteAsync(host, new(["input", "--copy-attachments-from", "source", "--prefix=copy-", "--", "output"]), token)
            : mode == "attachment-job" ? Qpdf.ExecuteJobAsync(host, System.Text.Json.JsonSerializer.Serialize(new
            { inputFile = "input", outputFile = "output", addAttachment = Enumerable.Range(0, 4).Select(index => new
                { file = "payload", key = index.ToString("D3"), filename = "Euro \u20ac", description = new string('x', 256) }).ToArray() }), token)
            : mode == "attachment" ? Qpdf.ExecuteAsync(host, new(AttachmentArguments()), token)
            : mode == "os-qdf-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","objectStreams":"generate","qdf":""}""", token)
            : mode == "os-qdf-noids-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","objectStreams":"generate","qdf":"","noOriginalObjectIds":""}""", token)
            : mode == "os-split-qdf-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","objectStreams":"generate","qdf":"","splitPages":"1"}""", token)
            : mode == "os-split-qdf-noids-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","objectStreams":"generate","qdf":"","noOriginalObjectIds":"","splitPages":"1"}""", token)
            : mode == "os-generate-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","objectStreams":"generate"}""", token)
            : mode == "os-preserve-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","objectStreams":"preserve"}""", token)
            : mode == "os-split-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","objectStreams":"generate","splitPages":"1"}""", token)
            : mode == "os-linearized-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","objectStreams":"generate","linearize":true}""", token)
            : mode.StartsWith("os-", StringComparison.Ordinal) ? Qpdf.ExecuteAsync(host, new(mode switch {
                "os-qdf" => ["input", "--object-streams=generate", "--qdf", "output"],
                "os-qdf-noids" => ["input", "--object-streams=generate", "--qdf", "--no-original-object-ids", "output"],
                "os-split-qdf" => ["input", "--object-streams=generate", "--qdf", "--split-pages=1", "output"],
                "os-split-qdf-noids" => ["input", "--object-streams=generate", "--qdf", "--no-original-object-ids", "--split-pages=1", "output"],
                "os-split" => ["input", "--object-streams=generate", "--split-pages=1", "output"],
                "os-linearized" => ["input", "--object-streams=generate", "--linearize", "output"],
                "os-preserve" => ["input", "--object-streams=preserve", "output"],
                _ => ["input", "--object-streams=generate", "output"] }), token)
            : mode == "split-max-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","splitPages":"2147483647"}""", token)
            : mode == "split-max" ? Qpdf.ExecuteAsync(host, new(["input", "--split-pages=2147483647", "output"]), token)
            : mode == "catalog-extras-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1,1"}],"rotate":["+90:2"]}""", token)
            : mode == "catalog-extras" ? Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1,1", "--", "--rotate=+90:2", "output"]), token)
            : mode == "catalog-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1"}],"addAttachment":[{"file":"payload"}]}""", token)
            : mode == "catalog" ? Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1", "--", "--add-attachment", "payload", "--", "output"]), token)
            : mode == "overlay-form-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","overlay":{"file":"overlay"}}""", token)
            : mode is "generate-job" or "generate-text-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","generateAppearances":true}""", token)
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(mode == "merge" ? ["input", "--pages", ".", "1", "overlay", "1", "--", "output"] : mode is "overlay" or "overlay-form"
            ? ["input", "--overlay", "overlay", "--", "output"]
            : ["input", mode switch { "generate" or "generate-text" => "--generate-appearances", "flatten" => "--flatten-annotations=all", "coalesce" => "--coalesce-contents",
                "normalize" => "--normalize-content=y", _ => "--externalize-inline-images" }, "--ii-min-bytes=0", "--", "output"]), token);

    // Keep exhaustive growth refusal bounded; construction tests cover 65+ name trees.
    private static IReadOnlyList<string> AttachmentArguments()
    {
        List<string> arguments = ["input"];
        for (int index = 0; index < 4; index++)
        { arguments.AddRange(["--add-attachment", "payload", "--key=" + index.ToString("D3"), "--filename=Euro \u20ac", "--description=" + new string('x', 256), "--"]); }
        arguments.Add("output"); return arguments;
    }

    [Theory]
    [InlineData("os-qdf")]
    [InlineData("os-qdf-job")]
    [InlineData("os-qdf-noids")]
    [InlineData("os-qdf-noids-job")]
    [InlineData("os-split-qdf")]
    [InlineData("os-split-qdf-job")]
    [InlineData("os-split-qdf-noids")]
    [InlineData("os-split-qdf-noids-job")]
    [InlineData("os-generate")]
    [InlineData("os-generate-job")]
    [InlineData("os-preserve")]
    [InlineData("os-preserve-job")]
    [InlineData("os-split")]
    [InlineData("os-split-job")]
    [InlineData("os-linearized")]
    [InlineData("os-linearized-job")]
    [InlineData("catalog-extras")]
    [InlineData("catalog-extras-job")]
    [InlineData("split-max")]
    [InlineData("split-max-job")]
    [InlineData("attachment")]
    [InlineData("attachment-job")]
    [InlineData("attachment-copy")]
    [InlineData("attachment-copy-job")]
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
        Assert.InRange(growths, 1, mode.Contains("qdf", StringComparison.Ordinal) ? 128
            : mode.StartsWith("os-", StringComparison.Ordinal) ? 96 : 64);
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
                QpdfPath target = mode.StartsWith("split-max", StringComparison.Ordinal) || mode.StartsWith("os-split", StringComparison.Ordinal) ? QpdfPath.From("output-1") : Output;
                Assert.True(storage.TryGetOutput(target, out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes);
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(0L, root.ReservedBytes); Assert.NotEmpty(result.Diagnostics);
        }
    }

    [Theory]
    [InlineData("create", "os-qdf")]
    [InlineData("write", "os-qdf")]
    [InlineData("commit", "os-qdf")]
    [InlineData("cancel", "os-qdf")]
    [InlineData("close", "os-qdf")]
    [InlineData("create", "os-qdf-job")]
    [InlineData("write", "os-qdf-job")]
    [InlineData("commit", "os-qdf-job")]
    [InlineData("cancel", "os-qdf-job")]
    [InlineData("close", "os-qdf-job")]
    [InlineData("create", "os-qdf-noids")]
    [InlineData("write", "os-qdf-noids")]
    [InlineData("commit", "os-qdf-noids")]
    [InlineData("cancel", "os-qdf-noids")]
    [InlineData("close", "os-qdf-noids")]
    [InlineData("create", "os-qdf-noids-job")]
    [InlineData("write", "os-qdf-noids-job")]
    [InlineData("commit", "os-qdf-noids-job")]
    [InlineData("cancel", "os-qdf-noids-job")]
    [InlineData("close", "os-qdf-noids-job")]
    [InlineData("create", "os-split-qdf")]
    [InlineData("write", "os-split-qdf")]
    [InlineData("commit", "os-split-qdf")]
    [InlineData("cancel", "os-split-qdf")]
    [InlineData("close", "os-split-qdf")]
    [InlineData("create", "os-split-qdf-job")]
    [InlineData("write", "os-split-qdf-job")]
    [InlineData("commit", "os-split-qdf-job")]
    [InlineData("cancel", "os-split-qdf-job")]
    [InlineData("close", "os-split-qdf-job")]
    [InlineData("create", "os-split-qdf-noids")]
    [InlineData("write", "os-split-qdf-noids")]
    [InlineData("commit", "os-split-qdf-noids")]
    [InlineData("cancel", "os-split-qdf-noids")]
    [InlineData("close", "os-split-qdf-noids")]
    [InlineData("create", "os-split-qdf-noids-job")]
    [InlineData("write", "os-split-qdf-noids-job")]
    [InlineData("commit", "os-split-qdf-noids-job")]
    [InlineData("cancel", "os-split-qdf-noids-job")]
    [InlineData("close", "os-split-qdf-noids-job")]
    [InlineData("create", "os-generate")]
    [InlineData("write", "os-generate")]
    [InlineData("commit", "os-generate")]
    [InlineData("cancel", "os-generate")]
    [InlineData("close", "os-generate")]
    [InlineData("create", "os-generate-job")]
    [InlineData("write", "os-generate-job")]
    [InlineData("commit", "os-generate-job")]
    [InlineData("cancel", "os-generate-job")]
    [InlineData("close", "os-generate-job")]
    [InlineData("create", "os-preserve")]
    [InlineData("write", "os-preserve")]
    [InlineData("commit", "os-preserve")]
    [InlineData("cancel", "os-preserve")]
    [InlineData("close", "os-preserve")]
    [InlineData("create", "os-preserve-job")]
    [InlineData("write", "os-preserve-job")]
    [InlineData("commit", "os-preserve-job")]
    [InlineData("cancel", "os-preserve-job")]
    [InlineData("close", "os-preserve-job")]
    [InlineData("create", "os-split")]
    [InlineData("write", "os-split")]
    [InlineData("commit", "os-split")]
    [InlineData("cancel", "os-split")]
    [InlineData("close", "os-split")]
    [InlineData("create", "os-split-job")]
    [InlineData("write", "os-split-job")]
    [InlineData("commit", "os-split-job")]
    [InlineData("cancel", "os-split-job")]
    [InlineData("close", "os-split-job")]
    [InlineData("create", "os-linearized")]
    [InlineData("write", "os-linearized")]
    [InlineData("commit", "os-linearized")]
    [InlineData("cancel", "os-linearized")]
    [InlineData("close", "os-linearized")]
    [InlineData("create", "os-linearized-job")]
    [InlineData("write", "os-linearized-job")]
    [InlineData("commit", "os-linearized-job")]
    [InlineData("cancel", "os-linearized-job")]
    [InlineData("close", "os-linearized-job")]
    [InlineData("create", "catalog-extras")]
    [InlineData("write", "catalog-extras")]
    [InlineData("commit", "catalog-extras")]
    [InlineData("cancel", "catalog-extras")]
    [InlineData("close", "catalog-extras")]
    [InlineData("create", "catalog-extras-job")]
    [InlineData("write", "catalog-extras-job")]
    [InlineData("commit", "catalog-extras-job")]
    [InlineData("cancel", "catalog-extras-job")]
    [InlineData("close", "catalog-extras-job")]
    [InlineData("create", "split-max")]
    [InlineData("write", "split-max")]
    [InlineData("commit", "split-max")]
    [InlineData("cancel", "split-max")]
    [InlineData("close", "split-max")]
    [InlineData("create", "split-max-job")]
    [InlineData("write", "split-max-job")]
    [InlineData("commit", "split-max-job")]
    [InlineData("cancel", "split-max-job")]
    [InlineData("close", "split-max-job")]
    [InlineData("create", "attachment")]
    [InlineData("write", "attachment")]
    [InlineData("commit", "attachment")]
    [InlineData("cancel", "attachment")]
    [InlineData("close", "attachment")]
    [InlineData("create", "attachment-job")]
    [InlineData("write", "attachment-job")]
    [InlineData("commit", "attachment-job")]
    [InlineData("cancel", "attachment-job")]
    [InlineData("close", "attachment-job")]
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
    [InlineData("create", "attachment-copy")]
    [InlineData("write", "attachment-copy")]
    [InlineData("commit", "attachment-copy")]
    [InlineData("cancel", "attachment-copy")]
    [InlineData("close", "attachment-copy")]
    [InlineData("create", "attachment-copy-job")]
    [InlineData("write", "attachment-copy-job")]
    [InlineData("commit", "attachment-copy-job")]
    [InlineData("cancel", "attachment-copy-job")]
    [InlineData("close", "attachment-copy-job")]
    public async Task OutputFaultsRefundOwnersAndRespectCommit(string phase, string mode)
    {
        using InMemoryQpdfHost storage = await Storage(mode);
        RecordingMemoryBudget root = new(64L * 1024 * 1024);
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        {
            QpdfExecutionResult result = await Run(new(storage, owner, phase, cancellation), mode, cancellation.Token);
            Assert.Equal(phase == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            QpdfPath target = mode.StartsWith("split-max", StringComparison.Ordinal) || mode.StartsWith("os-split", StringComparison.Ordinal) ? QpdfPath.From("output-1") : Output;
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.True(storage.TryGetOutput(target, out byte[] bytes));
            if (phase == "close") { Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8)); }
            else { Assert.Equal(new byte[] { 17, 23, 41 }, bytes); }
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}


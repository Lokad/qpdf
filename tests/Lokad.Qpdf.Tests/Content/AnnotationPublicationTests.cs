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
        byte[] input = mode == "signature" ? File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "signature-incremental.pdf"))
            : mode.StartsWith("navigation", StringComparison.Ordinal) ? RotationDestinationContextTests.Source("explicit")
            : mode.StartsWith("overlay-clip", StringComparison.Ordinal) ? OverlayAnnotationClippingTests.Target()
            : mode.StartsWith("labels-split", StringComparison.Ordinal)
            ? File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "labels-split-start.pdf"))
            : mode.StartsWith("split-membership", StringComparison.Ordinal) ? SplitPageMembershipTests.Fixture("shared", true)
            : mode.StartsWith("source-groups", StringComparison.Ordinal) || mode.StartsWith("zero-collation", StringComparison.Ordinal) ? SourceGroupIdentityTests.Fixture("A")
            : mode == "preserve-many" ? PreservationAdmissionTests.Scalars(128)
            : mode is "preserve-rollback" or "preserve-split" ? PreservationGraphTests.RollbackFixture()
            : mode.StartsWith("form-indirect", StringComparison.Ordinal) ? FormNeedAppearancesTests.Fixture("9 3 R", "true", 3)
            : mode.StartsWith("attachment", StringComparison.Ordinal) ? File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf"))
            : mode.StartsWith("rotation", StringComparison.Ordinal) ? RotationAppearanceTests.SharedFixture(270)
            : mode.StartsWith("os-", StringComparison.Ordinal) ? ObjectStreamPlanningTests.Fixture(mode.Contains("qdf", StringComparison.Ordinal) ? 16 : 201)
            : mode.StartsWith("generate-parent", StringComparison.Ordinal) ? AppearanceParentScopeTests.Fixture("value", "info", false)
            : mode.StartsWith("generate-array", StringComparison.Ordinal) ? AnnotationArrayIsolationTests.Fixture("button", "attachment", true, 3, 1)
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
        host.AddInput(QpdfPath.From("overlay"), mode.StartsWith("overlay-clip", StringComparison.Ordinal) ? OverlayAnnotationClippingTests.StateSource(64)
            : mode.StartsWith("overlay-generate", StringComparison.Ordinal)
            ? OverlayGenerationContextTests.Source(false, false, 90, 2, true) : mode == "overlay-form-indirect"
            ? OverlayDefaultTests.Fixture("/DA 9 0 R /Q 10 0 R /NeedAppearances 11 0 R", "(/F1 18 Tf 0.5 g)", "2", "true", false, "LIVE")
            : mode == "merge" ? FormMergeStorageTests.Fixture(new string('F', 32768), true) : mode.StartsWith("overlay-form", StringComparison.Ordinal) ? OverlayFormStorageTests.Fixture() : FlattenResourceStorageTests.Fixture(4, false), QpdfInputOwnership.Transfer);
        QpdfPath initialPath = mode == "preserve-split" || mode.StartsWith("labels-split", StringComparison.Ordinal) || mode.StartsWith("split-", StringComparison.Ordinal) || mode.StartsWith("os-split", StringComparison.Ordinal) ? QpdfPath.From("output-1") : Output;
        await using IQpdfOutputTransaction initial = await host.CreateOutputAsync(initialPath, CancellationToken.None);
        await initial.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await initial.CommitAsync(CancellationToken.None);
        return host;
    }

    private static ValueTask<QpdfExecutionResult> Run(Host host, string mode, CancellationToken token)
        => mode == "signature" ? Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "3,1,3", "--", "output"]), token)
            : mode == "zero-collation-job" ? Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","outputFile":"output","collate":"1,0","pages":[{"file":".","range":"1,2"},{"file":".","range":"3"}]}""", token)
            : mode == "zero-collation" ? Qpdf.ExecuteAsync(host, new(["input", "--collate=1,0", "--pages", ".", "1,2", ".", "3", "--", "output"]), token)
            : mode == "split-membership-job" ? Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","outputFile":"output","splitPages":"1","pages":[{"file":".","range":"1"},{"file":".","range":"2,3"}]}""", token)
            : mode == "split-membership" ? Qpdf.ExecuteAsync(host, new(["input", "--split-pages=1", "--pages", ".", "1", ".", "2,3", "--", "output"]), token)
            : mode == "source-groups-job" ? Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1"},{"file":"input","range":"2,3"}]}""", token)
            : mode.StartsWith("source-groups", StringComparison.Ordinal) ? Qpdf.ExecuteAsync(host, new(mode == "source-groups-linear"
                ? ["input", "--pages", ".", "1", "input", "2,3", "--", "--linearize", "--object-streams=generate", "output"]
                : ["input", "--pages", ".", "1", "input", "2,3", "--", "output"]), token)
            : mode.StartsWith("preserve-", StringComparison.Ordinal) ? Qpdf.ExecuteAsync(host, new(mode == "preserve-split"
            ? ["input", "--preserve-unreferenced", "--split-pages=1", "output"] : ["input", "--preserve-unreferenced", "output"]), token)
            : mode == "labels-split" ? Qpdf.ExecuteAsync(host, new(["input", "--split-pages=1", "output"]), token)
            : mode == "labels-split-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","splitPages":"1"}""", token)
            : mode.StartsWith("form-indirect", StringComparison.Ordinal) ? Qpdf.ExecuteJobAsync(host, mode == "form-indirect-both"
            ? """{"inputFile":"input","outputFile":"output","generateAppearances":true,"flattenAnnotations":"all"}"""
            : """{"inputFile":"input","outputFile":"output","generateAppearances":true}""", token)
            : mode == "navigation-job" ? Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"flattenRotation\":true}", token)
            : mode == "navigation" ? Qpdf.ExecuteAsync(host, new(["input", "--flatten-rotation", "output"]), token)
            : mode.StartsWith("rotation", StringComparison.Ordinal) ? RotationAppearanceTests.Run(host, mode == "rotation-job" ? "job" : "argv", token)
            : mode == "attachment-copy-job" ? Qpdf.ExecuteJobAsync(host,
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
            : mode == "overlay-generate-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","generateAppearances":true,"overlay":{"file":"overlay"}}""", token)
            : mode == "overlay-generate" ? Qpdf.ExecuteAsync(host, new(["input", "--overlay", "overlay", "--", "--generate-appearances", "output"]), token)
            : mode is "overlay-form-job" or "overlay-form-indirect" or "overlay-clip-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","overlay":{"file":"overlay"}}""", token)
            : mode is "generate-job" or "generate-text-job" or "generate-array-job" or "generate-parent-job" ? Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","generateAppearances":true}""", token)
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(mode == "merge" ? ["input", "--pages", ".", "1", "overlay", "1", "--", "output"] : mode is "overlay" or "overlay-form" or "overlay-clip"
            ? ["input", "--overlay", "overlay", "--", "output"]
            : ["input", mode switch { "generate" or "generate-text" or "generate-array" or "generate-parent" => "--generate-appearances", "flatten" => "--flatten-annotations=all", "coalesce" => "--coalesce-contents",
                "normalize" => "--normalize-content=y", _ => "--externalize-inline-images" }, "--ii-min-bytes=0", "--", "output"]), token);

    // Keep exhaustive growth refusal bounded; construction tests cover 65+ name trees.
    private static IReadOnlyList<string> AttachmentArguments()
    {
        List<string> arguments = ["input"];
        for (int index = 0; index < 4; index++)
        { arguments.AddRange(["--add-attachment", "payload", "--key=" + index.ToString("D3"), "--filename=Euro \u20ac", "--description=" + new string('x', 256), "--"]); }
        arguments.Add("output"); return arguments;
    }

    public static IEnumerable<object[]> Preparations()
    {
        foreach (string mode in new[] {
            "signature",
            "source-groups",
            "source-groups-job",
            "source-groups-linear",
            "os-qdf",
            "os-qdf-job",
            "os-qdf-noids",
            "os-qdf-noids-job",
            "os-split-qdf",
            "os-split-qdf-job",
            "os-split-qdf-noids",
            "os-split-qdf-noids-job",
            "os-generate",
            "os-generate-job",
            "os-preserve",
            "os-preserve-job",
            "os-split",
            "os-split-job",
            "os-linearized",
            "os-linearized-job",
            "catalog-extras",
            "catalog-extras-job",
            "split-max",
            "split-max-job",
            "attachment",
            "attachment-job",
            "attachment-copy",
            "attachment-copy-job",
            "catalog",
            "catalog-job",
            "flatten",
            "overlay",
            "overlay-form",
            "overlay-form-job",
            "overlay-form-indirect",
            "overlay-generate",
            "overlay-generate-job",
            "overlay-clip",
            "overlay-clip-job",
            "coalesce",
            "normalize",
            "externalize",
            "merge",
            "preserve-many",
            "preserve-rollback",
            "preserve-split",
            "form-indirect-generate",
            "form-indirect-both",
            "generate",
            "generate-job",
            "generate-text",
            "generate-text-job",
            "generate-array",
            "generate-array-job",
            "generate-parent",
            "generate-parent-job",
            "navigation",
            "navigation-job",
            "rotation",
            "rotation-job",
            "labels-split",
            "labels-split-job",
            "split-membership",
            "split-membership-job",
            "zero-collation",
            "zero-collation-job",
        }) { yield return [mode]; }
    }

    [Theory]
    [MemberData(nameof(Preparations))]
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
        Assert.InRange(growths, 1, mode.Contains("qdf", StringComparison.Ordinal) || mode.StartsWith("labels-split", StringComparison.Ordinal) ? 128
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
                QpdfPath target = mode == "preserve-split" || mode.StartsWith("labels-split", StringComparison.Ordinal) || mode.StartsWith("split-", StringComparison.Ordinal) || mode.StartsWith("os-split", StringComparison.Ordinal) ? QpdfPath.From("output-1") : Output;
                Assert.True(storage.TryGetOutput(target, out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes);
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(0L, root.ReservedBytes); Assert.NotEmpty(result.Diagnostics);
        }
    }

    public static IEnumerable<object[]> PublicationFailures()
    {
        // Foundation owns shared host boundaries. These cases retain each
        // prepared graph's write-failure cleanup; generation and placement also
        // cover complete boundaries for their distinct result lifetimes.
        foreach (object[] preparation in Preparations())
        {
            string mode = (string)preparation[0];
            if (mode.EndsWith("-job", StringComparison.Ordinal) || mode == "source-groups-linear") { continue; }
            foreach (string phase in mode is "generate" or "overlay-form"
                ? new[] { "create", "write", "commit", "cancel", "close" } : ["write"])
            { yield return [phase, mode]; }
        }
    }

    [Theory]
    [MemberData(nameof(PublicationFailures))]
    public async Task OutputFaultsRefundOwnersAndRespectCommit(string phase, string mode)
    {
        using InMemoryQpdfHost storage = await Storage(mode);
        RecordingMemoryBudget root = new(64L * 1024 * 1024);
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        {
            QpdfExecutionResult result = await Run(new(storage, owner, phase, cancellation), mode, cancellation.Token);
            Assert.Equal(phase == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            QpdfPath target = mode == "preserve-split" || mode.StartsWith("labels-split", StringComparison.Ordinal) || mode.StartsWith("split-", StringComparison.Ordinal) || mode.StartsWith("os-split", StringComparison.Ordinal) ? QpdfPath.From("output-1") : Output;
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.True(storage.TryGetOutput(target, out byte[] bytes));
            if (phase == "close") { Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8)); }
            else { Assert.Equal(new byte[] { 17, 23, 41 }, bytes); }
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}


using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class CatalogRecoveryTests
{
    private static readonly string[] Kinds = ["extras", "outlines", "structure", "mark", "late", "names"];

    private static byte[] Input(string kind)
    {
        string extra = kind switch
        {
            "outlines" => "/Outlines 5 0 R /Audit 5 0 R",
            "direct" => "/Outlines << /Tag (REJECTED) /Child 6 0 R /Bad 1 0 R >>",
            "structure" => "/StructTreeRoot 5 0 R /Audit 5 0 R",
            "mark" => "/StructTreeRoot << /Type /StructTreeRoot /Shared 9 0 R >> /MarkInfo 5 0 R /Audit 5 0 R",
            "late" => "/StructTreeRoot 9 0 R /MarkInfo 5 0 R /Audit 5 0 R",
            "names" => "/Names << /First 10 0 R /Bad 5 0 R /Alias 5 0 R /Last 10 0 R >>",
            "cycle" => "/A 5 0 R /Z 5 0 R /Cycle 7 0 R",
            _ => "/A 5 0 R /Z 5 0 R"
        };
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + extra + " /Good 10 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length 4 >>", "q Q\n"u8.ToArray()),
            (5, "<< /Tag (REJECTED) /Child 6 0 R /Shared 9 0 R /Bad 1 0 R >>", null),
            (6, "<< /Tag (CHILD) /Back 5 0 R >>", null),
            (7, kind == "cycle" ? "<< /Next 8 0 R >>" : "null", null),
            (8, kind == "cycle" ? "<< /Back 7 0 R >>" : "null", null),
            (9, "<< /Tag (KEEP) >>", null), (10, "<< /Length 4 >>", "kept"u8.ToArray())], 1);
    }

    public static IEnumerable<object[]> InternalCases()
    {
        foreach (string kind in Kinds)
        foreach (bool shared in new[] { false, true }) { yield return [kind, shared]; }
        yield return ["direct", false];
        yield return ["direct", true];
    }

    [Theory]
    [MemberData(nameof(InternalCases))]
    public async Task RecoveryRetiresOnlyNewObjectsAndReusesIds(string kind, bool shared)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input(kind), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 100, owner);
            PdfReference? prior = shared ? Assert.IsType<PdfReference>(copier.CopyValue(new PdfReference(9, 0), 0)) : null;
            QpdfObject? kept = prior is null ? null : copier.CopiedObjects[prior.ObjectNumber];
            int firstId = copier.NextId;
            Assert.Null(CatalogCopy.TryCopyOutlines(document.Engine, copier, true, false, governor, CancellationToken.None));
            Assert.Equal((null, null), CatalogCopy.TryCopyStructure(document.Engine, copier, true, false, governor, CancellationToken.None));
            Assert.Equal(firstId, copier.NextId);
            IReadOnlyDictionary<PdfName, QpdfObject> extras = CatalogCopy.TryCopyExtras(document.Engine, copier, true, false, governor, owner, CancellationToken.None);
            Assert.DoesNotContain(5, copier.SourceToDestination.Keys);
            Assert.DoesNotContain(6, copier.SourceToDestination.Keys);
            Assert.Equal(shared, copier.SourceToDestination.ContainsKey(9));
            Assert.Equal(firstId, copier.SourceToDestination[10]);
            Assert.Equal(firstId + 1, copier.NextId);
            Assert.Equal(shared ? 2 : 1, copier.CopiedObjects.Count);
            Assert.Equal(firstId, Assert.IsType<PdfReference>(extras[PdfName.FromAscii("Good")]).ObjectNumber);
            if (prior is not null) { Assert.Same(kept, copier.CopiedObjects[prior.ObjectNumber]); }
            if (kind == "names")
            {
                PdfDictionary names = Assert.IsType<PdfDictionary>(extras[PdfName.FromAscii("Names")]);
                Assert.False(names.TryGet(PdfName.FromAscii("Bad"), out _));
                Assert.False(names.TryGet(PdfName.FromAscii("Alias"), out _));
                Assert.True(names.TryGet(PdfName.FromAscii("First"), out QpdfObject? first));
                Assert.True(names.TryGet(PdfName.FromAscii("Last"), out QpdfObject? last));
                Assert.Equal(first, last);
            }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    public static IEnumerable<object[]> PublicCases()
    {
        foreach (string kind in Kinds)
        foreach (bool job in new[] { false, true })
        foreach (string operation in new[] { "copy", "select", "linearize" }) { yield return [kind, job, operation]; }
    }

    private static Task<QpdfExecutionResult> Run(IQpdfHost host, bool job, string operation, CancellationToken token)
    {
        if (job)
        {
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["compressStreams"] = "n" };
            if (operation == "select") { settings["pages"] = new[] { new { file = ".", range = "1" } }; }
            if (operation == "linearize") { settings["linearize"] = true; }
            return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), token).AsTask();
        }
        List<string> argv = ["input", "--compress-streams=n"];
        if (operation == "select") { argv.AddRange(["--pages", ".", "1", "--"]); }
        if (operation == "linearize") { argv.Add("--linearize"); }
        argv.Add("output");
        return Qpdf.ExecuteAsync(host, new(argv), token).AsTask();
    }

    [Theory]
    [MemberData(nameof(PublicCases))]
    public async Task PublicRecoveryPublishesCleanGraphs(string kind, bool job, string operation)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input(kind), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, operation, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] pdf));
        string text = Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("REJECTED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CHILD", text, StringComparison.Ordinal);
        host.AddInput(QpdfPath.From("reread"), pdf, QpdfInputOwnership.Copy);
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("reread"), CancellationToken.None);
        Assert.All(check.Checks, x => Assert.NotEqual(QpdfCheckStatus.Failed, x.Status));
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task PublicStreamSurvivesRecoveredCopyAndHostClosure()
    {
        PdfStream escaped;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Input("extras"), QpdfInputOwnership.Copy);
            Assert.Equal(QpdfExitCode.Success, (await Run(host, false, "copy", CancellationToken.None)).ExitCode);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] pdf));
            host.AddInput(QpdfPath.From("reread"), pdf, QpdfInputOwnership.Copy);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread"), CancellationToken.None))
            {
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
                Assert.True(catalog.TryGet(PdfName.FromAscii("Good"), out QpdfObject? raw));
                escaped = Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(raw).ObjectNumber));
            }
            Assert.Equal(0, host.OpenLeaseCount);
        }
        Assert.Equal("kept"u8.ToArray(), escaped.Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidCycleAfterSkippedRootsKeepsItsIdentity(bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input("cycle"), QpdfInputOwnership.Copy);
        Assert.Equal(QpdfExitCode.Success, (await Run(host, job, "copy", CancellationToken.None)).ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] pdf));
        host.AddInput(QpdfPath.From("reread"), pdf, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread"), CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("Cycle"), out QpdfObject? first));
        PdfReference root = Assert.IsType<PdfReference>(first);
        PdfDictionary cycle = Assert.IsType<PdfDictionary>(document.GetObject(root.ObjectNumber));
        Assert.True(cycle.TryGet(PdfName.FromAscii("Next"), out QpdfObject? next));
        PdfDictionary child = Assert.IsType<PdfDictionary>(document.GetObject(Assert.IsType<PdfReference>(next).ObjectNumber));
        Assert.True(child.TryGet(PdfName.FromAscii("Back"), out QpdfObject? back));
        Assert.Equal(root, back);
    }

    private sealed class OutputHost(InMemoryQpdfHost inner, string fault) : IQpdfHost
    {
        internal int Created { get; private set; }
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Created += 1;
            return ValueTask.FromResult<IQpdfOutputTransaction>(fault == "write" ? new FailingWriteTransaction(path) : new FailingCommitTransaction(path));
        }
    }

    [Theory]
    [InlineData(false, "write")]
    [InlineData(true, "write")]
    [InlineData(false, "commit")]
    [InlineData(true, "commit")]
    public async Task OutputFailureAfterRecoveryPreservesPreviousTarget(bool job, string fault)
    {
        using InMemoryQpdfHost inner = new();
        inner.AddInput(QpdfPath.From("input"), Input("names"), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await inner.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        OutputHost host = new(inner, fault);
        QpdfExecutionResult result = await Run(host, job, "linearize", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(1, host.Created);
        Assert.True(inner.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0, inner.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionFailureDuringCatalogCopyRemainsFatal(bool sharedBudget)
    {
        using InMemoryQpdfHost input = new();
        input.AddInput(QpdfPath.From("input"), Input("names"), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(input, QpdfPath.From("input"), CancellationToken.None);
        RecordingMemoryBudget root = new(1L << 20) { RefuseOnGrowth = sharedBudget ? 2 : 0 };
        using IMemoryScope caller = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, caller);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = sharedBudget ? 1L << 20 : 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, limits, 100, owner);
            owner.Reserve(1024L - governor.Snapshot().LiveMemoryBytes - 384L);
            if (sharedBudget) { Assert.Throws<QpdfMemoryAdmissionException>(() => CatalogCopy.TryCopyExtras(document.Engine, copier, true, false, governor, owner, CancellationToken.None)); }
            else { Assert.Throws<QpdfLimitException>(() => CatalogCopy.TryCopyExtras(document.Engine, copier, true, false, governor, owner, CancellationToken.None)); }
            Assert.NotEmpty(copier.SourceToDestination);
        }
        governor.CompletePrivateStorage();
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class CancelingObjects(Action cancel) : Dictionary<int, QpdfObject>, IReadOnlyDictionary<int, QpdfObject>
    {
        bool IReadOnlyDictionary<int, QpdfObject>.TryGetValue(int key, out QpdfObject value)
        {
            bool found = TryGetValue(key, out QpdfObject? resolved);
            if (key == 9) { cancel(); }
            value = resolved ?? PdfNull.Instance;
            return found;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringPartialCopyIsNotSwallowed(bool caller)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input("extras"), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        CancelingObjects objects = new(cancellation.Cancel);
        foreach (int id in new[] { 1, 5, 6, 9, 10 }) { objects[id] = document.GetObject(id); }
        document.Engine.SetInspectionObjects(objects);
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 100, owner);
            Assert.ThrowsAny<OperationCanceledException>(() => CatalogCopy.TryCopyExtras(document.Engine, copier, true, false,
                governor, owner, caller ? cancellation.Token : CancellationToken.None));
        }
        governor.CompletePrivateStorage();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        await document.DisposeAsync();
        Assert.Equal(0, host.OpenLeaseCount);
    }
}

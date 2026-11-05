using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Tests.Writing;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationBorderFailureTests
{
    public static IEnumerable<object[]> MalformedCases()
    {
        string huge = "1" + new string('0', 308) + ".0";
        foreach (string style in new[]
        {
            "/BS /Bad", "/BS << /W -1 >>", "/BS << /W /Bad >>",
            "/BS << /S /D /D [-1 2] >>", "/BS << /S /D /D [0 0] >>",
            "/BS << /S /D /D /Bad >>", "/Border [0 0]", "/Border [-1 0 2]",
            "/BS << /W " + huge + " >>", "/BS << /S /D /D [" + huge + "] >>",
        })
        foreach (bool job in new[] { false, true }) { yield return [style, job]; }
    }

    [Theory]
    [MemberData(nameof(MalformedCases))]
    public async Task ActiveMalformedOrOverflowingBordersPreservePriorOutput(string style, bool job)
    {
        RecordingMemoryBudget budget = new(8L * 1024 * 1024);
        using (IMemoryScope owner = budget.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        {
            AddInputs(host, style);
            await Seed(host);
            QpdfExecutionResult result = await Execute(host, job, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, d => d.Severity == QpdfDiagnosticSeverity.Error);
            AssertPrior(host);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData(false, "write")]
    [InlineData(true, "commit")]
    [InlineData(false, "cancel")]
    [InlineData(true, "write")]
    [InlineData(false, "commit")]
    [InlineData(true, "cancel")]
    public async Task BorderProjectionCannotPublishThroughOutputFailure(bool job, string fault)
    {
        RecordingMemoryBudget budget = new(8L * 1024 * 1024);
        using (IMemoryScope owner = budget.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        {
            AddInputs(host, "/BS << /W 3 /S /D /D [4 2] >>");
            await Seed(host);
            using CancellationTokenSource cancellation = new();
            SelectiveAttachmentExtractionTests.FaultHost faulting = new(host, fault, cancellation);
            QpdfExecutionResult result = await Execute(faulting, job, cancellation.Token);
            Assert.Equal(fault == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(1, faulting.Created); AssertPrior(host);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BothDashPassesObserveCallerAndGovernorCancellation(bool caller, bool constructionPass)
    {
        const int count = 2048;
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(document.Engine, construction, QpdfLimits.Default, 1, copierMemory);
        QpdfObject[] values = Enumerable.Repeat<QpdfObject>(new PdfReference(1, 0), count).ToArray();
        PdfArray dash = new(values);
        PdfDictionary source = Dictionary(("Subtype", PdfName.FromAscii("Link")),
            ("BS", Dictionary(("W", new PdfInteger("3", 3)), ("S", PdfName.FromAscii("D")), ("D", dash))));
        CancelingObjects objects = new(cancellation.Cancel, (constructionPass ? count : 0) + 16)
        { [1] = new PdfInteger("4", 4) };
        using (ParserMemoryScope memoMemory = ParserMemoryScope.Create(governor))
        {
            Dictionary<(int, AnnotationCopyKind, bool), PdfReference> copies = ParserContainers.CreateDictionary<(int, AnnotationCopyKind, bool), PdfReference>(memoMemory);
            long before = governor.Snapshot().LiveMemoryBytes;
            using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, caller ? cancellation.Token : CancellationToken.None))
            {
                PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out _);
                int allocator = copier.NextId;
                Assert.ThrowsAny<OperationCanceledException>(() => AnnotationBorders.Apply(source, entries,
                    [2, 0, 0, 2, 0, 0], new(objects), graph, copier, copies, memoMemory, ref allocator));
            }
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Empty(copies);
            Assert.Empty(copier.CopiedObjects); Assert.Equal(objects.CancelAt, objects.Reads);
            Assert.Equal(count, dash.Count); Assert.Same(values[0], dash[count - 1]);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeDashRefusesBeforeItsDestinationArrayIsAllocated(bool shared)
    {
        RecordingMemoryBudget budget = new(shared ? 65536L : 8L * 1024 * 1024);
        using (IMemoryScope owner = budget.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        {
            host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
            QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shared ? 8L * 1024 * 1024 : 65536L };
            ResourceGovernor governor = new(limits, CancellationToken.None, host);
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
            using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, construction, QpdfLimits.Default, 1, copierMemory);
            PdfInteger four = new("4", 4);
            PdfArray dash = new(Enumerable.Repeat<QpdfObject>(four, 262144).ToArray());
            PdfDictionary source = Dictionary(("Subtype", PdfName.FromAscii("Link")),
                ("BS", Dictionary(("W", new PdfInteger("3", 3)), ("S", PdfName.FromAscii("D")), ("D", dash))));
            using (ParserMemoryScope memoMemory = ParserMemoryScope.Create(governor))
            {
                Dictionary<(int, AnnotationCopyKind, bool), PdfReference> copies = ParserContainers.CreateDictionary<(int, AnnotationCopyKind, bool), PdfReference>(memoMemory);
                long before = governor.Snapshot().LiveMemoryBytes;
                using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None))
                {
                    PdfNameTable<QpdfObject> entries = PdfDictionaryRebuild.CreateEntries(graph, out _);
                    int allocator = copier.NextId; long allocated = GC.GetAllocatedBytesForCurrentThread();
                    Exception? failure = Record.Exception(() => AnnotationBorders.Apply(source, entries,
                        [2, 0, 0, 2, 0, 0], new(copier.CopiedObjects), graph, copier, copies, memoMemory, ref allocator));
                    Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
                    if (shared) { Assert.IsType<QpdfMemoryAdmissionException>(failure); }
                    else { Assert.IsType<QpdfLimitException>(failure); }
                }
                Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Empty(copies);
                Assert.Empty(copier.CopiedObjects); Assert.Same(four, dash[dash.Count - 1]);
            }
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Fact]
    public async Task FinalHeaderRefusalKeepsTheOriginalAfterBorderObjectsWerePrepared()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        PdfArray dash = new([new PdfInteger("4", 4), new PdfInteger("2", 2)]);
        PdfDictionary style = Dictionary(("W", new PdfInteger("3", 3)), ("S", PdfName.FromAscii("D")), ("D", new PdfReference(3, 0)));
        PdfDictionary source = Dictionary(("Subtype", PdfName.FromAscii("Link")), ("BS", new PdfReference(2, 0)),
            ("Rect", new PdfArray([new PdfInteger("10", 10), new PdfInteger("20", 20), new PdfInteger("90", 90), new PdfInteger("80", 80)])));
        long peak = 0L;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
            using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, construction, QpdfLimits.Default, 1, copierMemory);
            copier.AddIndirectObjectWithId(source, 1); copier.AddIndirectObjectWithId(style, 2); copier.AddIndirectObjectWithId(dash, 3);
            ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = attempt == 0 ? 65536L : peak - 1 }, CancellationToken.None);
            using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
            {
                Dictionary<(int, AnnotationCopyKind, bool), PdfReference> copies = ParserContainers.CreateDictionary<(int, AnnotationCopyKind, bool), PdfReference>(memory);
                int allocator = copier.NextId;
                if (attempt == 0)
                {
                    Overlays.TransformOverlayRect(copier, 1, [2, 0, 0, 2, 0, 0], governor, memory, CancellationToken.None, copies, memory, ref allocator);
                    Assert.NotSame(source, copier.CopiedObjects[1]); peak = governor.Snapshot().PeakMemoryBytes;
                }
                else
                {
                    Assert.Throws<QpdfLimitException>(() => Overlays.TransformOverlayRect(copier, 1,
                        [2, 0, 0, 2, 0, 0], governor, memory, CancellationToken.None, copies, memory, ref allocator));
                    Assert.Same(source, copier.CopiedObjects[1]);
                    Assert.True(copier.CopiedObjects.Count > 3); Assert.Equal(2, copies.Count);
                }
                Assert.Same(style, copier.CopiedObjects[2]); Assert.Same(dash, copier.CopiedObjects[3]);
            }
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
    }

    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    private sealed class CancelingObjects(Action cancel, int cancelAt) : Dictionary<int, QpdfObject>, IReadOnlyDictionary<int, QpdfObject>
    {
        internal int Reads { get; private set; }
        internal int CancelAt => cancelAt;
        bool IReadOnlyDictionary<int, QpdfObject>.TryGetValue(int key, [MaybeNullWhen(false)] out QpdfObject value)
        {
            bool found = base.TryGetValue(key, out value); Reads++;
            if (Reads == cancelAt) { cancel(); }
            return found;
        }
    }

    private static void AddInputs(InMemoryQpdfHost host, string style)
    {
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Rotate 90 /UserUnit 2 /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Link /Rect [10 20 90 80] " + style + " >>", null)], 1), QpdfInputOwnership.Transfer);
    }

    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await transaction.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await transaction.CommitAsync(CancellationToken.None);
    }

    private static void AssertPrior(InMemoryQpdfHost host)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes); Assert.Equal(0, host.OpenLeaseCount);
    }

    private static ValueTask<QpdfExecutionResult> Execute(IQpdfHost host, bool job, CancellationToken token)
        => job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
        { inputFile = "input", outputFile = "output", compressStreams = "n", overlay = new { file = "stamp" } }), token)
        : Qpdf.ExecuteAsync(host, new(["input", "--compress-streams=n", "--overlay", "stamp", "--", "output"]), token);
}

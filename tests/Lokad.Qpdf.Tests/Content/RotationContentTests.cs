using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class RotationContentTests
{
    internal static byte[] Fixture(string contents, string array, byte[] data) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 20] /Resources << >> /Rotate 90 /Contents " + contents + " /Annots [6 0 R] >>", null),
        (4, "<< /Length " + data.Length + " >>", data),
        (5, array, null),
        (6, "<< /Subtype /Text /Rect [1 2 3 4] >>", null)], 1);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ChecksInspectDirectAndIndirectArraysEqually(bool indirect, bool malformed)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(indirect ? "5 0 R" : "[4 0 R 4 0 R]", "[4 0 R 4 0 R]", malformed ? "(unterminated"u8.ToArray() : "q Q"u8.ToArray()), QpdfInputOwnership.Transfer);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
        QpdfCheckOutcome content = Assert.Single(result.Checks, c => c.Name == "page-content");
        Assert.Equal(malformed ? QpdfCheckStatus.Failed : QpdfCheckStatus.Passed, content.Status);
        Assert.Equal(!malformed, result.Passed);
        Assert.Contains(malformed ? "Unterminated string" : "2 content streams tokenized, 0 skipped", content.Detail);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RotationKeepsStreamOrderIdentityAndEscapedObjects(bool indirect, bool job)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(indirect ? "5 0 R" : "[4 0 R 4 0 R]", "[4 0 R 4 0 R]", "q Q"u8.ToArray()), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","flattenRotation":true,"pages":[{"file":".","range":"1"}]}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--flatten-rotation", "--pages", ".", "1", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); host.AddInput(QpdfPath.From("again"), bytes, QpdfInputOwnership.Transfer);
        PdfDictionary escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None))
        {
            escaped = document.PageReferences[0].Page;
            Assert.False(escaped.TryGet(PdfName.FromAscii("Rotate"), out _));
            Assert.True(escaped.TryGet(PdfName.FromAscii("Contents"), out QpdfObject raw)); PdfArray contents = Assert.IsType<PdfArray>(raw);
            Assert.Equal(4, contents.Count); Assert.Equal(contents[1], contents[2]); Assert.IsType<PdfReference>(contents[1]);
        }
        host.Dispose(); Assert.True(escaped.TryGet(PdfName.FromAscii("MediaBox"), out QpdfObject media));
        Assert.Equal(20L, Assert.IsType<PdfInteger>(Assert.IsType<PdfArray>(media)[2]).Value);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("42")]
    [InlineData("5 0 R")]
    public async Task DamagedContentKeepsRotationAndItsWarning(string body)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("5 0 R", body, "q"u8.ToArray()), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--flatten-rotation", "--pages", ".", "1", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode); Assert.Contains(result.Diagnostics, d => d.Message.Contains("unreadable content", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); host.AddInput(QpdfPath.From("again"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
        Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Rotate"), out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LargeOwnedArraysRefuseBeforeCopyingAndRefund(bool annotations, bool canceled)
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject>());
        using ParserMemoryScope fixtureStorage = ParserMemoryScope.Create(construction);
        Dictionary<int, XrefEntry> fixtureEntries = ParserContainers.CreateDictionary<int, XrefEntry>(fixtureStorage);
        using ParserMemoryScope fixtureValues = ParserMemoryScope.Create(construction);
        List<PdfDictionary> fixtureTrailers = ParserContainers.CreateList<PdfDictionary>(fixtureStorage, 1, IntPtr.Size);
        fixtureTrailers.Add(trailer);
        XRefTable fixtureTable = XRefTable.CreateOwned(0L, fixtureEntries, trailer, fixtureTrailers,
            construction, fixtureStorage, fixtureValues, false);
        DocumentEngine fixtureEngine;
        try
        {
            fixtureStorage.Reserve(32L);
            fixtureEngine = DocumentEngine.CreateOwned(ReadOnlyMemory<byte>.Empty, fixtureTable, QpdfLimits.Default,
                construction, new(construction), QpdfRecoveryMode.Strict, 32L);
            fixtureStorage.Retain();
        }
        catch { fixtureTable.Dispose(); throw; }
        using DocumentEngine engine = fixtureEngine;
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(engine, construction, QpdfLimits.Default, 1, copierMemory);
        PdfArray large = new(Enumerable.Repeat<QpdfObject>(annotations ? PdfNull.Instance : new PdfReference(2, 0), 262144).ToArray());
        PdfDictionary page = new(new Dictionary<PdfName, QpdfObject> {
            [PdfName.FromAscii("Rotate")] = new PdfInteger("90", 90),
            [PdfName.FromAscii("MediaBox")] = new PdfArray([new PdfInteger("0", 0), new PdfInteger("0", 0), new PdfInteger("10", 10), new PdfInteger("20", 20)]),
            [PdfName.FromAscii(annotations ? "Annots" : "Contents")] = large });
        copier.AddIndirectObjectWithId(page, 1); copier.AddIndirectObjectWithId(new PdfStream(new(new Dictionary<PdfName, QpdfObject>()), "q"u8.ToArray()), 2);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = annotations ? 16384 : 8192 }, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        void Run() => FlattenRotation.Apply([(copier, 1)], false, null, false, QpdfLimits.Default, governor, new(governor), canceled ? new CancellationToken(true) : CancellationToken.None, 3);
        if (canceled) { Assert.ThrowsAny<OperationCanceledException>(Run); } else { Assert.Throws<QpdfLimitException>(Run); }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Same(page, copier.CopiedObjects[1]); Assert.Equal(262144, large.Count);
    }
}

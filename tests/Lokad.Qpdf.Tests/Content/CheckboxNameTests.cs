using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class CheckboxNameTests
{
    [Theory]
    [InlineData("Yes", false, false)]
    [InlineData("A#23B", false, false)]
    [InlineData("C#20D", false, false)]
    [InlineData("#FF", false, false)]
    [InlineData("A#23B", true, false)]
    [InlineData("A#23B", false, true)]
    [InlineData("A#23B", true, true)]
    public async Task SynchronizationKeepsRawAppearanceKeys(string name, bool separate, bool off)
    {
        string value = off ? "Off" : "Initial";
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [" + (separate ? "8" : "5") + " 0 R] /NeedAppearances true >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Annots [5 0 R] >>", null),
            (4, "null", null),
            (5, "<< /Subtype /Widget /Rect [0 0 10 10] /P 3 0 R " + (separate ? "/Parent 8 0 R" : "/FT /Btn /V /" + value) + " /AS /Off /AP << /N << /Off 6 0 R /" + name + " 7 0 R >> >> >>", null),
            (6, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 1 >>", "q"u8.ToArray()),
            (7, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 1 >>", "Q"u8.ToArray()),
            (8, "<< /FT /Btn /V /" + value + " /Kids [5 0 R] >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--generate-appearances", "--pages", ".", "1", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); host.AddInput(QpdfPath.From("again"), bytes, QpdfInputOwnership.Transfer);
        PdfName escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None))
        {
            PdfDictionary page = document.PageReferences[0].Page;
            Assert.True(page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annots));
            PdfDictionary widget = ResolveDictionary(document, Assert.IsType<PdfArray>(annots)[0]);
            Assert.True(widget.TryGet(PdfName.FromAscii("AS"), out QpdfObject state)); escaped = Assert.IsType<PdfName>(state);
            Assert.True(widget.TryGet(PdfName.FromAscii("AP"), out QpdfObject appearances));
            Assert.True(Assert.IsType<PdfDictionary>(appearances).TryGet(PdfName.FromAscii("N"), out QpdfObject normal));
            Assert.True(Assert.IsType<PdfDictionary>(normal).TryGet(escaped, out _));
            PdfDictionary field = widget;
            if (separate) { Assert.True(widget.TryGet(PdfName.FromAscii("Parent"), out QpdfObject parent)); field = ResolveDictionary(document, parent); }
            Assert.True(field.TryGet(PdfName.FromAscii("V"), out QpdfObject fieldValue)); Assert.Equal(escaped, Assert.IsType<PdfName>(fieldValue));
        }
        host.Dispose(); Assert.Equal(off ? "Off" : name, escaped.DisplayText);
    }

    private static PdfDictionary ResolveDictionary(QpdfDocument document, QpdfObject value)
        => Assert.IsType<PdfDictionary>(value is PdfReference link ? document.GetObject(link.ObjectNumber) : value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusalAndCancellationLeaveBothGraphSlotsUntouched(bool canceled)
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
        Dictionary<PdfName, QpdfObject> entries = new();
        for (int i = 0; i < 16384; i++) { entries[new PdfName(System.Text.Encoding.ASCII.GetBytes("Key" + i))] = PdfNull.Instance; }
        entries[PdfName.FromAscii("FT")] = PdfName.FromAscii("Btn"); entries[PdfName.FromAscii("V")] = PdfName.FromAscii("Yes");
        PdfDictionary field = new(entries), widget = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Widget"), [PdfName.FromAscii("Parent")] = new PdfReference(2, 0) });
        PdfDictionary page = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Annots")] = new PdfArray([new PdfReference(3, 0)]) });
        copier.AddIndirectObjectWithId(page, 1); copier.AddIndirectObjectWithId(field, 2); copier.AddIndirectObjectWithId(widget, 3);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        void Run() => GenerateAppearances.Apply([(copier, 1)], new Dictionary<GraphCopier, CopiedForm?>(), QpdfLimits.Default, governor, new(governor), canceled ? new CancellationToken(true) : CancellationToken.None, new HashSet<int> { 3 }, false, 4);
        if (canceled) { Assert.ThrowsAny<OperationCanceledException>(Run); } else { Assert.Throws<QpdfLimitException>(Run); }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Same(field, copier.CopiedObjects[2]); Assert.Same(widget, copier.CopiedObjects[3]);
    }
}

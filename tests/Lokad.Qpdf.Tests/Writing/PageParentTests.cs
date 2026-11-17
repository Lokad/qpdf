using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PageParentTests
{
    private static byte[] Source() => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> /Audit << /Private 10 0 R /Field 6 0 R /Popup 9 0 R >> >>", null),
        (2, "<< /Type /Pages /Count 2 /Kids [8 0 R] >>", null),
        (3, "<< /Type /Page /Parent 8 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [5 0 R 11 0 R 9 0 R] >>", null),
        (4, "null", null),
        (5, "<< /Type /Annot /Subtype /Widget /Rect [1 1 10 10] /P 3 0 R /Parent 6 0 R >>", null),
        (6, "<< /FT /Tx /T (Field) /Kids [5 0 R] >>", null),
        (7, "<< /Type /Page /Parent 8 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
        (8, "<< /Type /Pages /Parent 2 0 R /Count 2 /Kids [3 0 R 7 0 R] >>", null),
        (9, "<< /Type /Annot /Subtype /Popup /Rect [10 10 30 30] /Parent 11 0 R >>", null),
        (10, "<< /Parent 12 0 R >>", null),
        (11, "<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /Popup 9 0 R >>", null),
        (12, "<< /Kind /PrivateParent >>", null)], 1);

    public static IEnumerable<object[]> Routes()
    {
        yield return ["argv", "plain"]; yield return ["job", "plain"];
        yield return ["typed", "single"]; yield return ["typed", "multi"];
        yield return ["argv", "split"];
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryPublishedPagePointsToItsNewTreeWithoutChangingOtherParentRoles(string route, string layout)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Source(), QpdfInputOwnership.Transfer);
        List<QpdfPath> outputs = await Publish(host, route, layout);
        Assert.Equal(0L, host.OpenLeaseCount);
        int occurrence = 0;
        foreach (QpdfPath path in outputs)
        {
            await using QpdfDocument document = await ReadOutput(host, path);
            Assert.Equal(layout == "split" ? 1 : 3, document.PageCount);
            AssertPageParents(document);
            foreach (var location in document.PageReferences)
            {
                bool hasAnnotations = location.Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject raw);
                Assert.Equal(occurrence++ < 2, hasAnnotations);
                if (hasAnnotations)
                {
                    PdfArray annots = Assert.IsType<PdfArray>(Resolve(document, raw)); Assert.Equal(3, annots.Count);
                    PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(document, annots[0]));
                    PdfReference fieldReference = Assert.IsType<PdfReference>(Read(widget, "Parent"));
                    Assert.NotEqual(PageRoot(document), fieldReference);
                    PdfDictionary field = Assert.IsType<PdfDictionary>(Resolve(document, fieldReference));
                    Assert.Contains(annots[0], Assert.IsType<PdfArray>(Resolve(document, Read(field, "Kids"))).Items);
                    PdfDictionary popup = Assert.IsType<PdfDictionary>(Resolve(document, annots[2])); Assert.Equal(annots[1], Read(popup, "Parent"));
                }
            }
            PdfDictionary catalog = Catalog(document);
            bool preservePrivate = route != "typed" && layout != "split";
            Assert.Equal(preservePrivate, catalog.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit));
            if (preservePrivate)
            {
                PdfDictionary data = Assert.IsType<PdfDictionary>(Resolve(document, Read(Assert.IsType<PdfDictionary>(Resolve(document, audit)), "Private")));
                PdfDictionary parent = Assert.IsType<PdfDictionary>(Resolve(document, Read(data, "Parent")));
                Assert.Equal("PrivateParent", Assert.IsType<PdfName>(Read(parent, "Kind")).DisplayText);
            }
        }
        Assert.Equal(3, occurrence);
    }

    [Theory]
    [InlineData("packed")]
    [InlineData("qdf")]
    [InlineData("linear")]
    public async Task RequestedLayoutIsPresentAndKeepsThePageTree(string layout)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Source(), QpdfInputOwnership.Transfer);
        QpdfPath output = Assert.Single(await Publish(host, "argv", layout));
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        await using QpdfDocument document = await ReadOutput(host, output);
        Assert.Equal(3, document.PageCount); AssertPageParents(document);
        if (layout == "packed")
        {
            PdfDictionary audit = Assert.IsType<PdfDictionary>(Resolve(document, Read(Catalog(document), "Audit")));
            PdfReference data = Assert.IsType<PdfReference>(Read(audit, "Private"));
            Assert.Equal(XrefEntryKind.Compressed, document.Engine.XRefEntries[data.ObjectNumber].Kind);
        }
        else if (layout == "qdf")
        { Assert.Contains("%QDF-1.0", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal); }
        else
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(LinearizationStatus.Valid, LinearizationCheck.Inspect(document.Engine, 3, QpdfLimits.Default, governor, CancellationToken.None).Status);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
    }

    private static async Task<List<QpdfPath>> Publish(InMemoryQpdfHost host, string route, string layout)
    {
        List<QpdfPath> outputs = [];
        if (route == "typed")
        {
            if (layout == "single") { await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), PdfPageSelectionOptions.FromPages([1, 1, 2]), CancellationToken.None); }
            else
            {
                host.AddInput(QpdfPath.From("other"), Source(), QpdfInputOwnership.Transfer);
                await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages([1, 1])), new(QpdfPath.From("other"), new(2))], QpdfPath.From("output"), CancellationToken.None);
            }
            outputs.Add(QpdfPath.From("output"));
        }
        else
        {
            List<string> argv = ["input", "--pages", ".", "1,1,2", "--"];
            Dictionary<string, object> job = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["pages"] = new[] { new { file = ".", range = "1,1,2" } } };
            if (layout == "packed") { argv.Add("--object-streams=generate"); job["objectStreams"] = "generate"; }
            else if (layout == "qdf") { argv.Add("--qdf"); job["qdf"] = true; }
            else if (layout == "linear") { argv.Add("--linearize"); job["linearize"] = true; }
            else if (layout == "split") { argv.Add("--split-pages=1"); job["splitPages"] = "1"; }
            argv.Add("output");
            QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            outputs.AddRange(layout == "split" ? [QpdfPath.From("output-1"), QpdfPath.From("output-2"), QpdfPath.From("output-3")] : [QpdfPath.From("output")]);
        }
        return outputs;
    }

    private static async Task<QpdfDocument> ReadOutput(InMemoryQpdfHost host, QpdfPath path)
    {
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(path, out byte[] output)); QpdfPath read = QpdfPath.From("read-" + path.Value);
        host.AddInput(read, output, QpdfInputOwnership.Transfer);
        return await QpdfDocument.OpenAsync(host, read, CancellationToken.None);
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject raw) => raw is PdfReference reference ? document.GetObject(reference) : raw;
    private static QpdfObject Read(PdfDictionary dictionary, string key) { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
    private static PdfDictionary Catalog(QpdfDocument document) => Assert.IsType<PdfDictionary>(Resolve(document, Read(document.Engine.Trailer, "Root")));
    private static PdfReference PageRoot(QpdfDocument document) => Assert.IsType<PdfReference>(Read(Catalog(document), "Pages"));

    private static void AssertPageParents(QpdfDocument document)
    {
        PdfReference root = PageRoot(document);
        PdfArray kids = Assert.IsType<PdfArray>(Resolve(document, Read(Assert.IsType<PdfDictionary>(Resolve(document, root)), "Kids")));
        Assert.Equal(document.PageCount, kids.Count);
        foreach (var location in document.PageReferences)
        {
            Assert.Contains(location.Source, kids.Items);
            Assert.Equal(root, Assert.IsType<PdfReference>(Read(location.Page, "Parent")));
        }
    }

    [Fact]
    public async Task ReadingKeepsOriginalAncestry()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Source(), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        foreach (var page in document.PageReferences)
        { Assert.True(page.Page.TryGet(PdfName.FromAscii("Parent"), out QpdfObject parent)); Assert.Equal(new PdfReference(8, 0), Assert.IsType<PdfReference>(parent)); }
    }
}

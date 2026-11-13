using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Content;

public sealed class SignedAppearanceTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");

    public static IEnumerable<object[]> Selections()
    {
        foreach (string fixture in new[] { "signature-visible", "signature-incremental", "signature-private-alias", "signature-shared-branch" })
        foreach (string route in new[] { "typed", "argv", "job", "argv-restrictions", "job-restrictions" })
        foreach (bool preview in new[] { false, true }) { yield return [fixture, route, preview]; }
    }

    [Theory]
    [MemberData(nameof(Selections))]
    public async Task SelectionPreservesMarksAndDisablesOnlyTheFieldRole(string fixture, string route, bool preview)
    {
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture + ".pdf"));
        byte[] original = (byte[])input.Clone();
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, input, QpdfInputOwnership.Transfer);
        int[] expected = preview ? [1, 3] : [3, 1, 3];
        string range = preview ? "1,z" : "z,1,z";
        if (route == "typed")
        {
            await Qpdf.SelectPageAsync(host, Input, Output,
                preview ? PdfPageSelectionOptions.FirstAndLast(1) : PdfPageSelectionOptions.FromPages(expected), CancellationToken.None);
        }
        else
        {
            List<string> argv = ["input.pdf", "--pages", ".", range, "--", "output.pdf"];
            Dictionary<string, object> fields = new()
            { ["inputFile"] = "input.pdf", ["outputFile"] = "output.pdf", ["pages"] = new[] { new { file = ".", range } } };
            if (route.EndsWith("-restrictions", StringComparison.Ordinal))
            { argv.Add("--remove-restrictions"); fields["removeRestrictions"] = ""; }
            QpdfExecutionResult result = route.StartsWith("argv", StringComparison.Ordinal)
                ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, System.Text.Json.JsonSerializer.Serialize(fields), CancellationToken.None);
            Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        }
        Assert.Equal(original, input);
        Assert.Equal(0, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] output));
        using (var independent = UglyToad.PdfPig.PdfDocument.Open(output))
        {
            Assert.Equal(expected.Length, independent.NumberOfPages);
            for (int index = 0; index < expected.Length; index++)
            { Assert.Contains(expected[index] == 1 ? "First" : "Last", independent.GetPage(index + 1).Text, StringComparison.Ordinal); }
        }
        host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
        PdfDictionary catalog = Dict(document, Get(document.Engine.Trailer, "Root"));
        Assert.False(catalog.TryGet(PdfName.FromAscii("Perms"), out _));
        if (catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject form))
        { Assert.False(Dict(document, form).TryGet(PdfName.FromAscii("SigFlags"), out _)); }
        List<PdfReference> widgets = [];
        for (int index = 0; index < expected.Length; index++)
        {
            PdfDictionary page = document.PageReferences[index].Page;
            PdfArray annots = Assert.IsType<PdfArray>(Resolve(document, Get(page, "Annots")));
            PdfReference widgetRef = Assert.IsType<PdfReference>(Assert.Single(annots.Items));
            widgets.Add(widgetRef);
            PdfDictionary widget = Dict(document, widgetRef);
            double[] rect = expected[index] == 1 ? [20, 30, 100, 70] : [40, 60, 120, 100];
            Assert.Equal(rect, Numbers(document, Get(widget, "Rect")));
            AssertDisabled(document, widget);
            PdfDictionary ap = Dict(document, Get(widget, "AP"));
            PdfStream appearance = Assert.IsType<PdfStream>(Resolve(document, Get(ap, "N")));
            Assert.Equal("q /Mark Do Q", Decode(appearance));
            Assert.Equal(new double[] { 0, 0, 80, 40 }, Numbers(document, Get(appearance.Dictionary, "BBox")));
            PdfDictionary resources = Dict(document, Get(appearance.Dictionary, "Resources"));
            PdfDictionary xobjects = Dict(document, Get(resources, "XObject"));
            PdfStream mark = Assert.IsType<PdfStream>(Resolve(document, Get(xobjects, "Mark")));
            Assert.Equal("0 0.6 0 RG 5 w 10 20 m 25 5 l 70 35 l S", Decode(mark));
            if (expected[index] == 1 && fixture == "signature-private-alias")
            {
                QpdfObject privateRef = Get(page, "Audit");
                Assert.NotEqual(widgetRef, privateRef);
                PdfDictionary privateParent = Dict(document, Get(Dict(document, privateRef), "Parent"));
                Assert.Equal(PdfName.FromAscii("Sig"), Get(privateParent, "FT"));
                Assert.True(Dict(document, Get(privateParent, "V")).TryGet(PdfName.FromAscii("ByteRange"), out _));
            }
        }
        Assert.Equal(widgets.Count, widgets.Distinct().Count());
    }

    [Theory]
    [InlineData("rewrite")]
    [InlineData("json")]
    [InlineData("merge")]
    public async Task WholeDocumentAndMergeKeepOrdinaryForms(string operation)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "signature-shared-branch.pdf")), QpdfInputOwnership.Transfer);
        if (operation == "json")
        {
            QpdfExecutionResult exported = await Qpdf.ExecuteAsync(host,
                new(["input.pdf", "--json-output=2", "--json-stream-data=inline", "--", "export.json"]), CancellationToken.None);
            Assert.True(exported.Status == QpdfExecutionStatus.Completed, string.Join("; ", exported.Diagnostics.Select(d => d.Message)));
            Assert.True(host.TryGetOutput(QpdfPath.From("export.json"), out byte[] json));
            host.AddInput(QpdfPath.From("json"), json, QpdfInputOwnership.Transfer);
            await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("json"), Output, CancellationToken.None);
        }
        else if (operation == "merge")
        {
            host.AddInput(QpdfPath.From("unsigned"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
            await Qpdf.SelectPagesAsync(host, [new(Input, PdfPageSelectionOptions.FromPages([1, 2, 3])),
                new(QpdfPath.From("unsigned"), new PdfPageSelectionOptions(1))], Output, CancellationToken.None);
        }
        else { Assert.Equal(QpdfExecutionStatus.Completed, (await Qpdf.ExecuteAsync(host, new(["input.pdf", "output.pdf"]), CancellationToken.None)).Status); }
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        host.AddInput(QpdfPath.From("again"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
        Assert.Equal(operation == "merge" ? 4 : 3, document.PageCount);
        PdfDictionary page = document.PageReferences[1].Page;
        PdfDictionary ordinary = Dict(document, Assert.IsType<PdfArray>(Resolve(document, Get(page, "Annots")))[0]);
        Assert.Equal(PdfName.FromAscii("Tx"), Get(ordinary, "FT"));
        Assert.Equal("Keep me", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(ordinary, "V")).Value));
        PdfDictionary catalog = Dict(document, Get(document.Engine.Trailer, "Root"));
        PdfDictionary form = Dict(document, Get(catalog, "AcroForm"));
        PdfArray roots = Assert.IsType<PdfArray>(Resolve(document, Get(form, "Fields")));
        PdfDictionary group = Dict(document, Assert.Single(roots.Items));
        PdfArray kids = Assert.IsType<PdfArray>(Resolve(document, Get(group, "Kids")));
        Assert.Equal(ordinary, Dict(document, Assert.Single(kids.Items)));
        PdfArray order = Assert.IsType<PdfArray>(Resolve(document, Get(form, "CO")));
        Assert.Equal(ordinary, Dict(document, Assert.Single(order.Items)));
        PdfDictionary signed = Dict(document, Assert.IsType<PdfArray>(Resolve(document, Get(document.PageReferences[0].Page, "Annots")))[0]);
        AssertDisabled(document, signed);
    }

    [Fact]
    public async Task SharedOrdinaryWidgetsKeepTheirFieldAndAppearanceAcrossPages()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ordinary-shared-widgets.pdf")), QpdfInputOwnership.Transfer);
        await Qpdf.SelectPageAsync(host, Input, Output,
            PdfPageSelectionOptions.FromPages(Enumerable.Range(1, 128).ToArray()), CancellationToken.None);
        Assert.Equal(0, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] output));
        using (var independent = UglyToad.PdfPig.PdfDocument.Open(output))
        { Assert.Equal(128, independent.NumberOfPages); }
        host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
        PdfDictionary form = Dict(document, Get(Dict(document, Get(document.Engine.Trailer, "Root")), "AcroForm"));
        PdfReference fieldRef = Assert.IsType<PdfReference>(Assert.Single(Assert.IsType<PdfArray>(Resolve(document, Get(form, "Fields"))).Items));
        PdfDictionary field = Dict(document, fieldRef);
        Assert.Equal(PdfName.FromAscii("Tx"), Get(field, "FT"));
        Assert.Equal("Keep me", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(field, "V")).Value));
        PdfArray kids = Assert.IsType<PdfArray>(Resolve(document, Get(field, "Kids")));
        Assert.Equal(128, kids.Count);
        PdfStream? sharedAppearance = null;
        foreach (var page in document.PageReferences)
        {
            PdfReference widgetRef = Assert.IsType<PdfReference>(Assert.Single(Assert.IsType<PdfArray>(Resolve(document, Get(page.Page, "Annots"))).Items));
            Assert.Contains(widgetRef, kids.Items);
            PdfDictionary widget = Dict(document, widgetRef);
            Assert.Equal(PdfName.FromAscii("Widget"), Get(widget, "Subtype"));
            Assert.Equal(fieldRef, Get(widget, "Parent"));
            PdfStream appearance = Assert.IsType<PdfStream>(Resolve(document, Get(Dict(document, Get(widget, "AP")), "N")));
            Assert.Equal("BT /F1 12 Tf 2 4 Td (Keep me) Tj ET", Decode(appearance));
            if (sharedAppearance is not null) { Assert.Same(sharedAppearance, appearance); }
            sharedAppearance = appearance;
        }
    }

    private static void AssertDisabled(QpdfDocument document, PdfDictionary field)
    {
        Assert.Equal(PdfName.FromAscii("Stamp"), Get(field, "Subtype"));
        Assert.False(field.TryGet(PdfName.FromAscii("Parent"), out _));
        for (int depth = 0; ; depth++)
        {
            Assert.True(depth < 10);
            foreach (string key in new[] { "FT", "V", "DV", "SV", "Lock" })
            { Assert.False(field.TryGet(PdfName.FromAscii(key), out _), key + " remained on a disabled signature field."); }
            if (!field.TryGet(PdfName.FromAscii("Parent"), out QpdfObject parent)) { break; }
            field = Dict(document, parent);
        }
    }

    [Theory]
    [InlineData("overlay")]
    [InlineData("underlay")]
    public async Task PlacementKeepsSignatureArtworkAndTheSharedOrdinaryField(string side)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf")), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("signed"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "signature-shared-branch.pdf")), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input.pdf", "--" + side, "signed", "--", "output.pdf"]), CancellationToken.None);
        Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        host.AddInput(QpdfPath.From("again"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None);
        PdfDictionary stamp = Dict(document, Assert.Single(Assert.IsType<PdfArray>(Resolve(document, Get(document.PageReferences[0].Page, "Annots"))).Items));
        AssertDisabled(document, stamp);
        PdfStream appearance = Assert.IsType<PdfStream>(Resolve(document, Get(Dict(document, Get(stamp, "AP")), "N")));
        Assert.True(ContainsMark(appearance, 0));
        PdfDictionary ordinary = Dict(document, Assert.Single(Assert.IsType<PdfArray>(Resolve(document, Get(document.PageReferences[1].Page, "Annots"))).Items));
        Assert.Equal(PdfName.FromAscii("Tx"), Get(ordinary, "FT"));
        Assert.Equal("Keep me", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(ordinary, "V")).Value));
        PdfDictionary form = Dict(document, Get(Dict(document, Get(document.Engine.Trailer, "Root")), "AcroForm"));
        PdfDictionary root = Dict(document, Assert.Single(Assert.IsType<PdfArray>(Resolve(document, Get(form, "Fields"))).Items));
        Assert.Equal(ordinary, Dict(document, Assert.Single(Assert.IsType<PdfArray>(Resolve(document, Get(root, "Kids"))).Items)));

        bool ContainsMark(PdfStream stream, int depth)
        {
            Assert.True(depth < 10);
            if (Decode(stream) == "0 0.6 0 RG 5 w 10 20 m 25 5 l 70 35 l S") { return true; }
            if (!stream.Dictionary.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resources)
                || !Dict(document, resources).TryGet(PdfName.FromAscii("XObject"), out QpdfObject xobjects)) { return false; }
            PdfDictionary dictionary = Dict(document, xobjects);
            foreach (PdfName name in dictionary.Keys)
            {
                Assert.True(dictionary.TryGet(name, out QpdfObject value));
                if (Resolve(document, value) is PdfStream child && ContainsMark(child, depth + 1)) { return true; }
            }
            return false;
        }
    }
    private static QpdfObject Get(PdfDictionary value, string key)
    { Assert.True(value.TryGet(PdfName.FromAscii(key), out QpdfObject item), key); return item; }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static PdfDictionary Dict(QpdfDocument document, QpdfObject value) => Assert.IsType<PdfDictionary>(Resolve(document, value));
    private static double[] Numbers(QpdfDocument document, QpdfObject value)
        => Assert.IsType<PdfArray>(Resolve(document, value)).Items.Select(item => item is PdfInteger number ? (double)number.Value : Assert.IsType<PdfReal>(item).Value).ToArray();
    private static string Decode(PdfStream stream)
    {
        if (!stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _)) { return Encoding.ASCII.GetString(stream.Data); }
        using MemoryStream input = new(stream.Data);
        using ZLibStream decoder = new(input, CompressionMode.Decompress);
        using StreamReader text = new(decoder, Encoding.ASCII);
        return text.ReadToEnd();
    }
}

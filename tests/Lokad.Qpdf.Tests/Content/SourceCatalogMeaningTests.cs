using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class SourceCatalogMeaningTests
{
    private static byte[] Fixture(string kind, bool primary)
    {
        string catalog = primary ? "/URI << /Base (https://primary.test/base/) >> /Dests << /Here [3 0 R /Fit] >> /OCProperties << /OCGs [5 0 R] /D << /BaseState /ON /OFF [5 0 R] >> >>"
            : kind == "layers" ? "/OCProperties << /OCGs [5 0 R] /D << /BaseState /ON /OFF [5 0 R] >> >>"
            : kind == "uri" ? "/URI << /Base (https://source.test/base/) >>"
            : "/Dests << /Here [3 0 R /Fit] >> /Names << /Dests << /Names [(Here) [3 0 R /Fit]] >> >>";
        bool layer = primary || kind == "layers";
        string page = kind == "layers" ? "/Resources << /Properties << /L 5 0 R >> >>" : primary ? "/Resources <<>>" : "/Resources <<>> /Annots [5 0 R]";
        string value = layer ? "<< /Type /OCG /Name (Layer) >>"
            : "<< /Type /Annot /Subtype /Link /Rect [10 20 30 40] /A " + (kind == "uri" ? "<< /S /URI /URI (item.pdf) >>" : "<< /S /GoTo /D " + (kind == "dest-name" ? "/Here" : "(Here)") + " >>") + " >>";
        byte[] content = kind == "layers" ? "/OC /L BDC 1 0 0 rg 20 20 40 40 re f EMC"u8.ToArray() : "q Q"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + catalog + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " + page + " >>", null),
            (4, "<< /Length " + content.Length + " >>", content),
            (5, value, null)], 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string kind in new[] { "layers", "uri", "dest-name", "dest-string" })
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string mode in new[] { "primary", "secondary" })
        { yield return [kind, route, mode]; }
        foreach (string kind in new[] { "layers", "uri" })
        foreach (string route in new[] { "argv", "job" })
        foreach (string mode in new[] { "overlay", "underlay" })
        { yield return [kind, route, mode]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RetainedSupportedObjectsKeepTheirSourceMeaning(string kind, string route, string mode)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(kind, mode != "primary"), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("source"), Fixture(kind, false), QpdfInputOwnership.Transfer);
        if (route == "typed")
        {
            if (mode == "primary") { await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(1), CancellationToken.None); }
            else { await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("input"), new(1)), new(QpdfPath.From("source"), new(1))], QpdfPath.From("output"), new PdfPageMergeOptions(), CancellationToken.None); }
        }
        else
        {
            List<string> argv = ["input"]; Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
            if (mode == "secondary") { argv.AddRange(["--pages", ".", "1", "source", "1", "--"]); settings["pages"] = new[] { new { file = ".", range = "1" }, new { file = "source", range = "1" } }; }
            else if (mode != "primary") { argv.AddRange(["--" + mode, "source", "--"]); settings[mode] = new { file = "source" }; }
            argv.Add("output");
            QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        PdfDictionary Dictionary(PdfDictionary owner, string key)
        { Assert.True(owner.TryGet(PdfName.FromAscii(key), out QpdfObject value), "Missing required source context: " + key); return Assert.IsType<PdfDictionary>(Resolve(value)); }
        if (kind == "layers")
        {
            PdfDictionary properties = Dictionary(catalog, "OCProperties"), configuration = Dictionary(properties, "D");
            Assert.True(properties.TryGet(PdfName.FromAscii("OCGs"), out QpdfObject rawGroups));
            PdfArray groups = Assert.IsType<PdfArray>(Resolve(rawGroups)); Assert.Equal(mode == "primary" ? 1 : 2, groups.Count);
            Assert.Equal(groups.Count, groups.Items.OfType<PdfReference>().Select(r => r.ObjectNumber).Distinct().Count());
            Assert.True(configuration.TryGet(PdfName.FromAscii("OFF"), out QpdfObject rawOff));
            PdfArray off = Assert.IsType<PdfArray>(Resolve(rawOff));
            Assert.Equal(groups.Count, off.Count);
            Assert.All(groups.Items, group => Assert.Contains(group, off.Items));
            return;
        }
        PdfDictionary page = document.PageReferences[mode == "secondary" ? 1 : 0].Page;
        Assert.True(page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject rawAnnots));
        PdfArray annots = Assert.IsType<PdfArray>(Resolve(rawAnnots));
        PdfDictionary action = Dictionary(Assert.IsType<PdfDictionary>(Resolve(Assert.Single(annots.Items))), "A");
        if (kind == "uri")
        {
            Assert.True(action.TryGet(PdfName.FromAscii("URI"), out QpdfObject rawUri));
            string text = Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Resolve(rawUri)).ValueSpan);
            Uri actual;
            if (Uri.TryCreate(text, UriKind.Absolute, out Uri? absolute)) { actual = absolute; }
            else
            {
                Assert.True(Dictionary(catalog, "URI").TryGet(PdfName.FromAscii("Base"), out QpdfObject rawBase));
                actual = new Uri(new Uri(Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Resolve(rawBase)).ValueSpan)), text);
            }
            Assert.Equal("https://source.test/base/item.pdf", actual.AbsoluteUri); return;
        }
        Assert.True(action.TryGet(PdfName.FromAscii("D"), out QpdfObject destination)); destination = Resolve(destination);
        if (destination is PdfName name) { Assert.True(Dictionary(catalog, "Dests").TryGet(name, out destination)); }
        else if (destination is PdfByteString text)
        {
            PdfDictionary tree = Dictionary(Dictionary(catalog, "Names"), "Dests"); Assert.True(tree.TryGet(PdfName.FromAscii("Names"), out QpdfObject rawNames));
            PdfArray names = Assert.IsType<PdfArray>(Resolve(rawNames)); Assert.Equal(text, Assert.IsType<PdfByteString>(Resolve(names[0]))); destination = names[1];
        }
        PdfArray target = Assert.IsType<PdfArray>(Resolve(destination));
        Assert.Equal(document.PageReferences[mode == "secondary" ? 1 : 0].Source, Assert.IsType<PdfReference>(target[0]));
    }
}

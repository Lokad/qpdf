using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceParentScopeTests
{
    internal static byte[] Fixture(string kind, string alias, bool own)
    {
        string catalog = alias == "catalog" ? "/Private 7 0 R" : alias == "attachment"
            ? "/Names << /EmbeddedFiles << /Names [(file) 9 0 R] >> >>" : string.Empty;
        string field = kind == "flags" ? "/FT /Btn /V /Off /AS /Yes /AP << /N << /Off 11 0 R /Yes 12 0 R >> >> " + (own ? "/Ff 0" : string.Empty)
            : kind == "appearance" ? "/FT /Tx /V (own) " + (own ? "/DA (/F1 12 Tf 0 g)" : string.Empty)
            : "/FT /Tx /DA (/F1 9 Tf 0 g) " + (own ? "/V (own)" : string.Empty);
        string foreign = kind == "flags" ? "/Ff 32768" : kind == "appearance" ? "/DA (/F1 27 Tf 1 0 0 rg)" : "/V (foreign)";
        byte[] on = "1 0 0 rg 0 0 90 40 re f"u8.ToArray();
        byte[] data = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R " + catalog + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [6 0 R] " + (alias == "page" ? "/Private 7 0 R" : string.Empty) + " >>", null),
            (4, "null", null),
            (5, "<< /Fields [6 0 R] /NeedAppearances true /DA (/F1 9 Tf 0 g) /DR << /Font << /F1 8 0 R >> >> >>", null),
            (6, "<< /Type /Annot /Subtype /Widget /Rect [5 5 95 45] /T (field) /F 4 " + field + " >>", null),
            (7, "<< /Kids [6 0 R] " + foreign + " >>", null),
            (8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (9, "<< /Type /Filespec /F (file) /EF << /F 10 0 R >> /Private 7 0 R >>", null),
            (10, "<< /Length 6 >>", "opaque"u8.ToArray()),
            (11, "<< /Type /XObject /Subtype /Form /BBox [0 0 90 40] /Resources << >> /Length 3 >>", "q Q"u8.ToArray()),
            (12, "<< /Type /XObject /Subtype /Form /BBox [0 0 90 40] /Resources << >> /Length " + on.Length + " >>", on)], 1);
        return alias == "info" ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(data).Replace("/Root 1 0 R >>", "/Root 1 0 R /Info 7 0 R >>", StringComparison.Ordinal)) : data;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string alias in new[] { "none", "catalog", "info", "attachment", "page" })
        foreach (bool own in new[] { false, true })
        foreach (string route in new[] { "argv", "job" }) { yield return ["value", alias, own, route, false, false]; }
        foreach (string kind in new[] { "appearance", "flags" })
        foreach (string alias in new[] { "catalog", "attachment" })
        foreach (bool own in new[] { false, true })
        foreach (string route in new[] { "argv", "job" }) { yield return [kind, alias, own, route, false, false]; }
        foreach (string kind in new[] { "value", "appearance", "flags" })
        foreach (bool selected in new[] { false, true })
        foreach (string route in new[] { "argv", "job" }) { yield return [kind, "info", false, route, true, selected]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PrivateKidsDoNotSupplyFieldValuesDefaultsOrFlags(string kind, string alias, bool own,
        string route, bool flatten, bool selected)
    {
        using InMemoryQpdfHost host = new();
        byte[] source = Fixture(kind, alias, own);
        host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Execute(host, route, flatten, selected, QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("result"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument reader = await QpdfDocument.OpenAsync(host, QpdfPath.From("result"), CancellationToken.None);
        PdfDictionary page = reader.PageReferences[0].Page;
        if (flatten) { Assert.IsType<PdfNull>(Get(page, "Annots")); }
        else
        {
            PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(reader, Assert.IsType<PdfArray>(Resolve(reader, Get(page, "Annots")))[0]));
            if (kind == "flags") { Assert.Equal("Off", Assert.IsType<PdfName>(Get(widget, "AS")).DisplayText); }
        }
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(reader, Get(reader.Engine.Trailer, "Root")));
        if (Get(catalog, "AcroForm") is not PdfNull)
        {
            QpdfObject ready = Get(Assert.IsType<PdfDictionary>(Resolve(reader, Get(catalog, "AcroForm"))), "NeedAppearances");
            Assert.True(ready is PdfNull || ready is PdfBoolean flag && !flag.Value);
        }
        string[] programs = reader.Engine.XRefEntries.Keys.Select(reader.GetObject).OfType<PdfStream>()
            .Where(stream => stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject subtype) && subtype.Equals(PdfName.FromAscii("Form")))
            .Select(stream => Program(reader, stream)).ToArray();
        Assert.DoesNotContain(programs, program => program.Contains("foreign", StringComparison.Ordinal) || program.Contains("27 Tf", StringComparison.Ordinal));
        if (kind != "flags")
        {
            string text = kind == "value" && !own ? "() Tj" : "(own) Tj";
            Assert.Contains(programs, program => program.Contains(text, StringComparison.Ordinal));
            Assert.Contains(programs, program => program.Contains(kind == "appearance" && own ? "/F1 12 Tf 0 g" : "/F1 9 Tf 0 g", StringComparison.Ordinal));
        }
        if (alias != "none")
        {
            PdfDictionary privateData = alias switch
            {
                "catalog" => Assert.IsType<PdfDictionary>(Resolve(reader, Get(catalog, "Private"))),
                "page" => Assert.IsType<PdfDictionary>(Resolve(reader, Get(page, "Private"))),
                "info" => Assert.IsType<PdfDictionary>(Resolve(reader, Get(reader.Engine.Trailer, "Info"))),
                _ => AttachmentPrivate(reader, catalog)
            };
            if (kind == "value") { Assert.Equal("foreign", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(privateData, "V")).ValueSpan)); }
            Assert.IsType<PdfArray>(Resolve(reader, Get(privateData, "Kids")));
        }
    }

    private static PdfDictionary AttachmentPrivate(QpdfDocument reader, PdfDictionary catalog)
    {
        PdfDictionary names = Assert.IsType<PdfDictionary>(Resolve(reader, Get(catalog, "Names")));
        PdfDictionary tree = Assert.IsType<PdfDictionary>(Resolve(reader, Get(names, "EmbeddedFiles")));
        PdfArray items = Assert.IsType<PdfArray>(Resolve(reader, Get(tree, "Names")));
        PdfDictionary spec = Assert.IsType<PdfDictionary>(Resolve(reader, items[1]));
        return Assert.IsType<PdfDictionary>(Resolve(reader, Get(spec, "Private")));
    }

    internal static Task<QpdfExecutionResult> Execute(IQpdfHost host, string route, bool flatten, bool selected, QpdfLimits limits)
    {
        if (route == "job")
        {
            Dictionary<string, object> job = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["generateAppearances"] = true };
            if (flatten) { job["flattenAnnotations"] = "all"; }
            if (selected) { job["pages"] = new[] { new { file = ".", range = "1" } }; }
            return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new(limits), CancellationToken.None).AsTask();
        }
        List<string> arguments = ["input", "--generate-appearances"];
        if (flatten) { arguments.Add("--flatten-annotations=all"); }
        if (selected) { arguments.AddRange(["--pages", ".", "1", "--"]); }
        arguments.Add("output");
        return Qpdf.ExecuteAsync(host, new(arguments), new(limits), CancellationToken.None).AsTask();
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? value : PdfNull.Instance;
    private static QpdfObject Resolve(QpdfDocument reader, QpdfObject value)
        => value is PdfReference link ? reader.GetObject(link.ObjectNumber) : value;
    private static string Program(QpdfDocument reader, PdfStream stream)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using FilterPlan stages = FilterChain.ResolveScoped(stream.Dictionary, reader.Engine, governor, CancellationToken.None);
        return Encoding.ASCII.GetString(stages.Count == 0 ? stream.RawMemory.Span
            : FilterChain.Decode(stream.ArrayData(memory, governor, CancellationToken.None), stages, QpdfLimits.Default, governor, memory, CancellationToken.None));
    }
}

using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationArrayIsolationTests
{
    internal static byte[] Fixture(string kind, string alias, bool indirect, int pages, int directCount)
    {
        int registered = pages + 3, font = pages + 4, array = pages + 5, spec = pages + 6;
        int on = pages + 7, off = pages + 8, content = pages + 9;
        string states = "/AP << /N << /Yes " + on + " 0 R /Off " + off + " 0 R >> >> /AS /Off";
        string common = "/Type /Annot /Subtype /Widget /Rect [10 10 90 30] ";
        string body = common + (kind == "button" ? "/FT /Btn /V /Yes " + states
            : "/FT " + (kind == "skip" ? "/Unknown" : "/Tx") + " /V (Dval) /DA (/F1 12 Tf)"
                + (kind == "existing" ? " /AP << /N " + on + " 0 R >>" : ""));
        string annots = "[" + registered + " 0 R " + string.Join(' ', Enumerable.Repeat("<< " + body + " /T (Direct) >>", directCount)) + "]";
        string catalog = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [" + registered + " 0 R] /NeedAppearances true /DR << /Font << /F1 " + font + " 0 R >> >> >>";
        if (alias == "catalog") { catalog += " /Audit " + array + " 0 R"; }
        if (alias == "attachment") { catalog += " /Names << /EmbeddedFiles << /Names [(K) " + spec + " 0 R] >> >>"; }
        if (alias == "metadata") { catalog += " /Metadata " + spec + " 0 R"; }
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, catalog + " >>", null),
            (2, "<< /Type /Pages /Count " + pages + " /Kids [" + string.Join(' ', Enumerable.Range(3, pages).Select(n => n + " 0 R")) + "] >>", null)];
        for (int page = 3; page < pages + 3; page++)
        { objects.Add((page, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents " + content + " 0 R /Annots " + (indirect ? array + " 0 R" : annots) + " >>", null)); }
        objects.Add((registered, "<< " + common + " /T (Registered) " + (kind == "button" ? "/FT /Btn /V /Yes " + states : "/FT /Tx /V (Rval) /DA (/F1 12 Tf)") + " >>", null));
        objects.Add((font, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null));
        objects.Add((array, annots, null));
        objects.Add(alias == "metadata" ? (spec, "<< /Type /Metadata /Subtype /XML /Audit " + array + " 0 R /Length 10 >>", "<xmpmeta/>"u8.ToArray())
            : (spec, "<< /Type /Filespec /F (K) /Audit " + array + " 0 R /EF << /F " + content + " 0 R >> >>", null));
        byte[] appearance = kind == "button" ? "1 0 0 rg 0 0 80 20 re f"u8.ToArray() : "/Tx BMC BT (stale) Tj ET EMC"u8.ToArray();
        objects.Add((on, "<< /Type /XObject /Subtype /Form /BBox [0 0 80 20] /Length " + appearance.Length + " >>", appearance));
        objects.Add((off, "<< /Type /XObject /Subtype /Form /BBox [0 0 80 20] /Length 3 >>", "q Q"u8.ToArray()));
        objects.Add((content, "<< /Length 3 >>", "q Q"u8.ToArray()));
        byte[] data = CheckTests.Assemble(objects, 1);
        return alias == "info" ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(data).Replace("/Root 1 0 R >>", "/Root 1 0 R /Info " + spec + " 0 R >>", StringComparison.Ordinal)) : data;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string kind in new[] { "fresh", "existing", "button", "skip" })
        foreach (string alias in new[] { "catalog", "attachment" })
        foreach (bool indirect in new[] { false, true })
        foreach (int pages in new[] { 1, 3 })
        foreach (string route in new[] { "argv", "job" }) { yield return [kind, alias, indirect, pages, route, false, false]; }
        foreach (string kind in new[] { "fresh", "button" })
        foreach (string alias in new[] { "catalog", "attachment" })
        foreach (string route in new[] { "argv", "job" }) { yield return [kind, alias, true, 3, route, true, false]; }
        foreach (string alias in new[] { "catalog", "attachment" })
        foreach (bool indirect in new[] { false, true })
        foreach (string route in new[] { "argv", "job" }) { yield return ["fresh", alias, indirect, 3, route, false, true]; }
        foreach (int pages in new[] { 1, 3 })
        foreach (string route in new[] { "argv", "job" }) { yield return ["fresh", "none", true, pages, route, false, false]; }
        foreach (string kind in new[] { "fresh", "existing" })
        foreach (string alias in new[] { "info", "metadata" })
        foreach (string route in new[] { "argv", "job" }) { yield return [kind, alias, true, 3, route, false, false]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task GenerationKeepsIndependentArraysAndSharesAffectedPageReplacements(
        string kind, string alias, bool indirect, int pages, string route, bool flatten, bool selection)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(kind, alias, indirect, pages, 1), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Execute(host, route, flatten, selection, QpdfLimits.Default);
        Assert.Equal(selection ? QpdfExitCode.Success : QpdfExitCode.Warning, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("result"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument reader = await QpdfDocument.OpenAsync(host, QpdfPath.From("result"), CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(reader, Get(reader.Engine.Trailer, "Root")));
        QpdfObject originalReference = PdfNull.Instance;
        if (alias != "none")
        {
            originalReference = alias switch
            {
                "catalog" => Get(catalog, "Audit"),
                "info" => Get(Assert.IsType<PdfDictionary>(Resolve(reader, Get(reader.Engine.Trailer, "Info"))), "Audit"),
                "metadata" => Get(Assert.IsType<PdfStream>(Resolve(reader, Get(catalog, "Metadata"))).Dictionary, "Audit"),
                _ => AttachmentArray(reader, catalog)
            };
            PdfDictionary original = Assert.IsType<PdfDictionary>(Assert.IsType<PdfArray>(Resolve(reader, originalReference))[1]);
            if (kind == "button") { Assert.Equal("Off", Assert.IsType<PdfName>(Get(original, "AS")).DisplayText); }
            else if (kind == "existing")
            {
                PdfDictionary ap = Assert.IsType<PdfDictionary>(Resolve(reader, Get(original, "AP")));
                Assert.Contains("stale", Program(reader, Assert.IsType<PdfStream>(Resolve(reader, Get(ap, "N")))), StringComparison.Ordinal);
            }
            else { Assert.IsType<PdfNull>(Get(original, "AP")); }
        }
        PdfReference? replacement = null;
        foreach (PageLocation location in reader.PageReferences)
        {
            QpdfObject annots = Get(location.Page, "Annots");
            if (flatten) { Assert.IsType<PdfNull>(annots); continue; }
            if (indirect)
            {
                PdfReference reference = Assert.IsType<PdfReference>(annots);
                if (replacement is null) { replacement = reference; } else { Assert.Equal(replacement, reference); }
                if (alias != "none")
                { if (selection || kind == "skip") { Assert.Equal(originalReference, reference); } else { Assert.NotEqual(originalReference, reference); } }
            }
            PdfDictionary direct = Assert.IsType<PdfDictionary>(Assert.IsType<PdfArray>(Resolve(reader, annots))[1]);
            if (kind == "button") { Assert.Equal("Yes", Assert.IsType<PdfName>(Get(direct, "AS")).DisplayText); }
            else if (kind == "skip" || selection) { Assert.IsType<PdfNull>(Get(direct, "AP")); }
            else
            {
                PdfDictionary ap = Assert.IsType<PdfDictionary>(Resolve(reader, Get(direct, "AP")));
                string program = Program(reader, Assert.IsType<PdfStream>(Resolve(reader, Get(ap, "N"))));
                Assert.Contains("Dval", program, StringComparison.Ordinal); Assert.DoesNotContain("stale", program, StringComparison.Ordinal);
            }
        }
        Assert.Equal(pages, reader.PageCount);
        if (flatten) { Assert.IsType<PdfNull>(Get(catalog, "AcroForm")); return; }
        PdfDictionary form = Assert.IsType<PdfDictionary>(Resolve(reader, Get(catalog, "AcroForm")));
        QpdfObject ready = Get(form, "NeedAppearances");
        if (kind == "skip" || selection) { Assert.True(Assert.IsType<PdfBoolean>(ready).Value); }
        else { Assert.True(ready is PdfNull || ready is PdfBoolean flag && !flag.Value); }
    }

    private static QpdfObject AttachmentArray(QpdfDocument reader, PdfDictionary catalog)
    {
        PdfDictionary names = Assert.IsType<PdfDictionary>(Resolve(reader, Get(catalog, "Names")));
        PdfDictionary files = Assert.IsType<PdfDictionary>(Resolve(reader, Get(names, "EmbeddedFiles")));
        PdfArray entries = Assert.IsType<PdfArray>(Resolve(reader, Get(files, "Names")));
        return Get(Assert.IsType<PdfDictionary>(Resolve(reader, entries[1])), "Audit");
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? value : PdfNull.Instance;
    private static QpdfObject Resolve(QpdfDocument reader, QpdfObject value)
        => value is PdfReference reference ? reader.GetObject(reference.ObjectNumber) : value;

    private static string Program(QpdfDocument reader, PdfStream stream)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using FilterPlan stages = FilterChain.ResolveScoped(stream.Dictionary, reader.Engine, governor, CancellationToken.None);
        if (stages.Count == 0) { return Encoding.ASCII.GetString(stream.RawMemory.Span); }
        byte[] bytes = FilterChain.Decode(stream.ArrayData(memory, governor, CancellationToken.None), stages,
            QpdfLimits.Default, governor, memory, CancellationToken.None);
        return Encoding.ASCII.GetString(bytes);
    }

    private static Task<QpdfExecutionResult> Execute(IQpdfHost host, string route, bool flatten, bool selection, QpdfLimits limits)
    {
        if (route == "job")
        {
            Dictionary<string, object> job = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["generateAppearances"] = true };
            if (flatten) { job["flattenAnnotations"] = "all"; }
            if (selection) { job["pages"] = new[] { new { file = ".", range = "1-z" } }; }
            return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new(limits), CancellationToken.None).AsTask();
        }
        List<string> args = ["input", "--generate-appearances"];
        if (flatten) { args.Add("--flatten-annotations=all"); }
        if (selection) { args.AddRange(["--pages", ".", "1-z", "--"]); }
        if (!selection) { args.Add("--"); }
        args.Add("output");
        return Qpdf.ExecuteAsync(host, new(args), new(limits), CancellationToken.None).AsTask();
    }

    [Theory]
    [InlineData("argv", 1)]
    [InlineData("job", 1)]
    [InlineData("argv", 2)]
    [InlineData("job", 2)]
    public async Task SharedArrayWarningsRemainBoundedWithoutWaivingRealOverflow(string route, int directCount)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("fresh", "catalog", true, 3, directCount), QpdfInputOwnership.Copy);
        await JsonPageTreeTests.Seed(host);
        QpdfExecutionResult result = await Execute(host, route, false, false, QpdfLimits.Default with { MaxDiagnostics = 1L });
        Assert.Equal(directCount == 1 ? QpdfExitCode.Warning : QpdfExitCode.Error, result.ExitCode);
        if (directCount == 1) { Assert.Single(result.Diagnostics); }
        else { JsonPageTreeTests.AssertPrior(host); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task OriginalAndGeneratedArraysRemainOwnedAfterTheirReaderAndHostClose()
    {
        RecordingMemoryBudget root = new(16777216L);
        using (IMemoryScope caller = root.CreateScope())
        {
            PdfArray original, replacement; PdfStream appearance;
            ReadOnlyMemory<byte> payload;
            using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, caller))
            {
                host.AddInput(QpdfPath.From("input"), Fixture("fresh", "catalog", true, 3, 1), QpdfInputOwnership.Transfer);
                QpdfExecutionResult generated = await Execute(host, "job", false, false, QpdfLimits.Default);
                Assert.Equal(QpdfExitCode.Warning, generated.ExitCode);
                using (IMemoryScope export = root.CreateScope())
                {
                    Assert.True(host.TryGetOutput(QpdfPath.From("output"), export, out byte[] output));
                    host.AddInput(QpdfPath.From("result"), output, QpdfInputOwnership.Copy);
                }
                await using QpdfDocument reader = await QpdfDocument.OpenAsync(host, QpdfPath.From("result"), CancellationToken.None);
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(reader, Get(reader.Engine.Trailer, "Root")));
                original = Assert.IsType<PdfArray>(Resolve(reader, Get(catalog, "Audit")));
                replacement = Assert.IsType<PdfArray>(Resolve(reader, Get(reader.PageReferences[0].Page, "Annots")));
                PdfDictionary direct = Assert.IsType<PdfDictionary>(replacement[1]);
                appearance = Assert.IsType<PdfStream>(Resolve(reader, Get(Assert.IsType<PdfDictionary>(Resolve(reader, Get(direct, "AP"))), "N")));
                payload = appearance.RawMemory;
            }
            Assert.True(root.ReservedBytes > 0L); Assert.NotSame(original, replacement);
            Assert.IsType<PdfNull>(Get(Assert.IsType<PdfDictionary>(original[1]), "AP"));
            Assert.IsType<PdfDictionary>(Get(Assert.IsType<PdfDictionary>(replacement[1]), "AP"));
            Assert.Equal(payload, appearance.RawMemory); Assert.False(payload.IsEmpty);
            GC.KeepAlive(original); GC.KeepAlive(replacement); GC.KeepAlive(appearance);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}

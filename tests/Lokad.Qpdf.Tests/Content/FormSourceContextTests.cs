using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FormSourceContextTests
{
    [Theory]
    [InlineData("fallback", "merge", false)]
    [InlineData("fallback", "overlay", true)]
    [InlineData("fallback", "underlay", false)]
    [InlineData("same-reference", "merge", true)]
    [InlineData("same-reference", "overlay", false)]
    [InlineData("same-reference", "underlay", true)]
    [InlineData("other-reference", "merge", false)]
    [InlineData("other-reference", "overlay", true)]
    [InlineData("other-reference", "underlay", false)]
    public async Task RewrittenSharedProgramsKeepOneIdentityAndTheirOriginalAliases(string shape, string operation, bool job)
    {
        const int Count = 32;
        string large = "/F1 12 Tf 0 g " + new string(' ', 65536);
        string refs = string.Join(' ', Enumerable.Range(16, Count).Select(id => id + " 0 R"));
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 12 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R /Annots [" + refs + "] >>", null),
            (4, "<< /Length 0 >>", []), (5, "null", null),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null), (7, "null", null),
            (8, "(" + (shape == "other-reference" ? "/F1 12 Tf 0 g" : large) + ")", null),
            (9, "<< /Font << /F1 6 0 R >> >>", null), (10, "null", null), (11, "null", null),
            (12, "<< /Fields [" + refs + "] /DR 9 0 R /DA 8 0 R /Q 2 >>", null),
            (13, "null", null), (14, "(" + large + ")", null), (15, "null", null)];
        for (int index = 0; index < Count; index++)
        {
            string da = shape == "fallback" ? "" : "/DA " + (shape == "same-reference" ? "8" : "14") + " 0 R";
            objects.Add((16 + index, "<< /Subtype /Widget /FT /Tx /T (Source" + index + ") /V (Source) /Rect [0 0 200 30] /Audit 14 0 R " + da + " >>", null));
        }
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(false, "absent", false, true), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("source"), CheckTests.Assemble(objects, 1), QpdfInputOwnership.Copy);
        List<string> argv = ["input"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
        if (operation == "merge")
        { argv.AddRange(["--pages", ".", "1", "source", "1", "--"]); settings["pages"] = new[] { new { file = ".", range = "1" }, new { file = "source", range = "1" } }; }
        else { argv.AddRange(["--" + operation, "source", "--"]); settings[operation] = new { file = "source" }; }
        argv.Add("output");
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxOutputBytes = 3L * large.Length });
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), options, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.True(output.Length < 3 * large.Length);
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject raw) => raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw;
        PdfReference? shared = null; int seen = 0;
        foreach (var page in document.PageReferences)
        {
            Assert.True(page.Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annots));
            foreach (QpdfObject item in Assert.IsType<PdfArray>(Resolve(annots)).BorrowedItems)
            {
                PdfDictionary field = Assert.IsType<PdfDictionary>(Resolve(item));
                Assert.True(field.TryGet(PdfName.FromAscii("V"), out QpdfObject value));
                if (!Assert.IsType<PdfByteString>(Resolve(value)).ValueSpan.SequenceEqual("Source"u8)) { continue; }
                seen++; Assert.True(field.TryGet(PdfName.FromAscii("DA"), out QpdfObject raw));
                PdfReference reference = Assert.IsType<PdfReference>(raw); shared ??= reference; Assert.Equal(shared, reference);
                Assert.True(field.TryGet(PdfName.FromAscii("Audit"), out QpdfObject alias));
                Assert.Equal(large, Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Resolve(alias)).ValueSpan));
            }
        }
        Assert.Equal(Count, seen); Assert.NotNull(shared);
        Assert.Equal(large.Replace("/F1 ", "/F1_1 ", StringComparison.Ordinal), Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Resolve(shared)).ValueSpan));
    }

    internal static byte[] Fixture(bool source, string shape, bool equalDefaults, bool indirectCategory)
    {
        string size = source || equalDefaults ? "12" : "9";
        string da = "/F1 " + size + " Tf 0 g";
        string fieldDa = shape switch
        {
            "direct" => "/DA (" + da + ")", "indirect" => "/DA 8 0 R",
            "null" => "/DA null", "indirect-null" => "/DA 13 0 R", _ => ""
        };
        bool shared = shape == "shared-parent";
        bool parent = shared || shape.StartsWith("parent-", StringComparison.Ordinal);
        string parentDa = shape switch
        {
            "parent-direct" => "/DA (" + da + ")", "parent-indirect" or "shared-parent" => "/DA 8 0 R",
            "parent-null" => "/DA 13 0 R", "parent-quadding" => "/Q 1", _ => ""
        };
        string font = shape == "third-source" ? "Times-Roman" : source ? "Courier" : "Helvetica";
        string name = shape == "third-source" ? "Third" : source ? "Source" : "Target";
        byte[] appearance = Encoding.ASCII.GetBytes("/Tx BMC q BT /F1 " + size + " Tf 0 g 2 10 Td (" + name + ") Tj ET Q EMC\n");
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 12 0 R >>", null),
            (2, shared ? "<< /Type /Pages /Count 2 /Kids [3 0 R 15 0 R] >>" : "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R /Annots [5 0 R] /AuditDa 8 0 R >>", null),
            (4, "<< /Length 0 >>", []),
            (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (" + name + ") /V (" + name + ") /P 3 0 R /Rect [0 0 200 30] /AP << /N 7 0 R >> " + fieldDa + (parent ? " /Parent 11 0 R" : "") + " >>", null),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /" + font + " /Encoding /WinAnsiEncoding >>", null),
            (7, "<< /Type /XObject /Subtype /Form /BBox [0 0 200 30] /Resources << /Font << /F1 " + (shape == "local-appearance" ? "14" : "6") + " 0 R >> >> /Length " + appearance.Length + " >>", appearance),
            (8, "(" + da + ")", null),
            (9, "<< /Font " + (indirectCategory ? "10 0 R" : "<< /F1 6 0 R >>") + " >>", null),
            (10, "<< /F1 6 0 R >>", null),
            (11, "<< /T (Group) /Kids [5 0 R " + (shared ? "14 0 R" : "") + "] " + parentDa + " >>", null),
            (12, "<< /Fields [" + (parent ? "11" : "5") + " 0 R] /DR 9 0 R /DA 8 0 R /Q " + (source ? "2" : "0") + " >>", null),
            (13, "null", null)];
        if (shared)
        {
            objects.Add((14, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Second) /V (Source) /Parent 11 0 R /P 15 0 R /Rect [0 0 200 30] /AP << /N 7 0 R >> >>", null));
            objects.Add((15, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R /Annots [14 0 R] /AuditDa 8 0 R >>", null));
        }
        if (shape == "local-appearance") { objects.Add((14, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>", null)); }
        return CheckTests.Assemble(objects, 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "direct", "indirect", "absent", "null", "indirect-null", "parent-direct", "parent-indirect", "parent-null", "parent-quadding" })
        foreach (string operation in new[] { "merge", "overlay", "underlay" })
        foreach (bool equalDefaults in new[] { false, true })
        foreach (string route in new[] { "argv", "job" })
        { yield return [shape, operation, equalDefaults, route]; }
        foreach (string shape in new[] { "indirect", "absent", "parent-indirect" })
        foreach (string operation in new[] { "merge", "overlay", "underlay" })
        foreach (string layout in new[] { "packed", "linear", "generated" })
        { yield return [shape, operation, false, layout]; }
        foreach (string shape in new[] { "direct", "indirect", "absent", "parent-indirect", "parent-null", "parent-quadding" })
        foreach (string route in new[] { "typed", "omitted", "repeated", "groups" })
        { yield return [shape, "merge", true, route]; }
        foreach (string route in new[] { "argv", "job", "typed", "reverse" })
        { yield return ["shared-parent", "merge", true, route]; }
        foreach (string shape in new[] { "indirect", "absent", "parent-indirect" })
        foreach (string route in new[] { "argv", "job" })
        { yield return [shape, "mixed", true, route]; }
        foreach (string operation in new[] { "merge", "overlay", "underlay" })
        foreach (string route in new[] { "argv", "generated" })
        { yield return ["local-appearance", operation, true, route]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RetainedFieldsKeepSourceFontAndDefaults(string shape, string operation, bool equalDefaults, string route)
    {
        List<PdfByteString> escaped = [];
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(false, "absent", equalDefaults, true), QpdfInputOwnership.Copy);
            host.AddInput(QpdfPath.From("source"), Fixture(true, shape, equalDefaults, true), QpdfInputOwnership.Copy);
            bool shared = shape == "shared-parent";
            if (operation == "mixed") { host.AddInput(QpdfPath.From("third"), Fixture(true, "third-source", equalDefaults, true), QpdfInputOwnership.Copy); }
            if (route == "typed")
            {
                await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("input"), new(1)), new(QpdfPath.From("source"), shared ? PdfPageSelectionOptions.FromPages([1, 2]) : new(1))],
                    QpdfPath.From("output"), QpdfLimits.Default, CancellationToken.None);
            }
            else
            {
                List<string> argv = ["input"];
                Dictionary<string, object> job = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
                if (operation is "merge" or "mixed")
                {
                    argv.Add("--pages"); List<object> sources = [];
                    if (route != "omitted") { argv.AddRange([".", "1"]); sources.Add(new { file = ".", range = "1" }); }
                    string range = shared ? route == "reverse" ? "2,1" : "1-2" : route == "repeated" ? "1,1" : "1";
                    argv.AddRange(["source", range]); sources.Add(new { file = "source", range });
                    if (route == "groups") { argv.AddRange(["source", "1"]); sources.Add(new { file = "source", range = "1" }); }
                    argv.Add("--"); job["pages"] = sources;
                    if (operation == "mixed")
                    { argv.AddRange(["--overlay", "third", "--to=1", "--from=1", "--"]); job["overlay"] = new { file = "third", to = "1", from = "1" }; }
                }
                else { argv.AddRange(["--" + operation, "source", "--"]); job[operation] = new { file = "source" }; }
                if (route == "packed") { argv.Add("--object-streams=generate"); job["objectStreams"] = "generate"; }
                if (route == "linear") { argv.Add("--linearize"); job["linearize"] = true; }
                if (route == "generated") { argv.Add("--generate-appearances"); job["generateAppearances"] = true; }
                argv.Add("output");
                QpdfExecutionResult result = route == "job"
                    ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None)
                    : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
                Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            }
            Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
            QpdfObject Resolve(QpdfObject raw) => raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw;
            QpdfObject Get(PdfDictionary node, string key) { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(value); }
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Get(document.Engine.Trailer, "Root"));
            PdfDictionary form = Assert.IsType<PdfDictionary>(Get(catalog, "AcroForm"));
            PdfDictionary fonts = Assert.IsType<PdfDictionary>(Get(Assert.IsType<PdfDictionary>(Get(form, "DR")), "Font"));
            QpdfObject Effective(PdfDictionary node, string key)
            {
                for (int depth = 0; depth < 8; depth++)
                {
                    if (node.TryGet(PdfName.FromAscii(key), out QpdfObject raw) && Resolve(raw) is not PdfNull) { return Resolve(raw); }
                    if (!node.TryGet(PdfName.FromAscii("Parent"), out QpdfObject parent) || Resolve(parent) is not PdfDictionary next) { break; }
                    node = next;
                }
                return Get(form, key);
            }
            int seen = 0; List<PdfReference> parents = [];
            foreach (var page in document.PageReferences)
            foreach (QpdfObject entry in Assert.IsType<PdfArray>(Get(page.Page, "Annots")).BorrowedItems)
            {
                PdfDictionary field = Assert.IsType<PdfDictionary>(Resolve(entry));
                PdfByteString value = Assert.IsType<PdfByteString>(Get(field, "V"));
                bool third = value.ValueSpan.SequenceEqual("Third"u8);
                if (!third && !value.ValueSpan.SequenceEqual("Source"u8)) { continue; }
                seen++;
                PdfByteString da = Assert.IsType<PdfByteString>(Effective(field, "DA")); escaped.Add(da);
                string program = Encoding.ASCII.GetString(da.ValueSpan);
                Assert.Contains("12 Tf", program);
                string fontKey = program.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0][1..];
                PdfDictionary font = Assert.IsType<PdfDictionary>(Get(fonts, fontKey));
                Assert.Equal(PdfName.FromAscii(third ? "Times-Roman" : "Courier"), Get(font, "BaseFont"));
                Assert.Equal(shape == "parent-quadding" ? 1 : 2, Assert.IsType<PdfInteger>(Effective(field, "Q")).Value);
                if (shape == "local-appearance")
                {
                    PdfStream appearance = Assert.IsType<PdfStream>(Get(Assert.IsType<PdfDictionary>(Get(field, "AP")), "N"));
                    PdfDictionary localFonts = Assert.IsType<PdfDictionary>(Get(Assert.IsType<PdfDictionary>(Get(appearance.Dictionary, "Resources")), "Font"));
                    Assert.Equal(PdfName.FromAscii("Times-Roman"), Get(Assert.IsType<PdfDictionary>(Get(localFonts, "F1")), "BaseFont"));
                }
                if (!third && operation is "merge" or "mixed")
                {
                    PdfByteString alias = Assert.IsType<PdfByteString>(Get(page.Page, "AuditDa"));
                    Assert.Equal("/F1 12 Tf 0 g", Encoding.ASCII.GetString(alias.ValueSpan));
                }
                if (shared) { Assert.True(field.TryGet(PdfName.FromAscii("Parent"), out QpdfObject parent)); parents.Add(Assert.IsType<PdfReference>(parent)); }
            }
            Assert.Equal(shared || operation == "mixed" || route is "repeated" or "groups" ? 2 : 1, seen);
            if (shared) { Assert.Equal(2, parents.Count); Assert.Equal(parents[0], parents[1]); }
        }
        foreach (PdfByteString da in escaped) { Assert.Contains("12 Tf", Encoding.ASCII.GetString(da.ValueSpan)); }
    }
}

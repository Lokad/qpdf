using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayDefaultTests
{
    internal static byte[] Fixture(string defaults, string appearance, string quadding, string need, bool own, string value)
    {
        byte[] data = "BT /F1 10 Tf 2 5 Td (kept) Tj ET\n"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 8 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Annots [5 0 R] >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Field) /V (" + value + ") /Rect [0 0 100 20] /AP << /N 7 0 R >> " + (own ? "/DA (/F1 14 Tf 0 g) /Q 1" : "") + " >>", null),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (7, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /F1 6 0 R >> >> /Length " + data.Length + " >>", data),
            (8, "<< /Fields [5 0 R] /DR << /Font << /F1 6 0 R >> >> " + defaults + " >>", null),
            (9, appearance, null), (10, quadding, null), (11, need, null)], 1);
    }

    private static Task<QpdfExecutionResult> Run(IQpdfHost host, bool job, string mode, bool selection, bool repeated, CancellationToken token)
    {
        string range = repeated ? "1,1" : "1";
        List<string> argv = ["input"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", [mode] = new { file = "stamp", to = range, from = range } };
        if (selection) { argv.AddRange(["--pages", ".", "1", "--"]); settings["pages"] = new[] { new { file = ".", range = "1" } }; }
        argv.AddRange(["--" + mode, "stamp", "--to=" + range, "--from=" + range, "--", "output"]);
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), token).AsTask()
            : Qpdf.ExecuteAsync(host, new(argv), token).AsTask();
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject raw)
        => raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw;
    private static QpdfObject Get(QpdfDocument document, PdfDictionary node, string key)
    { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject raw)); return Resolve(document, raw); }
    private static QpdfObject Effective(QpdfDocument document, PdfDictionary field, PdfDictionary form, string key)
        => field.TryGet(PdfName.FromAscii(key), out QpdfObject raw) && Resolve(document, raw) is not PdfNull
            ? Resolve(document, raw) : Get(document, form, key);

    private static async Task Check(byte[] output, int imports, bool own, bool destination)
    {
        List<PdfByteString> escaped = [];
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            PdfDictionary form = Assert.IsType<PdfDictionary>(Get(document, catalog, "AcroForm"));
            Assert.True(Assert.IsType<PdfBoolean>(Get(document, form, "NeedAppearances")).Value);
            PdfArray fields = Assert.IsType<PdfArray>(Get(document, form, "Fields")); Assert.Equal(imports + (destination ? 1 : 0), fields.Count);
            int seen = 0;
            for (int i = 0; i < fields.Count; i++)
            {
                PdfDictionary field = Assert.IsType<PdfDictionary>(Resolve(document, fields[i]));
                PdfByteString value = Assert.IsType<PdfByteString>(Get(document, field, "V"));
                bool source = !value.ValueSpan.SequenceEqual("BASE"u8);
                Assert.True(!source || value.ValueSpan.SequenceEqual("LIVE"u8) || value.ValueSpan.SequenceEqual("kept"u8));
                if (source) { seen++; }
                Assert.Equal(source && own ? 1L : source ? 2L : 0L, Assert.IsType<PdfInteger>(Effective(document, field, form, "Q")).Value);
                PdfByteString da = Assert.IsType<PdfByteString>(Effective(document, field, form, "DA")); escaped.Add(da);
                string program = Encoding.ASCII.GetString(da.ValueSpan);
                Assert.Contains(source && own ? "14 Tf 0 g" : source ? "18 Tf 0.5 g" : "12 Tf 0 g", program);
            }
            Assert.Equal(imports, seen);
        }
        foreach (PdfByteString da in escaped) { Assert.Contains(" Tf ", Encoding.ASCII.GetString(da.ValueSpan)); }
    }

    public static IEnumerable<object[]> SourceCases()
    {
        foreach (bool job in new[] { false, true })
        foreach (string mode in new[] { "overlay", "underlay" })
        foreach (bool selection in new[] { false, true })
        foreach (bool repeated in new[] { false, true })
        foreach (string name in new[] { "direct", "indirect-da", "indirect-q", "indirect-need", "all-indirect", "field-overrides" })
        { yield return [job, mode, selection, repeated, name]; }
    }

    [Theory]
    [MemberData(nameof(SourceCases))]
    public async Task ImportedDefaultsRetainEffectiveValues(bool job, string mode, bool selection, bool repeated, string name)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("stamp"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "form-defaults-" + name + ".pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, mode, selection, repeated, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        await Check(output, repeated ? 2 : 1, name == "field-overrides", false);
    }

    [Theory]
    [InlineData(false, "overlay", false)]
    [InlineData(true, "overlay", true)]
    [InlineData(false, "underlay", true)]
    [InlineData(true, "underlay", false)]
    public async Task IndirectDestinationDefaultsDoNotOverrideImportedValues(bool job, string mode, bool selection)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/DA 9 0 R /Q 10 0 R /NeedAppearances 11 0 R", "(/F1 12 Tf 0 g)", "0", "false", false, "BASE"), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("stamp"), Fixture("/DA 9 0 R /Q 10 0 R /NeedAppearances 11 0 R", "(/F1 18 Tf 0.5 g)", "2", "true", false, "LIVE"), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, mode, selection, true, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); await Check(output, 2, false, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EqualIndirectDestinationDefaultsAvoidRedundantFieldOverrides(bool job)
    {
        using InMemoryQpdfHost host = new();
        const string defaults = "/DA 9 0 R /Q 10 0 R /NeedAppearances 11 0 R";
        host.AddInput(QpdfPath.From("input"), Fixture(defaults, "(/F1 18 Tf 0.5 g)", "2", "false", false, "BASE"), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("stamp"), Fixture(defaults, "(/F1 18 Tf 0.5 g)", "2", "true", false, "LIVE"), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, "overlay", true, false, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary form = Assert.IsType<PdfDictionary>(Get(document, Assert.IsType<PdfDictionary>(document.GetObject(1)), "AcroForm"));
        PdfArray fields = Assert.IsType<PdfArray>(Get(document, form, "Fields")); Assert.Equal(2, fields.Count);
        PdfDictionary imported = Assert.IsType<PdfDictionary>(Resolve(document, fields[1]));
        Assert.Equal("LIVE"u8.ToArray(), Assert.IsType<PdfByteString>(Get(document, imported, "V")).Value);
        Assert.False(imported.TryGet(PdfName.FromAscii("DA"), out _));
        Assert.False(imported.TryGet(PdfName.FromAscii("Q"), out _));
        Assert.Equal(2L, Assert.IsType<PdfInteger>(Effective(document, imported, form, "Q")).Value);
    }

    [Theory]
    [InlineData("/DA null /Q null /NeedAppearances null", "null", "null", "null")]
    [InlineData("/DA /Bad /Q /Bad /NeedAppearances /Bad", "/Bad", "/Bad", "/Bad")]
    [InlineData("/DA 9 0 R /Q 10 0 R /NeedAppearances 11 0 R", "null", "null", "null")]
    [InlineData("/DA 9 0 R /Q 10 0 R /NeedAppearances 11 0 R", "<< >>", "2147483648", "(true)")]
    [InlineData("/DA 9 0 R /Q 10 0 R /NeedAppearances 11 0 R", "9 0 R", "10 0 R", "11 0 R")]
    public async Task UnusableDefaultsKeepFallbacksAndLocalOverrides(string defaults, string da, string q, string need)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("stamp"), Fixture(defaults, da, q, need, true, "LIVE"), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, true, "overlay", false, false, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal(0L, host.OpenLeaseCount);
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary form = Assert.IsType<PdfDictionary>(Get(document, Assert.IsType<PdfDictionary>(document.GetObject(1)), "AcroForm"));
        Assert.False(form.TryGet(PdfName.FromAscii("NeedAppearances"), out QpdfObject flag) && Resolve(document, flag) is PdfBoolean boolean && boolean.Value);
        PdfArray fields = Assert.IsType<PdfArray>(Get(document, form, "Fields"));
        PdfDictionary field = Assert.IsType<PdfDictionary>(Resolve(document, fields[0]));
        Assert.Equal(1L, Assert.IsType<PdfInteger>(Effective(document, field, form, "Q")).Value);
        Assert.Contains("14 Tf 0 g", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Effective(document, field, form, "DA")).ValueSpan));
    }

}

using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FormNeedAppearancesTests
{
    internal static byte[] Fixture(string flag, string value, int generation)
    {
        byte[] appearance = "/Tx BMC BT /F1 10 Tf 2 5 Td (kept) Tj ET EMC\n"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 8 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Annots [5 0 R] >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Field) /V (LIVE) /Rect [0 0 100 20] /AP << /N 7 0 R >> >>", null),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (7, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /F1 6 0 R >> >> /Length " + appearance.Length + " >>", appearance),
            (8, "<< /Fields [5 0 R] /DR << /Font << /F1 6 0 R >> >> /DA (/F1 18 Tf 0.5 g) /Q 2 " + (flag.Length == 0 ? "" : "/NeedAppearances " + flag) + " >>", null),
            (9, value, null)], 1, new Dictionary<int, int> { [9] = generation });
    }

    private static Task<QpdfExecutionResult> Run(IQpdfHost host, bool job, string path, string operation, CancellationToken token)
    {
        List<string> argv = ["input", "--compress-streams=n"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["compressStreams"] = "n" };
        if (operation is "generate" or "both") { argv.Add("--generate-appearances"); settings["generateAppearances"] = true; }
        if (operation is "flatten" or "both") { argv.Add("--flatten-annotations=all"); settings["flattenAnnotations"] = "all"; }
        if (path is "select" or "merge")
        {
            argv.AddRange(["--pages", ".", "1"]);
            List<object> pages = [new { file = ".", range = "1" }];
            if (path == "merge") { argv.AddRange(["other", "1"]); pages.Add(new { file = "other", range = "1" }); }
            argv.Add("--"); settings["pages"] = pages;
        }
        if (path == "split") { argv.Add("--split-pages=1"); settings["splitPages"] = "1"; }
        argv.Add("output");
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), token).AsTask()
            : Qpdf.ExecuteAsync(host, new(argv), token).AsTask();
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject raw)
        => raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw;
    private static QpdfObject Get(QpdfDocument document, PdfDictionary node, string key)
    {
        Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject raw));
        return Resolve(document, raw);
    }

    private static async Task Verify(byte[] output, bool flattened, bool generated)
    {
        PdfByteString? escaped = null;
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            PdfDictionary page = document.PageReferences[0].Page;
            if (flattened)
            {
                if (page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject raw))
                { Assert.Equal(0, Assert.IsType<PdfArray>(Resolve(document, raw)).Count); }
                PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(document, page, "Resources"));
                PdfDictionary forms = Assert.IsType<PdfDictionary>(Get(document, resources, "XObject"));
                List<string> programs = [];
                foreach (PdfName name in forms.Keys)
                { programs.Add(Encoding.ASCII.GetString(Assert.IsType<PdfStream>(Get(document, forms, name.DisplayText)).Data)); }
                Assert.Contains(programs, p => p.Contains(generated ? "(LIVE)" : "(kept)", StringComparison.Ordinal));
            }
            else
            {
                PdfArray annots = Assert.IsType<PdfArray>(Get(document, page, "Annots")); Assert.Equal(1, annots.Count);
                PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(document, annots[0]));
                escaped = Assert.IsType<PdfByteString>(Get(document, widget, "V"));
                PdfDictionary ap = Assert.IsType<PdfDictionary>(Get(document, widget, "AP"));
                string program = Encoding.ASCII.GetString(Assert.IsType<PdfStream>(Get(document, ap, "N")).Data);
                Assert.Contains(generated ? "(LIVE)" : "(kept)", program);
                Assert.DoesNotContain(generated ? "(kept)" : "(LIVE)", program);
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
                PdfDictionary form = Assert.IsType<PdfDictionary>(Get(document, catalog, "AcroForm"));
                bool needs = form.TryGet(PdfName.FromAscii("NeedAppearances"), out QpdfObject flag)
                    && Resolve(document, flag) is PdfBoolean boolean && boolean.Value;
                Assert.Equal(!generated, needs);
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        if (escaped is not null) { Assert.Equal("LIVE"u8.ToArray(), escaped.Value); }
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (string path in new[] { "whole", "select", "merge", "split" })
        foreach (string operation in new[] { "copy", "generate", "flatten", "both" })
        foreach (bool indirect in new[] { false, true }) { yield return [job, path, operation, indirect]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DirectAndIndirectTrueKeepCurrentValues(bool job, string path, string operation, bool indirect)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(indirect ? "9 3 R" : "true", "true", 3), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("other"), Fixture("false", "false", 0), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, path, operation, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(operation == "flatten" ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
        if (operation == "flatten") { Assert.Contains(result.Diagnostics, d => d.Message.Contains("will not be flattened", StringComparison.Ordinal)); }
        else { Assert.Empty(result.Diagnostics); }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From(path == "split" ? "output-1" : "output"), out byte[] output));
        await Verify(output, operation == "both", operation is "generate" or "both");
    }

    [Theory]
    [InlineData("", "true", 0)]
    [InlineData("false", "true", 0)]
    [InlineData("null", "true", 0)]
    [InlineData("/Bad", "true", 0)]
    [InlineData("9 0 R", "false", 0)]
    [InlineData("9 0 R", "null", 0)]
    [InlineData("9 0 R", "<< >>", 0)]
    [InlineData("9 0 R", "9 0 R", 0)]
    [InlineData("9 0 R", "true", 3)]
    public async Task UnusableOrFalseFlagsKeepTheExistingFlattenFallback(string flag, string value, int generation)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(flag, value, generation), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, false, "whole", "both", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.Equal(0L, host.OpenLeaseCount); await Verify(output, true, false);
    }

    [Fact]
    public async Task MissingFlagObjectStillFailsBeforePublication()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("99 0 R", "true", 0), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Run(host, true, "whole", "both", CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.NotEmpty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
    }
}

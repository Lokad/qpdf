using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceProgramTests
{
    internal static byte[] Fixture(string program, string field, string form, string resources)
    {
        byte[] data = Encoding.ASCII.GetBytes(program);
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /NeedAppearances true /DR << /Font << /Helv 9 0 R >> >> " + form + " >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [5 0 R] >>", null),
            (4, "null", null),
            (5, "<< /Subtype /Widget /FT /Tx /T (Text) /V (Hello) /Rect [0 0 100 20] /AP << /N 6 0 R >> " + field + " >>", null),
            (6, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] " + resources + " /Length " + data.Length + " >>", data),
            (7, "(/H#65lv 17 Tf)", null), (8, "<< /Font << /Helv 9 0 R >> >>", null),
            (9, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null),
            (10, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>", null), (11, "<< >>", null)], 1);
    }

    [Theory]
    [InlineData("/Tx BMC BT (Old) Tj ET EMC")]
    [InlineData("/Tx BMC /Span BMC BT (Old) Tj ET EMC EMC")]
    [InlineData("/Tx << /MCID 0 /ActualText (EMC BMC) >> BDC /Span BMC BT (Old) Tj ET EMC EMC")]
    [InlineData("/T#78 BMC BT (Old) Tj ET EMC")]
    [InlineData("/Tx BMC BI /W 1 /H 1 /BPC 8 /CS /RGB ID EMC EI BT (Old) Tj ET EMC")]
    public async Task ReplacesTheMatchingTextBlockAndPreservesUnrelatedBytes(string block)
    {
        const string prefix = "% prefix\n/Artifact BMC 0 0 3 3 re f EMC\n";
        const string suffix = "\n/Span BMC BT (Unrelated) Tj ET EMC % suffix";
        foreach (bool job in new[] { false, true })
        {
            byte[] input = Fixture(prefix + block + suffix, "/DA (/Helv 17 Tf)", "", "/Resources 8 0 R");
            var generated = await Generate(input, job, false); Assert.Equal(QpdfExitCode.Success, generated.Result.ExitCode);
            PdfStream appearance = await ReadAppearance(generated.Pdf);
            string text = Encoding.ASCII.GetString(appearance.RawMemory.Span);
            Assert.StartsWith(prefix, text); Assert.EndsWith(suffix, text); Assert.Contains("(Hello) Tj", text); Assert.DoesNotContain("(Old)", text);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            ContentTokenizer tokenizer = new(appearance.RawMemory, QpdfLimits.Default, governor);
            int starts = 0, ends = 0;
            while (tokenizer.TryReadToken(out ContentToken token))
            {
                if (token.Kind != ContentTokenKind.Operator) { continue; }
                if (token.Data.Span.SequenceEqual("BMC"u8) || token.Data.Span.SequenceEqual("BDC"u8)) { starts++; }
                if (token.Data.Span.SequenceEqual("EMC"u8)) { ends++; }
            }
            Assert.Equal(3, starts); Assert.Equal(3, ends);
        }
    }

    [Theory]
    [InlineData("/Tx BMC BT (Old) Tj ET")]
    [InlineData("EMC /Tx BMC EMC")]
    [InlineData("/Tx << /MCID [0 >> BDC EMC")]
    [InlineData("/Tx BDC EMC")]
    [InlineData("42 BMC EMC")]
    [InlineData("/Tx 42 BDC EMC")]
    [InlineData("/Tx BMC (unterminated")]
    public async Task MalformedProgramsWarnAndKeepTheOriginalAppearance(string program)
    {
        foreach (bool job in new[] { false, true })
        {
            var generated = await Generate(Fixture(program, "/DA (/Helv 17 Tf)", "", "/Resources 8 0 R"), job, false);
            Assert.Equal(QpdfExitCode.Warning, generated.Result.ExitCode);
            Assert.Contains(generated.Result.Diagnostics, d => d.Message.Contains("malformed appearance", StringComparison.Ordinal));
            Assert.Equal(Encoding.ASCII.GetBytes(program), (await ReadAppearance(generated.Pdf)).RawMemory.ToArray());
        }
    }

    [Fact]
    public async Task MarkedContentNestingLimitFailsBeforePublication()
    {
        string program = "/Tx BMC " + string.Concat(Enumerable.Repeat("/Span BMC ", 20)) + string.Concat(Enumerable.Repeat("EMC ", 21));
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 16 };
        using InMemoryQpdfHost host = new(limits); host.AddInput(QpdfPath.From("input"), Fixture(program, "/DA (/Helv 17 Tf)", "", "/Resources 8 0 R"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--generate-appearances", "--", "output"]), new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded && d.Message.Contains(nameof(QpdfLimits.MaxNestingDepth), StringComparison.Ordinal));
        Assert.False(host.TryGetOutput(QpdfPath.From("output"), out _)); Assert.Equal(0L, host.OpenLeaseCount);
    }

    internal static async Task<(byte[] Pdf, QpdfExecutionResult Result)> Generate(byte[] input, bool job, bool selection)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, selection
                ? """{"inputFile":"input","outputFile":"output","generateAppearances":true,"compressStreams":"n","pages":[{"file":".","range":"1"}]}"""
                : """{"inputFile":"input","outputFile":"output","generateAppearances":true,"compressStreams":"n"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(selection
                ? ["input", "--generate-appearances", "--compress-streams=n", "--pages", ".", "1", "--", "output"]
                : ["input", "--generate-appearances", "--compress-streams=n", "--", "output"]), CancellationToken.None);
        Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); return (output, result);
    }

    internal static async Task<PdfStream> ReadAppearance(byte[] pdf)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), pdf, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annotations));
        PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(document, Assert.IsType<PdfArray>(Resolve(document, annotations))[0]));
        Assert.True(widget.TryGet(PdfName.FromAscii("AP"), out QpdfObject ap));
        Assert.True(Assert.IsType<PdfDictionary>(Resolve(document, ap)).TryGet(PdfName.FromAscii("N"), out QpdfObject normal));
        PdfStream stream = Assert.IsType<PdfStream>(Resolve(document, normal)); Assert.False(stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _)); return stream;
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
}

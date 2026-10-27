using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PdfTextIntegrationTests
{
    private const string Title = "Résumé € 😀";

    [Fact]
    public void EveryPdfDocByteKeepsItsEncodingInverseOrRemainsUndefined()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        Dictionary<char, byte> first = new();
        for (int unit = 0; unit <= 255; unit++)
        {
            char decoded = Assert.Single(PdfText.Decode(new byte[] { (byte)unit }, memory, governor, CancellationToken.None));
            if (decoded == '\uFFFD') { Assert.False(PdfText.TryEncodePdfDocByte(decoded, out _)); continue; }
            first.TryAdd(decoded, (byte)unit);
            Assert.True(PdfText.TryEncodePdfDocByte(decoded, out byte encoded)); Assert.Equal(first[decoded], encoded);
        }
    }

    private static (byte[] Bytes, string Expected) Text(string encoding) => encoding switch
    {
        "utf8" => ([0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(Title)], Title),
        "utf8-invalid" => ([0xef, 0xbb, 0xbf, (byte)'A', 0xe2, 0x82], "A�"),
        "utf16-big" => ([0xfe, 0xff, .. Encoding.BigEndianUnicode.GetBytes(Title)], Title),
        "utf16-little" => ([0xff, 0xfe, .. Encoding.Unicode.GetBytes(Title)], Title),
        _ => ([.. Encoding.Latin1.GetBytes("Résumé "), 0xa0], "Résumé €"),
    };

    private static byte[] Fixture(byte[] text)
    {
        string value = "<" + Convert.ToHexString(text) + ">";
        byte[] bytes = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R /AcroForm 6 0 R /Names << /EmbeddedFiles 8 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Annots [7 0 R] >>", null),
            (4, "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>", null),
            (5, "<< /Title " + value + " /Parent 4 0 R /Dest [3 0 R /Fit] >>", null),
            (6, "<< /Fields [7 0 R] >>", null),
            (7, "<< /Type /Annot /Subtype /Widget /P 3 0 R /Rect [0 0 10 10] /FT /Ch /T " + value
                + " /TU " + value + " /TM " + value + " /V " + value + " /Opt [" + value + " [(export) " + value + "]] >>", null),
            (8, "<< /Names [" + value + " 9 0 R] >>", null),
            (9, "<< /Type /Filespec /F " + value + " /UF " + value + " /Desc " + value + " /EF << /F 10 0 R /UF 10 0 R >> >>", null),
            (10, "<< /Type /EmbeddedFile /Length 3 >>", "abc"u8.ToArray()),
        ], 1);
        bytes[5] = (byte)'2'; bytes[7] = (byte)'0';
        return bytes;
    }

    public static IEnumerable<object[]> Encodings()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string encoding in new[] { "utf8", "utf8-invalid", "utf16-big", "utf16-little", "pdfdoc" })
        { yield return [route, encoding]; }
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task MetadataAndAttachmentLookupUseTheSameTextDecoding(string route, string encoding)
    {
        (byte[] raw, string expected) = Text(encoding);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(raw), QpdfInputOwnership.Copy);
        QpdfPath output = QpdfPath.From("report");
        string[] keys = ["pages", "outlines", "acroform", "attachments"];
        if (route == "typed")
        { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), output, new QpdfJsonOptions(2, keys), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--json=2", "--json-key=pages", "--json-key=outlines", "--json-key=acroform", "--json-key=attachments", "--", "report"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "report", json = "2", jsonKey = keys }), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.True(host.TryGetOutput(output, out byte[] report));
        using JsonDocument json = JsonDocument.Parse(report);
        JsonElement root = json.RootElement;
        Assert.Equal(expected, root.GetProperty("outlines")[0].GetProperty("title").GetString());
        Assert.Equal(expected, root.GetProperty("pages")[0].GetProperty("outlines")[0].GetProperty("title").GetString());
        JsonElement field = root.GetProperty("acroform").GetProperty("fields")[0];
        foreach (string property in new[] { "fullname", "partialname", "alternativename", "mappingname" })
        { Assert.Equal(expected, field.GetProperty(property).GetString()); }
        Assert.Equal(new[] { expected, expected }, field.GetProperty("choices").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(expected, Assert.Single(root.GetProperty("attachments").EnumerateObject()).Name);
        QpdfAttachmentInfo info = Assert.Single(await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("input"), CancellationToken.None));
        Assert.Equal(expected, info.Key); Assert.Equal(expected, info.FileName); Assert.Equal(expected, info.Description);
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("payload"), CancellationToken.None))
        { await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        if (route == "typed")
        { await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), expected, QpdfPath.From("payload"), CancellationToken.None); }
        else
        {
            QpdfExecutionResult extraction = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--show-attachment=" + expected, "--", "payload"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "payload", showAttachment = expected }), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, extraction.ExitCode); Assert.Equal(0L, extraction.Counters.OpenHandles);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] bytes)); Assert.Equal("abc"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount); host.Dispose();
        Assert.Equal(expected, info.Key); Assert.Equal(expected, info.FileName); Assert.Equal(expected, info.Description);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("shared")]
    [InlineData("caller-cancel")]
    [InlineData("governor-cancel")]
    public void Utf8DecodeRefusalAndCancellationPreserveEarlierOwnership(string outcome)
    {
        byte[] raw = [0xef, 0xbb, 0xbf, .. Encoding.ASCII.GetBytes(new string('A', 1048576))];
        RecordingMemoryBudget root = new(outcome == "shared" ? 4096L : 8388608L);
        using (IMemoryScope caller = root.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, caller))
        using (CancellationTokenSource cancellation = new())
        {
            ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = outcome == "local" ? 4096L : 8388608L }, cancellation.Token, host);
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(71L);
            long baseline = governor.Snapshot().LiveMemoryBytes;
            if (outcome == "governor-cancel") { cancellation.Cancel(); }
            CancellationToken token = outcome == "caller-cancel" ? new CancellationToken(true) : CancellationToken.None;
            string Decode() => PdfText.Decode(raw, memory, governor, token);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            if (outcome.EndsWith("cancel", StringComparison.Ordinal)) { Assert.ThrowsAny<OperationCanceledException>(Decode); }
            else if (outcome == "shared") { Assert.Throws<QpdfMemoryAdmissionException>(Decode); }
            else { Assert.Throws<QpdfLimitException>(Decode); }
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("EFBBBF")]
    [InlineData("EFBBBFF09FFFFEEDA080E282")]
    public void EmptyAndMalformedUtf8UseReplacementDecoding(string hex)
    {
        byte[] raw = Convert.FromHexString(hex);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            string expected = Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
            Assert.Equal(expected, PdfText.Decode(raw, memory, governor, CancellationToken.None));
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

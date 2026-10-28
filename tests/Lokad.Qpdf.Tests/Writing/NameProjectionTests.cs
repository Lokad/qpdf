using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class NameProjectionTests
{
    public static IEnumerable<object[]> DescriptorCases()
    {
        (string Name, string Expected)[] names = [
            ("On", "/On"), ("A#23B", "/A#B"), ("A#20B", "/A B"),
            ("Caf#C3#A9", "/Café"), ("#F0#9F#98#80", "/😀"),
            ("A#2FB", "/A/B"), ("#28#29#3C#3E#5B#5D#7B#7D#25", "/()<>[]{}%"),
            ("#22#5C#0A", "/\"\\\n"), ("A#2323B", "/A#23B"),
            ("#EF#BB#BFOn", "/\uFEFFOn"), ("#FF", "n:/#ff"),
            ("#E2#82", "n:/#e2#82"), ("", "/")];
        foreach ((string name, string expected) in names)
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (int version in new[] { 1, 2 }) { yield return [name, expected, route, version]; }
    }

    internal static byte[] Form(string name) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Annots [4 0 R] >>", null),
        (4, "<< /Type /Annot /Subtype /Widget /P 3 0 R /Rect [0 0 10 10] /T (field) /FT 5 0 R /AS 5 0 R /V 5 0 R >>", null),
        (5, name, null)], 1);

    private static async Task<byte[]> Export(InMemoryQpdfHost host, string route, int version, string key)
    {
        if (route == "typed")
        {
            await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("json"),
                new QpdfJsonOptions(version, [key]), CancellationToken.None);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--json=" + version, "--json-key=" + key, "--", "json"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
                { inputFile = "input", outputFile = "json", json = version.ToString(System.Globalization.CultureInfo.InvariantCulture), jsonKey = new[] { key } }), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("json"), out byte[] bytes));
        Assert.Equal(0L, host.OpenLeaseCount);
        return bytes;
    }

    [Theory]
    [MemberData(nameof(DescriptorCases))]
    public async Task FormDescriptorsPreserveSemanticNames(string name, string expected, string route, int version)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Form("/" + name), QpdfInputOwnership.Transfer);
        byte[] bytes = await Export(host, route, version, "acroform"); host.Dispose();
        using JsonDocument json = JsonDocument.Parse(bytes);
        JsonElement field = Assert.Single(json.RootElement.GetProperty("acroform").GetProperty("fields").EnumerateArray());
        Assert.Equal(expected, field.GetProperty("fieldtype").GetString());
        Assert.Equal(expected, field.GetProperty("annotation").GetProperty("appearancestate").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("<< >>")]
    public async Task NonNameFormDescriptorsRemainEmptyStrings(string value)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Form(value), QpdfInputOwnership.Transfer);
        foreach (int version in new[] { 1, 2 })
        {
            using JsonDocument json = JsonDocument.Parse(await Export(host, "typed", version, "acroform"));
            JsonElement field = json.RootElement.GetProperty("acroform").GetProperty("fields")[0];
            Assert.Equal("", field.GetProperty("fieldtype").GetString());
            Assert.Equal("", field.GetProperty("annotation").GetProperty("appearancestate").GetString());
        }
    }

    public static IEnumerable<object[]> MimeCreationCases()
    {
        foreach (string mime in new[] { "text/plain", "application/x#hash", "application/x##hash", "application/x#23hash" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [mime, route]; }
    }

    [Theory]
    [MemberData(nameof(MimeCreationCases))]
    public async Task MimeMetadataRoundTripsWithoutAddingEscapes(string mime, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("source"), Form("/Btn"), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("payload"), "X"u8.ToArray(), QpdfInputOwnership.Transfer);
        if (route == "typed")
        {
            await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("source"), QpdfPath.From("output"),
                [new("payload", "K", "note", null, mime, null, null, false)], CancellationToken.None);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["source", "--add-attachment", "payload", "--key=K", "--filename=note", "--mimetype=" + mime, "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "source", outputFile = "output",
                    addAttachment = new[] { new { file = "payload", key = "K", filename = "note", mimetype = mime } } }), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("input"), output, QpdfInputOwnership.Transfer);
        QpdfAttachmentInfo info = Assert.Single(await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("input"), CancellationToken.None));
        Assert.Equal(mime, info.MimeType);
        foreach (int version in new[] { 1, 2 })
        {
            using JsonDocument json = JsonDocument.Parse(await Export(host, route, version, "attachments"));
            AssertMime(json, mime);
        }
        await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("source"), QpdfPath.From("roundtrip"),
            [new("payload", "K", "note", null, info.MimeType, null, null, false)], CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("roundtrip"), out byte[] roundtrip));
        host.AddInput(QpdfPath.From("again"), roundtrip, QpdfInputOwnership.Transfer);
        QpdfAttachmentInfo reread = Assert.Single(await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("again"), CancellationToken.None));
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None))
        {
            PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(reread.StreamObjectNumber));
            Assert.True(stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject raw));
            Assert.Equal(Encoding.ASCII.GetBytes(mime), Assert.IsType<PdfName>(raw).RawBytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount); host.Dispose();
        Assert.Equal(mime, info.MimeType); Assert.Equal(mime, reread.MimeType);
    }

    private static void AssertMime(JsonDocument json, string expected)
    {
        JsonElement attachment = json.RootElement.GetProperty("attachments").GetProperty("K");
        foreach (JsonProperty stream in attachment.GetProperty("streams").EnumerateObject())
        { Assert.Equal(expected.Length == 0 ? null : expected, stream.Value.GetProperty("mimetype").GetString()); }
    }

    internal static byte[] Attachment(string subtype) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(K) 4 0 R] >> >> >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>", null),
        (4, "<< /Type /Filespec /UF (note) /EF << /UF 5 0 R >> >>", null),
        (5, "<< /Type /EmbeddedFile /Length 1 /Subtype " + subtype + " >>", "X"u8.ToArray())], 1);

    [Theory]
    [InlineData("/application#2Fx#23hash", "application/x#hash")]
    [InlineData("/application#2Fx#2323hash", "application/x#23hash")]
    [InlineData("/Caf#C3#A9#F0#9F#98#80", "Café😀")]
    [InlineData("/#EF#BB#BFOn", "\uFEFFOn")]
    [InlineData("/#FF#E2#82", "��")]
    [InlineData("/#22#5C#0A", "\"\\\n")]
    [InlineData("/", "")]
    [InlineData("42", "")]
    public async Task MimeNamesDecodeAsUtf8WithoutTextStringBomRules(string subtype, string expected)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Attachment(subtype), QpdfInputOwnership.Transfer);
        QpdfAttachmentInfo info = Assert.Single(await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("input"), CancellationToken.None));
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (int version in new[] { 1, 2 })
        {
            using JsonDocument json = JsonDocument.Parse(await Export(host, route, version, "attachments"));
            AssertMime(json, expected);
        }
        host.Dispose(); Assert.Equal(expected, info.MimeType);
    }
}

using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentJsonTests
{
    [Theory]
    [InlineData("/CreationDate (D:20240102030405Z) /ModDate (D:20250607080910Z)", "2024-01-02T03:04:05Z", "2025-06-07T08:09:10Z")]
    [InlineData("/CreationDate (D:20240102030405Z)", "2024-01-02T03:04:05Z", null)]
    [InlineData("/ModDate (D:20250607080910Z)", null, "2025-06-07T08:09:10Z")]
    [InlineData("/ModDate (D:20250607080910+05'30')", null, "2025-06-07T08:09:10+05:30")]
    [InlineData("", null, null)]
    public async Task DatesAreIndependentAcrossPublicRoutes(string parameters, string? creation, string? modification)
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        {
            using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), AttachmentMetadataTests.Fixture("note", parameters), QpdfInputOwnership.Transfer);
            if (route == "typed") { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["attachments"]), CancellationToken.None); }
            else
            {
                QpdfExecutionResult result = route == "job"
                    ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","json":"2","jsonKey":["attachments"]}""", CancellationToken.None)
                    : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--json=2", "--json-key=attachments", "--", "output"]), CancellationToken.None);
                Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            }
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal(0L, host.OpenLeaseCount);
            using JsonDocument json = JsonDocument.Parse(output); JsonElement stream = json.RootElement.GetProperty("attachments").GetProperty("K").GetProperty("streams").GetProperty("UF");
            Assert.Equal(creation, stream.GetProperty("creationdate").GetString()); Assert.Equal(modification, stream.GetProperty("modificationdate").GetString());
        }
    }

    [Fact]
    public async Task NamedStreamsKeepTheirOwnMetadataAndPreferredIdentity()
    {
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>", null),
            (4, "<< /Names [(K) 5 0 R] >>", null),
            (5, "<< /F (legacy) /UF (preferred) /EF << /F 6 0 R /UF 7 0 R >> >>", null),
            (6, "<< /Length 1 /Subtype /text#2Fplain /Params 8 0 R >>", "F"u8.ToArray()),
            (7, "<< /Length 1 /Subtype /application#2Fpdf /Params << /ModDate (D:20250607080910Z) /CheckSum <FE> >> >>", "U"u8.ToArray()),
            (8, "<< /CreationDate (D:20240102030405Z) /CheckSum <AB> >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["attachments"]), CancellationToken.None);
        host.Dispose(); using JsonDocument json = JsonDocument.Parse(result.Json);
        JsonElement attachment = json.RootElement.GetProperty("attachments").GetProperty("K");
        Assert.Equal("preferred", attachment.GetProperty("preferredname").GetString()); Assert.Equal("7 0 R", attachment.GetProperty("preferredcontents").GetString());
        JsonElement legacy = attachment.GetProperty("streams").GetProperty("F"), preferred = attachment.GetProperty("streams").GetProperty("UF");
        Assert.Equal("2024-01-02T03:04:05Z", legacy.GetProperty("creationdate").GetString()); Assert.Null(legacy.GetProperty("modificationdate").GetString());
        Assert.Equal("ab", legacy.GetProperty("checksum").GetString()); Assert.Equal("text/plain", legacy.GetProperty("mimetype").GetString());
        Assert.Null(preferred.GetProperty("creationdate").GetString()); Assert.Equal("2025-06-07T08:09:10Z", preferred.GetProperty("modificationdate").GetString());
        Assert.Equal("fe", preferred.GetProperty("checksum").GetString()); Assert.Equal("application/pdf", preferred.GetProperty("mimetype").GetString());
    }

    [Theory]
    [InlineData("D:20240102030405", "2024-01-02T03:04:05Z")]
    [InlineData("D:20240102030405Z", "2024-01-02T03:04:05Z")]
    [InlineData("D:20240102030405+00'00'", "2024-01-02T03:04:05Z")]
    [InlineData("D:20240102030405-02'30'", "2024-01-02T03:04:05-02:30")]
    [InlineData("D:20240102030405+24'00'", null)]
    [InlineData("D:20240102030405+05'60'", null)]
    [InlineData("D:20240102030405+05'3x'", null)]
    [InlineData("D:20240102030405+05'30", null)]
    [InlineData("D:2024", null)]
    public void DateFormattingHasBoundedSyntax(string value, string? expected) => Assert.Equal(expected, DocumentJson.ToIso8601(value));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DamagedOptionalParametersPreservePreferredAndAlternateStreamPolicies(bool preferred)
    {
        byte[] input = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>", null),
            (4, "<< /Names [(K) 5 0 R] >>", null),
            (5, "<< /UF (preferred) /EF << /UF 6 0 R /F 7 0 R >> >>", null),
            (6, "<< /Length 1" + (preferred ? " /Params 99 0 R" : "") + " >>", "U"u8.ToArray()),
            (7, "<< /Length 1 /Params 99 0 R >>", "F"u8.ToArray())], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["attachments"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(result.Json); JsonElement attachments = json.RootElement.GetProperty("attachments");
        if (preferred)
        { Assert.Empty(attachments.EnumerateObject()); Assert.Contains(result.Warnings, d => d.Message.Contains("Skipped unlistable attachment", StringComparison.Ordinal)); }
        else
        { Assert.Equal(JsonValueKind.Null, attachments.GetProperty("K").GetProperty("streams").GetProperty("F").GetProperty("modificationdate").ValueKind); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public void InvalidLongDatesDoNotProduceLargeSubstringCopies()
    {
        string value = "D:20240102030405" + new string('X', 1048576);
        long before = GC.GetAllocatedBytesForCurrentThread(); Assert.Null(DocumentJson.ToIso8601(value));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 65536L);
    }
}

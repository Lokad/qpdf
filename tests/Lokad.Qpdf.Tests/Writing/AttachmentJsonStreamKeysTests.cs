using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentJsonStreamKeysTests
{
    internal static byte[] Fixture(string shape)
    {
        string entries = "/UF 5 0 R /F 6 0 R /Custom 6 0 R /Extra#2FPart 7 0 R";
        if (shape == "shared") { entries += " /Alias 5 0 R"; }
        if (shape == "nullable") { entries += " /Null null /LiveNull 9 7 R /Stale 6 1 R /Missing 99 0 R /Number 17 /Text (ignored) /Array []"; }
        if (shape == "damaged") { entries += " /Damaged 12 0 R"; }
        if (shape == "long") { entries += " /" + new string('A', 65536) + " 5 0 R"; }
        string ef = "<< " + entries + " >>";
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(K) 4 0 R] >> >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null),
            (4, "<< /Type /Filespec /UF (preferred.txt) /F (fallback.txt) /Custom (private filename) /EF " + (shape == "direct" ? ef : "8 0 R") + " >>", null),
            (5, "<< /Type /EmbeddedFile /Length 1 /Subtype /application#2Fplain /Params << /Size 1 /CheckSum <05> >> >>", [5]),
            (6, shape == "metadata" ? "<< /Type /EmbeddedFile /Length 1 /Subtype 11 0 R /Params 10 0 R >>"
                : "<< /Type /EmbeddedFile /Length 1 /Subtype /application#2Fcustom /Params << /Size 1 /CheckSum <06> >> >>", [6]),
            (7, "<< /Type /EmbeddedFile /Length 1 /Subtype /application#2Fextra /Params << /Size 1 /CheckSum <07> >> >>", [7]),
            (8, ef, null), (9, "null", null),
            (10, "<< /Size 1 /CheckSum <06> /CreationDate (D:20240102030405Z) /ModDate (D:20250607080910Z) >>", null),
            (11, "/application#2Fcustom", null), (12, shape == "damaged" ? "(unterminated" : "null", null)];
        return PdfTestDocument.Assemble(objects, 1, new Dictionary<int, int> { [9] = 7 });
    }

    private static void AssertAttachment(JsonElement attachment, string shape)
    {
        Assert.Equal("5 0 R", attachment.GetProperty("preferredcontents").GetString());
        Assert.Equal("preferred.txt", attachment.GetProperty("preferredname").GetString());
        Assert.Equal(new[] { "/F", "/UF" }, attachment.GetProperty("names").EnumerateObject().Select(p => p.Name).Order());
        JsonElement streams = attachment.GetProperty("streams");
        string[] expected = shape == "shared" ? ["/Alias", "/Custom", "/Extra/Part", "/F", "/UF"] : ["/Custom", "/Extra/Part", "/F", "/UF"];
        Assert.Equal(expected, streams.EnumerateObject().Select(p => p.Name));
        foreach (string key in expected)
        {
            int number = key is "/UF" or "/Alias" ? 5 : key is "/F" or "/Custom" ? 6 : 7;
            string subtype = number == 5 ? "plain" : number == 6 ? "custom" : "extra";
            JsonElement stream = streams.GetProperty(key);
            Assert.Equal("application/" + subtype, stream.GetProperty("mimetype").GetString());
            Assert.Equal("0" + number, stream.GetProperty("checksum").GetString());
            Assert.Equal(shape == "metadata" && number == 6 ? "2024-01-02T03:04:05Z" : null, stream.GetProperty("creationdate").GetString());
            Assert.Equal(shape == "metadata" && number == 6 ? "2025-06-07T08:09:10Z" : null, stream.GetProperty("modificationdate").GetString());
        }
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("indirect")]
    [InlineData("shared")]
    [InlineData("nullable")]
    [InlineData("metadata")]
    [InlineData("damaged")]
    public async Task EveryValidEfStreamIsReportedWithoutChangingFilenamePreference(string shape)
    {
        byte[] input = Fixture(shape), original = input.ToArray();
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), new QpdfJsonOptions(2, ["attachments"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(result.Json); AssertAttachment(json.RootElement.GetProperty("attachments").GetProperty("K"), shape);
        await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), "K", QpdfPath.From("payload"), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] payload)); Assert.Equal(new byte[] { 5 }, payload);
        Assert.Empty(result.Warnings); Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(original, input);
    }

    [Theory]
    [InlineData(1, "argv", "shared")]
    [InlineData(2, "job", "indirect")]
    public async Task BothVersionsAndCommandAdaptersUseTheSameStreamMap(int version, string route, string shape)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(shape), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new(["input", "--json=" + version, "--json-key=attachments"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","json":"2","jsonKey":["attachments"]}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(result.OutputText)); AssertAttachment(json.RootElement.GetProperty("attachments").GetProperty("K"), shape);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        internal int Created;
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created++; return storage.CreateOutputAsync(path, token); }
    }

    private static async Task Seed(InMemoryQpdfHost storage, string shape)
    {
        storage.AddInput(QpdfPath.From("input"), Fixture(shape), QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("report"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AdditionalLongNamesObeyOutputLimitsBeforeOpeningTheReport()
    {
        using InMemoryQpdfHost storage = new(); await Seed(storage, "long");
        using RecordingMemoryBudget budget = new(16L * 1024 * 1024); using IMemoryScope owner = budget.CreateScope(); Host host = new(storage, owner);
        QpdfLimitException error = await Assert.ThrowsAsync<QpdfLimitException>(() => Qpdf.GetDocumentJsonAsync(host,
            QpdfPath.From("input"), QpdfPath.From("report"), new QpdfJsonOptions(2, ["attachments"]),
            QpdfLimits.Default with { MaxOutputBytes = 4096 }, CancellationToken.None).AsTask());
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), error.LimitName); Assert.Equal(0, host.Created);
        Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, budget.ReservedBytes);
        Assert.True(storage.TryGetOutput(QpdfPath.From("report"), out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
    }

    [Fact]
    public async Task ExtraStreamInspectionRefundsEveryRefusedSharedGrowth()
    {
        using InMemoryQpdfHost storage = new(); await Seed(storage, "metadata");
        using RecordingMemoryBudget baseline = new(16L * 1024 * 1024);
        using (IMemoryScope owner = baseline.CreateScope())
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(new Host(storage, owner), QpdfPath.From("input"), QpdfPath.From("report"), new QpdfJsonOptions(2, ["attachments"]), CancellationToken.None);
            using JsonDocument json = JsonDocument.Parse(result.Json); AssertAttachment(json.RootElement.GetProperty("attachments").GetProperty("K"), "metadata");
        }
        int growths = baseline.GrowthCalls; Assert.True(growths > 0);
        for (int fail = 1; fail <= growths; fail++)
        {
            await Seed(storage, "metadata");
            using RecordingMemoryBudget budget = new(16L * 1024 * 1024) { RefuseOnGrowth = fail };
            using IMemoryScope owner = budget.CreateScope(); Host host = new(storage, owner);
            await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Qpdf.GetDocumentJsonAsync(host,
                QpdfPath.From("input"), QpdfPath.From("report"), new QpdfJsonOptions(2, ["attachments"]), CancellationToken.None).AsTask());
            Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.True(storage.TryGetOutput(QpdfPath.From("report"), out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        }
    }
}

using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentIndirectMetadataTests
{
    internal static byte[] Fixture(bool indirect, string? scalarOverride)
    {
        string[] values = ["(preferred.txt)", "(description)", "/text#2Fplain", "(D:20240102030405Z)", "(D:20250607080910Z)", "<0102>", "1"];
        string Value(int index) => indirect ? $"{index + 6} 0 R" : scalarOverride ?? values[index];
        string metadata = $"/Subtype {Value(2)} /Params << /CreationDate {Value(3)} /ModDate {Value(4)} /CheckSum {Value(5)} /Size {Value(6)} >>";
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(K) 4 0 R] >> >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null),
            (4, $"<< /Type /Filespec /UF {Value(0)} /Desc {Value(1)} /EF << /UF 5 0 R /F 13 0 R >> >>", null),
            (5, $"<< /Type /EmbeddedFile /Length 1 {metadata} >>", "U"u8.ToArray())];
        for (int index = 0; index < values.Length; index++) { objects.Add((index + 6, scalarOverride ?? values[index], null)); }
        objects.Add((13, $"<< /Type /EmbeddedFile /Length 1 {metadata} >>", "F"u8.ToArray()));
        return CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task MetadataAndAlternateStreamsAgreeAcrossPublicRoutes(bool indirect, int version)
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        {
            byte[] output; IReadOnlyList<QpdfAttachmentInfo> listing;
            using (InMemoryQpdfHost host = new())
            {
                host.AddInput(QpdfPath.From("input"), Fixture(indirect, null), QpdfInputOwnership.Transfer);
                if (route == "typed") { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(version, ["attachments"]), CancellationToken.None); }
                else
                {
                    QpdfExecutionResult result = route == "argv"
                        ? await Qpdf.ExecuteAsync(host, new(["input", $"--json={version}", "--json-key=attachments", "--", "output"]), CancellationToken.None)
                        : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", json = version.ToString(), jsonKey = new[] { "attachments" } }), CancellationToken.None);
                    Assert.Equal(QpdfExitCode.Success, result.ExitCode);
                }
                listing = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("input"), CancellationToken.None);
                await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), "K", QpdfPath.From("payload"), CancellationToken.None);
                Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] payload)); Assert.Equal("U"u8.ToArray(), payload);
                Assert.True(host.TryGetOutput(QpdfPath.From("output"), out output)); Assert.Equal(0L, host.OpenLeaseCount);
            }
            QpdfAttachmentInfo info = Assert.Single(listing);
            Assert.Equal("preferred.txt", info.FileName); Assert.Equal("description", info.Description); Assert.Equal("text/plain", info.MimeType);
            Assert.Equal("D:20240102030405Z", info.CreationDate); Assert.Equal("D:20250607080910Z", info.ModDate);
            Assert.Equal("0102", info.Checksum); Assert.Equal(1L, info.Size);
            using JsonDocument json = JsonDocument.Parse(output);
            JsonElement attachment = json.RootElement.GetProperty("attachments").GetProperty("K");
            Assert.Equal("preferred.txt", attachment.GetProperty("preferredname").GetString()); Assert.Equal("description", attachment.GetProperty("description").GetString());
            Assert.Equal("preferred.txt", attachment.GetProperty("names").GetProperty("/UF").GetString());
            JsonProperty[] streams = attachment.GetProperty("streams").EnumerateObject().ToArray(); Assert.Equal(2, streams.Length);
            Assert.Equal(new[] { "/F", "/UF" }, streams.Select(stream => stream.Name).Order());
            foreach (JsonProperty stream in streams)
            {
                Assert.Equal("text/plain", stream.Value.GetProperty("mimetype").GetString()); Assert.Equal("0102", stream.Value.GetProperty("checksum").GetString());
                Assert.Equal("2024-01-02T03:04:05Z", stream.Value.GetProperty("creationdate").GetString());
                Assert.Equal("2025-06-07T08:09:10Z", stream.Value.GetProperty("modificationdate").GetString());
            }
        }
    }

    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("99 0 R")]
    [InlineData("6 0 R")]
    public async Task UnusableOptionalScalarsRemainOmitted(string value)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(true, value), QpdfInputOwnership.Transfer);
        QpdfAttachmentInfo info = Assert.Single(await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("input"), CancellationToken.None));
        Assert.Empty(info.FileName); Assert.Empty(info.Description); Assert.Empty(info.MimeType); Assert.Empty(info.CreationDate);
        Assert.Empty(info.ModDate); Assert.Empty(info.Checksum); Assert.Equal(-1L, info.Size); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private sealed class Host(IQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }

    [Fact]
    public async Task IndirectListingLifetimesAndAdmissionRefusalKeepTheirOwners()
    {
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), Fixture(true, null), QpdfInputOwnership.Transfer);
        RecordingMemoryBudget baseline = new(4194304L); IReadOnlyList<QpdfAttachmentInfo> listing;
        using (IMemoryScope caller = baseline.CreateScope())
        {
            listing = await Qpdf.ListAttachmentsAsync(new Host(storage, caller), QpdfPath.From("input"), CancellationToken.None);
            Assert.InRange(baseline.ReservedBytes, 1L, 4096L);
        }
        int growths = baseline.GrowthCalls;
        Assert.Equal(0L, baseline.ReservedBytes);
        for (int fail = 1; fail <= growths; fail++)
        {
            RecordingMemoryBudget root = new(4194304L) { RefuseOnGrowth = fail }; using IMemoryScope caller = root.CreateScope();
            await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Qpdf.ListAttachmentsAsync(new Host(storage, caller), QpdfPath.From("input"), CancellationToken.None).AsTask());
            Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0L, storage.OpenLeaseCount);
        }
        storage.Dispose(); Assert.Equal("preferred.txt", Assert.Single(listing).FileName); Assert.Equal("0102", listing[0].Checksum);
    }
}

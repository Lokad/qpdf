using System.IO.Compression;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class ExternalStreamDataTests
{
    private static readonly byte[] Payload = "q Q\n"u8.ToArray();

    public static IEnumerable<object[]> Representations()
    {
        foreach (string role in new[] { "content", "attachment", "metadata" })
        foreach (string descriptor in new[] { "absent", "null", "ref-null", "stale-null", "file", "filespec", "ref-file" })
        foreach (bool encoded in new[] { false, true }) { yield return [role, descriptor, encoded]; }
    }

    private static bool IsExternal(string descriptor) => descriptor is "file" or "filespec" or "ref-file";

    internal static (byte[] Input, byte[] Physical) Fixture(string role, string descriptor, bool encoded)
    {
        string file = descriptor switch
        {
            "absent" => "", "null" => "/F null", "ref-null" => "/F 8 0 R", "stale-null" => "/F 7 1 R",
            "file" => "/F (external.bin)", "filespec" => "/F << /Type /Filespec /F (external.bin) >>", _ => "/F 7 0 R",
        };
        byte[] physical = Payload;
        if (encoded)
        {
            using MemoryStream buffer = new();
            using (ZLibStream zlib = new(buffer, CompressionLevel.Fastest, true)) { zlib.Write(Payload); }
            physical = buffer.ToArray();
        }
        string extra = role == "metadata" ? "/Metadata 5 0 R" : role == "attachment"
            ? "/Names << /EmbeddedFiles << /Names [(K) 6 0 R] >> >>" : "";
        string contents = role == "content-array" ? "[5 0 R 4 0 R]" : role == "content" ? "5 0 R" : "4 0 R";
        List<(int, string, byte[]?)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /Original 5 0 R " + extra + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents " + contents + " >>", null),
            (4, "<< /Length 4 >>", Payload),
            (5, "<< /Length " + physical.Length + " " + file + " /FFilter /ASCIIHexDecode /FDecodeParms null"
                + (encoded ? " /Filter /FlateDecode" : "") + " >>", physical),
            (6, "<< /Type /Filespec /F (K) /EF << /F 5 0 R >> >>", null),
            (7, "(external.bin)", null), (8, "null", null),
        ];
        return (CheckTests.Assemble(objects, 1), physical);
    }

    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction output = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await output.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await output.CommitAsync(CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(Representations))]
    public async Task RawInspectionKeepsPhysicalBytesButFilteredInspectionRequiresEmbeddedData(string role, string descriptor, bool encoded)
    {
        (byte[] input, byte[] physical) = Fixture(role, descriptor, encoded);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        foreach (bool job in new[] { false, true })
        foreach (bool filtered in new[] { false, true })
        {
            await Seed(host);
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"showObject\":\"5\",\""
                    + (filtered ? "filteredStreamData" : "rawStreamData") + "\":true}", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--show-object=5", filtered ? "--filtered-stream-data" : "--raw-stream-data", "--", "output"]), CancellationToken.None);
            bool unavailable = filtered && IsExternal(descriptor);
            Assert.Equal(unavailable ? QpdfExitCode.Error : QpdfExitCode.Success, result.ExitCode);
            if (unavailable) { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature && d.Severity == QpdfDiagnosticSeverity.Error); }
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] actual));
            Assert.Equal(unavailable ? "prior"u8.ToArray() : filtered ? Payload : physical, actual);
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    [Theory]
    [MemberData(nameof(Representations))]
    public async Task CheckingReportsExternalCoverageUnavailable(string role, string descriptor, bool encoded)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(role, descriptor, encoded).Input, QpdfInputOwnership.Transfer);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.True(result.Passed);
        Assert.Equal(IsExternal(descriptor) ? QpdfCheckStatus.Skipped : QpdfCheckStatus.Passed,
            Assert.Single(result.Checks, c => c.Name == "streams").Status);
        if (IsExternal(descriptor)) { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckSkipped); }
        if (role == "content")
        {
            Assert.Equal(IsExternal(descriptor) ? QpdfCheckStatus.Skipped : QpdfCheckStatus.Passed,
                Assert.Single(result.Checks, c => c.Name == "page-content").Status);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [MemberData(nameof(Representations))]
    public async Task OpaqueRewritingKeepsExternalDescriptorsAndPhysicalStorage(string role, string descriptor, bool encoded)
    {
        (byte[] input, byte[] physical) = Fixture(role, descriptor, encoded);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        foreach (bool job in new[] { false, true })
        {
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output"}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
            {
                PdfReference root = Assert.IsType<PdfReference>(Value(document.Engine.Trailer, "Root"));
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(root.ObjectNumber));
                PdfReference original = Assert.IsType<PdfReference>(Value(catalog, "Original"));
                PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(original.ObjectNumber));
                QpdfObject file = Value(stream.Dictionary, "F");
                if (file is PdfReference reference) { Assert.True(((IIndirectResolver)document.Engine).TryResolve(reference, out QpdfObject? resolved)); file = resolved ?? PdfNull.Instance; }
                Assert.Equal(IsExternal(descriptor), file is not PdfNull);
                if (IsExternal(descriptor)) { Assert.Equal(physical, stream.RawMemory.ToArray()); }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    [Theory]
    [InlineData("absent", false)]
    [InlineData("ref-null", true)]
    [InlineData("stale-null", false)]
    [InlineData("file", false)]
    [InlineData("filespec", true)]
    [InlineData("ref-file", false)]
    public async Task AttachmentExtractionCannotFallbackToExternalPlaceholder(string descriptor, bool encoded)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("attachment", descriptor, encoded).Input, QpdfInputOwnership.Transfer);
        await Seed(host);
        if (IsExternal(descriptor))
        {
            await Assert.ThrowsAnyAsync<QpdfException>(async () => await Qpdf.ExtractAttachmentAsync(host,
                QpdfPath.From("input"), "K", QpdfPath.From("output"), CancellationToken.None));
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
        }
        else
        {
            await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), "K", QpdfPath.From("output"), CancellationToken.None);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] actual)); Assert.Equal(Payload, actual);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static QpdfObject Value(PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? value : PdfNull.Instance;
}

using System.IO.Compression;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class LogicalContentPartitionTests
{
    private static readonly byte[] Image = [255, 0, 0];

    public static IEnumerable<object[]> Partitions()
    {
        foreach (string shape in new[] { "single", "before-BI", "after-BI", "dictionary", "before-ID", "after-EI", "empty-prefix", "empty-middle", "empty-tail" })
        foreach (bool encoded in new[] { false, true })
        foreach (bool indirect in new[] { false, true }) { yield return [shape, encoded, indirect]; }
    }

    internal static (byte[] Bytes, byte[][] Parts) Fixture(string shape, bool encoded, bool indirect, bool aliases)
    {
        byte[] program = [.. "q BI /W 1 /H 1 /BPC 8 /CS /RGB ID "u8, .. Image, .. " EI Q"u8];
        byte[][] parts = shape switch
        {
            "before-BI" => ["q"u8.ToArray(), program[2..]],
            "after-BI" => ["q BI"u8.ToArray(), program[5..]],
            "dictionary" => ["q BI /W 1 /H 1"u8.ToArray(), "/BPC 8 /CS /RGB"u8.ToArray(), [.. "ID "u8, .. Image, .. " EI Q"u8]],
            "before-ID" => ["q BI /W 1 /H 1 /BPC 8 /CS /RGB"u8.ToArray(), [.. "ID "u8, .. Image, .. " EI Q"u8]],
            "after-EI" => [program[..^2], "Q"u8.ToArray()],
            "empty-prefix" => [[], program],
            "empty-middle" => ["q BI /W 1 /H 1"u8.ToArray(), [], program[14..]],
            "empty-tail" => [program, []],
            _ => [program],
        };
        int arrayNumber = 4 + parts.Length, metadataNumber = arrayNumber + 1, fileNumber = arrayNumber + 2;
        string array = "[" + string.Join(' ', Enumerable.Range(4, parts.Length).Select(x => x + " 0 R")) + "]";
        string roots = aliases ? $"/AuditArray {arrayNumber} 0 R /AuditStream 4 0 R /Metadata {metadataNumber} 0 R /Names << /EmbeddedFiles << /Names [(copy) {fileNumber} 0 R] >> >>" : "";
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R " + roots + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents " + (indirect ? arrayNumber + " 0 R" : array) + " >>", null),
        ];
        for (int index = 0; index < parts.Length; index++)
        {
            byte[] data = parts[index];
            if (encoded)
            {
                using MemoryStream buffer = new();
                using (ZLibStream zlib = new(buffer, CompressionLevel.SmallestSize, true)) { zlib.Write(data); }
                data = buffer.ToArray();
                if (data.Length == 0) { data = [0x78, 0x9c, 3, 0, 0, 0, 0, 1]; }
            }
            objects.Add((4 + index, "<< /Length " + data.Length + (encoded ? " /Filter /FlateDecode" : "") + " >>", data));
        }
        objects.Add((arrayNumber, array, null));
        if (aliases)
        {
            objects.Add((metadataNumber, $"<< /Type /Metadata /Subtype /XML /KeepStream 4 0 R /KeepArray {arrayNumber} 0 R /Length 7 >>", "<root/>"u8.ToArray()));
            objects.Add((fileNumber, "<< /Type /Filespec /F (content.bin) /EF << /F 4 0 R >> >>", null));
        }
        return (CheckTests.Assemble(objects, 1), parts);
    }

    [Theory]
    [MemberData(nameof(Partitions))]
    public async Task CheckingTreatsEquivalentPartitionsAsOnePageProgram(string shape, bool encoded, bool indirect)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(shape, encoded, indirect, false).Bytes, QpdfInputOwnership.Transfer);
        QpdfCheckResult typed = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(typed.Checks, x => x.Name == "page-content").Status);
        Assert.True(typed.Passed); Assert.False(typed.HasSkipped);
        foreach (bool job in new[] { false, true })
        {
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"check\":\"\"}", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--check"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Null(result.OutputBytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [MemberData(nameof(Partitions))]
    public async Task NormalizationPreservesPhysicalPartsAndInlineSamples(string shape, bool encoded, bool indirect)
    {
        (byte[] input, byte[][] parts) = Fixture(shape, encoded, indirect, false);
        foreach (bool qdf in new[] { false, true })
        foreach (bool job in new[] { false, true })
        {
            using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
            List<string> arguments = ["input", "--normalize-content=y", "--stream-data=uncompress", "--compress-streams=n"];
            Dictionary<string, object> values = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["normalizeContent"] = "y", ["streamData"] = "uncompress", ["compressStreams"] = "n" };
            if (qdf) { arguments.Add("--qdf"); values["qdf"] = true; }
            arguments.Add("output");
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(values), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("copy"), output, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), CancellationToken.None))
            {
                QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
                Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject value));
                QpdfObject contents = Resolve(value); QpdfObject[] entries = contents is PdfArray array ? array.Items : [value];
                Assert.Equal(parts.Length, entries.Length);
                byte[] logical = entries.SelectMany(x => Assert.IsType<PdfStream>(Resolve(x)).Data.Append((byte)'\n')).ToArray();
                int start = logical.AsSpan().IndexOf("ID"u8); Assert.True(start >= 0);
                Assert.Equal((byte)' ', logical[start + 2]); Assert.Equal(Image, logical.AsSpan(start + 3, Image.Length).ToArray());
            }
            QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("copy"), CancellationToken.None);
            Assert.True(check.Passed); Assert.False(check.HasSkipped); Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LogicalNormalizationLeavesIndependentArrayAndStreamRootsIntact(bool encoded, bool coalesce)
    {
        (byte[] input, byte[][] parts) = Fixture("dictionary", encoded, true, true);
        foreach (bool job in new[] { false, true })
        {
            using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
            List<string> arguments = ["input", "--normalize-content=y", "--stream-data=uncompress", "--compress-streams=n"];
            Dictionary<string, object> values = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["normalizeContent"] = "y", ["streamData"] = "uncompress", ["compressStreams"] = "n" };
            if (coalesce) { arguments.Add("--coalesce-contents"); values["coalesceContents"] = true; }
            arguments.Add("output");
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(values), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("copy"), output, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), CancellationToken.None))
            {
                QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
                QpdfObject Get(PdfDictionary dictionary, string key) { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(Get(document.Engine.Trailer, "Root")));
                QpdfObject stream = Get(catalog, "AuditStream"); Assert.Equal(parts[0], Assert.IsType<PdfStream>(Resolve(stream)).Data);
                PdfArray old = Assert.IsType<PdfArray>(Resolve(Get(catalog, "AuditArray"))); Assert.Equal(parts.Length, old.Count); Assert.Equal(stream, old[0]);
                for (int index = 0; index < old.Count; index++) { Assert.Equal(parts[index], Assert.IsType<PdfStream>(Resolve(old[index])).Data); }
                PdfDictionary metadata = Assert.IsType<PdfStream>(Resolve(Get(catalog, "Metadata"))).Dictionary;
                Assert.Equal(stream, Get(metadata, "KeepStream")); Assert.Equal(Get(catalog, "AuditArray"), Get(metadata, "KeepArray"));
                QpdfObject contents = Resolve(Get(document.PageReferences[0].Page, "Contents"));
                Assert.Equal(coalesce ? 1 : parts.Length, contents is PdfArray array ? array.Count : 1);
            }
            await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("copy"), "copy", QpdfPath.From("data"), CancellationToken.None);
            Assert.True(host.TryGetOutput(QpdfPath.From("data"), out byte[] data)); Assert.Equal(parts[0], data); Assert.Equal(0L, host.OpenLeaseCount);
        }
    }
}

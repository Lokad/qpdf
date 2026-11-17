using System.IO.Compression;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

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
        int last = parts.Length - 1;
        while (parts[last].Length == 0) { last--; }
        parts[last] = [.. parts[last], .. " /Span << /ActualText (a\\015\\012b) >> BDC EMC"u8];
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
        return (PdfTestDocument.Assemble(objects, 1), parts);
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
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, "after-BI", true, false)]
    [InlineData(true, "before-ID", true, true)]
    public async Task CommandCheckingUsesTheLogicalPartitionReader(bool job, string shape, bool encoded, bool indirect)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(shape, encoded, indirect, false).Bytes, QpdfInputOwnership.Transfer);
        ValueTask<QpdfExecutionResult> Inspect(QpdfLimits limits) => job
            ? Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"check\":\"\"}", new(limits), CancellationToken.None)
            : Qpdf.ExecuteAsync(host, new(["input", "--check"]), new(limits), CancellationToken.None);
        QpdfExecutionResult result = await Inspect(QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Null(result.OutputBytes);
        Assert.True(result.Counters.TotalDecodedBytes > 1L);
        QpdfExecutionResult limited = await Inspect(QpdfLimits.Default with { MaxDecodedBytes = 1L });
        Assert.Equal(QpdfExecutionStatus.Failed, limited.Status);
        Assert.Contains(limited.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(limited.Counters.HostOperations > 0L); Assert.Null(limited.OutputBytes);
        Assert.Equal(0L, limited.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [MemberData(nameof(Partitions))]
    public async Task NormalizationPreservesPhysicalPartsAndInlineSamples(string shape, bool encoded, bool indirect)
    {
        await AssertNormalizationAsync(shape, encoded, indirect, false, false, true);
    }

    [Theory]
    [InlineData(false, false, "empty-tail", false, true)]
    [InlineData(true, false, "after-EI", true, false)]
    [InlineData(false, true, "dictionary", true, true)]
    [InlineData(true, true, "before-ID", false, false)]
    public async Task NormalizationWiringAndExplicitDisableHaveDistinctBytes(bool job, bool qdf, string shape, bool encoded, bool indirect)
    {
        foreach (bool normalize in new[] { false, true })
        { await AssertNormalizationAsync(shape, encoded, indirect, job, qdf, normalize); }
    }

    private static async Task AssertNormalizationAsync(string shape, bool encoded, bool indirect, bool job, bool qdf, bool normalize)
    {
        (byte[] input, byte[][] parts) = Fixture(shape, encoded, indirect, false);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await ExecuteNormalization(host, job, qdf, false, normalize);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("copy"), output, QpdfInputOwnership.Transfer);
        byte[] logical;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), CancellationToken.None))
        {
            QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
            Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject value));
            QpdfObject contents = Resolve(value); QpdfObject[] entries = contents is PdfArray array ? array.Items : [value];
            Assert.Equal(parts.Length, entries.Length);
            logical = entries.SelectMany(x => Assert.IsType<PdfStream>(Resolve(x)).Data.Append((byte)'\n')).ToArray();
            int start = logical.AsSpan().IndexOf("ID"u8); Assert.True(start >= 0);
            Assert.Equal((byte)' ', logical[start + 2]); Assert.Equal(Image, logical.AsSpan(start + 3, Image.Length).ToArray());
        }
        Assert.Equal(normalize, logical.AsSpan().IndexOf("(a\\r\\nb)"u8) >= 0);
        Assert.Equal(!normalize, logical.AsSpan().IndexOf("(a\\015\\012b)"u8) >= 0);
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("copy"), CancellationToken.None);
        Assert.True(check.Passed); Assert.False(check.HasSkipped); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static ValueTask<QpdfExecutionResult> ExecuteNormalization(InMemoryQpdfHost host, bool job, bool qdf, bool coalesce, bool normalize)
    {
        string setting = normalize ? "y" : "n";
        List<string> arguments = ["input", "--normalize-content=" + setting, "--stream-data=uncompress", "--compress-streams=n"];
        Dictionary<string, object> values = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["normalizeContent"] = setting, ["streamData"] = "uncompress", ["compressStreams"] = "n" };
        if (qdf) { arguments.Add("--qdf"); values["qdf"] = true; }
        if (coalesce) { arguments.Add("--coalesce-contents"); values["coalesceContents"] = true; }
        arguments.Add("output");
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(values), CancellationToken.None)
            : Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
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
            QpdfExecutionResult result = await ExecuteNormalization(host, job, false, coalesce, true);
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

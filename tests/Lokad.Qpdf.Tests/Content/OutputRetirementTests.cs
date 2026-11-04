using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OutputRetirementTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach ((string operation, int flags) in new[] { ("prune", 4), ("all", 4), ("screen", 4), ("print", 4), ("screen", 36), ("print", 0) })
        foreach (string alias in new[] { "none", "catalog", "attachment" })
        foreach (bool preserve in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [operation, flags, alias, preserve, "classic", job]; }
        foreach (string writer in new[] { "packed", "linear", "qdf" })
        foreach (string operation in new[] { "prune", "all" })
        foreach (string alias in new[] { "none", "catalog" })
        foreach (bool job in new[] { false, true }) { yield return [operation, 4, alias, false, writer, job]; }
        foreach (string operation in new[] { "plain", "no-prune" })
        foreach (bool job in new[] { false, true }) { yield return [operation, 4, "none", false, "classic", job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FinalRootsRetireRemovedEdgesAndPreserveIndependentAliases(
        string operation, int flags, string alias, bool preserve, string writer, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(operation, flags, alias), QpdfInputOwnership.Transfer);
        List<string> argv = ["input", "--compress-streams=n", "--decode-level=none"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["compressStreams"] = "n", ["decodeLevel"] = "none" };
        if (operation is "prune" or "no-prune")
        {
            argv.AddRange(["--pages", ".", "1", "--"]);
            settings["pages"] = new[] { new { file = ".", range = "1" } };
            string value = operation == "prune" ? "yes" : "no";
            argv.Add("--remove-unreferenced-resources=" + value); settings["removeUnreferencedResources"] = value;
        }
        else if (operation != "plain") { argv.Add("--flatten-annotations=" + operation); settings["flattenAnnotations"] = operation; }
        if (preserve) { argv.Add("--preserve-unreferenced"); settings["preserveUnreferenced"] = true; }
        if (writer == "packed") { argv.Add("--object-streams=generate"); settings["objectStreams"] = "generate"; }
        if (writer == "linear") { argv.Add("--linearize"); settings["linearize"] = true; }
        if (writer == "qdf") { argv.Add("--qdf"); settings["qdf"] = true; }
        argv.Add("output");
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        List<QpdfObject> marked = [];
        foreach ((int number, XrefEntry entry) in document.Engine.XRefEntries)
        {
            if (entry.Kind == XrefEntryKind.Free) { continue; }
            QpdfObject value = document.GetObject(number);
            PdfDictionary? header = value as PdfDictionary ?? (value as PdfStream)?.Dictionary;
            if (header is not null && header.TryGet(PdfName.FromAscii("Marker"), out _)) { marked.Add(value); }
        }
        bool kept = preserve || alias != "none" || operation is "plain" or "no-prune";
        Assert.Equal(kept ? 1 : 0, marked.Count);
        if (kept)
        {
            QpdfObject target = marked[0];
            if (target is PdfStream image) { Assert.Equal(16384, image.RawMemory.Length); Assert.All(image.RawMemory.ToArray(), b => Assert.Equal(127, b)); }
            else
            {
                PdfDictionary annotation = Assert.IsType<PdfDictionary>(target);
                Assert.True(annotation.TryGet(PdfName.FromAscii("Private"), out QpdfObject raw));
                PdfByteString text = Assert.IsType<PdfByteString>(raw); Assert.Equal(16384, text.ValueMemory.Length);
                Assert.All(text.ValueMemory.ToArray(), b => Assert.Equal((byte)'x', b));
            }
        }
    }

    private static byte[] Fixture(string operation, int flags, string alias)
    {
        bool pruning = operation is "prune" or "no-prune" or "plain";
        string catalog = "<< /Type /Catalog /Pages 2 0 R" + (alias == "catalog" ? " /Independent 5 0 R" : "")
            + (alias == "attachment" ? " /Names << /EmbeddedFiles << /Names [(K) 7 0 R] >> >>" : "") + " >>";
        byte[] payload = new byte[16384]; Array.Fill(payload, (byte)127);
        return CheckTests.Assemble([
            (1, catalog, null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources <<"
                + (pruning ? " /XObject << /Unused 5 0 R >>" : "") + " >>" + (pruning ? "" : " /Annots [5 0 R]") + " >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, pruning ? "<< /Type /XObject /Subtype /Image /Width 1 /Height 16384 /ColorSpace /DeviceGray /BitsPerComponent 8 /Marker (retired) /Length 16384 >>"
                : "<< /Type /Annot /Subtype /Stamp /Rect [10 20 40 40] /F " + flags + " /AP << /N 6 0 R >> /Marker (retired) /Private (" + new string('x', 16384) + ") >>", pruning ? payload : null),
            (6, "<< /Type /XObject /Subtype /Form /BBox [0 0 30 20] /Resources << >> /Length 3 >>", "q Q"u8.ToArray()),
            (7, "<< /Type /Filespec /F (K) /EF << /F 8 0 R >> /Independent 5 0 R >>", null),
            (8, "<< /Type /EmbeddedFile /Length 1 >>", new byte[] { 42 })], 1);
    }
}

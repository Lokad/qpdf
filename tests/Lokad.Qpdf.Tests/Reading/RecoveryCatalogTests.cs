using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class RecoveryCatalogTests
{
    private static readonly string[] Kinds =
    [
        "direct", "generation", "type-forward", "type-backward", "type-escaped", "type-generation",
        "stale-size-root", "stale-size-child", "stale-size-type", "combined-stale-root",
        "tail-live-root", "tail-live-type", "tail-stale-generation", "last-live-root", "overwritten-root",
        "last-live-generation", "invalid-type-name", "invalid-type-self", "invalid-type-generation", "direct-escaped",
        "embedded-stream-header", "embedded-string-header", "missing-endobj"
    ];

    private const string EmbeddedHeader = "7 0 obj\n<< /Type /Catalog /Pages 2 0 R /Audit (fake) >>\nendobj\n";

    private static (byte[] Bytes, int Root, int Generation, string Marker, int Type, int Page) Fixture(string kind)
    {
        int root = 1, generation = 0, type = 0, typeGeneration = 0, typeReferenceGeneration = 0, page = 3;
        string scalarName = "Catalog", tail = "missing";
        bool smallSize = false, backup = false;
        switch (kind)
        {
            case "direct": case "direct-escaped": break;
            case "generation": generation = 5; break;
            case "type-forward": case "type-escaped": type = 5; break;
            case "type-backward": root = 7; type = 5; break;
            case "type-generation": root = 7; generation = 5; type = 5; typeGeneration = typeReferenceGeneration = 3; break;
            case "stale-size-root": root = 7; smallSize = true; tail = "live"; break;
            case "stale-size-child": page = 9; smallSize = true; tail = "live"; break;
            case "stale-size-type": type = 11; typeGeneration = typeReferenceGeneration = 3; smallSize = true; tail = "live"; break;
            case "combined-stale-root": root = 7; generation = 5; type = 11; typeGeneration = typeReferenceGeneration = 3; page = 9; smallSize = true; tail = "stale"; break;
            case "tail-live-root": root = 7; generation = 5; tail = "live"; break;
            case "tail-live-type": type = 5; typeGeneration = typeReferenceGeneration = 3; tail = "live"; break;
            case "tail-stale-generation": root = 7; generation = 5; tail = "stale"; break;
            case "last-live-root": case "overwritten-root": root = 7; backup = true; break;
            case "last-live-generation": root = 7; generation = 5; backup = true; break;
            case "invalid-type-name": type = 5; scalarName = "Page"; break;
            case "invalid-type-self": type = 1; break;
            case "invalid-type-generation": type = 5; typeGeneration = 3; break;
            case "embedded-stream-header": case "embedded-string-header": break;
            case "missing-endobj": root = 7; break;
            default: throw new ArgumentException(nameof(kind));
        }
        if (kind == "type-escaped") { scalarName = "Cat#61log"; }
        int maximum = Math.Max(root, Math.Max(type, page));
        bool embedded = kind.StartsWith("embedded-", StringComparison.Ordinal);
        if (embedded) { maximum = Math.Max(maximum, 4); }
        List<(int Number, string Dict, byte[]? StreamData)> objects = [];
        for (int number = 1; number <= maximum; number++) { objects.Add((number, "null", null)); }
        string marker = "root-" + root + "-" + generation;
        string rootType = type > 0 ? type + " " + typeReferenceGeneration + " R" : kind == "direct-escaped" ? "/Cat#61log" : "/Catalog";
        string alias = type > 0 && type != root ? " /AuditType " + type + " " + typeReferenceGeneration + " R" : "";
        objects[root - 1] = (root, "<< /Type " + rootType + " /Pages 2 0 R /Audit (" + marker + ")" + alias
            + (embedded ? " /Carrier 4 0 R" : "") + " >>", null);
        objects[1] = (2, "<< /Type /Pages /Kids [" + page + " 0 R] /Count 1 >>", null);
        objects[page - 1] = (page, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>", null);
        if (type > 0 && type != root) { objects[type - 1] = (type, "/" + scalarName, null); }
        if (backup) { objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Audit (backup) >>", null); }
        if (kind == "embedded-stream-header")
        {
            byte[] data = Encoding.ASCII.GetBytes(EmbeddedHeader);
            objects[3] = (4, "<< /Length " + data.Length + " >>", data);
        }
        if (kind == "embedded-string-header") { objects[3] = (4, "(" + EmbeddedHeader + ")", null); }
        Dictionary<int, int> generations = new() { [root] = generation };
        if (type > 0 && type != root) { generations[type] = typeGeneration; }
        string source = Encoding.ASCII.GetString(CheckTests.Assemble(objects, root, generations));
        if (kind == "missing-endobj") { source = source.Replace("4 0 obj\nnull\nendobj", "4 0 obj\nnull", StringComparison.Ordinal); }
        int trailer = source.LastIndexOf("trailer\n", StringComparison.Ordinal);
        string extra = kind == "overwritten-root" ? "7 1 obj\nnull\nendobj\n"
            : kind == "last-live-generation" ? "7 6 obj\n<< /Type /Catalog /Pages 2 0 R /Audit (latest) >>\nendobj\n" : "";
        string rootEntry = tail == "missing" ? "" : " /Root " + root + " " + (tail == "stale" ? 0 : generation) + " R";
        source = source[..trailer] + extra + "trailer\n<< /Size " + (smallSize ? 4 : maximum + 1) + rootEntry + " >>\nstartxref\n0\n%%EOF\n";
        if (kind == "overwritten-root") { root = 1; generation = 0; marker = "backup"; }
        if (kind == "last-live-generation") { generation = 6; marker = "latest"; }
        return (Encoding.ASCII.GetBytes(source), root, generation, marker, type, page);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string kind in Kinds)
        foreach (bool job in new[] { false, true })
        foreach (bool rewrite in new[] { false, true }) { yield return [kind, job, rewrite]; }
    }

    private static QpdfObject Get(QpdfDocument document, PdfDictionary node, string key)
    {
        Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RecoveryKeepsLiveCatalogIdentityAndDependencies(string kind, bool job, bool rewrite)
    {
        var source = Fixture(kind);
        bool success = !kind.StartsWith("invalid-", StringComparison.Ordinal);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), source.Bytes, QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        Dictionary<string, object> settings = new() { ["inputFile"] = "input" };
        bool embedded = kind.StartsWith("embedded-", StringComparison.Ordinal);
        if (embedded && rewrite) { settings["streamData"] = "uncompress"; }
        if (rewrite) { settings["outputFile"] = "output"; } else { settings["showNpages"] = ""; }
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(rewrite ? embedded ? ["input", "--stream-data=uncompress", "output"] : ["input", "output"]
                : ["input", "--show-npages"]), CancellationToken.None);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(result.ExitCode == (success ? QpdfExitCode.Warning : QpdfExitCode.Error), string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(success ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        if (!success || !rewrite) { Assert.Equal("prior"u8.ToArray(), output); }
        if (!success) { return; }
        if (!rewrite)
        { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount && d.Message.EndsWith(": 1", StringComparison.Ordinal)); return; }
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Get(document, document.Engine.Trailer, "Root"));
        Assert.Equal(Encoding.ASCII.GetBytes(source.Marker), Assert.IsType<PdfByteString>(Get(document, catalog, "Audit")).Value);
        if (source.Type > 0) { Assert.Equal(PdfName.FromAscii("Catalog"), Get(document, catalog, "AuditType")); }
        if (embedded)
        {
            QpdfObject carrier = Get(document, catalog, "Carrier");
            byte[] data = kind == "embedded-stream-header" ? Assert.IsType<PdfStream>(carrier).Data : Assert.IsType<PdfByteString>(carrier).Value;
            Assert.Equal(Encoding.ASCII.GetBytes(EmbeddedHeader), data);
        }
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("type-generation")]
    [InlineData("stale-size-root")]
    [InlineData("stale-size-child")]
    [InlineData("stale-size-type")]
    [InlineData("combined-stale-root")]
    [InlineData("overwritten-root")]
    [InlineData("last-live-generation")]
    [InlineData("embedded-stream-header")]
    [InlineData("embedded-string-header")]
    public void ReconstructedTableUsesLiveGenerationsAndRetainsRequiredEntries(string kind)
    {
        var source = Fixture(kind);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (XRefTable table = XRefTable.Parse(source.Bytes, QpdfLimits.Default, governor, new(governor), QpdfRecoveryMode.BoundedRepair, valuesEscape: false))
        {
            Assert.True(table.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject value));
            PdfReference root = Assert.IsType<PdfReference>(value);
            Assert.Equal(source.Root, root.ObjectNumber); Assert.Equal(source.Generation, root.Generation);
            Assert.Equal(source.Generation, table.Entries[source.Root].Second);
            Assert.Equal(XrefEntryKind.Uncompressed, table.Entries[source.Page].Kind);
            if (source.Type > 0) { Assert.Equal(XrefEntryKind.Uncompressed, table.Entries[source.Type].Kind); }
        }
        governor.CompleteStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

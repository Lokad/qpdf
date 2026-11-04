using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class LogicalContentContextTests
{
    private static readonly byte[] Header = "q BI /W 1 /H 1 /BPC 8 /CS /C"u8.ToArray();
    private static readonly byte[] Tail = [.. "ID "u8, 255, 0, 0, .. " EI Q"u8];

    private static byte[] Fixture(bool firstInvalid, bool standalone)
    {
        string Page(bool invalid) => "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources "
            + (invalid && !standalone ? "10 0 R" : "9 0 R") + " /Contents " + (invalid && standalone ? "5 0 R" : "6 0 R") + " >>";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AuditArray 6 0 R /AuditTail 5 0 R >>", null),
            (2, "<< /Type /Pages /Count 3 /Kids [3 0 R 7 0 R 8 0 R] >>", null),
            (3, Page(firstInvalid), null), (4, "<< /Length " + Header.Length + " >>", Header),
            (5, "<< /Length " + Tail.Length + " >>", Tail), (6, "[4 0 R 5 0 R]", null),
            (7, Page(false), null), (8, Page(!firstInvalid), null),
            (9, "<< /ColorSpace << /C /DeviceRGB >> >>", null), (10, "<< >>", null)], 1);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task FragmentAndArrayMemoizationRequiresTheirActualContext(bool firstInvalid, bool standalone, bool job)
    {
        List<PdfStream> escaped = [];
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture(firstInvalid, standalone), QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","normalizeContent":"y","streamData":"preserve","compressStreams":"n","removeUnreferencedResources":"no"}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--normalize-content=y", "--stream-data=preserve", "--compress-streams=n", "--remove-unreferenced-resources=no", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Warning, result.ExitCode); Assert.Single(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("copy"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), CancellationToken.None);
            QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
            QpdfObject Get(PdfDictionary dictionary, string key) { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(Get(document.Engine.Trailer, "Root")));
            QpdfObject originalArray = Get(catalog, "AuditArray"), originalTail = Get(catalog, "AuditTail");
            PdfArray source = Assert.IsType<PdfArray>(Resolve(originalArray));
            Assert.Equal(Header, Assert.IsType<PdfStream>(Resolve(source[0])).Data);
            Assert.Equal(Tail, Assert.IsType<PdfStream>(Resolve(originalTail)).Data);
            Assert.Equal(originalTail, source[1]); escaped.Add(Assert.IsType<PdfStream>(Resolve(originalTail)));
            PdfReference? normalizedHeader = null, normalizedTail = null;
            for (int index = 0; index < 3; index++)
            {
                QpdfObject contents = Get(document.PageReferences[index].Page, "Contents");
                bool invalid = index == (firstInvalid ? 0 : 2);
                if (invalid)
                { Assert.Equal(standalone ? originalTail : originalArray, contents); continue; }
                PdfArray parts = Assert.IsType<PdfArray>(Resolve(contents)); Assert.Equal(2, parts.Count);
                Assert.NotEqual(source[0], parts[0]); Assert.NotEqual(source[1], parts[1]);
                PdfReference header = Assert.IsType<PdfReference>(parts[0]), tail = Assert.IsType<PdfReference>(parts[1]);
                if (normalizedHeader is not null) { Assert.Equal(normalizedHeader, header); Assert.Equal(normalizedTail, tail); }
                normalizedHeader = header; normalizedTail = tail;
                Assert.Equal(Header, Assert.IsType<PdfStream>(Resolve(header)).Data);
                Assert.Equal(Tail, Assert.IsType<PdfStream>(Resolve(tail)).Data);
            }
        }
        Assert.Equal(Tail, Assert.Single(escaped).Data);
    }

    [Theory]
    [InlineData("literal", false)]
    [InlineData("literal", true)]
    [InlineData("hex", false)]
    [InlineData("hex", true)]
    public async Task ACompleteTokenCanCrossThePhysicalPartBoundary(string kind, bool job)
    {
        byte[] first = kind == "literal" ? "BT /F1 12 Tf (A\r"u8.ToArray() : "BT /F1 12 Tf <4142"u8.ToArray();
        byte[] last = kind == "literal" ? "B) Tj ET\n"u8.ToArray() : "4344> Tj ET\n"u8.ToArray();
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 6 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Contents 6 0 R >>", null),
            (4, "<< /Length " + first.Length + " >>", first), (5, "<< /Length " + last.Length + " >>", last), (6, "[4 0 R 5 0 R]", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","normalizeContent":"y","streamData":"uncompress"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--normalize-content=y", "--stream-data=uncompress", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("copy"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), CancellationToken.None))
        {
            Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
            PdfArray parts = Assert.IsType<PdfArray>(contents); Assert.Equal(2, parts.Count);
            byte[] program = parts.Items.SelectMany(x => Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(x).ObjectNumber)).Data.Append((byte)'\n')).ToArray();
            Assert.Contains(kind == "literal" ? "(A\\nB)" : "<4142\n4344>", Encoding.ASCII.GetString(program), StringComparison.Ordinal);
        }
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("copy"), CancellationToken.None);
        Assert.True(check.Passed); Assert.False(check.HasSkipped); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("RunLengthDecode", QpdfStreamDecodeLevel.Generalized, QpdfCheckStatus.Skipped)]
    [InlineData("RunLengthDecode", QpdfStreamDecodeLevel.Specialized, QpdfCheckStatus.Passed)]
    [InlineData("Bogus", QpdfStreamDecodeLevel.All, QpdfCheckStatus.Skipped)]
    [InlineData("ASCIIHexDecode", QpdfStreamDecodeLevel.All, QpdfCheckStatus.Failed)]
    [InlineData("external", QpdfStreamDecodeLevel.All, QpdfCheckStatus.Skipped)]
    public async Task MissingFragmentsDoNotBecomeStandaloneInvalidPrograms(string filter, QpdfStreamDecodeLevel level, QpdfCheckStatus expected)
    {
        byte[] prefix = "q BI /W 1 /H 1 /BPC 8 /CS /RGB"u8.ToArray();
        byte[] encoded = filter == "RunLengthDecode" ? [(byte)(prefix.Length - 1), .. prefix, 128] : prefix;
        string header = filter == "external" ? "/F (unavailable)" : "/Filter /" + filter;
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents [4 0 R 5 0 R] >>", null),
            (4, "<< /Length " + encoded.Length + " " + header + " >>", encoded), (5, "<< /Length " + Tail.Length + " >>", Tail)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), new() { DecodeLevel = level }, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(expected, Assert.Single(result.Checks, x => x.Name == "page-content").Status);
        Assert.DoesNotContain(result.Diagnostics, x => x.Message.Contains("no BI header", StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task FailedProgramsDoNotPublishUnusedFragmentsAndLaterPagesCanUseAComputedFragment(bool preserve, bool laterPage, bool job)
    {
        byte[] good = "% Kept\r\nq \r\nQ\r\n"u8.ToArray();
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /Keep 4 0 R >>", null),
            (2, "<< /Type /Pages /Count " + (laterPage ? "2 /Kids [3 0 R 7 0 R]" : "1 /Kids [3 0 R]") + " >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 5 0 R >>", null),
            (4, "<< /Length " + good.Length + " >>", good), (5, "[4 0 R 6 0 R]", null), (6, "<< /Length 1 >>", "("u8.ToArray())];
        if (laterPage) { objects.Add((7, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>", null)); }
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), CheckTests.Assemble(objects, 1), QpdfInputOwnership.Transfer);
        List<string> args = ["input", "--normalize-content=y", "--stream-data=preserve", "--compress-streams=n", "--remove-unreferenced-resources=no"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["normalizeContent"] = "y", ["streamData"] = "preserve", ["compressStreams"] = "n", ["removeUnreferencedResources"] = "no" };
        if (preserve) { args.Add("--preserve-unreferenced"); settings["preserveUnreferenced"] = true; }
        args.Add("output");
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, System.Text.Json.JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode); Assert.Single(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("copy"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("copy"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
        PdfArray parts = Assert.IsType<PdfArray>(Resolve(contents)); Assert.Equal(2, parts.Count);
        Assert.Equal(good, Assert.IsType<PdfStream>(Resolve(parts[0])).Data);
        Assert.Equal("("u8.ToArray(), Assert.IsType<PdfStream>(Resolve(parts[1])).Data);
        Assert.Equal(laterPage ? 3 : 2, document.Engine.XRefEntries.Keys.Where(number => number > 0).Count(number => document.GetObject(number) is PdfStream));
        if (laterPage)
        {
            Assert.True(document.PageReferences[1].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject single));
            Assert.IsType<PdfReference>(single); Assert.NotEqual(parts[0], single);
            Assert.Equal("% Kept\n\nq \nQ\n"u8.ToArray(), Assert.IsType<PdfStream>(Resolve(single)).Data);
        }
    }
}

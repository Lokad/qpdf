using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Content;

public sealed class PrintPageRangeTests
{
    private static readonly QpdfPath Primary = QpdfPath.From("primary.pdf");

    [Theory]
    [InlineData("1", "101", "1,1")]
    [InlineData("2,3,1", "102,103,101", "2,3")]
    [InlineData("1,1,2", "101,101,102", "1,2")]
    [InlineData("2", "102", "")]
    [InlineData("1-3,x1-3", "", "")]
    public async Task SelectionProjectsPrimaryPrintPositions(string range, string heights, string expected)
    {
        using InMemoryQpdfHost host = Input();
        QpdfExecutionResult result = await Run(host, "primary.pdf", "--pages", ".", range, "--", "selected.pdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertOutput(host, "selected.pdf", heights, expected, false, "1,1,3,3");
    }

    [Fact]
    public async Task WholeRewritePreservesTheSharedRange()
    {
        using InMemoryQpdfHost host = Input();
        QpdfExecutionResult result = await Run(host, "primary.pdf", "whole.pdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertOutput(host, "whole.pdf", "101,102,103", "1,1,3,3", true, "1,1,3,3");
    }

    [Theory]
    [InlineData("3 3 1 2 2 3", "1,3", "3,3,1,2,2,3")]
    [InlineData("1 1 4 4", "", "1,1,4,4")]
    [InlineData("1 1 3", "", "1,1,3")]
    public async Task ChangedOrderCompactsValidRangesAndDropsMalformedRanges(string pairs, string expected, string original)
    {
        using InMemoryQpdfHost host = Input(pairs);
        QpdfExecutionResult result = await Run(host, "primary.pdf", "--pages", ".", "3,2,1", "--", "selected.pdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertOutput(host, "selected.pdf", "103,102,101", expected, false, original);
    }

    [Fact]
    public async Task CollationCountsSecondarySlotsWithoutInheritingTheirPreset()
    {
        using InMemoryQpdfHost host = Input();
        host.AddInput(QpdfPath.From("secondary.pdf"), Source(200, "1 3"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"primary.pdf","outputFile":"collated.pdf","collate":"1","pages":[{"file":".","range":"2,3"},{"file":"secondary.pdf","range":"1,2"}]}""",
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertOutput(host, "collated.pdf", "102,201,103,202", "3,3", false, "1,1,3,3");

        result = await Run(host, "primary.pdf", "--pages", "secondary.pdf", "1", "--", "secondary-only.pdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        await AssertOutput(host, "secondary-only.pdf", "201", "", false, "1,1,3,3");
    }

    [Fact]
    public async Task AssembledJsonReportsTheSameProjectedPreset()
    {
        using InMemoryQpdfHost host = Input();
        QpdfExecutionResult result = await Run(host, "primary.pdf", "--pages", ".", "2,3,1", "--", "--json=2",
            "--json-key=qpdf", "--json-key=pages");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using JsonDocument report = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        JsonElement catalog = report.RootElement.GetProperty("qpdf")[1];
        JsonElement root = Resolve(catalog, catalog.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        JsonElement active = root.GetProperty("/ViewerPreferences");
        JsonElement original = Resolve(catalog, root.GetProperty("/PrivatePreferences"));
        Assert.True(active.GetProperty("/DisplayDocTitle").GetBoolean());
        Assert.Equal([2, 3], active.GetProperty("/PrintPageRange").EnumerateArray().Select(static value => value.GetInt32()));
        Assert.Equal([1, 1, 3, 3], Resolve(catalog, original.GetProperty("/PrintPageRange"))
            .EnumerateArray().Select(static value => value.GetInt32()));
        Assert.Equal([102, 103, 101], report.RootElement.GetProperty("pages").EnumerateArray()
            .Select(page => Resolve(catalog, page.GetProperty("object")).GetProperty("/MediaBox")[3].GetInt32()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BoundSplitProjectsEachPartAgainstTheBoundPageOrder(int groupSize)
    {
        using InMemoryQpdfHost host = Input();
        host.AddInput(QpdfPath.From("patch.json"),
            """{"qpdf":[{"jsonversion":2},{"obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["4 0 R","5 0 R","3 0 R"],"/Count":3}}}]}"""u8.ToArray(),
            QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, "primary.pdf", "--update-from-json=patch.json", $"--split-pages={groupSize}", "part.pdf");
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        if (groupSize == 1)
        {
            await AssertOutput(host, "part-1.pdf", "102", "1,1", false, "1,1,3,3");
            await AssertOutput(host, "part-2.pdf", "103", "", false, "1,1,3,3");
        }
        else
        {
            await AssertOutput(host, "part-1-2.pdf", "102,103", "1,1", false, "1,1,3,3");
        }
        await AssertOutput(host, "part-3.pdf", "101", "1,1", false, "1,1,3,3");
    }

    private static JsonElement Resolve(JsonElement objects, JsonElement reference)
        => objects.GetProperty("obj:" + reference.GetString()).GetProperty("value");

    private static ValueTask<QpdfExecutionResult> Run(InMemoryQpdfHost host, params string[] argv)
        => Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);

    private static InMemoryQpdfHost Input() => Input("1 1 3 3");

    private static InMemoryQpdfHost Input(string pairs)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(Primary, Source(100, pairs), QpdfInputOwnership.Transfer);
        return host;
    }

    private static byte[] Source(int height, string pairs) => PdfTestDocument.Assemble(
    [
        (1, "<< /Type /Catalog /Pages 2 0 R /ViewerPreferences 6 0 R /PrivatePreferences 6 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>", null),
        (3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 {height + 1}] /Resources << >> >>", null),
        (4, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 {height + 2}] /Resources << >> >>", null),
        (5, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 {height + 3}] /Resources << >> >>", null),
        (6, "<< /DisplayDocTitle true /PrintPageRange 7 0 R >>", null),
        (7, $"[{pairs}]", null),
    ], 1, null, "1.7", "");

    private static async Task AssertOutput(InMemoryQpdfHost host, string path, string heights, string expected,
        bool sameDictionary, string original)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] output));
        using InMemoryQpdfHost read = new();
        QpdfPath input = QpdfPath.From("read.pdf");
        read.AddInput(input, output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(read, input, CancellationToken.None);
        Assert.Equal(Numbers(heights), document.PageReferences.Select(page => Assert.IsType<PdfInteger>(
            Assert.IsType<PdfArray>(Get(page.Page, "MediaBox"))[3]).Value));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(document, Get(document.Engine.Trailer, "Root")));
        QpdfObject active = Get(catalog, "ViewerPreferences"), alias = Get(catalog, "PrivatePreferences");
        Assert.Equal(sameDictionary, active.Equals(alias));
        AssertPreset(document, Resolve(document, active), expected);
        AssertPreset(document, Resolve(document, alias), original);
    }

    private static void AssertPreset(QpdfDocument document, QpdfObject value, string expected)
    {
        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(value);
        Assert.True(Assert.IsType<PdfBoolean>(Get(dictionary, "DisplayDocTitle")).Value);
        bool present = dictionary.TryGet(PdfName.FromAscii("PrintPageRange"), out QpdfObject pairs);
        Assert.Equal(expected.Length != 0, present);
        if (present)
        {
            PdfArray range = Assert.IsType<PdfArray>(Resolve(document, pairs));
            Assert.Equal(Numbers(expected), range.BorrowedItems.Select(item => Assert.IsType<PdfInteger>(Resolve(document, item)).Value));
        }
    }

    private static long[] Numbers(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(static value => long.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static QpdfObject Get(PdfDictionary dictionary, string name)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value));
        return value;
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference) : value;
}

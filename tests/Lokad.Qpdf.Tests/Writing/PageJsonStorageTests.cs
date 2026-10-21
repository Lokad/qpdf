using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PageJsonStorageTests
{
    internal static byte[] Fixture(string contents, string array, string resources) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R 7 0 R] /Count 2 /Resources << /XObject << /Inherited 4 0 R >> >> >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents " + contents + " " + resources + " >>", null),
        (4, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 1 >>", "q"u8.ToArray()),
        (5, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 1 >>", "Q"u8.ToArray()),
        (6, array, null),
        (7, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 6 0 R /Resources << >> >>", null)], 1);

    [Theory]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("typed", true)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task DirectAndSharedIndirectArraysListStreamIdentities(string route, bool indirect)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(indirect ? "6 0 R" : "[4 0 R 5 0 R]", "[4 0 R 5 0 R]", "/Resources << >>"), QpdfInputOwnership.Transfer);
        if (route == "typed") { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["pages"]), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","json":"2","jsonKey":["pages"]}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--json=2", "--json-key=pages", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal(0L, host.OpenLeaseCount);
        host.Dispose(); using JsonDocument json = JsonDocument.Parse(output);
        foreach (JsonElement page in json.RootElement.GetProperty("pages").EnumerateArray())
        { Assert.Equal(new[] { "4 0 R", "5 0 R" }, page.GetProperty("contents").EnumerateArray().Select(e => e.GetString())); }
    }

    [Theory]
    [InlineData("6 0 R", "[4 0 R null 99 0 R [5 0 R] 42]", "[\"4 0 R\",null,\"99 0 R\",[\"5 0 R\"],42]")]
    [InlineData("6 0 R", "null", "[\"6 0 R\"]")]
    [InlineData("6 0 R", "6 0 R", "[\"6 0 R\"]")]
    [InlineData("42", "[]", "[42]")]
    [InlineData("4 0 R", "[]", "[\"4 0 R\"]")]
    [InlineData("99 0 R", "[]", "[\"99 0 R\"]")]
    public async Task MalformedContentsRemainVisible(string contents, string array, string expected)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(contents, array, "/Resources << >>"), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["pages"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(result.Json), value = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(value.RootElement, json.RootElement.GetProperty("pages")[0].GetProperty("contents")));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("", "4 0 R")]
    [InlineData("/Resources 99 0 R", null)]
    [InlineData("/Resources 42", null)]
    public async Task ImageResourceInheritanceRetainsInvalidOverridePolicy(string resources, string? expected)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("[]", "[]", resources), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["pages"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(result.Json); JsonElement images = json.RootElement.GetProperty("pages")[0].GetProperty("images");
        if (expected is null) { Assert.Equal(0, images.GetArrayLength()); }
        else { Assert.Equal(expected, images[0].GetProperty("object").GetString()); }
    }

    [Fact]
    public async Task ImagesKeepOrdinalDisplayNameOrderWithoutComparisonStrings()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("[]", "[]", "/Resources << /XObject << /Z 4 0 R /A#23B 5 0 R /#FF 4 0 R /Skip 42 >> >>"), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["pages"]), CancellationToken.None);
        host.Dispose(); using JsonDocument json = JsonDocument.Parse(result.Json);
        Assert.Equal(new[] { "/?", "/A#B", "/Z" }, json.RootElement.GetProperty("pages")[0].GetProperty("images").EnumerateArray().Select(e => e.GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData("contents", false)]
    [InlineData("images", false)]
    [InlineData("image-count", false)]
    [InlineData("contents", true)]
    [InlineData("images", true)]
    public async Task RefusalAndCancellationDoNotCloneLargeOwnedCollections(string mode, bool canceled)
    {
        string contents = mode == "contents" ? "[" + string.Join(' ', Enumerable.Repeat("4 0 R", 262144)) + "]" : "[]";
        string resources = mode == "images" || mode == "image-count"
            ? "/Resources << /XObject << " + string.Join(' ', Enumerable.Range(0, mode == "images" ? 4 : 16384).Reverse().Select(i => "/" + (mode == "images" ? new string('N', 262144) : "N") + i + " 4 0 R")) + " >> >>"
            : "/Resources << >>";
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(contents, "[]", resources), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        for (int number = 1; number <= 7; number++) { document.GetObject(number); }
        _ = document.PageReferences;
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(71L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        void Inspect()
        {
            using PreparedJsonOutput output = PreparedJsonOutput.Create(governor);
            using DisplayBuffer text = DocumentJson.WriteDocument(document, new(2, ["pages"], "none", "none", null, [], false), output,
                limits, governor, new(governor), canceled ? new CancellationToken(true) : CancellationToken.None);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        if (canceled) { Assert.ThrowsAny<OperationCanceledException>(Inspect); }
        else { Assert.Throws<QpdfLimitException>(Inspect); }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }
}

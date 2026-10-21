using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonLabelCursorTests
{
    internal static byte[] Fixture(int count, string labels)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [" + labels + "] >> >>", null),
            (2, "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(3, count).Select(i => i + " 0 R")) + "] /Count " + count + " >>", null)];
        for (int i = 0; i < count; i++) { objects.Add((3 + i, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null)); }
        return CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8192)]
    public async Task SequentialPagesPreserveEachBreakpoint(int count)
    {
        string labels = string.Join(" ", Enumerable.Range(0, count).Select(i => i + " " + i));
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(count, labels), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
            new QpdfJsonOptions(2, ["pages", "pagelabels"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(result.Json); JsonElement pages = json.RootElement.GetProperty("pages");
        Assert.Equal(count, pages.GetArrayLength()); Assert.Equal(count, json.RootElement.GetProperty("pagelabels").GetArrayLength());
        for (int i = 0; i < count; i++) { Assert.Equal(i, pages[i].GetProperty("label").GetInt32()); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task GapsDuplicatesAndNonDictionaryLabelsKeepTheirValues()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(5, "1 17 1 42 3 /Roman 8 (unused)"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input","outputFile":"output","json":"2","jsonKey":["pages"]}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        using JsonDocument json = JsonDocument.Parse(output); JsonElement pages = json.RootElement.GetProperty("pages");
        Assert.Equal(JsonValueKind.Null, pages[0].GetProperty("label").ValueKind);
        Assert.Equal(42, pages[1].GetProperty("label").GetInt32()); Assert.Equal(42, pages[2].GetProperty("label").GetInt32());
        Assert.Equal("/Roman", pages[3].GetProperty("label").GetString()); Assert.Equal("/Roman", pages[4].GetProperty("label").GetString());
    }

    [Fact]
    public async Task FilteredInspectionDoesNotFundAnUnusedIndexForThousandsOfPages()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(8192, ""), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (PreparedJsonOutput output = PreparedJsonOutput.Create(governor))
        using (DisplayBuffer text = DocumentJson.WriteDocument(document, new(2, ["encrypt"], "none", "none", null, [], false),
            output, limits, governor, new(governor), CancellationToken.None))
        { Assert.InRange(text.Utf8Bytes, 1L, 4096L); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

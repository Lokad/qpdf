using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LabelTreeIndirectionTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string form in new[] { "direct", "nums", "kids", "key", "entry", "start" })
        foreach (string operation in new[] { "select", "merge", "inspect" })
        foreach (string route in new[] { "typed", "argv", "job" })
        { yield return new object[] { form, operation, route }; }
    }

    private static byte[] Fixture(string form) => form switch
    {
        "nums" => LabelIndirectionTests.Build(2, "<< /Nums 5 0 R >>", (5, "[0 << /S /D /St 10 >>]")),
        "kids" => LabelIndirectionTests.Build(2, "<< /Kids 5 0 R >>", (5, "[6 0 R]"), (6, "<< /Nums [0 << /S /D /St 10 >>] >>")),
        "key" => LabelIndirectionTests.Build(2, "<< /Nums [5 0 R << /S /D /St 10 >>] >>", (5, "0")),
        "entry" => LabelIndirectionTests.Build(2, "<< /Nums [0 5 0 R] >>", (5, "<< /S /D /St 10 >>")),
        "start" => LabelIndirectionTests.Build(2, "<< /Nums [0 << /S /D /St 5 0 R >>] >>", (5, "10")),
        _ => LabelIndirectionTests.Build(2, "<< /Nums [0 << /S /D /St 10 >>] >>"),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task IndirectTreeValuesMatchDirectSemantics(string form, string operation, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(form), QpdfInputOwnership.Transfer);
        if (operation != "inspect")
        { await LabelIndirectionTests.Select(host, route, operation == "merge"); }
        else if (route == "typed")
        { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(2, ["pages", "pagelabels"]), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=pages", "--json-key=pagelabels", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","json":"2","jsonKey":["pages","pagelabels"]}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        if (operation != "inspect")
        {
            host.AddInput(QpdfPath.From("readback"), bytes, QpdfInputOwnership.Transfer);
            QpdfJsonResult report = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("readback"), QpdfPath.From("report"), new(2, ["pages", "pagelabels"]), CancellationToken.None);
            bytes = System.Text.Encoding.UTF8.GetBytes(report.Json);
        }
        host.Dispose(); Assert.Equal(0L, host.OpenLeaseCount);
        using JsonDocument json = JsonDocument.Parse(bytes);
        JsonElement labels = json.RootElement.GetProperty("pagelabels"); Assert.Equal(1, labels.GetArrayLength());
        long start = operation == "select" ? 11 : 10;
        Assert.Equal(start, labels[0].GetProperty("label").GetProperty("/St").GetInt64());
        foreach (JsonElement page in json.RootElement.GetProperty("pages").EnumerateArray())
        { Assert.Equal(start, page.GetProperty("label").GetProperty("/St").GetInt64()); }
    }

    [Theory]
    [InlineData("odd")]
    [InlineData("key")]
    [InlineData("cycle")]
    public async Task MalformedIndirectTreesFailBeforePublication(string defect)
    {
        byte[] input = defect switch
        {
            "odd" => LabelIndirectionTests.Build(2, "<< /Nums 5 0 R >>", (5, "[0]")),
            "key" => LabelIndirectionTests.Build(2, "<< /Nums [5 0 R << >>] >>", (5, "/bad")),
            _ => LabelIndirectionTests.Build(2, "<< /Kids 5 0 R >>", (5, "[6 0 R]"), (6, "<< /Kids 5 0 R >>")),
        };
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await seed.WriteAsync(new byte[] { 17, 23 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--json", "--json-key=pagelabels", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal(new byte[] { 17, 23 }, prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("depth")]
    [InlineData("memory")]
    [InlineData("caller-cancel")]
    [InlineData("governor-cancel")]
    public async Task IndirectTraversalKeepsBoundsAndRetiresScratch(string outcome)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), LabelIndirectionTests.Build(2, "<< /Kids [5 0 R] >>", (5, "<< /Kids 6 0 R >>"), (6, "[7 0 R]"), (7, "<< /Nums [0 << /St 10 >>] >>")), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        using CancellationTokenSource canceled = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = outcome == "depth" ? 1 : QpdfLimits.Default.MaxNestingDepth, MaxMemoryBytes = outcome == "memory" ? 128 : QpdfLimits.Default.MaxMemoryBytes };
        ResourceGovernor governor = new(limits, outcome == "governor-cancel" ? canceled.Token : CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            canceled.Cancel();
            void Read() => DocumentJson.ReadLabelTree(document.Engine, limits, governor, memory, outcome == "caller-cancel" ? canceled.Token : CancellationToken.None);
            if (outcome.EndsWith("cancel", StringComparison.Ordinal)) { Assert.ThrowsAny<OperationCanceledException>(Read); }
            else if (outcome == "depth") { Assert.Throws<QpdfParseException>(Read); }
            else { Assert.Throws<QpdfLimitException>(Read); }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

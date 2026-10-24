using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LabelIndirectionTests
{
    internal static byte[] Build(int pages, string tree, params (int Number, string Body)[] extra)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /PageLabels " + tree + " >>", null),
            (2, "<< /Type /Pages /Count " + pages + " /Kids [" + string.Join(" ", Enumerable.Range(3, pages).Select(n => n + " 0 R")) + "] >>", null),
        ];
        for (int i = 0; i < pages; i++)
        { objects.Add((i + 3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> >>", null)); }
        foreach (var item in extra) { objects.Add((item.Number, item.Body, null)); }
        HashSet<int> numbers = objects.Select(item => item.Number).ToHashSet();
        for (int number = 1, last = numbers.Max(); number <= last; number++)
        { if (!numbers.Contains(number)) { objects.Add((number, "null", null)); } }
        return Inspection.CheckTests.Assemble(objects, 1);
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    internal static PdfArray Numbers(QpdfDocument document)
    {
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject raw));
        PdfDictionary tree = Assert.IsType<PdfDictionary>(Resolve(document, raw));
        Assert.True(tree.TryGet(PdfName.FromAscii("Nums"), out QpdfObject numbers));
        return Assert.IsType<PdfArray>(Resolve(document, numbers));
    }

    private static async Task Select(InMemoryQpdfHost host, string route, bool merge)
    {
        if (route == "typed")
        {
            if (merge)
            { await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages([1, 2]))], QpdfPath.From("output"), CancellationToken.None); }
            else
            { await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), PdfPageSelectionOptions.FromPages([2]), CancellationToken.None); }
            return;
        }
        string range = merge ? "1-2" : "2";
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", range, "--", "output"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"pages\":[{\"file\":\".\",\"range\":\"" + range + "\"}]}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
    }

    [Theory]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("typed", true)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task IndirectStartPreservesSelectionNumbering(string route, bool merge)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Build(2, "<< /Nums [0 << /S /D /St 5 0 R >>] >>", (5, "10")), QpdfInputOwnership.Transfer);
        await Select(host, route, merge);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("readback"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        PdfArray numbers = Numbers(document); Assert.Equal(2, numbers.Count);
        PdfDictionary label = Assert.IsType<PdfDictionary>(numbers[1]);
        Assert.True(label.TryGet(PdfName.FromAscii("St"), out QpdfObject start));
        Assert.Equal(merge ? 10L : 11L, Assert.IsType<PdfInteger>(start).Value);
    }

    [Theory]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("typed", true)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task MergeKeepsDifferentSourcePrefixes(string route, bool direct)
    {
        using InMemoryQpdfHost host = new();
        foreach ((string path, string prefix, int start) in new[] { ("input", "A-", 10), ("other", "B-", 12) })
        {
            byte[] data = direct
                ? Build(2, "<< /Nums [0 << /S /D /P (" + prefix + ") /St " + start + " >>] >>")
                : Build(2, "<< /Nums [0 << /S 5 0 R /P 6 0 R /St " + start + " >>] >>", (5, "/D"), (6, "(" + prefix + ")"));
            host.AddInput(QpdfPath.From(path), data, QpdfInputOwnership.Transfer);
        }
        if (route == "typed")
        { await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages([1, 2])), new(QpdfPath.From("other"), PdfPageSelectionOptions.FromPages([1, 2]))], QpdfPath.From("output"), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1-2", "other", "1-2", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1-2"},{"file":"other","range":"1-2"}]}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("readback"), bytes, QpdfInputOwnership.Transfer);
        PdfArray retained;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            retained = Numbers(document); Assert.Equal(4, retained.Count);
            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(i * 2L, Assert.IsType<PdfInteger>(retained[i * 2]).Value);
                PdfDictionary label = Assert.IsType<PdfDictionary>(retained[i * 2 + 1]);
                Assert.True(label.TryGet(PdfName.FromAscii("P"), out QpdfObject prefix));
                Assert.Equal(i == 0 ? "A-" : "B-", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Resolve(document, prefix)).ValueMemory.Span));
                Assert.True(label.TryGet(PdfName.FromAscii("St"), out QpdfObject start));
                Assert.Equal(i == 0 ? 10L : 12L, Assert.IsType<PdfInteger>(start).Value);
            }
        }
        host.Dispose(); Assert.Equal(0, host.OpenLeaseCount);
        Assert.Equal(4, retained.Count); Assert.IsType<PdfDictionary>(retained[3]);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task RenumberedReferencesCompressOneContinuousRun(string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Build(128, "<< /Nums [0 << /S 900 0 R /P 901 0 R /St 10 >>] >>", (900, "/D"), (901, "(P-)")), QpdfInputOwnership.Transfer);
        if (route == "typed")
        { await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages(Enumerable.Range(1, 128)))], QpdfPath.From("output"), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1-128", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1-128"}]}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("readback"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        Assert.Equal(2, Numbers(document).Count);
    }
}

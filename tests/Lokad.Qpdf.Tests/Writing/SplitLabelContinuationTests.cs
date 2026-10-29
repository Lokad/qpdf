using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class SplitLabelContinuationTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string fixture in new[] { "default", "start", "transition", "boundary", "prefix", "indirect", "unlabelled" })
        foreach (string selection in new[] { "all", "subset", "reordered", "duplicates" })
        foreach (int size in new[] { 1, 2, 3 })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [fixture, selection, size, route]; }
    }

    private static async Task<QpdfExecutionResult?> Run(InMemoryQpdfHost host, string route, int size, int[] pages)
    {
        if (route == "typed")
        {
            PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host,
                [new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages(pages))], QpdfPath.From("output"),
                new PdfPageMergeOptions { SplitPages = size }, CancellationToken.None);
            Assert.Empty(result.Warnings); return null;
        }
        string range = string.Join(',', pages);
        return route == "argv"
            ? await Qpdf.ExecuteAsync(host, new(["input", "--split-pages=" + size, "--pages", ".", range, "--", "output"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output",
                splitPages = size.ToString(System.Globalization.CultureInfo.InvariantCulture), pages = new[] { new { file = ".", range } } }), CancellationToken.None);
    }

    private static (string? Style, string? Prefix, long Start) Expected(string fixture, int page) => fixture switch
    {
        "default" => ("D", null, page),
        "start" or "indirect" => ("D", "P-", page + 4L),
        "transition" when page <= 3 => ("D", "A-", page + 4L),
        "transition" => ("R", "B-", page + 5L),
        "boundary" when page <= 2 => ("D", null, page + 4L),
        "boundary" when page <= 4 => ("R", null, page + 6L),
        "boundary" => ("a", null, page - 2L),
        "prefix" => (null, "Prefix-", page),
        _ => throw new InvalidOperationException("Unknown label fixture.")
    };

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static QpdfObject? Value(QpdfDocument document, PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject raw) ? Resolve(document, raw) : null;

    private static PdfArray Numbers(QpdfDocument document)
    {
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        PdfDictionary labels = Assert.IsType<PdfDictionary>(Value(document, catalog, "PageLabels"));
        return Assert.IsType<PdfArray>(Value(document, labels, "Nums"));
    }

    private static string Path(int first, int last) => "output-" + first + (first == last ? "" : "-" + last);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EverySplitKeepsTheSelectedPagesEffectiveLabels(string fixture, string selection, int size, string route)
    {
        int[] pages = selection switch
        {
            "all" => [1, 2, 3, 4, 5, 6], "subset" => [2, 3, 4, 5],
            "reordered" => [6, 4, 2, 3, 5, 1], _ => [2, 3, 3, 4, 5]
        };
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures",
            "labels-split-" + fixture + ".pdf")), QpdfInputOwnership.Transfer);
        QpdfExecutionResult? result = await Run(host, route, size, pages);
        if (result is not null) { Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); }
        List<(PdfName? Style, PdfByteString? Prefix, string? ExpectedStyle, string? ExpectedPrefix)> escaped = [];
        for (int first = 1; first <= pages.Length; first += size)
        {
            int last = Math.Min(pages.Length, first + size - 1);
            Assert.True(host.TryGetOutput(QpdfPath.From(Path(first, last)), out byte[] bytes));
            host.AddInput(QpdfPath.From("reread"), bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread"), CancellationToken.None);
            Assert.Equal(last - first + 1, document.PageReferences.Count);
            for (int page = first; page <= last; page++)
            {
                PdfDictionary actual = document.PageReferences[page - first].Page;
                Assert.Equal(pages[page - 1], Assert.IsType<PdfInteger>(Value(document, actual, "ReviewPage")).Value);
            }
            if (fixture == "unlabelled")
            {
                Assert.Null(Value(document, Assert.IsType<PdfDictionary>(document.GetObject(1)), "PageLabels")); continue;
            }
            PdfArray numbers = Numbers(document); Assert.Equal(0L, Assert.IsType<PdfInteger>(numbers[0]).Value);
            for (int index = 2; index < numbers.Count; index += 2)
            { Assert.True(Assert.IsType<PdfInteger>(numbers[index]).Value > Assert.IsType<PdfInteger>(numbers[index - 2]).Value); }
            for (int page = first; page <= last; page++)
            {
                int position = page - first, entry = 0;
                while (entry + 2 < numbers.Count && Assert.IsType<PdfInteger>(numbers[entry + 2]).Value <= position) { entry += 2; }
                int key = checked((int)Assert.IsType<PdfInteger>(numbers[entry]).Value);
                PdfDictionary label = Assert.IsType<PdfDictionary>(Resolve(document, numbers[entry + 1]));
                var expected = Expected(fixture, pages[page - 1]);
                Assert.Equal(expected.Start, Assert.IsType<PdfInteger>(Value(document, label, "St")).Value + position - key);
                escaped.Add((Value(document, label, "S") as PdfName, Value(document, label, "P") as PdfByteString, expected.Style, expected.Prefix));
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount); host.Dispose();
        foreach (var value in escaped)
        {
            if (value.ExpectedStyle is null) { Assert.Null(value.Style); }
            else { Assert.NotNull(value.Style); Assert.Equal(Encoding.ASCII.GetBytes(value.ExpectedStyle), value.Style.RawBytes); }
            if (value.ExpectedPrefix is null) { Assert.Null(value.Prefix); }
            else { Assert.NotNull(value.Prefix); Assert.Equal(Encoding.ASCII.GetBytes(value.ExpectedPrefix), value.Prefix.Value); }
        }
    }

    private static byte[] Fixture(string tree)
    {
        var objects = Enumerable.Range(3, 6).Select(number => (number,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>", (byte[]?)null)).ToList();
        objects.Insert(0, (2, "<< /Type /Pages /Count 6 /Kids [3 0 R 4 0 R 5 0 R 6 0 R 7 0 R 8 0 R] >>", null));
        objects.Insert(0, (1, "<< /Type /Catalog /Pages 2 0 R /PageLabels 9 0 R >>", null));
        objects.Add((9, "<< " + tree + " >>", null));
        return CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData("typed", "(bad)")]
    [InlineData("argv", "(bad)")]
    [InlineData("job", "(bad)")]
    [InlineData("typed", "1.5")]
    [InlineData("argv", "1.5")]
    [InlineData("job", "1.5")]
    public async Task MistypedStartsKeepTheirDefaultWhenSplittingInsideARun(string route, string value)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Nums [0 << /S /D /St " + value + " >>]"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult? result = await Run(host, route, 1, [2, 3]);
        if (result is not null) { Assert.Equal(QpdfExitCode.Success, result.ExitCode); }
        Assert.True(host.TryGetOutput(QpdfPath.From("output-2"), out byte[] bytes));
        host.AddInput(QpdfPath.From("reread"), bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread"), CancellationToken.None))
        {
            PdfDictionary label = Assert.IsType<PdfDictionary>(Numbers(document)[1]);
            Assert.Equal(3L, Assert.IsType<PdfInteger>(Value(document, label, "St")).Value);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task LargeStartsAdvanceByTheLocalOffsetWithoutIntermediateOverflow(string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Nums [0 << /S /D >> 3 << /S /R /St 9223372036854775806 >>]"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult? result = await Run(host, route, 1, [1, 2, 3, 4, 5]);
        if (result is not null) { Assert.Equal(QpdfExitCode.Success, result.ExitCode); }
        Assert.True(host.TryGetOutput(QpdfPath.From("output-5"), out byte[] bytes));
        host.AddInput(QpdfPath.From("reread"), bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread"), CancellationToken.None))
        {
            PdfDictionary label = Assert.IsType<PdfDictionary>(Numbers(document)[1]);
            Assert.Equal(long.MaxValue, Assert.IsType<PdfInteger>(Value(document, label, "St")).Value);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("typed", true)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task RealOverflowAndCyclicTreesFailBeforeAnySplitIsPublished(string route, bool cycle)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(cycle ? "/Kids [9 0 R]" : "/Nums [0 << /S /D /St 9223372036854775807 >>]"), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output-1"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        if (route == "typed") { await Assert.ThrowsAsync<QpdfParseException>(() => Run(host, route, 1, [1, 2])); }
        else { QpdfExecutionResult? result = await Run(host, route, 1, [1, 2]); Assert.NotNull(result); Assert.Equal(QpdfExecutionStatus.Failed, result.Status); }
        Assert.True(host.TryGetOutput(QpdfPath.From("output-1"), out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.False(host.TryGetOutput(QpdfPath.From("output-2"), out _)); Assert.Equal(0L, host.OpenLeaseCount);
    }
}

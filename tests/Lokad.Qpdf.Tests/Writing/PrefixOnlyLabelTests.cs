using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PrefixOnlyLabelTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach ((string style, string route, string order, int size) in new[]
        {
            ("absent", "typed", "second", 0), ("absent", "argv", "all", 0),
            ("absent", "job", "reverse", 2), ("null", "typed", "repeat", 3),
            ("null", "argv", "repeat", 0), ("null", "job", "second", 0),
        })
        foreach (bool maximum in new[] { false, true }) { yield return [style, route, order, size, maximum]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PrefixOnlyRunsIgnoreStartsAndKeepIndependentAliases(string style, string route, string order, int size, bool maximum)
    {
        RecordingMemoryBudget budget = new(32L * 1048576L);
        using (IMemoryScope owner = budget.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        {
            host.AddInput(QpdfPath.From("input"), Source(style, maximum, false), QpdfInputOwnership.Transfer);
            await Run(host, route, order, size, false);
            int count = order == "second" ? 1 : 4;
            PdfByteString? escaped = null;
            for (int first = 1; first <= count; first += size == 0 ? count : size)
            {
                int last = size == 0 ? count : Math.Min(count, first + size - 1);
                string path = size == 0 ? "output" : "output-" + first + (first == last ? "" : "-" + last);
                Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] output));
                host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
                PdfArray numbers = LabelIndirectionTests.Numbers(document);
                Assert.Equal(2, numbers.Count);
                PdfDictionary label = Assert.IsType<PdfDictionary>(numbers[1]);
                Assert.False(label.TryGet(PdfName.FromAscii("S"), out _));
                Assert.False(label.TryGet(PdfName.FromAscii("St"), out _));
                escaped = Assert.IsType<PdfByteString>(Read(document, label, "P"));
                Assert.Equal("Fixed"u8.ToArray(), escaped.Value);
                foreach (var page in document.PageReferences)
                {
                    Assert.Equal(maximum ? long.MaxValue : 7L, Assert.IsType<PdfInteger>(Read(document, page.Page, "PrivateCounter")).Value);
                    Assert.Same(escaped, Read(document, page.Page, "PrivatePrefix"));
                }
            }
            host.Dispose(); Assert.Equal(0L, host.OpenLeaseCount);
            Assert.Equal("Fixed"u8.ToArray(), Assert.IsType<PdfByteString>(escaped).Value);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task AuthoredNoNumberStyleOmitsInactiveStartsAcrossSplitBoundaries(string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Source("numbered", false, false), QpdfInputOwnership.Transfer);
        await Run(host, route, "all", 2, true);
        foreach (string path in new[] { "output-1-2", "output-3-4" })
        {
            Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] output));
            host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
            PdfArray numbers = LabelIndirectionTests.Numbers(document); Assert.Equal(2, numbers.Count);
            PdfDictionary label = Assert.IsType<PdfDictionary>(numbers[1]);
            Assert.False(label.TryGet(PdfName.FromAscii("S"), out _)); Assert.False(label.TryGet(PdfName.FromAscii("St"), out _));
            Assert.Equal("Fixed"u8.ToArray(), Assert.IsType<PdfByteString>(Read(document, label, "P")).Value);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task NumberedStylesStillRejectMeaningfulCounterOverflowBeforePublication(string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Source("numbered", true, false), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        if (route == "typed")
        { await Assert.ThrowsAsync<QpdfParseException>(async () => await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("input"), new PdfPageSelectionOptions(2))], QpdfPath.From("output"), CancellationToken.None)); }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "2", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"2"}]}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task DifferentInactiveStartsAcrossClosedSourcesShareOnePrefixOnlyRun()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Source("absent", false, false), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("other"), Source("null", true, false), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "4", "other", "4", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        Assert.Equal(2, LabelIndirectionTests.Numbers(document).Count);
        Assert.Equal(7L, Assert.IsType<PdfInteger>(Read(document, document.PageReferences[0].Page, "PrivateCounter")).Value);
        Assert.Equal(long.MaxValue, Assert.IsType<PdfInteger>(Read(document, document.PageReferences[1].Page, "PrivateCounter")).Value);
    }

    [Theory]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("typed", true)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task OnlyNumberedLabelsInterpretAnOtherwiseUnusedMalformedStart(string route, bool numbered)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Source(numbered ? "numbered" : "null", false, true), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        if (route == "typed")
        {
            Task operation = Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("input"), new PdfPageSelectionOptions(1))],
                QpdfPath.From("output"), CancellationToken.None).AsTask();
            if (numbered) { await Assert.ThrowsAsync<QpdfParseException>(() => operation); }
            else { await operation; }
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--suppress-recovery", "--pages", ".", "1", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","suppressRecovery":true,"pages":[{"file":".","range":"1"}]}""", CancellationToken.None);
            Assert.Equal(numbered ? QpdfExitCode.Error : QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        if (numbered) { Assert.Equal("prior"u8.ToArray(), output); }
        else
        {
            host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
            PdfDictionary label = Assert.IsType<PdfDictionary>(LabelIndirectionTests.Numbers(document)[1]);
            Assert.False(label.TryGet(PdfName.FromAscii("St"), out _));
            Assert.Equal("Fixed"u8.ToArray(), Assert.IsType<PdfByteString>(Read(document, label, "P")).Value);
            Assert.Equal(7L, Assert.IsType<PdfInteger>(Read(document, document.PageReferences[0].Page, "PrivateCounter")).Value);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static async Task Run(InMemoryQpdfHost host, string route, string order, int size, bool authored)
    {
        int[] pages = order switch { "second" => [2], "reverse" => [4, 3, 2, 1], "repeat" => [1, 1, 1, 1], _ => [1, 2, 3, 4] };
        if (route == "typed")
        {
            await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages(pages))], QpdfPath.From("output"),
                new PdfPageMergeOptions { SplitPages = size == 0 ? null : size, Labels = authored ? [new(1, PdfPageLabelStyle.None, 17, "Fixed")] : [] }, CancellationToken.None);
            return;
        }
        string range = string.Join(',', pages);
        QpdfExecutionResult result;
        if (route == "job")
        {
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["pages"] = new[] { new { file = ".", range } } };
            if (size != 0) { settings["splitPages"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            if (authored) { settings["setPageLabels"] = new[] { "1:/17/Fixed" }; }
            result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None);
        }
        else
        {
            List<string> arguments = ["input", "--pages", ".", range, "--"];
            if (size != 0) { arguments.Add("--split-pages=" + size); }
            if (authored) { arguments.AddRange(["--set-page-labels", "1:/17/Fixed", "--"]); }
            arguments.Add("output"); result = await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        }
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
    }

    private static byte[] Source(string style, bool maximum, bool malformedStart)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /PageLabels 10 0 R >>", null),
            (2, "<< /Type /Pages /Count 4 /Kids [3 0 R 4 0 R 5 0 R 6 0 R] >>", null),
        ];
        for (int page = 3; page <= 6; page++)
        { objects.Add((page, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /PrivateCounter 13 0 R /PrivatePrefix 12 0 R >>", null)); }
        objects.AddRange([(7, "null", null), (8, "null", null), (9, "null", null), (10, "<< /Nums [0 11 0 R] >>", null),
            (11, "<< /P 12 0 R /St " + (malformedStart ? "15" : "13") + " 0 R" + (style == "absent" ? "" : " /S 14 0 R") + " >>", null),
            (12, "(Fixed)", null), (13, maximum ? "9223372036854775807" : "7", null), (14, style == "numbered" ? "/D" : "null", null)]);
        if (malformedStart) { objects.Add((15, "(unterminated", null)); }
        return CheckTests.Assemble(objects, 1);
    }

    private static QpdfObject Read(QpdfDocument document, PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(document, value); }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
}

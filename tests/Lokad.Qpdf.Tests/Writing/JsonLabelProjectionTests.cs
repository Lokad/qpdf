using System.Text.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonLabelProjectionTests
{
    private static byte[] Fixture(string labels)
        => Fixture(labels, 3, true);

    private static byte[] Fixture(string labels, int pageCount, bool hasTree)
        => PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + (hasTree ? "/PageLabels << /Nums [" + labels + "] >>" : "") + " >>", null),
            (2, "<< /Type /Pages /Count " + pageCount + " /Kids [" + string.Join(' ', Enumerable.Range(3, pageCount).Select(n => n + " 0 R")) + "] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null),
            (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null),
            (6, "11", null), (7, "/D", null), (8, "(A)", null), (9, "null", null)], 1,
            new Dictionary<int, int> { [6] = 7, [7] = 7, [8] = 7, [9] = 7 });

    public static IEnumerable<object[]> Cases()
    {
        yield return ["0 << /S /D >>", new long?[] { 1, 2, 3 }, new long[] { 1 }];
        yield return ["0 << /S /D /St 7 /P (A) >>", new long?[] { 7, 8, 9 }, new long[] { 7 }];
        yield return ["0 << /P (prefix) /St 99 >>", new long?[] { 99, 100, 101 }, new long[] { 99 }];
        yield return ["0 << /S /r /St 4 >> 2 << /S /D /St 20 /P (B) >>", new long?[] { 4, 5, 20 }, new long[] { 4, 20 }];
        yield return ["1 << /S /D /St 4 >>", new long?[] { null, 4, 5 }, new long[] { 1, 4 }];
        yield return ["0 << /S /A /St 6 7 R >>", new long?[] { 11, 12, 13 }, new long[] { 11 }];
        yield return ["0 << /S 7 7 R /P 8 7 R /St 9 7 R /Private 42 >>", new long?[] { 1, 2, 3 }, new long[] { 1 }];
        yield return ["0 << /S 9 7 R /P 9 7 R /St /wrong >>", new long?[] { 1, 2, 3 }, new long[] { 1 }];
        yield return ["0 << /S /D /St 9223372036854775805 >>", new long?[] { long.MaxValue - 2, long.MaxValue - 1, long.MaxValue }, new long[] { long.MaxValue - 2 }];
        yield return ["0 << /S /D /St null >>", new long?[] { 1, 2, 3 }, new long[] { 1 }];
        yield return ["0 << /P <FEFF0041> /Private 42 >>", new long?[] { 1, 2, 3 }, new long[] { 1 }];
        yield return ["", new long?[] { null, null, null }, new long[] { 1 }];
        yield return ["8 << /S /D /St 5 >>", new long?[] { null, null, null }, new long[] { 1 }];
    }

    private static void AssertCounters(JsonElement report, IReadOnlyList<long?> counters, IReadOnlyList<long> starts)
    {
        JsonElement pages = report.GetProperty("pages"), labels = report.GetProperty("pagelabels");
        Assert.Equal(counters.Count, pages.GetArrayLength()); Assert.Equal(starts.Count, labels.GetArrayLength());
        for (int i = 0; i < counters.Count; i++)
        {
            JsonElement label = pages[i].GetProperty("label");
            if (counters[i] is long expected) { Assert.Equal(expected, label.GetProperty("/St").GetInt64()); }
            else { Assert.Equal(JsonValueKind.Null, label.ValueKind); }
        }
        for (int i = 0; i < starts.Count; i++) { Assert.Equal(starts[i], labels[i].GetProperty("label").GetProperty("/St").GetInt64()); }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PageCountersAdvanceWithinRunsWhileExplicitEntriesKeepTheirStarts(string labels, long?[] counters, long[] starts)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(labels), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), new QpdfJsonOptions(2, ["pages", "pagelabels"]), CancellationToken.None);
        using JsonDocument parsed = JsonDocument.Parse(result.Json); AssertCounters(parsed.RootElement, counters, starts);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ProjectionKeepsLiveFieldPairsAndLeavesOriginalDictionariesUntouched()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("0 << /S 7 7 R /P 8 7 R /St 9 7 R /Private 42 >>"), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), new QpdfJsonOptions(2, ["pages", "pagelabels", "qpdf"]), CancellationToken.None);
        using JsonDocument parsed = JsonDocument.Parse(result.Json);
        foreach (JsonElement page in parsed.RootElement.GetProperty("pages").EnumerateArray())
        {
            JsonElement label = page.GetProperty("label");
            Assert.Equal("7 7 R", label.GetProperty("/S").GetString()); Assert.Equal("8 7 R", label.GetProperty("/P").GetString());
            Assert.False(label.TryGetProperty("/Private", out _));
        }
        JsonElement original = parsed.RootElement.GetProperty("qpdf")[1].GetProperty("obj:1 0 R").GetProperty("value").GetProperty("/PageLabels").GetProperty("/Nums")[1];
        Assert.Equal(42, original.GetProperty("/Private").GetInt32()); Assert.False(original.TryGetProperty("/St", out _));
        Assert.Equal(0L, host.OpenLeaseCount);
        await using QpdfDocument source = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject labels));
        PdfDictionary tree = Assert.IsType<PdfDictionary>(labels);
        Assert.True(tree.TryGet(PdfName.FromAscii("Nums"), out QpdfObject numbers));
        PdfDictionary stored = Assert.IsType<PdfDictionary>(Assert.IsType<PdfArray>(numbers)[1]);
        Assert.True(stored.TryGet(PdfName.FromAscii("St"), out QpdfObject start));
        Assert.Equal(new PdfReference(9, 7), start);
    }

    [Fact]
    public async Task BinaryPrefixBytesKeepTheExistingVersionTwoIdentityPolicy()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("0 << /P <FEFF0041> >>"), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), new QpdfJsonOptions(2, ["pages", "pagelabels"]), CancellationToken.None);
        using JsonDocument parsed = JsonDocument.Parse(result.Json);
        foreach (JsonElement page in parsed.RootElement.GetProperty("pages").EnumerateArray())
        { Assert.Equal("b:feff0041", page.GetProperty("label").GetProperty("/P").GetString()); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionOneAdaptersUseTheSameCounterProjection(bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("0 << /S /r /St 4 >> 2 << /S /D /St 20 >>"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","json":"1","jsonKey":["pages","pagelabels"]}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--json=1", "--json-key=pages", "--json-key=pagelabels"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using JsonDocument parsed = JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException());
        AssertCounters(parsed.RootElement, [4, 5, 20], [4, 20]); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CounterOverflowPreservesPriorReportsAndRefundsCallerStorage(bool typed)
    {
        using RecordingMemoryBudget root = new(64L * 1024L * 1024L);
        Assert.True(root.TryReserve(97L, out IMemoryReservation? priorOwner));
        using (IMemoryScope caller = root.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, caller))
        {
            host.AddInput(QpdfPath.From("input"), Fixture("0 << /S /D /St 9223372036854775807 >>"), QpdfInputOwnership.Transfer);
            await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("report"), CancellationToken.None))
            { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
            if (typed)
            {
                QpdfParseException failure = await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.GetDocumentJsonAsync(host,
                    QpdfPath.From("input"), QpdfPath.From("report"), new QpdfJsonOptions(2, ["pages"]), CancellationToken.None).AsTask());
                Assert.Contains("Page-label start overflows", failure.Message, StringComparison.Ordinal);
            }
            else
            {
                QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=pages", "--", "report"]), CancellationToken.None);
                Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
            }
            Assert.True(host.TryGetOutput(QpdfPath.From("report"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        Assert.Equal(97L, root.ReservedBytes); priorOwner.Dispose(); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ZeroPageLabelCollectionsDistinguishAbsentEmptyAndExplicitTrees(bool declared, bool explicitStart)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(explicitStart ? "0 << /S /D /St 5 >>" : "", 0, declared), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), new QpdfJsonOptions(2, ["pages", "pagelabels"]), CancellationToken.None);
        using JsonDocument parsed = JsonDocument.Parse(result.Json);
        Assert.Empty(parsed.RootElement.GetProperty("pages").EnumerateArray());
        JsonElement labels = parsed.RootElement.GetProperty("pagelabels"); Assert.Equal(declared ? 1 : 0, labels.GetArrayLength());
        if (declared) { Assert.Equal(0, labels[0].GetProperty("index").GetInt32()); Assert.Equal(explicitStart ? 5 : 1, labels[0].GetProperty("label").GetProperty("/St").GetInt32()); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

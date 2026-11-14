using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class CheckStreamReuseTests
{
    private static byte[] Document(byte[][] programs, int[][] pages, string[] resources)
    {
        int firstStream = pages.Length + 3;
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, $"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(' ', Enumerable.Range(3, pages.Length).Select(number => $"{number} 0 R"))}] >>", null)];
        for (int index = 0; index < pages.Length; index++)
        {
            string contents = string.Join(' ', pages[index].Select(stream => $"{firstStream + stream} 0 R"));
            if (pages[index].Length != 1) { contents = $"[{contents}]"; }
            objects.Add((3 + index, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources {resources[index]} /Contents {contents} >>", null));
        }
        for (int index = 0; index < programs.Length; index++)
        {
            using MemoryStream encoded = new();
            using (ZLibStream compressor = new(encoded, CompressionLevel.Fastest, true)) { compressor.Write(programs[index]); }
            byte[] data = encoded.ToArray();
            objects.Add((firstStream + index, $"<< /Filter /FlateDecode /Length {data.Length} >>", data));
        }
        return CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(64)]
    public async Task SharedContentDecodesOnceAcrossStreamAndPageChecks(int pages)
    {
        byte[] program = Encoding.ASCII.GetBytes("% " + new string('a', 65533) + "\n");
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("shared.pdf");
        host.AddInput(input, Document([program], Enumerable.Range(0, pages).Select(_ => new[] { 0 }).ToArray(), Enumerable.Repeat("<< >>", pages).ToArray()), QpdfInputOwnership.Transfer);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, input, new QpdfCheckOptions(),
            QpdfLimits.Default with { MaxDecodedBytes = program.Length }, CancellationToken.None);
        Assert.True(result.Passed); Assert.False(result.HasSkipped); Assert.Empty(result.Diagnostics);
        Assert.Equal(pages, result.PageCount); Assert.Equal(program.Length, result.Counters.TotalDecodedBytes);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandContentArraysBorrowDecodedBytesThroughJoining(bool job)
    {
        using InMemoryQpdfHost host = new();
        byte[] first = "q\n"u8.ToArray(), second = "Q\n"u8.ToArray();
        host.AddInput(QpdfPath.From("shared.pdf"), Document([first, second], [[0, 1, 0, 1], [0, 1]], ["<< >>", "<< >>"]), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"shared.pdf","check":""}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["shared.pdf", "--check"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(first.Length + second.Length, result.Counters.TotalDecodedBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("<< /ColorSpace << /C /DeviceRGB >> >>", QpdfCheckStatus.Failed)]
    [InlineData("<< >>", QpdfCheckStatus.Skipped)]
    public async Task SharedDecodedBytesStillUseEachPagesOwnResources(string secondResources, QpdfCheckStatus expected)
    {
        byte[] program = "BI /W 1 /H 1 /BPC 8 /CS /C ID A EI\n"u8.ToArray();
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("inline.pdf");
        host.AddInput(input, Document([program], [[0], [0]], ["<< /ColorSpace << /C /DeviceGray >> >>", secondResources]), QpdfInputOwnership.Transfer);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, input, CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(result.Checks, check => check.Name == "streams").Status);
        QpdfCheckOutcome content = Assert.Single(result.Checks, check => check.Name == "page-content");
        Assert.Equal(expected, content.Status);
        if (expected == QpdfCheckStatus.Failed) { Assert.Contains("Page 2 content:", content.Detail); Assert.DoesNotContain("Page 1 content:", content.Detail); }
        Assert.Equal(program.Length, result.Counters.TotalDecodedBytes); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ContentArraysLargerThanTheCacheKeepEveryChunkThroughValidation()
    {
        byte[][] programs = Enumerable.Range(0, 40).Select(index => Encoding.ASCII.GetBytes($"% {index:D2}" + new string('a', 65530) + "\n")).ToArray();
        int[] order = Enumerable.Range(0, programs.Length).ToArray();
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("large-array.pdf");
        host.AddInput(input, Document(programs, [order, order.Reverse().ToArray()], ["<< >>", "<< >>"]), QpdfInputOwnership.Transfer);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 16L << 20 };
        QpdfCheckResult result = await Qpdf.CheckAsync(host, input, new QpdfCheckOptions(), limits, CancellationToken.None);
        Assert.True(result.Passed); Assert.False(result.HasSkipped); Assert.Empty(result.Diagnostics);
        Assert.InRange(result.Counters.TotalDecodedBytes, programs.Sum(program => (long)program.Length), 3L * programs.Sum(program => (long)program.Length));
        Assert.True(result.Counters.PeakMemoryBytes <= limits.MaxMemoryBytes); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public void CurrentPageBorrowsPreventEvictionAndNewPayloadsStayWithTheirDecoder()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (CheckStreamCache cache = CheckStreamCache.Create(governor, QpdfLimits.Default, CancellationToken.None))
        {
            cache.BeginPage();
            List<PdfStream> streams = [];
            for (int index = 0; index < 32; index++)
            {
                PdfStream stream = new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), []); streams.Add(stream);
                using ParserMemoryScope decoder = ParserMemoryScope.Create(governor); decoder.Reserve(40L);
                Assert.True(cache.Remember(stream, new byte[8], decoder)); Assert.Equal(0L, decoder.ReservedBytes);
            }
            using ParserMemoryScope next = ParserMemoryScope.Create(governor); next.Reserve(40L);
            PdfStream extra = new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), []);
            long live = governor.Snapshot().LiveMemoryBytes;
            Assert.False(cache.Remember(extra, new byte[8], next)); Assert.Equal(40L, next.ReservedBytes);
            Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
            Assert.All(streams, stream => Assert.True(cache.TryBorrow(stream, out _)));
            cache.EndPage(); cache.BeginPage();
            Assert.True(cache.Remember(extra, new byte[8], next)); Assert.Equal(0L, next.ReservedBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RefusedCacheMetadataLeavesDecodedPayloadAdmittedUntilCleanup()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (CheckStreamCache cache = CheckStreamCache.Create(governor, limits, CancellationToken.None))
        using (ParserMemoryScope decoder = ParserMemoryScope.Create(governor))
        {
            decoder.Reserve(40L); byte[] bytes = new byte[8];
            long live = governor.Snapshot().LiveMemoryBytes;
            PdfStream stream = new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), []);
            Assert.Throws<QpdfLimitException>(() => cache.Remember(stream, bytes, decoder));
            Assert.Equal(40L, decoder.ReservedBytes); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

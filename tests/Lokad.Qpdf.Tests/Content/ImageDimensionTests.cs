using System.Globalization;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ImageDimensionTests
{
    [Theory]
    [InlineData(long.MaxValue, long.MaxValue, false)]
    [InlineData(long.MaxValue, long.MaxValue, true)]
    [InlineData(2147483648L, 256L, false)]
    [InlineData(256L, 2147483648L, true)]
    public async Task UnsupportedDimensionsStayOpaqueWithoutCallingCodec(long width, long height, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(width, height, false), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, QpdfLimits.Default, job);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            PdfDictionary page = document.PageReferences[0].Page;
            Assert.True(page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resources));
            Assert.True(Assert.IsType<PdfDictionary>(resources).TryGet(PdfName.FromAscii("XObject"), out QpdfObject xobjects));
            Assert.True(Assert.IsType<PdfDictionary>(xobjects).TryGet(PdfName.FromAscii("Im1"), out QpdfObject link));
            PdfStream image = Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(link).ObjectNumber));
            Assert.True(image.Dictionary.TryGet(PdfName.FromAscii("Width"), out QpdfObject actualWidth));
            Assert.Equal(width, Assert.IsType<PdfInteger>(actualWidth).Value);
            Assert.Equal("x"u8.ToArray(), image.Data);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(200L, 200L, false)]
    [InlineData(200L, 200L, true)]
    [InlineData(2147483647L, 2147483647L, false)]
    [InlineData(2147483647L, 2147483647L, true)]
    public async Task SampleLimitPrecedesDecodeAndCodecAndPreservesOutput(long width, long height, bool job)
    {
        RecordingMemoryBudget root = new(16777216L);
        using (IMemoryScope caller = root.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, caller))
        {
            host.AddInput(QpdfPath.From("input"), Fixture(width, height, true), QpdfInputOwnership.Copy);
            await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
            {
                await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
                await prior.CommitAsync(CancellationToken.None);
            }
            QpdfExecutionResult result = await Run(host, QpdfLimits.Default with { MaxDecodedBytes = 1000L }, job);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            QpdfDiagnostic failure = Assert.Single(result.Diagnostics);
            Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, failure.Code);
            Assert.Contains(nameof(QpdfLimits.MaxDecodedBytes), failure.Message, StringComparison.Ordinal);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            Assert.Equal("prior"u8.ToArray(), output);
            Assert.Equal(0, host.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(0, root.LiveReservations);
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, QpdfLimits limits, bool job)
        => job ? Qpdf.ExecuteJobAsync(host,
            "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"optimizeImages\":true,\"streamData\":\"preserve\"}", new QpdfExecutionOptions(limits), CancellationToken.None)
            : Qpdf.ExecuteAsync(host, new(["input", "--optimize-images", "--stream-data=preserve", "output"]), new QpdfExecutionOptions(limits), CancellationToken.None);

    private static byte[] Fixture(long width, long height, bool invalidFilter)
        => CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>", null),
            (4, "<< /Length 7 >>", "/Im1 Do"u8.ToArray()),
            (5, "<< /Type /XObject /Subtype /Image /Width " + width.ToString(CultureInfo.InvariantCulture)
                + " /Height " + height.ToString(CultureInfo.InvariantCulture) + " /BitsPerComponent 8 /ColorSpace /DeviceRGB"
                + (invalidFilter ? " /Filter /FlateDecode" : "") + " /Length 1 >>", "x"u8.ToArray()),
        ], 1);
}

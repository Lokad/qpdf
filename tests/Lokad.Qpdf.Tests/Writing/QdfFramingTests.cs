using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class QdfFramingTests
{
    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    public static IEnumerable<object[]> AmbiguousCases()
    {
        foreach (bool packed in new[] { false, true })
        foreach (string tail in new[] { "after\n", "endobj\nafter\n", "endobj\n% comment\n", "endobj\n\n" })
        { yield return [packed, false, tail]; }
        yield return [true, true, "after\n"];
        yield return [true, true, "endobj\nafter\n"];
    }

    [Theory]
    [MemberData(nameof(AmbiguousCases))]
    public async Task AmbiguousStreamFramingFailsBeforeCommit(bool packed, bool member, string tail)
    {
        string payload = "before\nendstream\n" + tail;
        byte[] input = member
            ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(QdfRepairFixtures.ObjectStream(3, 8))
                .Replace("/Resources << >> >>\n", "/Resources << >> /Audit (" + payload + ") >>\n", StringComparison.Ordinal))
            : await QdfRepairFixtures.Emit(QdfRepairFixtures.Image(Encoding.ASCII.GetBytes(payload)), packed);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer); await Seed(host);
        QpdfExecutionResult result = await Qpdf.FixQdfAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("QDF", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
    }

    public static IEnumerable<object[]> PayloadControls()
    {
        foreach (bool packed in new[] { false, true })
        foreach (string marker in new[] { "endobj", "stream", "999 0 obj", "%QDF: ignore_newline", "endstream suffix", "endstream\r" })
        { yield return [packed, marker]; }
    }

    [Theory]
    [MemberData(nameof(PayloadControls))]
    public async Task OtherMarkerShapedDataStillRepairsWithoutChangingPayload(bool packed, string marker)
    {
        byte[] payload = Encoding.ASCII.GetBytes("before\n" + marker + "\nafter\n");
        byte[] input = await QdfRepairFixtures.Emit(QdfRepairFixtures.Image(payload), packed);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.FixQdfAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("again"), CancellationToken.None))
        {
            Assert.Equal(1, document.PageCount);
            Assert.True(document.Engine.Trailer.TryGet(PdfName.FromAscii("Size"), out QpdfObject size));
            int images = 0;
            for (int number = 1; number < Assert.IsType<PdfInteger>(size).Value; number++)
            {
                if (document.GetObject(number) is PdfStream stream && stream.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject type)
                    && type.Equals(PdfName.FromAscii("Image")))
                { images++; Assert.Equal(payload, stream.Data); }
            }
            Assert.Equal(1, images);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

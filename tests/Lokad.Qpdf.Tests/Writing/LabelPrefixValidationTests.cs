using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LabelPrefixValidationTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (int offset in new[] { 0, 255, 256, 511, 512 })
        foreach (string kind in new[] { "high", "low", "reversed" }) { yield return [offset, kind]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TypedLabelsRejectInvalidUtf16BeforePublicationAndRefundPrivateStorage(int offset, string kind)
    {
        string invalid = kind switch { "high" => "\ud800", "low" => "\udc00", _ => "\udc00\ud800" };
        string prefix = new string('p', offset) + invalid + "tail";
        RecordingMemoryBudget root = new(16777216);
        using (IMemoryScope owner = root.CreateScope())
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
            host.AddInput(QpdfPath.From("input"), LabelIndirectionTests.Build(1, "<< >>"), QpdfInputOwnership.Transfer);
            await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
            { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
            long retained = root.ReservedBytes;
            QpdfParseException failure = await Assert.ThrowsAsync<QpdfParseException>(async () => await Qpdf.SelectPagesAsync(host,
                [new(QpdfPath.From("input"), new(1))], QpdfPath.From("output"),
                new PdfPageMergeOptions { Labels = [new(1, PdfPageLabelStyle.Decimal, 1, prefix)] }, CancellationToken.None));
            Assert.Contains("UTF-16", failure.Message, StringComparison.Ordinal);
            Assert.Equal(retained, root.ReservedBytes); Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}

using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class StreamInspectionStorageTests
{
    private static byte[] Input(byte[] payload, string filter) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>", null),
        (4, "<< /Length " + payload.Length + (filter.Length == 0 ? "" : " /Filter /" + filter) + " >>", payload),
    ], 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawInspectionBorrowsSourceAndChecksOutputBeforeCopying(bool refuse)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input(new byte[8 * 1024 * 1024], ""), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(4));
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L, MaxOutputBytes = refuse ? 1L : 16777216L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        ShowObjectPlan plan = new(QpdfPath.From("input"), QpdfRecoveryMode.Strict, "4") { RawStreamData = true };
        long before = GC.GetAllocatedBytesForCurrentThread();
        if (refuse)
        {
            QpdfLimitException error = await Assert.ThrowsAsync<QpdfLimitException>(() =>
                Qpdf.ShowObjectAsync(host, document, plan, limits, governor, new(governor), memory, CancellationToken.None).AsTask());
            Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), error.LimitName);
        }
        else
        {
            ReadOnlyMemory<byte>? bytes = await Qpdf.ShowObjectAsync(host, document, plan, limits, governor, new(governor), memory, CancellationToken.None);
            Assert.Equal(stream.RawMemory, bytes);
        }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < stream.RawMemory.Length);
        Assert.Equal(0L, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilteredAndNormalizedBytesRemainFundedThroughConsumption(bool normalize)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input("7120202051>"u8.ToArray(), "ASCIIHexDecode"), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            ReadOnlyMemory<byte>? bytes = await Qpdf.ShowObjectAsync(host, document,
                new(QpdfPath.From("input"), QpdfRecoveryMode.Strict, "4") { FilteredStreamData = true, NormalizeContent = normalize },
                QpdfLimits.Default, governor, new(governor), memory, CancellationToken.None);
            Assert.NotNull(bytes); Assert.True(memory.ReservedBytes >= bytes.Value.Length + 32L);
            Assert.Contains((byte)'q', bytes.Value.ToArray()); Assert.Contains((byte)'Q', bytes.Value.ToArray());
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task DecodeRefusalReturnsOnlyNewStorage()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input(System.Text.Encoding.ASCII.GetBytes(new string('0', 32768) + ">"), "ASCIIHexDecode"), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(71L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        await Assert.ThrowsAsync<QpdfLimitException>(() => Qpdf.ShowObjectAsync(host, document,
            new(QpdfPath.From("input"), QpdfRecoveryMode.Strict, "4") { FilteredStreamData = true },
            limits, governor, new(governor), memory, CancellationToken.None).AsTask());
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }
}

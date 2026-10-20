using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedHintEncoderOwnershipTests
{
    private static HintPayload Encode(ResourceGovernor governor, QpdfLimits limits, CancellationToken token)
        => new LinearizedWriter(limits, governor).EncodeHintTablesOwned(3, [3, 2], [400, 200],
            [[], [2]], [100, 150, 150], 123L, 0, 0L, 3, 0, 0L, 0, 0L, out _, out _, token);

    [Fact]
    public void RepeatedScopedEncodesRefundTheirStorage()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        for (int cycle = 0; cycle < 64; cycle++)
        {
            using (HintPayload payload = Encode(governor, limits, CancellationToken.None))
            { Assert.Equal(71, payload.Data.Length); Assert.True(governor.Snapshot().LiveMemoryBytes >= payload.Data.Length); }
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
    }

    [Fact]
    public void RetainedPayloadStaysFundedAndUnchangedAcrossLaterEncodes()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using HintPayload kept = Encode(governor, limits, CancellationToken.None);
        byte[] expected = kept.Data.ToArray();
        using (HintPayload next = Encode(governor, limits, CancellationToken.None)) { Assert.Equal(expected, next.Data.ToArray()); }
        Assert.Equal(expected, kept.Data.ToArray());
        Assert.True(governor.Snapshot().LiveMemoryBytes >= kept.Data.Length);
        kept.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => kept.Data);
    }

    [Fact]
    public void FailedFinalAdmissionRetiresItsAttempt()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => Encode(governor, limits, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherCancellationTokenRetiresTheEncoder(bool governorToken)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(limits, governorToken ? cancellation.Token : CancellationToken.None);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Encode(governor, limits,
            governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

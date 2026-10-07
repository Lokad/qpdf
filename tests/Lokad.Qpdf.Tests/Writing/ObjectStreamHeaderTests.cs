using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ObjectStreamHeaderTests
{
    private static byte[] Expected(bool compressed, bool qdf) => System.Text.Encoding.ASCII.GetBytes(qdf
        ? "7 0 obj\n<<\n  /Type /ObjStm\n  /Length 123\n  /N 2\n  /First 9\n>>\nstream\n"
        : "7 0 obj\n<< /Type /ObjStm /Length 123" + (compressed ? " /Filter /FlateDecode" : "") + " /N 2 /First 9 >>\nstream\n");

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ExactBytesAreFundedUntilDisposal(bool compressed, bool qdf)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] expected = Expected(compressed, qdf);
        ObjectStreamHeader header = ObjectStreamHeader.Create(7, 2, 123, 9, compressed, qdf, QpdfLimits.Default, governor, CancellationToken.None);
        Assert.Equal(expected, header.Data.ToArray());
        Assert.Equal(160L + expected.Length, governor.Snapshot().LiveMemoryBytes);
        long literals = expected.Length - 6L;
        Assert.Equal(80L + literals + 2L * expected.Length, governor.Snapshot().TotalWork);
        header.Dispose(); header.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => header.Data);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void MaximumFieldsUseInvariantDecimalBytes(bool compressed, bool qdf)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using ObjectStreamHeader header = ObjectStreamHeader.Create(int.MaxValue, 100, long.MaxValue, long.MaxValue,
                compressed, qdf, QpdfLimits.Default, governor, CancellationToken.None);
            string result = System.Text.Encoding.ASCII.GetString(header.Data.Span);
            Assert.StartsWith("2147483647 0 obj\n", result);
            Assert.Contains("/Length 9223372036854775807", result);
            Assert.Contains("/First 9223372036854775807", result);
            Assert.Contains("/N 100", result);
            Assert.Equal(compressed, result.Contains("/FlateDecode", StringComparison.Ordinal));
            Assert.All(header.Data.ToArray(), value => Assert.InRange(value, (byte)0, (byte)127));
            Assert.True(header.Data.Length < 256);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OneByteShortAdmissionPreservesPriorOwnerAndCanRetry(bool compressed, bool qdf)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 8192L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor);
        prior.Reserve(97L);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - (160L + Expected(compressed, qdf).Length - 1L));
        long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => ObjectStreamHeader.Create(7, 2, 123, 9,
            compressed, qdf, limits, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(97L, prior.ReservedBytes);
        filler.Dispose();
        using (ObjectStreamHeader retry = ObjectStreamHeader.Create(7, 2, 123, 9, compressed, qdf, limits, governor, CancellationToken.None))
        {
            Assert.Equal(Expected(compressed, qdf), retry.Data.ToArray());
        }
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void WorkFailureAfterAdmissionRetiresOnlyTheAttempt(bool compressed, bool qdf)
    {
        int length = Expected(compressed, qdf).Length;
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 80L + length - 6L + length - 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor);
        prior.Reserve(97L);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => ObjectStreamHeader.Create(7, 2, 123, 9,
            compressed, qdf, limits, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes > 161L);
        Assert.Equal(80L + length - 6L, governor.Snapshot().TotalWork);
    }

    [Theory]
    [InlineData(0, 2, 123, 9, false, false)]
    [InlineData(7, 0, 123, 9, false, false)]
    [InlineData(7, 101, 123, 9, false, false)]
    [InlineData(7, 2, -1, 9, false, false)]
    [InlineData(7, 2, 123, -1, false, false)]
    [InlineData(7, 2, 123, 9, true, true)]
    public void InvalidFieldsDoNotAllocateOrCharge(int number, int count, long length, long first, bool compressed, bool qdf)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Throws<QpdfException>(() => ObjectStreamHeader.Create(number, count, length, first, compressed, qdf,
            QpdfLimits.Default, governor, CancellationToken.None));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().TotalWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherCancellationRoutePreservesPreparedAdmission(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor);
        prior.Reserve(97L);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ObjectStreamHeader.Create(7, 2, 123, 9, false, false,
            QpdfLimits.Default, governor, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().TotalWork);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OutputCeilingRejectsBeforeOwnedAllocation(bool compressed, bool qdf)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = Expected(compressed, qdf).Length - 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => ObjectStreamHeader.Create(7, 2, 123, 9,
            compressed, qdf, limits, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), failure.LimitName);
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Backing(ObjectStreamHeader owner)
        => new(typeof(ObjectStreamHeader).GetField("_bytes", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException("Expected header backing."));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RetainedDisposedWrapperDropsTheHeader(bool compressed, bool qdf)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        ObjectStreamHeader owner = ObjectStreamHeader.Create(7, 2, 123, 9, compressed, qdf, QpdfLimits.Default, governor, CancellationToken.None);
        WeakReference bytes = Backing(owner);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(bytes.IsAlive);
        owner.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(bytes.IsAlive);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(owner); GC.KeepAlive(governor);
    }

    [Theory]
    [InlineData(long.MinValue, "-9223372036854775808")]
    [InlineData(-1L, "-1")]
    [InlineData(0L, "0")]
    [InlineData(1L, "1")]
    [InlineData(long.MaxValue, "9223372036854775807")]
    public void SharedIntegerFormatterCoversAllDecimalExtremes(long value, string expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Span<byte> target = stackalloc byte[20];
        int written = WriterNumber.Format(value, target, governor);
        Assert.Equal(expected, System.Text.Encoding.ASCII.GetString(target.Slice(0, written)));
        Assert.Equal(20L, governor.Snapshot().TotalWork);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

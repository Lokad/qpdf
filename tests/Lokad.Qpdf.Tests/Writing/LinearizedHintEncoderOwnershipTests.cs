using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedHintEncoderOwnershipTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static byte[] Encode(ResourceGovernor governor, QpdfLimits limits, CancellationToken token)
    {
        object writer = Activator.CreateInstance(typeof(LinearizedWriter), Fields, null, new object[] { limits, governor }, null)
            ?? throw new InvalidOperationException();
        MethodInfo encode = typeof(LinearizedWriter).GetMethod("EncodeHintTables", Fields) ?? throw new InvalidOperationException();
        object?[] arguments = [3, new List<int> { 3, 2 }, new List<long> { 400, 200 },
            new List<List<int>> { new(), new() { 2 } }, new List<long> { 100, 150, 150 },
            123L, 0, 0L, 3, 0, 0L, 0, 0L, 0L, 0L, token];
        try { return (byte[])(encode.Invoke(writer, arguments) ?? throw new InvalidOperationException()); }
        catch (TargetInvocationException failure) when (failure.InnerException is QpdfException error) { throw error; }
        catch (TargetInvocationException failure) when (failure.InnerException is OperationCanceledException error) { throw error; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Emit(ResourceGovernor governor, QpdfLimits limits)
        => _ = Encode(governor, limits, CancellationToken.None);

    private static void Collect()
    {
        // Permit reclamation between bounded uses; neither compaction nor a
        // prescribed retirement round is part of the accounting contract.
        GC.Collect(2, GCCollectionMode.Forced, true, false);
        GC.WaitForPendingFinalizers();
    }

    [Fact]
    public void OriginalFourKiBEncoderCompletes64CallsWithoutDeadBodyCharges()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        for (int cycle = 0; cycle < 64; cycle++)
        {
            Emit(governor, limits);
            Collect();
        }
        Collect();
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 1L, 4096L);
        // The unchanged external probe snapshots before maintenance. This extra
        // control also exercises bounded job-thread retirement of its weak ledger.
        governor.ReserveMemory(0L);
        GC.KeepAlive(governor);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] Aliases(ResourceGovernor governor, QpdfLimits limits)
    {
        byte[] kept = Encode(governor, limits, CancellationToken.None);
        Emit(governor, limits);
        return kept;
    }

    [Fact]
    public void SurvivingBodyRemainsUsableAfterLaterEncodes()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        byte[] kept = Aliases(governor, limits);
        byte[] expected = (byte[])kept.Clone();
        Collect();
        governor.ReserveMemory(0L);
        Assert.Equal(71, kept.Length);
        Assert.Equal(expected, kept);
        GC.KeepAlive(kept);
    }

    [Fact]
    public void FailedFinalAdmissionRetiresItsAttempt()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => Encode(governor, limits, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
        Assert.True(governor.Snapshot().TotalWork > 0L);
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

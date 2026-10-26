using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class LzwDictionaryTests
{
    internal static List<(int Code, int Width)> RepeatedCodes(int count, bool early)
    {
        List<(int Code, int Width)> codes = [(256, 9), (65, 9)];
        int width = 9;
        for (int code = 258; code <= 256 + count; code++)
        {
            codes.Add((code, width));
            if (code + (early ? 1 : 0) is 511 or 1023 or 2047) { width++; }
        }
        codes.Add((257, width)); return codes;
    }

    public static IEnumerable<object[]> Boundaries()
    {
        foreach (bool early in new[] { false, true })
        foreach (int count in new[] { 1, 2, 252, 253, 254, 766, 767, 1790, 1791, 3839 })
        { yield return new object[] { early, count }; }
    }

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void KwKwKMatchesAcrossWidthBoundaries(bool early, int count)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] output = LzwFilter.Decode(LzwTests.PackCodes(RepeatedCodes(count, early)), early, QpdfLimits.Default, governor, CancellationToken.None, 0L);
        Assert.Equal(count * (count + 1) / 2, output.Length);
        Assert.True(output.AsSpan().IndexOfAnyExcept((byte)65) < 0);
        Assert.Equal(output.LongLength + 32, governor.Snapshot().LiveMemoryBytes);
        governor.ReleaseMemory(output.LongLength + 32); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongDictionaryFitsOutputAndBoundedScratch(bool early)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 20 * 1024 * 1024 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        byte[] output = LzwFilter.Decode(LzwTests.PackCodes(RepeatedCodes(3839, early)), early, limits, governor, CancellationToken.None, 0L);
        Assert.Equal(7_370_880, output.Length); Assert.True(output.AsSpan().IndexOfAnyExcept((byte)65) < 0);
        Assert.True(governor.Snapshot().PeakMemoryBytes <= output.LongLength + 8 * 1024 * 1024 + 65536);
        governor.ReleaseMemory(output.LongLength + 32); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal((byte)65, output[^1]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClearRestoresNineBitsAndInvalidatesOldEntries(bool early, bool stale)
    {
        var codes = RepeatedCodes(64, early); int width = codes[^1].Width; codes.RemoveAt(codes.Count - 1);
        codes.Add((256, width));
        if (!stale) { codes.Add((65, 9)); }
        codes.Add((258, 9)); codes.Add((257, 9));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] encoded = LzwTests.PackCodes(codes);
        if (stale)
        {
            Assert.Throws<QpdfFilterException>(() => LzwFilter.Decode(encoded, early, QpdfLimits.Default, governor, CancellationToken.None, 0L));
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        else
        {
            byte[] output = LzwFilter.Decode(encoded, early, QpdfLimits.Default, governor, CancellationToken.None, 0L);
            Assert.Equal(64 * 65 / 2 + 3, output.Length); Assert.True(output.AsSpan().IndexOfAnyExcept((byte)65) < 0);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FullTableRequiresClearBeforeAnotherDataCode(bool early, bool clear)
    {
        var codes = RepeatedCodes(3839, early); codes.RemoveAt(codes.Count - 1);
        if (clear) { codes.Add((256, 12)); }
        codes.Add((65, clear ? 9 : 12)); codes.Add((257, clear ? 9 : 12));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] encoded = LzwTests.PackCodes(codes);
        if (clear) { Assert.Equal(7_370_881, LzwFilter.Decode(encoded, early, QpdfLimits.Default, governor, CancellationToken.None, 0L).Length); }
        else
        {
            QpdfFilterException error = Assert.Throws<QpdfFilterException>(() => LzwFilter.Decode(encoded, early, QpdfLimits.Default, governor, CancellationToken.None, 0L));
            Assert.Contains("Table is full", error.Message, StringComparison.Ordinal); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidCodeRetiresDictionaryAndPreservesPriorFunding(bool early)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97);
        long live = governor.Snapshot().LiveMemoryBytes;
        byte[] encoded = DecoderOwnershipFixtures.Pack([256, 65, 66, 400]);
        Assert.Throws<QpdfFilterException>(() => LzwFilter.Decode(encoded, early, QpdfLimits.Default, governor, CancellationToken.None, 0L));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97, prior.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyOrSingleLiteralDoesNotAllocateDictionary(bool literal)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        byte[] encoded = DecoderOwnershipFixtures.Pack(literal ? [256, 65, 257] : [256, 257]);
        Assert.Equal(literal ? new byte[] { 65 } : [], LzwFilter.Decode(encoded, true, limits, governor, CancellationToken.None, 0L));
    }

    private sealed class Host(InMemoryQpdfHost inner, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => inner.CreateOutputAsync(path, token);
    }

    private sealed class CancelOnGrowth(RecordingMemoryBudget root, CancellationTokenSource canceled) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => root.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!root.TryReserve(bytes, out IMemoryReservation? inner)) { reservation = null; return false; }
            reservation = new Receipt(inner, canceled); return true;
        }
        private sealed class Receipt(IMemoryReservation inner, CancellationTokenSource canceled) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            {
                bool accepted = inner.TryResize(bytes);
                if (accepted && bytes >= 65536) { canceled.Cancel(); }
                return accepted;
            }
            public void Dispose() => inner.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherTokenAfterCodecGrowthRetiresScratch(bool caller)
    {
        using CancellationTokenSource canceled = new(); RecordingMemoryBudget root = new(1024 * 1024);
        using InMemoryQpdfHost storage = new(); Host host = new(storage, new CancelOnGrowth(root, canceled));
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : canceled.Token, host);
        byte[] encoded = LzwTests.PackCodes(RepeatedCodes(512, true));
        Assert.ThrowsAny<OperationCanceledException>(() => LzwFilter.Decode(encoded, true, QpdfLimits.Default, governor, caller ? canceled.Token : CancellationToken.None, 0L));
        Assert.True(canceled.IsCancellationRequested); Assert.True(root.PeakBytes >= 65536);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SharedRefusalRetiresAllCodecStorage(int growth)
    {
        RecordingMemoryBudget root = new(1024 * 1024) { RefuseOnGrowth = growth };
        using InMemoryQpdfHost storage = new(); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new Host(storage, root));
        byte[] encoded = LzwTests.PackCodes(RepeatedCodes(512, true));
        Assert.Throws<QpdfMemoryAdmissionException>(() => LzwFilter.Decode(encoded, true, QpdfLimits.Default, governor, CancellationToken.None, 0L));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void LocalRefusalPrecedesFixedTableAllocation()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 10000 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        byte[] encoded = DecoderOwnershipFixtures.Pack([256, 65, 66, 257]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => LzwFilter.Decode(encoded, true, limits, governor, CancellationToken.None, 0L));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8192);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task PublicInspectionFitsTwentyMiBAndRetainsOutput(string route)
    {
        byte[] encoded = LzwTests.PackCodes(RepeatedCodes(3839, true));
        using InMemoryQpdfHost host = FilterBoundaryTests.Host("LZWDecode", encoded);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 20 * 1024 * 1024 };
        if (route == "typed")
        {
            QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), new(), limits, CancellationToken.None);
            Assert.True(result.Passed); Assert.True(result.Counters.TotalDecodedBytes >= 7_370_880);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--show-object=4", "--filtered-stream-data"]), new(limits), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","showObject":"4","filteredStreamData":""}""", new(limits), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            ReadOnlyMemory<byte> output = Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes);
            host.Dispose(); Assert.Equal(7_370_880, output.Length); Assert.True(output.Span.IndexOfAnyExcept((byte)65) < 0);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class CodecDecodedBudgetTests
{
    [Theory]
    [InlineData("FlateDecode", false)]
    [InlineData("LZWDecode", false)]
    [InlineData("ASCIIHexDecode", false)]
    [InlineData("ASCII85Decode", false)]
    [InlineData("RunLengthDecode", false)]
    [InlineData("FlateDecode", true)]
    [InlineData("LZWDecode", true)]
    [InlineData("ASCIIHexDecode", true)]
    [InlineData("ASCII85Decode", true)]
    [InlineData("RunLengthDecode", true)]
    public void RemainingAggregateBudgetRejectsBeforeAnotherLargeAllocation(string codec, bool partial)
    {
        byte[] plain = new byte[131072]; byte[] encoded = DecoderOwnershipFixtures.Encode(codec, plain);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = plain.Length };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(97L);
        using FilterPlan plan = FilterPlan.Create(governor, CancellationToken.None);
        plan.Allocate(1); plan.Set(0, new(codec, null, null));
        byte[] first = [];
        if (partial) { governor.ChargeDecodedBytes(plain.Length - 32L); }
        else { first = FilterChain.Decode(encoded, plan, limits, governor, memory); }
        long before = governor.Snapshot().LiveMemoryBytes, decoded = governor.Snapshot().TotalDecodedBytes;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => FilterChain.Decode(encoded, plan, limits, governor, memory));
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(nameof(QpdfLimits.MaxDecodedBytes), failure.LimitName);
        Assert.True(failure.Requested > failure.Allowed);
        Assert.True(allocated < 100000L, $"Rejected {codec} allocated {allocated} bytes.");
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(decoded, governor.Snapshot().TotalDecodedBytes);
        plan.Dispose(); memory.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        if (!partial) { Assert.Equal(plain, first); }
    }

    [Theory]
    [InlineData("FlateDecode", 1, 0)]
    [InlineData("LZWDecode", 1, 0)]
    [InlineData("FlateDecode", 3, 0)]
    [InlineData("LZWDecode", 3, 0)]
    [InlineData("FlateDecode", 1, 5)]
    [InlineData("LZWDecode", 1, 5)]
    [InlineData("FlateDecode", 3, 5)]
    [InlineData("LZWDecode", 3, 5)]
    public void PngSelectorsFitTheRemainingSampleBudgetAcrossChains(string codec, int columns, int prior)
    {
        byte[] plain = Enumerable.Range(1, columns * 2).Select(i => (byte)i).ToArray();
        byte[] predicted = [0, .. plain.AsSpan(0, columns), 0, .. plain.AsSpan(columns)];
        byte[] encoded = DecoderOwnershipFixtures.Encode(codec, predicted);
        byte[] outer = DecoderOwnershipFixtures.Encode("ASCIIHexDecode", encoded);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = prior + encoded.Length + plain.Length };
        ResourceGovernor governor = new(limits, CancellationToken.None); governor.ChargeDecodedBytes(prior);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using FilterPlan plan = FilterPlan.Create(governor, CancellationToken.None);
        plan.Allocate(2); plan.Set(0, new("ASCIIHexDecode", null, null));
        plan.Set(1, new(codec, DecoderOwnershipFixtures.Dictionary(("Predictor", new PdfInteger("12", 12)),
            ("Columns", new PdfInteger(columns.ToString(), columns))), null));
        byte[] result = FilterChain.Decode(outer, plan, limits, governor, memory);
        Assert.Equal(plain, result); Assert.Equal(limits.MaxDecodedBytes, governor.Snapshot().TotalDecodedBytes);
        Assert.Equal(plain.Length + 32L, memory.ReservedBytes);
        plan.Dispose(); memory.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(plain, result);
    }

    [Theory]
    [InlineData("FlateDecode", 0)]
    [InlineData("LZWDecode", 0)]
    [InlineData("FlateDecode", 1)]
    [InlineData("LZWDecode", 1)]
    [InlineData("FlateDecode", 2)]
    [InlineData("LZWDecode", 2)]
    public void ExhaustedBudgetStillDistinguishesEmptyPartialAndSampleRows(string codec, int length)
    {
        byte[] encoded = DecoderOwnershipFixtures.Encode(codec, new byte[length]);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None); governor.ChargeDecodedBytes(1L);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(97L);
        using FilterPlan plan = FilterPlan.Create(governor, CancellationToken.None); plan.Allocate(1);
        plan.Set(0, new(codec, DecoderOwnershipFixtures.Dictionary(("Predictor", new PdfInteger("12", 12))), null));
        long before = governor.Snapshot().LiveMemoryBytes;
        byte[] Decode() => FilterChain.Decode(encoded, plan, limits, governor, memory);
        if (length == 0) { Assert.Empty(Decode()); }
        else if (length == 1) { Assert.Equal(QpdfFilterFailureReason.TruncatedInput, Assert.Throws<QpdfFilterException>(Decode).Reason); }
        else { Assert.Equal(nameof(QpdfLimits.MaxDecodedBytes), Assert.Throws<QpdfLimitException>(Decode).LimitName); }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, memory.ReservedBytes);
        Assert.Equal(1L, governor.Snapshot().TotalDecodedBytes);
    }

    [Theory]
    [InlineData("FlateDecode", 0)]
    [InlineData("LZWDecode", 0)]
    [InlineData("FlateDecode", 1)]
    [InlineData("LZWDecode", 1)]
    [InlineData("FlateDecode", 2)]
    [InlineData("LZWDecode", 2)]
    public void RawSelectorAllowancePreservesMalformedRowFailures(string codec, int malformed)
    {
        byte[] predicted = malformed switch { 0 => [0, 1, 2], 1 => [7, 1, 2, 3], _ => [0, 1, 2, 3, 0] };
        byte[] encoded = DecoderOwnershipFixtures.Encode(codec, predicted);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = 3L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(97L);
        using FilterPlan plan = FilterPlan.Create(governor, CancellationToken.None); plan.Allocate(1);
        plan.Set(0, new(codec, DecoderOwnershipFixtures.Dictionary(("Predictor", new PdfInteger("12", 12)),
            ("Columns", new PdfInteger("3", 3))), null));
        long before = governor.Snapshot().LiveMemoryBytes;
        QpdfFilterException failure = Assert.Throws<QpdfFilterException>(() => FilterChain.Decode(encoded, plan, limits, governor, memory));
        Assert.Equal(malformed == 1 ? QpdfFilterFailureReason.InvalidData : QpdfFilterFailureReason.TruncatedInput, failure.Reason);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, memory.ReservedBytes);
        Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes);
    }

    [Theory]
    [InlineData("FlateDecode")]
    [InlineData("LZWDecode")]
    public void SelectorCeilingRemainsBoundedWithWideCumulativeLimits(string codec)
    {
        byte[] encoded = DecoderOwnershipFixtures.Encode(codec, [0, 17]);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = long.MaxValue };
        ResourceGovernor governor = new(limits, CancellationToken.None); governor.ChargeDecodedBytes(long.MaxValue - 1L);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using FilterPlan plan = FilterPlan.Create(governor, CancellationToken.None); plan.Allocate(1);
        plan.Set(0, new(codec, DecoderOwnershipFixtures.Dictionary(("Predictor", new PdfInteger("12", 12))), null));
        Assert.Equal(new byte[] { 17 }, FilterChain.Decode(encoded, plan, limits, governor, memory));
        Assert.Equal(long.MaxValue, governor.Snapshot().TotalDecodedBytes); Assert.Equal(33L, memory.ReservedBytes);
    }

    public static IEnumerable<object[]> PublicBudgets()
    {
        foreach (string codec in new[] { "FlateDecode", "LZWDecode" })
        foreach (string route in new[] { "argv", "job" })
        foreach (int maximum in new[] { 3, 4, 7, 8 }) { yield return [codec, route, maximum]; }
    }

    [Theory]
    [MemberData(nameof(PublicBudgets))]
    public async Task PublicPngExtractionChargesSamplesAndPreservesPriorOutputOnRefusal(string codec, string route, int maximum)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), PredictorDefaultsTests.Fixture(codec, "<< /Predictor 12 >>",
            [0, (byte)'q', 0, (byte)' ', 0, (byte)'Q', 0, (byte)'\n']), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await seed.WriteAsync(new byte[] { 17, 23 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxDecodedBytes = maximum });
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new(["input", "--show-object=4", "--filtered-stream-data", "--", "output"]), options, CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","showObject":"4","filteredStreamData":""}""", options, CancellationToken.None);
        Assert.Equal(maximum >= 4 ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.Equal(maximum >= 4 ? "q Q\n"u8.ToArray() : [17, 23], output);
        Assert.Equal(maximum >= 4 ? 4L : 0L, result.Counters.TotalDecodedBytes); Assert.Equal(0L, host.OpenLeaseCount);
        host.Dispose(); Assert.Equal(maximum >= 4 ? "q Q\n"u8.ToArray() : [17, 23], output);
    }

    [Theory]
    [InlineData("FlateDecode", false)]
    [InlineData("LZWDecode", false)]
    [InlineData("ASCIIHexDecode", false)]
    [InlineData("ASCII85Decode", false)]
    [InlineData("RunLengthDecode", false)]
    [InlineData("FlateDecode", true)]
    [InlineData("LZWDecode", true)]
    [InlineData("ASCIIHexDecode", true)]
    [InlineData("ASCII85Decode", true)]
    [InlineData("RunLengthDecode", true)]
    public void MemoryRefusalRetiresOnlyCandidateStorage(string codec, bool shared)
    {
        byte[] encoded = DecoderOwnershipFixtures.Encode(codec, new byte[131072]);
        RecordingMemoryBudget budget = new(shared ? 65536L : 16777216L);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shared ? 16777216L : 65536L };
        using InMemoryQpdfHost host = new(limits, null, budget); ResourceGovernor governor = new(limits, CancellationToken.None, host);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(97L);
        using FilterPlan plan = FilterPlan.Create(governor, CancellationToken.None); plan.Allocate(1); plan.Set(0, new(codec, null, null));
        long before = governor.Snapshot().LiveMemoryBytes;
        if (shared) { Assert.Throws<QpdfMemoryAdmissionException>(() => FilterChain.Decode(encoded, plan, limits, governor, memory)); }
        else { Assert.Throws<QpdfLimitException>(() => FilterChain.Decode(encoded, plan, limits, governor, memory)); }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, memory.ReservedBytes);
        plan.Dispose(); memory.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, budget.ReservedBytes);
    }

    [Theory]
    [InlineData("FlateDecode", false)]
    [InlineData("LZWDecode", false)]
    [InlineData("FlateDecode", true)]
    [InlineData("LZWDecode", true)]
    public void PngCancellationChecksBothTokensWithoutDroppingPriorStorage(string codec, bool governorToken)
    {
        byte[] encoded = DecoderOwnershipFixtures.Encode(codec, [0, 1]);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(97L);
        using FilterPlan plan = FilterPlan.Create(governor, CancellationToken.None); plan.Allocate(1);
        plan.Set(0, new(codec, DecoderOwnershipFixtures.Dictionary(("Predictor", new PdfInteger("12", 12))), null));
        long before = governor.Snapshot().LiveMemoryBytes; cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FilterChain.Decode(encoded, plan, QpdfLimits.Default, governor, memory,
            governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, memory.ReservedBytes);
    }
}

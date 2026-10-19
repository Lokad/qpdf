using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

[CollectionDefinition(nameof(WriterFormattingCollection), DisableParallelization = true)]
public sealed class WriterFormattingCollection { }

[Collection(nameof(WriterFormattingCollection))]
public sealed class WriterFormattingTests
{
    public static IEnumerable<object[]> Numbers()
    {
        foreach (long value in new[] { long.MinValue, -9999999999L, -1L, 0L, 1L, 9999999999L, long.MaxValue })
        {
            foreach (string style in new[] { "plain", "zero", "right" }) { yield return [value, style]; }
        }
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public void DecimalFieldsPreserveSignDigitsAndReservedWidths(long value, string style)
    {
        CultureInfo prior = CultureInfo.CurrentCulture;
        CultureInfo changed = (CultureInfo)CultureInfo.InvariantCulture.Clone(); changed.NumberFormat.NegativeSign = "NEG";
        try
        {
            CultureInfo.CurrentCulture = changed;
            WriterPart part = style == "zero" ? WriterPart.ZeroPaddedNumber(value, 10)
                : style == "right" ? WriterPart.RightPaddedNumber(value, 21) : WriterPart.Number(value);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using WriterFormatting output = WriterFormatting.Create([part], QpdfLimits.Default, governor, CancellationToken.None);
            string expected = style == "zero" ? value.ToString("D10", CultureInfo.InvariantCulture)
                : value.ToString(CultureInfo.InvariantCulture);
            if (style == "right") { expected = expected.PadRight(21); }
            Assert.Equal(expected, Encoding.ASCII.GetString(output.Data.Span));
            Assert.Equal(160L + output.Data.Length, governor.Snapshot().LiveMemoryBytes);
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    [Fact]
    public void MixedFieldsPreserveExactHeaderIdAndAsciiReplacement()
    {
        byte[] raw = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterFormatting output = WriterFormatting.Create([WriterPart.Text("%PDF-"), WriterPart.Number(1), WriterPart.Text("."),
            WriterPart.Number(7), WriterPart.Text("\n<< /ID [<"), WriterPart.Hex(raw), WriterPart.Text(">] >>\n"),
            default, WriterPart.Text("é" + char.ConvertFromUtf32(0x1F600) + "\uD800"), WriterPart.Spaces(3)],
            QpdfLimits.Default, governor, CancellationToken.None);
        Assert.Equal("%PDF-1.7\n<< /ID [<" + Convert.ToHexString(raw) + ">] >>\n????   ", Encoding.ASCII.GetString(output.Data.Span));
        raw[0] = 255;
        Assert.Contains("[<000102", Encoding.ASCII.GetString(output.Data.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void PaddedReplayRegionIsExactlyTwoHundredBytes()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        const string prefix = " /Linearized 1 /L 2048 /H [ 128 64 ] /O 4 /E 768 /N 2 /T 1920";
        using WriterFormatting output = WriterFormatting.CreatePadded([WriterPart.Text(prefix)], 200,
            "Authored reserved region overflow.", QpdfLimits.Default, governor, CancellationToken.None);
        Assert.Equal(prefix.PadRight(200), Encoding.ASCII.GetString(output.Data.Span));
        Assert.Equal(360L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("number")]
    [InlineData("region")]
    public void ReservedWidthOverflowRejectsBeforeOwningStorage(string route)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        if (route == "number")
        {
            QpdfException failure = Assert.Throws<QpdfException>(() => WriterFormatting.Create([WriterPart.RightPaddedNumber(1234, 3)],
                QpdfLimits.Default, governor, CancellationToken.None));
            Assert.Equal("Linearized cross-reference exceeds its reserved space.", failure.Message);
        }
        else
        {
            QpdfParseException failure = Assert.Throws<QpdfParseException>(() => WriterFormatting.CreatePadded([WriterPart.Text("four")],
                3, "Authored reserved region overflow.", QpdfLimits.Default, governor, CancellationToken.None));
            Assert.Equal("Authored reserved region overflow.", failure.Message);
        }
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    private static WriterPart[] Parts(string kind, int count) => [kind switch
    {
        "text" => WriterPart.Text(new string('A', count)), "hex" => WriterPart.Hex(new byte[count]),
        "zero" => WriterPart.ZeroPaddedNumber(-1L, count), _ => WriterPart.Spaces(count),
    }];

    private static int Length(string kind, int count) => kind == "hex" ? 2 * count : kind == "zero" ? count + 1 : count;

    public static IEnumerable<object[]> Kinds()
    {
        foreach (string kind in new[] { "text", "hex", "spaces", "zero" }) { yield return [kind]; }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ExactAdmissionRejectsBeforeAllocationAndPreservesRetry(string kind)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        WriterPart[] parts = Parts(kind, 65536);
        int length = Length(kind, 65536);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - (160L + length - 1L));
        long live = governor.Snapshot().LiveMemoryBytes, before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => WriterFormatting.Create(parts, limits, governor, CancellationToken.None));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32768L);
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
        filler.Dispose();
        using (WriterFormatting retry = WriterFormatting.Create(parts, limits, governor, CancellationToken.None))
        {
            Assert.Equal(length, retry.Data.Length);
            Assert.Equal(161L + 160L + length, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void OutputQuotaRejectsBeforeCreatingOwner(string kind)
    {
        WriterPart[] parts = Parts(kind, 65536);
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = Length(kind, 65536) - 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => WriterFormatting.Create(parts, limits, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), failure.LimitName);
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    public static IEnumerable<object[]> Tokens()
    {
        foreach (object[] kind in Kinds()) { yield return [kind[0], false]; yield return [kind[0], true]; }
    }

    [Theory]
    [MemberData(nameof(Tokens))]
    public void EitherCanceledTokenRejectsBeforeAnyWork(string kind, bool governorToken)
    {
        WriterPart[] parts = Parts(kind, 16384);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => WriterFormatting.Create(parts, QpdfLimits.Default, governor,
            governorToken ? CancellationToken.None : cancellation.Token));
Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    [Theory]
    [MemberData(nameof(Tokens))]
    public async Task CancellationAfterObservedFillRetiresAttempt(string kind, bool governorToken)
    {
        const int count = 32 * 1024 * 1024;
        WriterPart[] parts = Parts(kind, count);
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim start = new();
        using ManualResetEventSlim reached = new();
        using ManualResetEventSlim resume = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        long preparedBytes = 161L + Length(kind, count);
        void ObserveFill()
        {
            if (governor.Snapshot().LiveMemoryBytes < preparedBytes) { return; }
            reached.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Formatter observation did not resume."); }
        }
        Task worker = Task.Factory.StartNew(() =>
        {
            start.Wait();
            using WriterFormatting output = WriterFormatting.Create(parts, QpdfLimits.Default, governor,
                governorToken ? CancellationToken.None : cancellation.Token, ObserveFill);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            start.Set();
            bool observed = reached.Wait(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            resume.Set();
            Assert.True(observed);             await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(parts);
        }
        finally
        {
            start.Set(); cancellation.Cancel(); resume.Set();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Backing(WriterFormatting owner) => new(typeof(WriterFormatting)
        .GetField("_bytes", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
        ?? throw new InvalidOperationException("Expected formatting backing."));

    [Theory]
    [MemberData(nameof(Kinds))]
    public void DisposedRootedOwnerDropsExclusiveBytes(string kind)
    {
        WriterPart[] parts = Parts(kind, 16384);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        WriterFormatting owner = WriterFormatting.Create(parts, QpdfLimits.Default, governor, CancellationToken.None);
        WeakReference bytes = Backing(owner);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Assert.True(bytes.IsAlive);
        owner.Dispose(); owner.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Assert.False(bytes.IsAlive);
        Assert.Throws<ObjectDisposedException>(() => owner.Data); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(owner); GC.KeepAlive(parts);
    }

    [Fact]
    public void EmptyFormattingBorrowsEmptyArrayAndRetiresWrapper()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        WriterFormatting owner = WriterFormatting.Create([], QpdfLimits.Default, governor, CancellationToken.None);
        Assert.Empty(owner.Data.ToArray()); Assert.Equal(128L, governor.Snapshot().LiveMemoryBytes);
        owner.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

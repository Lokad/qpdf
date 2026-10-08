using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Reading;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class DiagnosticFormattingTests
{
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NumbersNullFieldsAndCustomSignsKeepTheirExactText(bool staged)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "\u2212" + new string('S', 1024);
        CultureInfo.CurrentCulture = culture;
        try
        {
            string? empty = null;
            string expected = $"min={long.MinValue}; max={long.MaxValue}; null={empty}; invariant={long.MinValue.ToString(CultureInfo.InvariantCulture)}";
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            DiagnosticBag bag = new(governor);
            using DiagnosticStage? stage = staged ? bag.BeginStaging() : null;
            bag.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning,
                ["min=", long.MinValue, "; max=", long.MaxValue, "; null=", empty, "; invariant=", DiagnosticPart.Invariant(long.MinValue)]);
            stage?.Flush();
            Assert.Equal(expected, Assert.Single(bag.Entries).Message);
            Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SurrogatesCrossFragmentsAndStagingChunkBoundariesWithoutChangingQuotas(bool staged)
    {
        string prefix = new('A', 511);
        string high = "\ud800";
        string low = "\udc00";
        string tail = "\ud800X\udfff\u00e9";
        string expected = prefix + high + low + tail;
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnostics = 1L, MaxDiagnosticBytes = Encoding.UTF8.GetByteCount(expected) };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage? stage = staged ? bag.BeginStaging() : null;
        bag.AddParts(QpdfDiagnosticCode.DuplicateKey, QpdfDiagnosticSeverity.Warning, [prefix, high, (string?)null, low, tail]);
        stage?.Flush();
        Assert.Equal(expected, Assert.Single(bag.Entries).Message);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RejectLargeText(DiagnosticBag bag, string prepared, out QpdfLimitException failure)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        try { bag.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, ["prefix ", prepared, " suffix"]); }
        catch (QpdfLimitException caught)
        {
            failure = caught;
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        throw new InvalidOperationException("Expected formatting admission to reject.");
    }

    [Fact]
    public void MemoryRejectionPrecedesAllocatingTheLargeFormattedString()
    {
        string prepared = new('A', 10000);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2048L };
        _ = RejectLargeText(new DiagnosticBag(new ResourceGovernor(limits, CancellationToken.None)), prepared, out _);
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        long allocation = RejectLargeText(bag, prepared, out QpdfLimitException failure);
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.InRange(allocation, 0L, 4096L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
        GC.KeepAlive(prepared);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RejectName(DiagnosticBag bag, PdfName name, out QpdfLimitException failure)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        try { bag.AddParts(QpdfDiagnosticCode.DuplicateKey, QpdfDiagnosticSeverity.Warning, ["key ", DiagnosticPart.Name(name)]); }
        catch (QpdfLimitException caught)
        {
            failure = caught;
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        throw new InvalidOperationException("Expected the name quota to reject.");
    }

    [Fact]
    public void RawNameQuotaRejectionPrecedesCreatingItsDisplayString()
    {
        PdfName name = new(new byte[6000]);
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnosticBytes = 128L };
        _ = RejectName(new DiagnosticBag(new ResourceGovernor(limits, CancellationToken.None)), name, out _);
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        long allocation = RejectName(bag, name, out QpdfLimitException failure);
        Assert.Equal(nameof(QpdfLimits.MaxDiagnosticBytes), failure.LimitName);
        Assert.InRange(allocation, 0L, 4096L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
        GC.KeepAlive(name);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RejectPublicName(QpdfDocument document, out QpdfLimitException failure)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        try { _ = document.GetObject(5); }
        catch (QpdfLimitException caught)
        {
            failure = caught;
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        throw new InvalidOperationException("Expected the public diagnostic quota to reject.");
    }

    [Fact]
    public async Task PublicDuplicateNameQuotaRejectsBeforeMaterializingItsLargeDisplay()
    {
        string name = string.Concat(Enumerable.Repeat("#23", 6000));
        byte[] input = TolerantObjectLoadingTests.Pdf(["<< /" + name + " 1 /" + name + " 2 >>"], false);
        QpdfPath path = QpdfPath.From("inputs/a.pdf");
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnosticBytes = 128L };
        for (int iteration = 0; iteration < 2; iteration++)
        {
            InMemoryQpdfHost host = new();
            host.AddInput(path, input, QpdfInputOwnership.Copy);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, path, limits, CancellationToken.None))
            {
                long allocation = RejectPublicName(document, out QpdfLimitException failure);
                Assert.Equal(nameof(QpdfLimits.MaxDiagnosticBytes), failure.LimitName);
                Assert.Equal(limits.MaxDiagnosticBytes, failure.Allowed);
                Assert.True(failure.Requested > failure.Allowed);
                Assert.Equal(0L, document.Counters.DiagnosticCount);
                if (iteration == 1)
                {
                    // Covers synchronous parsing and rejection, excluding the
                    // prepared input/host/open. A full name display alone is
                    // 18,000 characters; per-byte formatting makes it far worse.
                    Assert.InRange(allocation, 0L, 3L * input.Length);
                }
            }
            Assert.Equal(0, host.OpenLeaseCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawNamesAndDecodeLevelsMatchExistingDisplaySemantics(bool staged)
    {
        PdfName name = new([0, 32, 35, 65, 126, 255]);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage? stage = staged ? bag.BeginStaging() : null;
        bag.AddParts(QpdfDiagnosticCode.DuplicateKey, QpdfDiagnosticSeverity.Warning,
            ["key=", DiagnosticPart.Name(name), "; level=", DiagnosticPart.DecodeLevel(QpdfStreamDecodeLevel.All)]);
        stage?.Flush();
        Assert.Equal("#00#20#23A~#FF", name.DisplayText);
        Assert.Equal("key=#00#20#23A~#FF; level=All", Assert.Single(bag.Entries).Message);
    }

    [Fact]
    public void EqualLargeFormattedMessagesShareWithoutAllocatingASecondString()
    {
        string prepared = new('A', 10000);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        bag.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, ["before ", prepared, " after ", 7]);
        string first = Assert.Single(bag.Entries).Message;
        bag.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, ["before ", prepared, " after ", 7]);
        Assert.Equal(2, bag.Entries.Count);
        Assert.Same(first, bag.Entries[1].Message);
        Assert.Equal(2L, governor.Snapshot().DiagnosticCount);
        Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
    }

    [Fact]
    public void FormattedWarningsStayPrivateUntilTheInputOwnerCloses()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        byte[] input = new byte[84000];
        governor.ReserveMemory(84024L);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        string field = new('A', 16);
        for (int index = 0; index < 100; index++)
        {
            bag.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, ["warning ", index, ": ", field]);
        }
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 84024L);
        governor.ReleaseMemory(84024L);
        stage.Flush();
        Assert.Equal(100, bag.Entries.Count);
        for (int index = 0; index < 100; index++) { Assert.Equal($"warning {index}: {field}", bag.Entries[index].Message); }
        Assert.Equal(100L, governor.Snapshot().DiagnosticCount);
        Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
        GC.KeepAlive(input);
    }

    [Fact]
    public void AcceptedFormattedTextRemainsReadableAfterWorkIsExhausted()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 500L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        bag.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, ["accepted ", 7]);
        QpdfDiagnostic prior = Assert.Single(bag.Entries);
        governor.ChargeWork(limits.MaxWork - governor.Snapshot().TotalWork);
        Assert.Same(prior, Assert.Single(bag.Entries));
        Assert.Equal("accepted 7", prior.Message);
        Assert.Equal(limits.MaxWork, governor.Snapshot().TotalWork);
    }

    private static string ThrowingField() => throw new InvalidOperationException("A later field failed.");

    [Fact]
    public void FormattingWorkFailurePreservesEarlierStagedTextAndItsPromise()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 500L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage.");
        bag.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, ["prior ", 7]);
        long memory = governor.Snapshot().LiveMemoryBytes;
        string field = new('A', 10000);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => bag.AddParts(
            QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, ["later ", field, long.MaxValue]));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(memory, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        Assert.True(governor.Snapshot().TotalWork < limits.MaxWork);
        stage.Flush();
        Assert.Equal("prior 7", Assert.Single(bag.Entries).Message);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        Assert.True(governor.Snapshot().TotalWork <= limits.MaxWork);
    }

    [Fact]
    public void CanceledFormattingReleasesPrivateStorageAndPreservesObservedCounters()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        DiagnosticBag bag = new(governor);
        using (DiagnosticStage stage = bag.BeginStaging() ?? throw new InvalidOperationException("Expected an exclusive stage."))
        {
            bag.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, ["prior ", 7]);
            QpdfExecutionCounters before = governor.Snapshot();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => bag.AddParts(QpdfDiagnosticCode.RepairedObject,
                QpdfDiagnosticSeverity.Warning, ["later ", long.MaxValue]));
            Assert.Equal(before, governor.Snapshot());
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        Assert.True(governor.Snapshot().TotalWork > 0L);
        Assert.Empty(bag.Entries);
    }

    [Fact]
    public void AThrowingLaterArgumentCreatesNoFormattingOwnerOrPartialEntry()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "prior");
        QpdfDiagnostic prior = Assert.Single(bag.Entries);
        QpdfExecutionCounters before = governor.Snapshot();
        Assert.Throws<InvalidOperationException>(() => bag.AddParts(QpdfDiagnosticCode.RepairedObject,
            QpdfDiagnosticSeverity.Warning, ["before ", ThrowingField(), " after"]));
        Assert.Equal(before, governor.Snapshot());
        Assert.Same(prior, Assert.Single(bag.Entries));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustedCountQuotaRejectsBeforeScanningLaterFields(bool staged)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnostics = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using DiagnosticStage? stage = staged ? bag.BeginStaging() : null;
        bag.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, ["prior ", 7]);
        QpdfExecutionCounters before = governor.Snapshot();
        string prepared = new('A', 10000);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => bag.AddParts(
            QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, [prepared, long.MinValue]));
        Assert.Equal(nameof(QpdfLimits.MaxDiagnostics), failure.LimitName);
        Assert.Equal(before, governor.Snapshot());
        stage?.Flush();
        Assert.Equal("prior 7", Assert.Single(bag.Entries).Message);
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
    }

    [Fact]
    public void CollidingHashesStillCompareFormattedFragmentsExactly()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        bag.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, [" ", "a"]);
        bag.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, ["!", "@"]);
        bag.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, ["", " a"]);
        Assert.Equal(new[] { " a", "!@", " a" }, bag.Entries.Select(entry => entry.Message));
        Assert.NotSame(bag.Entries[0].Message, bag.Entries[1].Message);
        Assert.Same(bag.Entries[0].Message, bag.Entries[2].Message);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddCallerField(DiagnosticBag bag)
    {
        string field = new('Q', 2000);
        WeakReference weak = new(field);
        bag.AddParts(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, ["prefix ", field, 7]);
        return weak;
    }

    [Fact]
    public void AcceptedTextDoesNotRetainItsBorrowedCallerFragments()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        WeakReference field = AddCallerField(bag);
        Collect();
        Assert.False(field.IsAlive);
        Assert.Equal("prefix " + new string('Q', 2000) + "7", Assert.Single(bag.Entries).Message);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 4000L);
        GC.KeepAlive(bag);
    }
}

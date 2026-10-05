using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;
using System.Text;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class CanonicalDisplayMemoryTests
{
    [Theory]
    [InlineData("binary")]
    [InlineData("literal")]
    [InlineData("name")]
    [InlineData("real")]
    public void LargeValuesFailBeforeUnboundedDisplayAllocation(string kind)
    {
        byte[] raw = new byte[4 * 1024 * 1024];
        QpdfObject value;
        if (kind == "real")
        {
            value = new PdfReal("0." + new string('0', raw.Length), 0D);
        }
        else
        {
            if (kind == "literal") { Array.Fill(raw, (byte)'A'); }
            if (kind == "name") { Array.Fill(raw, (byte)255); }
            value = kind == "name" ? new PdfName(raw) : new PdfByteString(raw, false);
        }

        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 64L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => CanonicalDisplay.Render(value, limits, governor));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
        Assert.InRange(allocated, 0L, 256L * 1024L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork > 0L);
    }

    [Fact]
    public void OmittedDictionaryValuesDoNotCloneLongKeyBytes()
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        for (int i = 0; i < 64; i++)
        {
            byte[] key = new byte[4096];
            Array.Fill(key, (byte)'A');
            key[^1] = (byte)i;
            entries.Add(new PdfName(key), PdfNull.Instance);
        }

        PdfDictionary value = new(entries);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 64L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();
        using GovernedText display = CanonicalDisplay.Render(value, limits, governor);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("<< >>", display.Value);
        Assert.InRange(allocated, 0L, 32L * 1024L);
    }

    [Theory]
    [InlineData(2500L)]
    [InlineData(5000L)]
    public void GrowthAndFinalCopyAdmitOverlappingOwners(long memory)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = memory };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
            CanonicalDisplay.Render(new PdfByteString(new byte[500], false), limits, governor));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 1L, memory);
        Assert.True(governor.Snapshot().TotalWork > 500L);
    }

    [Fact]
    public void DisposingTextReleasesItsReservationOnce()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        GovernedText text = CanonicalDisplay.Render(new PdfByteString([0, 1, 2], false), QpdfLimits.Default, governor);
        Assert.Equal("<000102>", text.Value);
        Assert.True(governor.Snapshot().PeakMemoryBytes > governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 2L * text.Value.Length);
        text.Dispose();
        text.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => text.Value);
    }

    [Fact]
    public void DiagnosticTransferRetainsTheSameStringAndChargesOnce()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnostics = 1L, MaxDiagnosticBytes = 8L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        using GovernedText text = CanonicalDisplay.Render(new PdfByteString([65, 65, 65, 65, 255], false), limits, governor);
        string alias = text.Value;
        bag.AddOwned(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, text);
        text.Dispose();
        Assert.Same(alias, Assert.Single(bag.Entries).Message);
        Assert.Equal(8, Encoding.UTF8.GetByteCount(alias));
        Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 2L * alias.Length);
        Assert.Throws<ObjectDisposedException>(() => text.Value);
    }

    [Theory]
    [InlineData(7L, false)]
    [InlineData(8L, true)]
    public void DiagnosticPreflightUsesEncodedBytes(long bytes, bool succeeds)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnostics = 1L, MaxDiagnosticBytes = bytes };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        PdfByteString value = new([65, 65, 65, 65, 255], true);
        if (succeeds)
        {
            CanonicalDisplay.AddDiagnostic(value, limits, governor, null, bag);
            Assert.Equal("(AAAA\u00ff)", Assert.Single(bag.Entries).Message);
            Assert.Equal(1L, governor.Snapshot().DiagnosticCount);
        }
        else
        {
            QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
                CanonicalDisplay.AddDiagnostic(value, limits, governor, null, bag));
            Assert.Equal(nameof(QpdfLimits.MaxDiagnosticBytes), error.LimitName);
            Assert.Empty(bag.Entries);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
        }
    }

    [Fact]
    public void ExhaustedDiagnosticCountRejectsBeforeDisplayWork()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnostics = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        bag.Add(QpdfDiagnosticCode.RepairedObject, QpdfDiagnosticSeverity.Warning, "repair");
        PdfByteString value = new(new byte[4 * 1024 * 1024], false);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
            CanonicalDisplay.AddDiagnostic(value, limits, governor, null, bag));
        Assert.Equal(nameof(QpdfLimits.MaxDiagnostics), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().TotalWork);
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
        Assert.Single(bag.Entries);
    }

    [Theory]
    [InlineData(0xd83d, 0xde00, 4L)]
    [InlineData(0xd83d, -1, 3L)]
    [InlineData(0xde00, -1, 3L)]
    [InlineData(0x0800, -1, 3L)]
    public void TextPreflightHandlesUtf16WithoutAllocatingEncodedCopies(int first, int second, long bytes)
    {
        string source = second < 0 ? new string((char)first, 1) : new string([(char)first, (char)second]);
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnosticBytes = bytes };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        CanonicalDisplay.AddDiagnostic(new PdfReal(source, 0D), limits, governor, null, bag);
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(Assert.Single(bag.Entries).Message));
    }

    [Fact]
    public void DiagnosticBytePreflightBoundsActualAllocation()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnosticBytes = 16L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        PdfByteString value = new(new byte[4 * 1024 * 1024], false);
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
            CanonicalDisplay.AddDiagnostic(value, limits, governor, null, bag));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(nameof(QpdfLimits.MaxDiagnosticBytes), error.LimitName);
        Assert.InRange(allocated, 0L, 16L * 1024L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Empty(bag.Entries);
    }

    [Fact]
    public void LongCommonPrefixSortingHonorsWorkAndReleasesScratch()
    {
        byte[] a = new byte[4096];
        byte[] b = new byte[4096];
        Array.Fill(a, (byte)'A');
        Array.Fill(b, (byte)'A');
        b[^1] = (byte)'B';
        PdfDictionary value = new(new Dictionary<PdfName, QpdfObject>
        {
            [new PdfName(a)] = PdfNull.Instance,
            [new PdfName(b)] = PdfNull.Instance,
        });
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 128L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => CanonicalDisplay.Render(value, limits, governor));
        Assert.Equal(nameof(QpdfLimits.MaxWork), error.LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes > 0L);
    }

    [Fact]
    public void OwnedTextCannotMoveToAnotherJobsDiagnostics()
    {
        ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None);
        ResourceGovernor other = new(QpdfLimits.Default, CancellationToken.None);
        using GovernedText text = CanonicalDisplay.Render(PdfBoolean.True, QpdfLimits.Default, owner);
        DiagnosticBag otherBag = new(other);
        Assert.Throws<InvalidOperationException>(() => otherBag.AddOwned(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, text));
        Assert.Equal("true", text.Value);
        Assert.Equal(0L, other.Snapshot().LiveMemoryBytes);
        Assert.Empty(otherBag.Entries);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(262144)]
    public void LargeDisplaysFitLinearWorkCeilings(int bytes)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 8L * bytes };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using GovernedText text = CanonicalDisplay.Render(new PdfByteString(new byte[bytes], false), limits, governor);
        Assert.Equal(2 * bytes + 2, text.Value.Length);
        Assert.StartsWith("<0000", text.Value);
        Assert.EndsWith("0000>", text.Value);
        Assert.InRange(governor.Snapshot().TotalWork, 1L, limits.MaxWork);
    }

    [Fact]
    public void DiagnosticRejectionLeavesTextWithItsOriginalOwner()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnosticBytes = 3L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag bag = new(governor);
        GovernedText text = CanonicalDisplay.Render(new PdfByteString([0, 1, 2], false), limits, governor);
        try
        {
            QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
                bag.AddOwned(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, text));
            Assert.Equal(nameof(QpdfLimits.MaxDiagnosticBytes), error.LimitName);
            Assert.Equal("<000102>", text.Value);
            Assert.Empty(bag.Entries);
            Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
            Assert.True(governor.Snapshot().LiveMemoryBytes > 0L);
        }
        finally { text.Dispose(); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task LongKeySortingCancelsAfterWorkStartsAndReleasesScratch()
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        for (int i = 0; i < 64; i++)
        {
            byte[] key = new byte[65536];
            Array.Fill(key, (byte)'A');
            key[^1] = (byte)i;
            entries.Add(new PdfName(key), PdfNull.Instance);
        }
        PdfDictionary value = new(entries);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        Task<GovernedText> sorting = Task.Factory.StartNew(() => CanonicalDisplay.Render(value, QpdfLimits.Default, governor),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        bool observed = SpinWait.SpinUntil(() => governor.Snapshot().TotalWork >= 8192L || sorting.IsCompleted, TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Assert.True(observed);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await sorting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(governor.Snapshot().TotalWork >= 8192L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, 1L, 8192L);
    }
}

using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class XrefTableOwnershipTests
{
    [Fact]
    public void OwnedEngineRetiresItsInputAndPrivateTableStorage()
    {
        byte[] data = FutureXrefTests.Fixture(1, false, false, true);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        using (ParserMemoryScope input = ParserMemoryScope.Create(governor))
        {
            input.Reserve(data.Length + 32L);
            XRefTable table = XRefTable.Parse(data, QpdfLimits.Default, governor, diagnostics, QpdfRecoveryMode.Strict, false);
            DocumentEngine engine;
            try
            {
                engine = DocumentEngine.CreateOwned(data, table, QpdfLimits.Default, governor,
                    diagnostics, QpdfRecoveryMode.Strict, input.ReservedBytes, false);
                input.Retain();
            }
            catch { table.Dispose(); throw; }
            Assert.Equal("stale-value"u8.ToArray(), Assert.IsType<PdfByteString>(engine.Load(5)).Value);
            engine.Dispose();
            engine.Dispose();
            Assert.Throws<ObjectDisposedException>(() => table.Entries);
            Assert.Equal(64L, governor.Snapshot().LiveMemoryBytes); // Only the open input scope.
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RejectedForeignTransferPreservesTheOriginalReceipt()
    {
        byte[] data = FutureXrefTests.Fixture(1, false, false, true);
        ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None);
        ResourceGovernor foreign = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary trailer;
        using (ParserMemoryScope destination = ParserMemoryScope.Create(owner))
        using (ParserMemoryScope wrong = ParserMemoryScope.Create(foreign))
        {
            using XRefTable table = XRefTable.Parse(data, QpdfLimits.Default, owner,
                new DiagnosticBag(owner), QpdfRecoveryMode.Strict, false);
            trailer = table.Trailer;
            long before = owner.Snapshot().LiveMemoryBytes;
            Assert.Throws<InvalidOperationException>(() => table.TransferExclusiveValuesTo(wrong));
            Assert.Equal(before, owner.Snapshot().LiveMemoryBytes);
            Assert.Equal(0L, wrong.ReservedBytes);
            table.TransferExclusiveValuesTo(destination);
            Assert.True(destination.ReservedBytes > 0L);
            table.Dispose();
            Assert.Equal(destination.ReservedBytes + 64L, owner.Snapshot().LiveMemoryBytes);
            Assert.True(trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject root));
            Assert.Equal(1, Assert.IsType<PdfReference>(root).ObjectNumber);
        }
        Assert.Equal(0L, owner.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, foreign.Snapshot().LiveMemoryBytes);
        Assert.True(trailer.TryGet(PdfName.FromAscii("Root"), out _));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("receipt")]
    [InlineData("refusal")]
    [InlineData("cancellation")]
    public void FailedEngineConstructionLeavesTableAndInputWithCaller(string failure)
    {
        byte[] data = FutureXrefTests.Fixture(1, false, false, true);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor owner = new(QpdfLimits.Default, cancellation.Token);
        ResourceGovernor foreign = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag diagnostics = new(owner);
        using (ParserMemoryScope input = ParserMemoryScope.Create(owner))
        using (XRefTable table = XRefTable.Parse(data, QpdfLimits.Default, owner,
            diagnostics, QpdfRecoveryMode.Strict, false))
        {
            input.Reserve(data.Length + 32L);
            long pressure = failure == "refusal" ? QpdfLimits.Default.MaxMemoryBytes - owner.Snapshot().LiveMemoryBytes - 16L : 0L;
            owner.ReserveMemory(pressure);
            try
            {
                if (failure == "cancellation") { cancellation.Cancel(); }
                long before = owner.Snapshot().LiveMemoryBytes;
                DocumentEngine Construct() => DocumentEngine.CreateOwned(data, table, QpdfLimits.Default,
                    failure == "owner" ? foreign : owner, diagnostics, QpdfRecoveryMode.Strict,
                    input.ReservedBytes + (failure == "receipt" ? 1L : 0L), false);
                if (failure == "refusal") { Assert.Throws<QpdfLimitException>(Construct); }
                else if (failure == "cancellation") { Assert.ThrowsAny<OperationCanceledException>(Construct); }
                else { Assert.Throws<InvalidOperationException>(Construct); }
                Assert.Equal(before, owner.Snapshot().LiveMemoryBytes);
                Assert.Equal(data.Length + 32L, input.ReservedBytes);
                Assert.Equal(XrefEntryKind.Uncompressed, table.Entries[5].Kind);
            }
            finally { owner.ReleaseMemory(pressure); }
        }
        Assert.Equal(0L, owner.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, foreign.Snapshot().LiveMemoryBytes);
    }
}

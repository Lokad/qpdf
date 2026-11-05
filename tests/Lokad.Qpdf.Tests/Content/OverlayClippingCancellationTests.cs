using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayClippingCancellationTests
{
    private sealed class CancelingBudget(IMemoryBudget inner, CancellationTokenSource cancellation) : IMemoryBudget
    {
        internal bool Armed;
        internal int Cancellations;
        public IMemoryScope CreateScope() => inner.CreateScope();
        private void Observe()
        {
            if (!Armed || cancellation.IsCancellationRequested) { return; }
            Cancellations++; cancellation.Cancel();
        }
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!inner.TryReserve(bytes, out IMemoryReservation? storage)) { reservation = null; return false; }
            reservation = new Receipt(this, storage); Observe(); return true;
        }
        private sealed class Receipt(CancelingBudget owner, IMemoryReservation storage) : IMemoryReservation
        {
            public long Bytes => storage.Bytes;
            public bool TryResize(long bytes)
            {
                long previous = storage.Bytes; bool accepted = storage.TryResize(bytes);
                if (accepted && bytes > previous) { owner.Observe(); } return accepted;
            }
            public void Dispose() => storage.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothTokensInterruptClippingAfterAdmissionWithoutPublishingTheHeader(bool caller)
    {
        RecordingMemoryBudget root = new(8L * 1024 * 1024);
        using IMemoryScope owner = root.CreateScope(); using CancellationTokenSource cancellation = new();
        CancelingBudget budget = new(owner, cancellation);
        using InMemoryQpdfHost source = new();
        source.AddInput(QpdfPath.From("source"), OverlayAnnotationClippingTests.StateSource(64), QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(source, QpdfPath.From("source"), CancellationToken.None))
        {
            using InMemoryQpdfHost governedHost = new(QpdfLimits.Default, null, budget);
            ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token, governedHost);
            using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
            {
                GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 10, memory);
                PdfReference annotation = Assert.IsType<PdfReference>(copier.CopyValue(new PdfReference(4, 0), 0));
                QpdfObject original = copier.CopiedObjects[annotation.ObjectNumber]; int allocator = copier.NextId;
                using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, caller ? cancellation.Token : CancellationToken.None))
                {
                    budget.Armed = true;
                    Assert.ThrowsAny<OperationCanceledException>(() => AppearanceClipping.Apply(copier, annotation.ObjectNumber,
                        new double[] { 0, 0, 100, 100 }, 0, QpdfLimits.Default, graph, ref allocator));
                    Assert.Equal(1, budget.Cancellations); Assert.Same(original, copier.CopiedObjects[annotation.ObjectNumber]);
                }
            }
            governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0, source.OpenLeaseCount); Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0, root.LiveReservations);
    }
}

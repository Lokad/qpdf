using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Objects;
using System.Runtime.InteropServices;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class CooperativeHostMemoryTests
{
    private static QpdfPath InputPath => QpdfPath.From("input");
    private static QpdfPath OutputPath => QpdfPath.From("output");

    [Fact]
    public void LongResourceNamesAreIncludedInDurableHostAdmission()
    {
        QpdfPath path = QpdfPath.From(new string('p', 2048));
        RecordingMemoryBudget root = new(65536L + 256L + 2L * (2048L - 64L));
        using IMemoryScope owner = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        host.AddInput(path, new byte[65536], QpdfInputOwnership.Transfer);
        Assert.Equal(root.LimitBytes, root.ReservedBytes);
        Assert.Throws<QpdfMemoryAdmissionException>(() => host.AddInput(InputPath, [], QpdfInputOwnership.Transfer));
        host.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task ReplacedInputRemainsChargedUntilItsLastLeaseCloses()
    {
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope storageOwner = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, storageOwner);
        byte[] original = new byte[65536]; original[0] = 41;
        host.AddInput(InputPath, original, QpdfInputOwnership.Copy);
        IQpdfInput first = await host.OpenInputAsync(InputPath, CancellationToken.None);
        IQpdfInput second = await host.OpenInputAsync(InputPath, CancellationToken.None);
        long oneInput = root.ReservedBytes;
        host.AddInput(InputPath, new byte[65536], QpdfInputOwnership.Transfer);
        Assert.Equal(2L * oneInput, root.ReservedBytes);
        Assert.Equal(41, ((IImmutableInputSnapshot)first).ReadSnapshot(CancellationToken.None).Span[0]);
        await first.DisposeAsync();
        Assert.Equal(2L * oneInput, root.ReservedBytes);
        await second.DisposeAsync();
        Assert.Equal(oneInput, root.ReservedBytes);
        host.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task RefusedRegistrationPreservesThePreviousInput()
    {
        RecordingMemoryBudget root = new(65792L);
        using IMemoryScope owner = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        byte[] original = new byte[65536]; original[0] = 41;
        host.AddInput(InputPath, original, QpdfInputOwnership.Copy);
        Assert.Throws<QpdfMemoryAdmissionException>(() => host.AddInput(InputPath, new byte[65536], QpdfInputOwnership.Copy));
        await using IQpdfInput input = await host.OpenInputAsync(InputPath, CancellationToken.None);
        Assert.Equal(41, ((IImmutableInputSnapshot)input).ReadSnapshot(CancellationToken.None).Span[0]);
        Assert.Equal(root.LimitBytes, root.ReservedBytes);
    }

    [Fact]
    public void ExistingInputReservationIsAdoptedWithoutAnotherCharge()
    {
        RecordingMemoryBudget root = new(65792L);
        using IMemoryScope owner = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        if (!owner.TryReserve(65792L, out IMemoryReservation? admission)) { throw new InvalidOperationException(); }
        host.AddInput(InputPath, new byte[65536], admission);
        Assert.Equal(1, root.ReserveCalls);
        host.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(0L, admission.Bytes);
        Assert.Throws<ObjectDisposedException>(() => admission.TryResize(1L));
    }

    [Fact]
    public void InvalidReservationHandoffLeavesItOwnedByTheCaller()
    {
        RecordingMemoryBudget root = new(65792L);
        using IMemoryScope owner = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        if (!owner.TryReserve(65536L, out IMemoryReservation? admission)) { throw new InvalidOperationException(); }
        using (admission)
        {
            Assert.Throws<ArgumentException>(() => host.AddInput(InputPath, new byte[65536], admission));
            host.Dispose();
            Assert.Equal(65536L, admission.Bytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task HostDisposalRequiresItsBorrowersToFinish()
    {
        RecordingMemoryBudget root = new(65536L);
        using IMemoryScope owner = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        host.AddInput(InputPath, new byte[1024], QpdfInputOwnership.Transfer);
        IQpdfInput input = await host.OpenInputAsync(InputPath, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(host.Dispose);
        Assert.Equal(1024, ((IImmutableInputSnapshot)input).ReadSnapshot(CancellationToken.None).Length);
        await input.DisposeAsync();
        host.Dispose(); host.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Throws<ObjectDisposedException>(() => host.AddInput(InputPath, [], QpdfInputOwnership.Transfer));
        Assert.True(owner.TryReserve(0L, out IMemoryReservation? zero)); zero.Dispose();
    }

    [Fact]
    public async Task StagingGrowthAdmitsBothCapacitiesAndDiscardRefundsImmediately()
    {
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope owner = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(OutputPath, CancellationToken.None);
        await transaction.WriteAsync(new byte[65536], CancellationToken.None);
        long first = root.ReservedBytes;
        await transaction.WriteAsync(new byte[1], CancellationToken.None);
        Assert.True(root.PeakBytes >= first + 131072L);
        Assert.Equal(131072L + InMemoryQpdfHost.StorageOverheadBytes, root.ReservedBytes);
        await transaction.DiscardAsync(CancellationToken.None);
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task ExactCommitRetainsTheSameChargeAndExportHasAnIndependentOwner()
    {
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope storage = root.CreateScope();
        IMemoryScope export = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, storage);
        await using IQpdfOutputTransaction transaction = await host.CreateOutputAsync(OutputPath, CancellationToken.None);
        ((IOutputCapacityHint)transaction).EnsureCapacity(65536L, CancellationToken.None);
        await transaction.WriteAsync(new byte[65536], CancellationToken.None);
        long staging = root.ReservedBytes;
        int calls = root.ReserveCalls;
        await transaction.CommitAsync(CancellationToken.None);
        Assert.Equal(staging, root.ReservedBytes);
        Assert.Equal(calls, root.ReserveCalls);
        Assert.True(host.TryGetOutput(OutputPath, export, out byte[] bytes));
        Assert.Equal(65536, bytes.Length);
        host.Dispose();
        Assert.Equal(65568L, root.ReservedBytes);
        export.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task RefusedCommitCopyPreservesPriorPublicationAndRetiresStaging()
    {
        RecordingMemoryBudget root = new(769L);
        using IMemoryScope owner = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        await using (IQpdfOutputTransaction first = await host.CreateOutputAsync(OutputPath, CancellationToken.None))
        {
            ((IOutputCapacityHint)first).EnsureCapacity(1L, CancellationToken.None);
            await first.WriteAsync(new byte[] { 41 }, CancellationToken.None);
            await first.CommitAsync(CancellationToken.None);
        }
        long retained = root.ReservedBytes;
        await using (IQpdfOutputTransaction replacement = await host.CreateOutputAsync(OutputPath, CancellationToken.None))
        {
            await replacement.WriteAsync(new byte[] { 42 }, CancellationToken.None);
            await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => replacement.CommitAsync(CancellationToken.None).AsTask());
        }
        Assert.Equal(retained, root.ReservedBytes);
        using IMemoryScope export = root.CreateScope();
        Assert.True(host.TryGetOutput(OutputPath, export, out byte[] bytes));
        Assert.Equal(new byte[] { 41 }, bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task DefaultExportRemainsChargedAfterHostDisposal()
    {
        RecordingMemoryBudget root = new(65536L);
        IMemoryScope owner = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        await using (IQpdfOutputTransaction transaction = await host.CreateOutputAsync(OutputPath, CancellationToken.None))
        {
            await transaction.WriteAsync(new byte[1024], CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        Assert.True(host.TryGetOutput(OutputPath, out byte[] bytes));
        host.Dispose();
        Assert.Equal(1056L, root.ReservedBytes);
        Assert.Equal(1024, bytes.Length);
        owner.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateInputSnapshotKeepsItsHostChargeWithoutAnotherRootAdmission(bool noDefaultLimits)
    {
        byte[] payload = new byte[1048576]; Array.Fill(payload, (byte)' ');
        byte[] pdf = CheckTests.Assemble(CheckTests.SinglePage("<< /Length " + payload.Length + " >>", payload), 1);
        RecordingMemoryBudget root = new(pdf.LongLength + 131328L);
        using IMemoryScope storage = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, storage);
        host.AddInput(InputPath, pdf, QpdfInputOwnership.Transfer);
        using IMemoryScope invocation = root.CreateScope();
        string[] arguments = noDefaultLimits ? ["input", "--no-default-limits", "--show-npages"] : ["input", "--show-npages"];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(new HostView(host, invocation), new QpdfCommandInvocation(arguments), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(root.PeakBytes < 2L * pdf.Length);
        Assert.Equal(0L, host.OpenLeaseCount);
        using IMemoryScope publicRead = root.CreateScope();
        await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => QpdfDocument.OpenAsync(
            new HostView(host, publicRead), InputPath, CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
        using IMemoryScope tightOwner = root.CreateScope();
        QpdfExecutionResult tight = await Qpdf.ExecuteAsync(new HostView(host, tightOwner),
            new QpdfCommandInvocation(["input", "--show-npages"]),
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxMemoryBytes = 65536L }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, tight.Status);
        Assert.Contains(tight.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded
            && item.Message.Contains(nameof(QpdfLimits.MaxMemoryBytes), StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectionSeparatesBorrowedInputAndHostStagingFromEngineStorage()
    {
        byte[] payload = new byte[1048576]; Array.Fill(payload, (byte)' ');
        byte[] pdf = CheckTests.Assemble(CheckTests.SinglePage("<< /Length " + payload.Length + " >>", payload), 1);
        RecordingMemoryBudget root = new(3L * pdf.Length + 131072L);
        using IMemoryScope storage = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, storage);
        host.AddInput(InputPath, pdf, QpdfInputOwnership.Transfer);
        using (IMemoryScope invocation = root.CreateScope())
        {
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(new HostView(host, invocation),
                new QpdfCommandInvocation(["input", "--stream-data=preserve", "output"]), CancellationToken.None);
            Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        Assert.True(root.ReservedBytes >= 2L * payload.Length);
        host.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task CopiedGraphKeepsSnapshotStorageChargedAfterItsReaderCloses()
    {
        byte[] payload = new byte[65536]; payload[0] = 41;
        byte[] pdf = CheckTests.Assemble(CheckTests.SinglePage("<< /Length " + payload.Length + " >>", payload), 1);
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope storage = root.CreateScope();
        using IMemoryScope invocation = root.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, storage);
        host.AddInput(InputPath, pdf, QpdfInputOwnership.Transfer);
        long inputCharge = root.ReservedBytes;
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new HostView(host, invocation));
        using ParserMemoryScope graph = ParserMemoryScope.Create(governor);
        await using QpdfDocument document = await QpdfDocument.OpenCore(host, InputPath, QpdfLimits.Default,
            governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict, CancellationToken.None, false, true);
        {
            PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(4));
            Assert.True(MemoryMarshal.TryGetArray(stream.RawMemory, out ArraySegment<byte> backing));
            Assert.Equal(pdf.Length, backing.Array?.Length);
            document.Engine.TransferExclusiveValuesTo(graph);
            await document.DisposeAsync();
            Assert.Equal(0L, host.OpenLeaseCount);
            long before = root.ReservedBytes;
            host.AddInput(InputPath, (byte[])pdf.Clone(), QpdfInputOwnership.Transfer);
            Assert.Equal(before + inputCharge, root.ReservedBytes);
            Assert.Equal(41, stream.RawMemory.Span[0]);
            Assert.Throws<InvalidOperationException>(host.Dispose);
        }
        graph.Dispose();
        Assert.Equal(inputCharge, root.ReservedBytes);
        host.Dispose();
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostPolicyStorageTransfersWithoutMovingOrDuplicatingItsSharedCharge(bool wholeScope)
    {
        RecordingMemoryBudget root = new(65536L);
        using IMemoryScope invocation = root.CreateScope();
        using InMemoryQpdfHost host = new();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new HostView(host, invocation));
        ParserMemoryScope source = ParserMemoryScope.Create(governor);
        source.ReserveHostMemory(1048576L);
        Assert.Throws<InvalidOperationException>(source.Retain);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 1048576L);
        Assert.Equal(1024L, root.ReservedBytes);
        ParserMemoryScope target = ParserMemoryScope.Create(governor);
        if (wholeScope) { source.TransferTo(target); }
        else { source.TransferHostTo(target, 1048576L); }
        source.Dispose();
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 1048576L);
        Assert.Equal(1024L, root.ReservedBytes);
        target.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, root.ReservedBytes);
        using (ParserMemoryScope discarded = ParserMemoryScope.Create(governor))
        {
            discarded.ReserveHostMemory(1048576L);
            discarded.Discard();
            Assert.True(governor.Snapshot().LiveMemoryBytes < 1024L);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    private sealed class HostView(IQpdfHost host, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
            => host.OpenInputAsync(path, cancellationToken);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
            => host.CreateOutputAsync(path, cancellationToken);
    }
}

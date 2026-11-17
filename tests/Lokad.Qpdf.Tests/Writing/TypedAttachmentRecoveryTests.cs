using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class TypedAttachmentRecoveryTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Source = QpdfPath.From("source"),
        Template = QpdfPath.From("template"), Output = QpdfPath.From("output");
    private static readonly QpdfPassword Password = QpdfPassword.FromText("fixture-owner");
    private static readonly byte[] Payload = "Private attachment payload.\n"u8.ToArray();
    private sealed record Outcome(int Count, IReadOnlyList<QpdfDiagnostic> Warnings, QpdfExecutionCounters Counters);

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget? budget, CancellationTokenSource? cancelOnClose) : IQpdfHost, IQpdfMemoryHost
    {
        internal int Opened, Created, Capabilities;
        internal Exception? CloseFailure;
        public IMemoryBudget? MemoryBudget { get { Capabilities++; return budget; } }
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        { Opened++; return new InputLease(await storage.OpenInputAsync(path, token), cancelOnClose, CloseFailure); }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created++; return storage.CreateOutputAsync(path, token); }
        private sealed class InputLease(IQpdfInput inner, CancellationTokenSource? cancel, Exception? failure) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
                => inner.ReadAsync(offset, destination, token);
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); if (failure is not null) { throw failure; } cancel?.Cancel(); }
        }
    }

    private static byte[] Encrypted() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-attachment.pdf"));
    private static byte[] Plain() => AttachmentMetadataTests.Fixture("name", "");

    private static async Task<InMemoryQpdfHost> Storage(byte[] input)
    {
        InMemoryQpdfHost storage = new();
        storage.AddInput(Input, input, QpdfInputOwnership.Copy);
        storage.AddInput(Source, Plain(), QpdfInputOwnership.Transfer);
        storage.AddInput(QpdfPath.From("extra"), "B"u8.ToArray(), QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(Output, CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
        return storage;
    }

    private static void AssertPrior(InMemoryQpdfHost storage)
    {
        Assert.True(storage.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    private static Task Default(IQpdfHost host, string operation)
        => operation switch
        {
            "list" => Qpdf.ListAttachmentsAsync(host, Input, Password, QpdfLimits.Default, CancellationToken.None).AsTask(),
            "extract" => Qpdf.ExtractAttachmentAsync(host, Input, "private.bin", Output, Password, QpdfLimits.Default, CancellationToken.None).AsTask(),
            "add" => Qpdf.AddAttachmentsAsync(host, Input, Output, [new("extra", "extra", null, null, null, null, null, false)], Password, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None).AsTask(),
            "remove" => Qpdf.RemoveAttachmentsAsync(host, Input, Output, ["private.bin"], Password, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None).AsTask(),
            "copy" => Qpdf.CopyAttachmentsAsync(host, Input, Output, [new(Source, "copy-")], Password, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None).AsTask(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static async Task<Outcome> Run(IQpdfHost host, string operation, QpdfRecoveryMode recovery, QpdfPassword? password, CancellationToken token)
    {
        if (operation == "list")
        {
            QpdfAttachmentListResult result = await Qpdf.ListAttachmentsAsync(host, Input, password, recovery, QpdfLimits.Default, token);
            return new(result.Attachments.Count, result.Warnings, result.Counters);
        }
        if (operation == "extract")
        {
            QpdfAttachmentExtraction result = await Qpdf.ExtractAttachmentAsync(host, Input, "private.bin", Output, password, recovery, QpdfLimits.Default, token);
            Assert.Equal(Payload.Length, result.ByteCount);
            return new(1, result.Warnings, result.Counters);
        }
        QpdfAttachmentEditResult edited = operation switch
        {
            "add" => await Qpdf.AddAttachmentsAsync(host, Input, Output, [new("extra", "extra", null, null, null, null, null, false)], password, QpdfOutputEncryption.Preserve, recovery, QpdfLimits.Default, token),
            "remove" => await Qpdf.RemoveAttachmentsAsync(host, Input, Output, ["private.bin"], password, QpdfOutputEncryption.Preserve, recovery, QpdfLimits.Default, token),
            "copy" => await Qpdf.CopyAttachmentsAsync(host, Input, Output, [new(Source, "copy-")], password, QpdfOutputEncryption.Preserve, recovery, QpdfLimits.Default, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        return new(edited.AttachmentCount, edited.Warnings, edited.Counters);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("extract")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("copy")]
    public async Task ExplicitRepairAuthenticatesDamagedPrimaryWhileDefaultsStayStrict(string operation)
    {
        byte[] input = TypedSelectionRecoveryTests.Damage(Encrypted(), "startxref"), original = input.ToArray();
        using InMemoryQpdfHost storage = await Storage(input); Host host = new(storage, null, null);
        await Assert.ThrowsAsync<QpdfParseException>(() => Default(host, operation)); AssertPrior(storage);
        await Assert.ThrowsAsync<QpdfParseException>(() => Run(host, operation, QpdfRecoveryMode.Strict, Password, CancellationToken.None)); AssertPrior(storage);
        Outcome result = await Run(host, operation, QpdfRecoveryMode.BoundedRepair, Password, CancellationToken.None);
        Assert.Contains(result.Warnings, warning => warning.Code == QpdfDiagnosticCode.RecoveryEngaged);
        Assert.Equal(0L, result.Counters.OpenHandles); Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(original, input);
        Assert.Equal(operation is "add" or "copy" ? 2 : operation == "remove" ? 0 : 1, result.Count);
        if (operation == "list") { AssertPrior(storage); return; }
        Assert.True(storage.TryGetOutput(Output, out byte[] bytes));
        if (operation == "extract") { Assert.Equal(Payload, bytes); return; }
        storage.AddInput(QpdfPath.From("readback"), bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(storage, QpdfPath.From("readback"), Password, QpdfLimits.Default, CancellationToken.None))
        { Assert.NotNull(document.Encryption); Assert.Equal(1, document.PageCount); }
        IReadOnlyList<QpdfAttachmentInfo> attachments = await Qpdf.ListAttachmentsAsync(storage, QpdfPath.From("readback"), Password, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(result.Count, attachments.Count); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("template")]
    public async Task RecoveryReachesIndependentCopyAndTemplateReaders(string role)
    {
        using InMemoryQpdfHost storage = await Storage(Plain());
        byte[] damaged = TypedSelectionRecoveryTests.Damage(Encrypted(), "startxref");
        storage.AddInput(role == "copy" ? Source : Template, damaged, QpdfInputOwnership.Copy);
        async Task<QpdfAttachmentEditResult> Execute(QpdfRecoveryMode recovery, QpdfPassword credential)
            => role == "copy"
                ? await Qpdf.CopyAttachmentsAsync(storage, Input, Output, [new(Source, "copy-", credential)], null, QpdfOutputEncryption.Preserve, recovery, QpdfLimits.Default, CancellationToken.None)
                : await Qpdf.AddAttachmentsAsync(storage, Input, Output, [new("extra", "extra", null, null, null, null, null, false)], null, QpdfOutputEncryption.CopyFrom(Template, credential), recovery, QpdfLimits.Default, CancellationToken.None);
        await Assert.ThrowsAsync<QpdfParseException>(() => Execute(QpdfRecoveryMode.Strict, Password)); AssertPrior(storage);
        await Assert.ThrowsAsync<QpdfSecurityException>(() => Execute(QpdfRecoveryMode.BoundedRepair, QpdfPassword.FromText("wrong"))); AssertPrior(storage);
        QpdfAttachmentEditResult result = await Execute(QpdfRecoveryMode.BoundedRepair, Password);
        Assert.Contains(result.Warnings, warning => warning.Code == QpdfDiagnosticCode.RecoveryEngaged);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(storage.TryGetOutput(Output, out byte[] bytes)); storage.AddInput(QpdfPath.From("readback"), bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(storage, QpdfPath.From("readback"), role == "template" ? Password : null, QpdfLimits.Default, CancellationToken.None))
        { Assert.Equal(role == "template", document.Encryption is not null); Assert.Equal(1, document.PageCount); }
        IReadOnlyList<QpdfAttachmentInfo> files = await Qpdf.ListAttachmentsAsync(storage, QpdfPath.From("readback"), role == "template" ? Password : null, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(2, files.Count); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("extract")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("copy")]
    public async Task InvalidRecoveryRejectsBeforeHostCapabilities(string operation)
    {
        using InMemoryQpdfHost storage = await Storage(Plain()); Host host = new(storage, null, null);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Run(host, operation, (QpdfRecoveryMode)99, null, CancellationToken.None));
        Assert.Equal(0, host.Capabilities); Assert.Equal(0, host.Opened); Assert.Equal(0, host.Created); AssertPrior(storage);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("extract")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("copy")]
    public async Task RepairDoesNotBypassPrimaryAuthentication(string operation)
    {
        using InMemoryQpdfHost storage = await Storage(Encrypted()); Host host = new(storage, null, null);
        await Assert.ThrowsAsync<QpdfSecurityException>(() => Run(host, operation, QpdfRecoveryMode.BoundedRepair, QpdfPassword.FromText("wrong"), CancellationToken.None));
        Assert.Equal(0, host.Created); AssertPrior(storage);
    }

    [Fact]
    public async Task ListingRetainsImmutableMetadataWarningsAndCountersAndRefundsEveryRefusal()
    {
        byte[] input = TypedSelectionRecoveryTests.Damage(AttachmentMetadataTests.Fixture(new string('N', 32768), ""), "startxref");
        int growths; QpdfAttachmentListResult escaped;
        using RecordingMemoryBudget baseline = new(16777216L);
        using (IMemoryScope owner = baseline.CreateScope())
        {
            using InMemoryQpdfHost storage = await Storage(input);
            escaped = await Qpdf.ListAttachmentsAsync(new Host(storage, owner, null), Input, null, QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(32768, Assert.Single(escaped.Attachments).FileName.Length);
            Assert.Contains(escaped.Warnings, warning => warning.Code == QpdfDiagnosticCode.RecoveryEngaged);
            Assert.Equal(0L, escaped.Counters.OpenHandles); Assert.True(baseline.ReservedBytes > 0L); growths = baseline.GrowthCalls;
        }
        Assert.Equal(0L, baseline.ReservedBytes);
        Assert.Equal(32768, escaped.Attachments[0].FileName.Length); Assert.NotEmpty(escaped.Warnings);
        Assert.False(escaped.Attachments is QpdfAttachmentInfo[]); Assert.False(escaped.Warnings is QpdfDiagnostic[]);
        for (int point = 1; point <= growths; point++)
        {
            using RecordingMemoryBudget refused = new(16777216L) { RefuseOnGrowth = point };
            using (IMemoryScope owner = refused.CreateScope())
            {
                using InMemoryQpdfHost storage = await Storage(input);
                await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Qpdf.ListAttachmentsAsync(new Host(storage, owner, null), Input, null, QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None).AsTask());
                AssertPrior(storage); Assert.Equal(0L, refused.ReservedBytes);
            }
            Assert.Equal(0L, refused.ReservedBytes);
        }
    }

    [Fact]
    public async Task CancellationAfterInputCleanupAbandonsUnreturnedListingMetadata()
    {
        using InMemoryQpdfHost storage = await Storage(TypedSelectionRecoveryTests.Damage(Plain(), "startxref"));
        using RecordingMemoryBudget budget = new(16777216L);
        using CancellationTokenSource cancel = new();
        using (IMemoryScope owner = budget.CreateScope())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Qpdf.ListAttachmentsAsync(new Host(storage, owner, cancel), Input, null,
                QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, cancel.Token).AsTask());
            Assert.True(cancel.IsCancellationRequested); Assert.Equal(0L, budget.ReservedBytes); AssertPrior(storage);
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }

    [Theory]
    [InlineData("extract")]
    [InlineData("add")]
    public async Task RepairedExtractionAndEditingRefundEveryAdmissionRefusal(string operation)
    {
        byte[] input = TypedSelectionRecoveryTests.Damage(Encrypted(), "startxref");
        int growths;
        using RecordingMemoryBudget baseline = new(16777216L);
        using (IMemoryScope owner = baseline.CreateScope())
        {
            using InMemoryQpdfHost storage = await Storage(input);
            Outcome result = await Run(new Host(storage, owner, null), operation, QpdfRecoveryMode.BoundedRepair, Password, CancellationToken.None);
            Assert.Contains(result.Warnings, warning => warning.Code == QpdfDiagnosticCode.RecoveryEngaged);
            Assert.Equal(0L, result.Counters.OpenHandles); growths = baseline.GrowthCalls;
        }
        Assert.Equal(0L, baseline.ReservedBytes);
        for (int point = 1; point <= growths; point++)
        {
            using RecordingMemoryBudget refused = new(16777216L) { RefuseOnGrowth = point };
            using (IMemoryScope owner = refused.CreateScope())
            {
                using InMemoryQpdfHost storage = await Storage(input);
                await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Run(new Host(storage, owner, null), operation,
                    QpdfRecoveryMode.BoundedRepair, Password, CancellationToken.None));
                AssertPrior(storage); Assert.Equal(0L, refused.ReservedBytes);
            }
            Assert.Equal(0L, refused.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("extract")]
    [InlineData("add")]
    public async Task CancellationAfterRepairedInputCleanupPrecedesPublication(string operation)
    {
        using InMemoryQpdfHost storage = await Storage(TypedSelectionRecoveryTests.Damage(Encrypted(), "startxref"));
        using RecordingMemoryBudget budget = new(16777216L);
        using CancellationTokenSource cancel = new();
        using (IMemoryScope owner = budget.CreateScope())
        {
            Host host = new(storage, owner, cancel);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(host, operation, QpdfRecoveryMode.BoundedRepair, Password, cancel.Token));
            Assert.True(cancel.IsCancellationRequested); Assert.Equal(0, host.Created);
            Assert.Equal(0L, budget.ReservedBytes); AssertPrior(storage);
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }

    [Fact]
    public async Task CleanExplicitListingKeepsWarningsEmptyAndClosesItsLease()
    {
        using InMemoryQpdfHost storage = await Storage(Encrypted());
        QpdfAttachmentListResult result = await Qpdf.ListAttachmentsAsync(storage, Input, Password, QpdfRecoveryMode.Strict, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal("private.bin", Assert.Single(result.Attachments).Key); Assert.Empty(result.Warnings);
        Assert.Equal(0L, result.Counters.OpenHandles); AssertPrior(storage);
    }

    [Fact]
    public async Task InputCleanupFailurePrecedesPublicationThroughTheExistingExtractionOverload()
    {
        using InMemoryQpdfHost storage = await Storage(Encrypted());
        using RecordingMemoryBudget budget = new(16777216L);
        using (IMemoryScope owner = budget.CreateScope())
        {
            Host host = new(storage, owner, null) { CloseFailure = new QpdfHostException(Input, QpdfHostFailureReason.ReadFailed) };
            await Assert.ThrowsAsync<QpdfHostException>(() => Default(host, "extract"));
            Assert.Equal(0, host.Created); AssertPrior(storage); Assert.Equal(0L, budget.ReservedBytes);
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }
}

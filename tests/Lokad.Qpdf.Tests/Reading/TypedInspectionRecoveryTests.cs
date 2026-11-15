using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class TypedInspectionRecoveryTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Report = QpdfPath.From("report.json");
    private sealed record Opened(int Pages, PdfStream Stream, IReadOnlyList<QpdfDiagnostic> Warnings, QpdfEncryptionInfo? Encryption);
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static InMemoryQpdfHost Storage(byte[] bytes)
    {
        InMemoryQpdfHost storage = new();
        storage.AddInput(Input, bytes, QpdfInputOwnership.Copy);
        return storage;
    }

    private static PdfStream FirstStream(QpdfDocument document)
    {
        Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
        QpdfObject Resolve(QpdfObject raw) => raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw;
        contents = Resolve(contents);
        return Assert.IsType<PdfStream>(contents is PdfArray array ? Resolve(array[0]) : contents);
    }

    private static async Task<object> Read(string operation, IQpdfHost host, QpdfPassword? password,
        QpdfRecoveryMode recovery, QpdfLimits limits, CancellationToken token)
    {
        if (operation == "open")
        {
            return await QpdfDocument.OpenAsync(host, Input, password, recovery, limits, token);
        }
        if (operation == "check")
        {
            return await Qpdf.CheckAsync(host, Input, new QpdfCheckOptions
            { Password = password, RecoveryMode = recovery, DecodeLevel = QpdfStreamDecodeLevel.Generalized }, limits, token);
        }
        if (operation is "json" or "json-result")
        {
            QpdfJsonOptions options = new(2, ["pages", "encrypt"]) { Password = password, RecoveryMode = recovery };
            return operation == "json-result"
                ? await Qpdf.GetDocumentJsonAsync(host, Input, options, limits, token)
                : await Qpdf.GetDocumentJsonAsync(host, Input, Report, options, limits, token);
        }
        throw new ArgumentOutOfRangeException(nameof(operation));
    }

    private static async Task<object> CloseOpenedDocument(object result)
    {
        if (result is not QpdfDocument document) { return result; }
        await using (document)
        { return new Opened(document.PageCount, FirstStream(document), document.Warnings, document.Encryption); }
    }

    private static async Task ReadDefault(string operation, IQpdfHost host, QpdfPassword? password)
    {
        if (operation == "open")
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, password, QpdfLimits.Default, CancellationToken.None);
        }
        else if (operation == "check")
        { await Qpdf.CheckAsync(host, Input, new QpdfCheckOptions { Password = password }, QpdfLimits.Default, CancellationToken.None); }
        else if (operation == "json-result")
        { await Qpdf.GetDocumentJsonAsync(host, Input, new QpdfJsonOptions(2, ["pages"]) { Password = password }, CancellationToken.None); }
        else
        { await Qpdf.GetDocumentJsonAsync(host, Input, Report, new QpdfJsonOptions(2, ["pages"]) { Password = password }, CancellationToken.None); }
    }

    private static void Verify(object result, int pages, bool encrypted)
    {
        IReadOnlyList<QpdfDiagnostic> warnings;
        if (result is Opened opened)
        {
            Assert.Equal(pages, opened.Pages);
            Assert.NotEmpty(opened.Stream.Data);
            Assert.Equal(encrypted, opened.Encryption is not null);
            warnings = opened.Warnings;
        }
        else if (result is QpdfCheckResult check)
        {
            Assert.Equal(pages, check.PageCount); Assert.True(check.Passed);
            warnings = check.Diagnostics;
        }
        else
        {
            QpdfJsonResult json = Assert.IsType<QpdfJsonResult>(result);
            using JsonDocument parsed = JsonDocument.Parse(json.Json);
            Assert.Equal(pages, parsed.RootElement.GetProperty("pages").GetArrayLength());
            Assert.Equal(encrypted, parsed.RootElement.GetProperty("encrypt").GetProperty("encrypted").GetBoolean());
            warnings = json.Warnings;
        }
        Assert.Contains(warnings, warning => warning.Code == QpdfDiagnosticCode.RecoveryEngaged);
    }

    [Theory]
    [InlineData("open", "minimal-1page.pdf", "zero-entry", null)]
    [InlineData("open", "objstm-1page.pdf", "startxref", null)]
    [InlineData("open", "security-r6-ascii.pdf", "startxref", "reader")]
    [InlineData("check", "security-r6-ascii.pdf", "startxref", "reader")]
    [InlineData("json", "security-r6-ascii.pdf", "startxref", "reader")]
    [InlineData("json-result", "security-r6-ascii.pdf", "startxref", "reader")]
    public async Task ExplicitRepairReturnsAuthenticatedValuesAndWarningsWhileDefaultsRemainStrict(
        string operation, string fixture, string damage, string? password)
    {
        byte[] clean = Fixture(fixture);
        QpdfPassword? credential = password is null ? null : QpdfPassword.FromText(password);
        byte[] expected;
        using (InMemoryQpdfHost baseline = Storage(clean))
        {
            await using QpdfDocument document = await QpdfDocument.OpenAsync(baseline, Input, credential, QpdfLimits.Default, CancellationToken.None);
            expected = FirstStream(document).Data;
        }
        using InMemoryQpdfHost storage = Storage(TypedSelectionRecoveryTests.Damage(clean, damage));
        await Assert.ThrowsAsync<QpdfParseException>(() => ReadDefault(operation, storage, credential));
        await Assert.ThrowsAsync<QpdfParseException>(() => Read(operation, storage, credential, QpdfRecoveryMode.Strict, QpdfLimits.Default, CancellationToken.None));
        object result = await Read(operation, storage, credential, QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None);
        result = await CloseOpenedDocument(result);
        Verify(result, 1, password is not null);
        if (result is Opened opened) { Assert.Equal(expected, opened.Stream.Data); }
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    private sealed class UntouchedHost : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => throw new InvalidOperationException("Host budget was accessed.");
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => throw new InvalidOperationException("Host input was accessed.");
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => throw new InvalidOperationException("Host output was accessed.");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public async Task UndefinedRecoveryValuesRejectBeforeAnyHostCapability(int value)
    {
        foreach (string operation in new[] { "open", "check", "json", "json-result" })
        { await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Read(operation, new UntouchedHost(), null, (QpdfRecoveryMode)value, QpdfLimits.Default, CancellationToken.None)); }
    }

    [Fact]
    public async Task RepairStillAuthenticatesAndPreservesPriorJsonOnWrongCredentials()
    {
        using InMemoryQpdfHost storage = Storage(TypedSelectionRecoveryTests.Damage(Fixture("security-r6-ascii.pdf"), "startxref"));
        byte[] prior = [17, 23, 41];
        await using (IQpdfOutputTransaction seed = await storage.CreateOutputAsync(Report, CancellationToken.None))
        { await seed.WriteAsync(prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        foreach (string operation in new[] { "open", "check", "json", "json-result" })
        {
            QpdfSecurityException failure = await Assert.ThrowsAsync<QpdfSecurityException>(() => Read(operation, storage,
                QpdfPassword.FromText("wrong-secret"), QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None));
            Assert.Equal(QpdfSecurityFailure.InvalidPassword, failure.Reason);
            Assert.DoesNotContain("wrong-secret", failure.Message, StringComparison.Ordinal);
            Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.True(storage.TryGetOutput(Report, out byte[] bytes)); Assert.Equal(prior, bytes);
    }

    [Fact]
    public async Task RecoveryDecodeExhaustionDoesNotRetryWeakerReading()
    {
        using InMemoryQpdfHost storage = Storage(TypedSelectionRecoveryTests.Damage(Fixture("objstm-1page.pdf"), "startxref"));
        foreach (string operation in new[] { "open", "check", "json", "json-result" })
        {
            QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() => Read(operation, storage, null,
                QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default with { MaxDecodedBytes = 1 }, CancellationToken.None));
            Assert.Equal(nameof(QpdfLimits.MaxDecodedBytes), failure.LimitName);
            Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.False(storage.TryGetOutput(Report, out _));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("check")]
    [InlineData("json")]
    [InlineData("json-result")]
    public async Task ReturnedValuesStayFundedAndEveryAdmissionRefusalCleansUp(string operation)
    {
        byte[] damaged = TypedSelectionRecoveryTests.Damage(Fixture("minimal-1page.pdf"), "zero-entry");
        int growths;
        using (InMemoryQpdfHost storage = Storage(damaged))
        {
            RecordingMemoryBudget root = new(4L << 20);
            using (IMemoryScope owner = root.CreateScope())
            {
                object result = await Read(operation, new MemoryBudgetHost(storage, owner), null, QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None);
                growths = root.GrowthCalls;
                // Admission refusal targets the public operation. Lazy object
                // reads and warning snapshots happen after opening succeeds.
                object escaped = await CloseOpenedDocument(result);
                Assert.True(root.ReservedBytes > 0); Assert.Equal(0L, storage.OpenLeaseCount);
                Verify(escaped, 1, false);
                GC.KeepAlive(escaped);
            }
            Assert.Equal(0L, root.ReservedBytes);
        }
        Assert.InRange(growths, 1, 128);
        for (int growth = 1; growth <= growths; growth++)
        {
            using InMemoryQpdfHost storage = Storage(damaged);
            RecordingMemoryBudget root = new(4L << 20) { RefuseOnGrowth = growth };
            using (IMemoryScope owner = root.CreateScope())
            {
                await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Read(operation, new MemoryBudgetHost(storage, owner), null,
                    QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None));
                Assert.Equal(0L, storage.OpenLeaseCount); Assert.False(storage.TryGetOutput(Report, out _));
                Assert.Equal(0L, root.ReservedBytes);
            }
            Assert.Equal(0L, root.ReservedBytes);
        }
    }

    private sealed class CancelAfterReadHost(InMemoryQpdfHost storage, CancellationTokenSource cancellation) : IQpdfHost
    {
        internal bool Triggered;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new Input(await storage.OpenInputAsync(path, token), this, cancellation);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
        private sealed class Input(IQpdfInput inner, CancelAfterReadHost host, CancellationTokenSource cancellation) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            { int count = await inner.ReadAsync(offset, destination, token); host.Triggered = true; cancellation.Cancel(); return count; }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("open")]
    [InlineData("check")]
    [InlineData("json")]
    [InlineData("json-result")]
    public async Task CancellationAfterAnActualReadRetiresFailedConstruction(string operation)
    {
        using InMemoryQpdfHost storage = Storage(TypedSelectionRecoveryTests.Damage(Fixture("minimal-1page.pdf"), "zero-entry"));
        using CancellationTokenSource cancellation = new();
        CancelAfterReadHost host = new(storage, cancellation);
        RecordingMemoryBudget root = new(4L << 20);
        using (IMemoryScope owner = root.CreateScope())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(operation, new MemoryBudgetHost(host, owner), null,
                QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, cancellation.Token));
            Assert.True(host.Triggered); Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.False(storage.TryGetOutput(Report, out _)); Assert.Equal(0L, root.ReservedBytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}

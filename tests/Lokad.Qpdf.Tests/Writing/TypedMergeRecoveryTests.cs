using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class TypedMergeRecoveryTests
{
    private static readonly QpdfPath First = QpdfPath.From("first.pdf"), Second = QpdfPath.From("second.pdf"), Output = QpdfPath.From("output.pdf");

    private static InMemoryQpdfHost Storage()
    {
        InMemoryQpdfHost storage = new();
        storage.AddInput(First, TypedSelectionRecoveryTests.Damage(SourceGroupIdentityTests.Fixture("A"), "zero-entry"), QpdfInputOwnership.Transfer);
        storage.AddInput(Second, SourceGroupIdentityTests.Fixture("B"), QpdfInputOwnership.Transfer);
        return storage;
    }

    private static PdfPageSource[] Sources() => [new(First, PdfPageSelectionOptions.FromPages([3, 1])),
        new(Second, PdfPageSelectionOptions.FromPages([2])), new(First, PdfPageSelectionOptions.FromPages([2]))];

    private sealed class ObservedHost(InMemoryQpdfHost storage, Action<QpdfPath>? read) : IQpdfHost
    {
        internal Dictionary<QpdfPath, int> Opens { get; } = [];
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            Opens[path] = Opens.GetValueOrDefault(path) + 1;
            return new Input(await storage.OpenInputAsync(path, token), read);
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
        private sealed class Input(IQpdfInput inner, Action<QpdfPath>? read) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            { int count = await inner.ReadAsync(offset, destination, token); read?.Invoke(Path); return count; }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRepairMergesRepeatedSourcesAndSplitsWhileDefaultsStayStrict(bool split)
    {
        using InMemoryQpdfHost storage = Storage();
        PdfPageSource[] sources = Sources();
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.SelectPagesAsync(storage, sources, Output, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.SelectPagesAsync(storage, sources, Output,
            new PdfPageMergeOptions { SplitPages = split ? 2 : null }, CancellationToken.None).AsTask());
        Assert.False(storage.TryGetOutput(Output, out _)); Assert.Equal(0L, storage.OpenLeaseCount);
        ObservedHost host = new(storage, null);
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host, sources, Output,
            new PdfPageMergeOptions { RecoveryMode = QpdfRecoveryMode.BoundedRepair, SplitPages = split ? 2 : null },
            QpdfLimits.Default with { MaxDecodedBytes = 1 }, CancellationToken.None);
        Assert.Equal(4, result.PageCount); Assert.Equal(new[] { 3, 3, 3 }, result.SourcePageCounts);
        Assert.Equal(new[] { (0, 3), (0, 1), (1, 2), (2, 2) }, result.Mapping.Select(item => (item.SourceIndex, item.SourcePage)));
        Assert.Contains(result.Warnings, warning => warning.Code == QpdfDiagnosticCode.RecoveryEngaged);
        Assert.Equal(1, host.Opens[First]); Assert.Equal(1, host.Opens[Second]);
        Assert.Equal(0L, result.Counters.TotalDecodedBytes); Assert.Equal(0L, storage.OpenLeaseCount);
        List<string> markers = [];
        foreach (QpdfPath output in result.OutputPaths)
        {
            Assert.True(storage.TryGetOutput(output, out byte[] bytes));
            QpdfPath alias = QpdfPath.From("reread-" + markers.Count);
            storage.AddInput(alias, bytes, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(storage, alias, CancellationToken.None);
            Assert.Equal(split ? 2 : 4, document.PageCount);
            QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
            QpdfObject Get(PdfDictionary dictionary, string key) { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
            foreach (var location in document.PageReferences)
            {
                markers.Add(Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(location.Page, "Marker")).Value));
                PdfDictionary resources = Assert.IsType<PdfDictionary>(Resolve(Get(location.Page, "Resources")));
                PdfDictionary images = Assert.IsType<PdfDictionary>(Resolve(Get(resources, "XObject")));
                Assert.Equal(new byte[] { 0, 255 }, Assert.IsType<PdfStream>(Resolve(Get(images, "Im0"))).Data);
            }
            using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(bytes);
            Assert.Equal(document.PageCount, independent.NumberOfPages);
        }
        Assert.Equal(new[] { "A3", "A1", "B2", "A2" }, markers);
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    private sealed class UnusedHost : IQpdfHost
    {
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => throw new InvalidOperationException("Host must remain unused.");
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => throw new InvalidOperationException("Host must remain unused.");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public async Task InvalidRecoveryRejectsBeforeHostAccess(int value)
    {
        ArgumentOutOfRangeException error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.SelectPagesAsync(new UnusedHost(), Sources(), Output,
            new PdfPageMergeOptions { RecoveryMode = (QpdfRecoveryMode)value }, CancellationToken.None).AsTask());
        Assert.Equal("mergeOptions", error.ParamName);
    }

    [Fact]
    public async Task RepairedEncryptedSourcesAuthenticateIndependentlyAndPreservePrimaryProtection()
    {
        using InMemoryQpdfHost storage = new();
        foreach ((QpdfPath path, string fixture) in new[] { (First, "security-r4-ascii.pdf"), (Second, "security-r6-ascii.pdf") })
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
            storage.AddInput(path, TypedSelectionRecoveryTests.Damage(bytes, fixture.Contains("r6", StringComparison.Ordinal) ? "startxref" : "zero-entry"), QpdfInputOwnership.Transfer);
        }
        PdfPageSource[] sources = [new(First, new(1), QpdfPassword.FromText("reader")), new(Second, new(1), QpdfPassword.FromText("reader")),
            new(First, new(1), QpdfPassword.FromText("reader"))];
        PdfPageMergeOptions options = new() { RecoveryMode = QpdfRecoveryMode.BoundedRepair, SplitPages = 2 };
        PdfPageSource[] wrong = [sources[0], new(Second, new(1), QpdfPassword.FromText("wrong-secondary"))];
        QpdfSecurityException failure = await Assert.ThrowsAsync<QpdfSecurityException>(() => Qpdf.SelectPagesAsync(storage, wrong, Output, options, CancellationToken.None).AsTask());
        Assert.Equal(QpdfSecurityFailure.InvalidPassword, failure.Reason); Assert.DoesNotContain("wrong-secondary", failure.ToString());
        Assert.False(storage.TryGetOutput(QpdfPath.From("output-1.pdf"), out _)); Assert.Equal(0L, storage.OpenLeaseCount);
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(storage, sources, Output, options, CancellationToken.None);
        Assert.Equal(3, result.PageCount); Assert.Equal(2, result.OutputPaths.Count);
        Assert.Contains(result.Warnings, warning => warning.Code == QpdfDiagnosticCode.RecoveryEngaged);
        for (int index = 0; index < result.OutputPaths.Count; index++)
        {
            Assert.True(storage.TryGetOutput(result.OutputPaths[index], out byte[] bytes));
            QpdfPath alias = QpdfPath.From("protected-" + index); storage.AddInput(alias, bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(storage, alias, QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(4, document.Encryption?.Revision); Assert.Equal(index == 0 ? 2 : 1, document.PageCount);
        }
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public async Task RecoveryPolicyAlsoAppliesToAnIndependentEncryptionTemplate()
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(First, SourceGroupIdentityTests.Fixture("A"), QpdfInputOwnership.Transfer);
        QpdfPath template = QpdfPath.From("template.pdf");
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-ascii.pdf"));
        storage.AddInput(template, TypedSelectionRecoveryTests.Damage(bytes, "startxref"), QpdfInputOwnership.Transfer);
        PdfPageSource[] sources = [new(First, new(1)), new(First, new(2))];
        PdfPageMergeOptions strict = new() { OutputEncryption = QpdfOutputEncryption.CopyFrom(template, QpdfPassword.FromText("reader")) };
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.SelectPagesAsync(storage, sources, Output, strict, CancellationToken.None).AsTask());
        Assert.False(storage.TryGetOutput(Output, out _)); Assert.Equal(0L, storage.OpenLeaseCount);
        PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(storage, sources, Output,
            strict with { RecoveryMode = QpdfRecoveryMode.BoundedRepair }, CancellationToken.None);
        Assert.Contains(result.Warnings, warning => warning.Code == QpdfDiagnosticCode.RecoveryEngaged);
        Assert.True(storage.TryGetOutput(Output, out byte[] output));
        storage.AddInput(Second, output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(storage, Second, QpdfPassword.FromText("reader"), QpdfLimits.Default, CancellationToken.None))
        { Assert.Equal(2, document.PageCount); Assert.Equal(6, document.Encryption?.Revision); }
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public async Task EveryRepairMergeAdmissionRefusalPreservesPriorOutput()
    {
        int growths;
        using (InMemoryQpdfHost baseline = Storage())
        using (RecordingMemoryBudget budget = new(8L << 20))
        {
            using (IMemoryScope owner = budget.CreateScope())
            {
                _ = await Qpdf.SelectPagesAsync(new MemoryBudgetHost(baseline, owner), Sources(), Output,
                    new PdfPageMergeOptions { RecoveryMode = QpdfRecoveryMode.BoundedRepair }, CancellationToken.None);
                growths = budget.GrowthCalls;
            }
            Assert.Equal(0L, budget.ReservedBytes);
        }
        Assert.InRange(growths, 1, 64);
        for (int fail = 1; fail <= growths; fail++)
        {
            using InMemoryQpdfHost storage = Storage();
            await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(Output, CancellationToken.None))
            { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
            using RecordingMemoryBudget budget = new(8L << 20) { RefuseOnGrowth = fail };
            using (IMemoryScope owner = budget.CreateScope())
            {
                await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => Qpdf.SelectPagesAsync(new MemoryBudgetHost(storage, owner), Sources(), Output,
                    new PdfPageMergeOptions { RecoveryMode = QpdfRecoveryMode.BoundedRepair }, CancellationToken.None).AsTask());
            }
            Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0L, storage.OpenLeaseCount);
            Assert.True(storage.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        }
    }

    [Fact]
    public async Task CancellationAfterSecondaryReadRetiresRecoveredPrimaryWithoutPublication()
    {
        using InMemoryQpdfHost storage = Storage(); using CancellationTokenSource cancellation = new();
        ObservedHost host = new(storage, path => { if (path.Equals(Second)) { cancellation.Cancel(); } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Qpdf.SelectPagesAsync(host, Sources(), Output,
            new PdfPageMergeOptions { RecoveryMode = QpdfRecoveryMode.BoundedRepair }, cancellation.Token).AsTask());
        Assert.Equal(1, host.Opens[First]); Assert.Equal(1, host.Opens[Second]);
        Assert.Equal(0L, storage.OpenLeaseCount); Assert.False(storage.TryGetOutput(Output, out _));
    }
}

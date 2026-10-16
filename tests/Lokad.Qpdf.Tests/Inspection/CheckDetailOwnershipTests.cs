using Lokad.Qpdf.Tests.Foundation;
using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class CheckDetailOwnershipTests
{
    private OwnershipBatch.Observation? _collection;

    private static void Collect(ResourceGovernor governor)
    {
        // Observe CLR references independently of coarse allowance ownership.
        for (int iteration = 0; iteration < 3; iteration++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            governor.ReserveMemory(0L);
        }
    }

    private static byte[] Fixture()
        => CheckTests.Assemble(CheckTests.SinglePage("<< /Length 0 >>", []), 1);

    private sealed class Alias(object value, WeakReference[] references, int detailLength)
    {
        internal object? Value = value;
        internal readonly WeakReference[] References = references;
        internal readonly int DetailLength = detailLength;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Capture(ResourceGovernor governor, QpdfLimits limits, byte[] prepared, string kind)
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("check-detail.pdf");
        host.AddInput(path, prepared, QpdfInputOwnership.Transfer);
        DiagnosticBag diagnostics = new(governor);
        QpdfDocument document = QpdfDocument.OpenCore(host, path, limits, governor, diagnostics,
            QpdfRecoveryMode.Strict, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        try
        {
            List<QpdfCheckOutcome> outcomes = DocumentCheck.Run(document, new QpdfCheckOptions(), limits,
                governor, diagnostics, false, CancellationToken.None);
            QpdfCheckOutcome selected = outcomes.Single(outcome => outcome.Name == "object-graph");
            QpdfCheckOutcome sibling = outcomes.Single(outcome => outcome.Name == "cross-reference");
            object value = kind == "list" ? outcomes : kind == "record" ? selected : selected.Detail;
            return new(value, [new(document), new(outcomes), new(selected), new(selected.Detail), new(sibling), new(sibling.Detail)], selected.Detail.Length);
        }
        finally
        {
            document.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyAndClear(Alias alias, ResourceGovernor governor, string kind)
    {
        Assert.False(alias.References[0].IsAlive);
        Assert.Equal(kind == "list", alias.References[1].IsAlive);
        Assert.Equal(kind != "detail", alias.References[2].IsAlive);
        Assert.True(alias.References[3].IsAlive);
        Assert.Equal(kind == "list", alias.References[4].IsAlive);
        Assert.Equal(kind == "list", alias.References[5].IsAlive);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 2L * alias.DetailLength + (kind == "detail" ? 0L : 40L));
        string detail = alias.Value is string text ? text : alias.Value is QpdfCheckOutcome outcome ? outcome.Detail
            : ((List<QpdfCheckOutcome>)(alias.Value ?? throw new InvalidOperationException("Missing alias."))).Single(entry => entry.Name == "object-graph").Detail;
        Assert.Equal("6 objects resolved with 1 streams.", detail);
        alias.Value = null;
    }

    [Theory]
    [InlineData("list")]
    [InlineData("record")]
    [InlineData("detail")]
    public void ReturnedAliasesRetireOnlyTheirDeadParentsAndSiblings(string kind)
    {
        byte[] prepared = Fixture();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        Alias alias = Capture(governor, limits, prepared, kind);
        Collect(governor);
        VerifyAndClear(alias, governor, kind);
        Collect(governor);
        Assert.All(alias.References, reference => Assert.False(reference.IsAlive));
        governor.ReserveMemory(0L);
        governor.CompletePrivateStorage();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        GC.KeepAlive(governor);
    }

    internal async Task ThirtyTwoChecksKeepMetadataWithinThe32KiBCeiling()
    {
        byte[] prepared = Fixture();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        for (int iteration = 0; iteration < 32; iteration++)
        {
            ResourceGovernor governor = new(limits, CancellationToken.None);
            Alias alias = Capture(governor, limits, prepared, "detail");
            await (_collection ?? throw new InvalidOperationException("Missing stress batch.")).CollectAsync(governor, 3, 3, false, true);
            VerifyAndClear(alias, governor, "detail");
            await (_collection ?? throw new InvalidOperationException("Missing stress batch.")).CollectAsync(governor, 3, 3, false, true);
            Assert.All(alias.References, reference => Assert.False(reference.IsAlive));
            governor.ReserveMemory(0L);
            governor.CompletePrivateStorage();
            Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(0L, governor.Snapshot().DiagnosticCount);
            Assert.InRange(governor.Snapshot().PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
        }
    }

    [Fact]
    public async Task SuccessfulPublicCheckKeepsTheOneByteWarningAllowanceUnused()
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("check.pdf");
        host.AddInput(path, Fixture(), QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnostics = 1L, MaxDiagnosticBytes = 1L };
        QpdfCheckResult result = await Qpdf.CheckAsync(host, path, new QpdfCheckOptions(), limits, CancellationToken.None);
        Assert.True(result.Passed);
        Assert.False(result.HasSkipped);
        Assert.Equal(9, result.Checks.Count);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, result.Counters.DiagnosticCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task UnsupportedNamesJoinOnceInFirstOccurrenceOrder()
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects = CheckTests.SinglePage("<< /Length 0 >>", []);
        objects.Add((6, "<< /Length 0 /Filter /MadeUp >>", []));
        objects.Add((7, "<< /Length 0 /Filter /Other >>", []));
        objects.Add((8, "<< /Length 0 /Filter /MadeUp >>", []));
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("unsupported.pdf");
        host.AddInput(path, CheckTests.Assemble(objects, 1), QpdfInputOwnership.Copy);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, path, CancellationToken.None);
        QpdfCheckOutcome outcome = Assert.Single(result.Checks, check => check.Name == "streams");
        Assert.Equal(QpdfCheckStatus.Skipped, outcome.Status);
        Assert.Equal("0 decoded, 1 unfiltered, 0 above level All. 3 skipped (MadeUp, Other unavailable).", outcome.Detail);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckSkipped
            && diagnostic.Message == "Skipped check 'streams': " + outcome.Detail + " Decoding unavailable filters arrives in a later milestone.");
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task PageContentFailureDetailsKeepTheirOrderAndEightFailureLimit()
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects = CheckTests.SinglePage("<< /Length 1 >>", Encoding.ASCII.GetBytes("("));
        string references = "4 0 R " + string.Join(" ", Enumerable.Range(6, 11).Select(number => number + " 0 R"));
        objects[2] = (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents [" + references + "] >>", null);
        for (int number = 6; number < 17; number++) { objects.Add((number, "<< /Length 1 >>", Encoding.ASCII.GetBytes("("))); }
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("damaged-content.pdf");
        host.AddInput(path, CheckTests.Assemble(objects, 1), QpdfInputOwnership.Copy);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, path, CancellationToken.None);
        QpdfCheckOutcome outcome = Assert.Single(result.Checks, check => check.Name == "page-content");
        Assert.Equal(QpdfCheckStatus.Failed, outcome.Status);
        Assert.Equal(string.Join(" ", Enumerable.Repeat("Page 1 content: Unterminated string in content stream. (offset 0).", 8)), outcome.Detail);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.CheckFailed
            && diagnostic.Message == "Check 'page-content' failed: " + outcome.Detail);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    internal static IEnumerable<(string Id, Func<OwnershipBatch.Observation, Task> Run)> StressCases()
        => (new object[][] { [] }).Select(row =>
            (OwnershipBatch.Id(nameof(CheckDetailOwnershipTests), nameof(ThirtyTwoChecksKeepMetadataWithinThe32KiBCeiling), row),
            (Func<OwnershipBatch.Observation, Task>)(async observation =>
            {
                CheckDetailOwnershipTests fixture = new() { _collection = observation };
                await fixture.ThirtyTwoChecksKeepMetadataWithinThe32KiBCeiling();
            })));
}

using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonPageTreeFailureTests
{
    private sealed class Host(IMemoryBudget? budget, string fault, CancellationTokenSource? cancel)
        : IQpdfHost, IQpdfMemoryHost, IDisposable
    {
        internal readonly InMemoryQpdfHost Inner = new();
        public IMemoryBudget? MemoryBudget => budget;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            IQpdfInput input = await Inner.OpenInputAsync(path, token);
            return path.Value == "json" ? new Input(input, fault, cancel) : input;
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Output(await Inner.CreateOutputAsync(path, token), fault);
        public void Dispose() => Inner.Dispose();

        private sealed class Input(IQpdfInput inner, string fault, CancellationTokenSource? cancel) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                int read = await inner.ReadAsync(offset, destination, token);
                if (fault == "cancel") { cancel?.Cancel(); }
                if (fault == "read") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
                return read;
            }
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                if (fault == "close") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
            }
        }

        private sealed class Output(IQpdfOutputTransaction inner, string fault) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
                => fault == "write" ? throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed) : inner.WriteAsync(data, token);
            public ValueTask CommitAsync(CancellationToken token)
                => fault == "commit" ? throw new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    public static IEnumerable<object[]> Failures()
    {
        foreach (bool update in new[] { false, true })
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string fault in new[] { "read", "close", "cancel", "write", "commit" })
        { yield return [update, route, fault]; }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task FailuresRetainPriorOutputAndReleaseStorage(bool update, string route, string fault)
    {
        RecordingMemoryBudget budget = new(16777216L);
        using (IMemoryScope caller = budget.CreateScope())
        using (CancellationTokenSource cancel = new())
        using (Host host = new(caller, fault, cancel))
        {
            JsonPageTreeTests.Register(host.Inner, JsonPageTreeTests.Fixture(1, "indirect", false)); await JsonPageTreeTests.Seed(host.Inner);
            if (route == "typed")
            {
                if (fault == "cancel")
                { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JsonPageTreeTests.Typed(host, update, QpdfLimits.Default, cancel.Token)); }
                else
                { await Assert.ThrowsAsync<QpdfHostException>(() => JsonPageTreeTests.Typed(host, update, QpdfLimits.Default, cancel.Token)); }
            }
            else
            {
                QpdfExecutionResult result = await JsonPageTreeTests.Execute(host, update, route, QpdfLimits.Default, cancel.Token);
                Assert.Equal(fault == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
                Assert.Equal(0L, result.Counters.OpenHandles);
                if (fault != "cancel") { Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.HostOperationFailed); }
            }
            JsonPageTreeTests.AssertPrior(host.Inner);
        }
        Assert.Equal(0L, budget.ReservedBytes);
    }

    public static IEnumerable<object[]> Refusals()
    {
        foreach (bool update in new[] { false, true })
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string layout in new[] { "indirect", "count-mismatch" })
        { yield return [update, route, layout]; }
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task EverySharedRefusalPrecedesPublication(bool update, string route, string layout)
    {
        int growths;
        RecordingMemoryBudget baseline = new(16777216L);
        using (IMemoryScope caller = baseline.CreateScope())
        using (Host host = new(caller, string.Empty, null))
        {
            JsonPageTreeTests.Register(host.Inner, JsonPageTreeTests.Fixture(1, layout, false)); await JsonPageTreeTests.Seed(host.Inner);
            if (route == "typed")
            { Assert.Equal(1, (await JsonPageTreeTests.Typed(host, update, QpdfLimits.Default, CancellationToken.None)).PageCount); }
            else
            {
                QpdfExecutionResult result = await JsonPageTreeTests.Execute(host, update, route, QpdfLimits.Default, CancellationToken.None);
                Assert.Equal(layout == "count-mismatch" ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
            }
            growths = baseline.GrowthCalls; Assert.Equal(0, host.Inner.OpenLeaseCount);
        }
        Assert.True(growths > 0); Assert.Equal(0L, baseline.ReservedBytes);
        for (int refused = 1; refused <= growths; refused++)
        {
            RecordingMemoryBudget budget = new(16777216L) { RefuseOnGrowth = refused };
            using (IMemoryScope caller = budget.CreateScope())
            using (Host host = new(caller, string.Empty, null))
            {
                JsonPageTreeTests.Register(host.Inner, JsonPageTreeTests.Fixture(1, layout, false)); await JsonPageTreeTests.Seed(host.Inner);
                if (route == "typed")
                { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => JsonPageTreeTests.Typed(host, update, QpdfLimits.Default, CancellationToken.None)); }
                else
                {
                    QpdfExecutionResult result = await JsonPageTreeTests.Execute(host, update, route, QpdfLimits.Default, CancellationToken.None);
                    Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
                    Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
                    Assert.Equal(0L, result.Counters.OpenHandles);
                }
                Assert.Equal(refused, budget.GrowthCalls); JsonPageTreeTests.AssertPrior(host.Inner);
            }
            Assert.Equal(0L, budget.ReservedBytes);
        }
    }

    private sealed class CancelingResolver(IReadOnlyDictionary<int, QpdfObject> values, CancellationTokenSource cancel) : IIndirectResolver
    {
        internal bool ReachedName;
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        {
            bool found = values.TryGetValue(reference.ObjectNumber, out value);
            if (reference.ObjectNumber == 4) { ReachedName = true; cancel.Cancel(); }
            return found;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedTraversalObservesEitherCancellationAfterIndirectResolution(bool governorToken)
    {
        using CancellationTokenSource cancel = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancel.Token : CancellationToken.None);
        PdfDictionary root = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Type")] = PdfName.FromAscii("Catalog"), [PdfName.FromAscii("Pages")] = new PdfReference(2, 0) });
        PdfDictionary pages = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Type")] = PdfName.FromAscii("Pages"), [PdfName.FromAscii("Kids")] = new PdfArray([new PdfReference(3, 0)]), [PdfName.FromAscii("Count")] = new PdfInteger("1", 1) });
        PdfDictionary page = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Type")] = new PdfReference(4, 0) });
        CancelingResolver resolver = new(new Dictionary<int, QpdfObject> { [2] = pages, [3] = page, [4] = PdfName.FromAscii("Page") }, cancel);
        long before = governor.Snapshot().LiveMemoryBytes;
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            Assert.ThrowsAny<OperationCanceledException>(() => PageTree.Read(resolver, root, new DiagnosticBag(governor), governor,
                QpdfLimits.Default, owner, governorToken ? CancellationToken.None : cancel.Token));
            Assert.True(resolver.ReachedName);
        }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }
}

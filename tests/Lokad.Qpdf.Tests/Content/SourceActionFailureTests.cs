using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class SourceActionFailureTests
{
    public static IEnumerable<object[]> PlacementFailures()
    {
        foreach (string feature in new[] { "bad-base", "non-ascii-uri", "missing-destination", "valid-uri", "valid-destination" })
        foreach (bool underlay in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [feature, underlay, job]; }
    }

    [Theory]
    [MemberData(nameof(PlacementFailures))]
    public async Task PlacementPreservesSemanticFailureCodesWithoutOptionalRecovery(string feature, bool underlay, bool job)
    {
        bool destination = feature.EndsWith("destination", StringComparison.Ordinal), valid = feature.StartsWith("valid", StringComparison.Ordinal);
        string context = destination ? "/Names << /Dests << /Names [(" + (valid ? "Here" : "Other") + ") [3 0 R /Fit]] >> >>"
            : "/URI << /Base " + (feature == "bad-base" ? "42" : "(https://source.test/)") + " >>";
        string action = destination ? "/S /GoTo /D (Here)" : "/S /URI /URI " + (feature == "non-ascii-uri" ? "<e9>" : "(relative.pdf)");
        byte[] placed = Tests.Inspection.CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + context + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Link /Border [0 0 0] /Rect [10 20 30 40] /A << " + action + " >> >>", null)], 1);
        RecordingMemoryBudget budget = new(16L << 20);
        using (IMemoryScope owner = budget.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        {
            host.AddInput(Input, Tests.Inspection.CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("placed"), placed, QpdfInputOwnership.Transfer); await Seed(host);
            Dictionary<string, object> request = new() { ["inputFile"] = "input", ["outputFile"] = "output", [underlay ? "underlay" : "overlay"] = new { file = "placed" } };
            QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, System.Text.Json.JsonSerializer.Serialize(request), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", underlay ? "--underlay" : "--overlay", "placed", "--", "output"]), CancellationToken.None);
            Assert.Equal(valid ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
            Assert.True(host.TryGetOutput(Output, out byte[] output));
            if (valid) { Assert.Empty(result.Diagnostics); Assert.NotEqual("prior"u8.ToArray(), output); }
            else
            {
                Assert.Equal("prior"u8.ToArray(), output);
                Assert.Contains(result.Diagnostics, d => d.Code == (feature == "non-ascii-uri" ? QpdfDiagnosticCode.UnsupportedFeature : QpdfDiagnosticCode.InvalidArguments));
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");

    private static async Task<QpdfExecutionResult?> Run(IQpdfHost host, string route, CancellationToken token)
    {
        if (route == "typed") { await Qpdf.SelectPageAsync(host, Input, Output, new(1), token); return null; }
        return route == "argv" ? await Qpdf.ExecuteAsync(host, new(["input", "output"]), token)
            : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\"}", token);
    }

    private static async Task Seed(InMemoryQpdfHost storage)
    {
        await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(Output, CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    public static IEnumerable<object[]> InvalidBases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string basis in new[] { "42", "(../relative/)", "<feff00680074>", "(D:\\134folder\\134file)", "(https://[invalid/)" })
        { yield return [route, basis]; }
    }

    [Theory]
    [MemberData(nameof(InvalidBases))]
    public async Task AnUnusableExplicitBaseCannotPublishChangedMeaning(string route, string basis)
    {
        RecordingMemoryBudget budget = new(16L << 20); using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost storage = new(); storage.AddInput(Input, SourceActionRoleTests.Fixture(true, basis + " /Source 1 0 R"), QpdfInputOwnership.Transfer);
        await Seed(storage); View host = new(storage, owner, "ordinary");
        if (route == "typed")
        {
            QpdfException error = await Assert.ThrowsAnyAsync<QpdfException>(() => Run(host, route, CancellationToken.None));
            if (basis == "42") { Assert.IsType<QpdfParseException>(error); } else { Assert.IsType<QpdfUnsupportedFeatureException>(error); }
        }
        else
        {
            QpdfExecutionResult? result = await Run(host, route, CancellationToken.None); Assert.NotNull(result); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, d => d.Code == (basis == "42" ? QpdfDiagnosticCode.InvalidArguments : QpdfDiagnosticCode.UnsupportedFeature));
        }
        Assert.True(storage.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(0, host.Created); Assert.Equal(0L, storage.OpenLeaseCount);
        storage.Dispose(); owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData("typed", "missing")]
    [InlineData("argv", "missing")]
    [InlineData("job", "missing")]
    [InlineData("typed", "cycle")]
    [InlineData("argv", "cycle")]
    [InlineData("job", "cycle")]
    [InlineData("typed", "odd")]
    [InlineData("argv", "odd")]
    [InlineData("job", "odd")]
    [InlineData("typed", "key-type")]
    [InlineData("argv", "key-type")]
    [InlineData("job", "key-type")]
    [InlineData("typed", "child-type")]
    [InlineData("argv", "child-type")]
    [InlineData("job", "child-type")]
    public async Task UnresolvedOrMalformedSourceDestinationsCannotPublish(string route, string shape)
    {
        string tree = shape switch { "cycle" => "<< /Kids [6 0 R] /Source 1 0 R >>", "odd" => "<< /Names [(Here)] /Source 1 0 R >>",
            "key-type" => "<< /Names [42 [3 0 R /Fit]] /Source 1 0 R >>", "child-type" => "<< /Kids [42] /Source 1 0 R >>", _ => "<< /Names [(Other) [3 0 R /Fit]] /Source 1 0 R >>" };
        byte[] source = Tests.Inspection.CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 6 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Link /Rect [10 10 20 20] /A 5 0 R >>", null),
            (5, "<< /S /GoTo /D (Here) >>", null), (6, tree, null)], 1);
        RecordingMemoryBudget budget = new(16L << 20); using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost storage = new(); storage.AddInput(Input, source, QpdfInputOwnership.Transfer); await Seed(storage);
        View host = new(storage, owner, "ordinary");
        if (route == "typed") { await Assert.ThrowsAsync<QpdfParseException>(() => Run(host, route, CancellationToken.None)); }
        else
        {
            QpdfExecutionResult? result = await Run(host, route, CancellationToken.None); Assert.NotNull(result); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
        }
        Assert.Equal(0, host.Created); Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.True(storage.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        storage.Dispose(); owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData("typed", "write")]
    [InlineData("argv", "write")]
    [InlineData("job", "write")]
    [InlineData("typed", "commit")]
    [InlineData("argv", "commit")]
    [InlineData("job", "commit")]
    public async Task OutputFailureRefundsBoundRolesAndKeepsThePriorTarget(string route, string failure)
    {
        RecordingMemoryBudget budget = new(16L << 20); using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost storage = new(); storage.AddInput(Input, SourceActionRoleTests.Fixture(true, "(https://source.test/base/) /Source 1 0 R"), QpdfInputOwnership.Transfer);
        await Seed(storage); View host = new(storage, owner, failure);
        if (route == "typed") { await Assert.ThrowsAsync<QpdfHostException>(() => Run(host, route, CancellationToken.None)); }
        else
        {
            QpdfExecutionResult? result = await Run(host, route, CancellationToken.None); Assert.NotNull(result); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.HostOperationFailed);
        }
        Assert.True(host.Created > 0); Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.True(storage.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        storage.Dispose(); owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData("typed", "actions")]
    [InlineData("argv", "actions")]
    [InlineData("job", "actions")]
    [InlineData("typed", "layers")]
    [InlineData("argv", "layers")]
    [InlineData("job", "layers")]
    public async Task EveryObservedAdmissionRefusalKeepsTheTargetAndRefunds(string route, string feature)
    {
        async Task<(int Growths, bool Refused)> Attempt(int failAt)
        {
            RecordingMemoryBudget budget = new(16L << 20) { RefuseOnGrowth = failAt }; using IMemoryScope owner = budget.CreateScope();
            using InMemoryQpdfHost storage = new();
            byte[] fixture = feature == "layers" ? OptionalContentTests.Fixture(true, "static", false)
                : SourceActionRoleTests.Fixture(true, "(https://source.test/base/) /Source 1 0 R");
            storage.AddInput(Input, fixture, QpdfInputOwnership.Transfer);
            await Seed(storage); View host = new(storage, owner, "ordinary"); bool refused = false;
            try
            {
                QpdfExecutionResult? result = await Run(host, route, CancellationToken.None);
                refused = result?.ExitCode == QpdfExitCode.Error;
                if (refused) { Assert.Contains(result?.Diagnostics ?? [], d => d.Code == QpdfDiagnosticCode.BudgetExceeded); }
            }
            catch (QpdfMemoryAdmissionException) { Assert.Equal("typed", route); refused = true; }
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.True(storage.TryGetOutput(Output, out byte[] output));
            if (failAt == 0) { Assert.False(refused); Assert.NotEqual("prior"u8.ToArray(), output); }
            else { Assert.True(refused); Assert.Equal("prior"u8.ToArray(), output); }
            int growths = budget.GrowthCalls; storage.Dispose(); owner.Dispose();
            Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations); return (growths, refused);
        }
        (int growths, _) = await Attempt(0); Assert.InRange(growths, 2, 64);
        for (int index = 1; index <= growths; index++) { await Attempt(index); }
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task CancellationAtUriScratchAdmissionClosesTheBorrowBeforeCleanup(string route)
    {
        RecordingMemoryBudget budget = new(16L << 20); using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost storage = new();
        storage.AddInput(Input, SourceActionRoleTests.Fixture(true, "(https://source.test/" + new string('a', 10000) + "/) /Source 1 0 R"), QpdfInputOwnership.Transfer);
        await Seed(storage); using CancellationTokenSource cancel = new();
        CancelBudget memory = new(owner, cancel.Cancel); View host = new(storage, memory, "ordinary");
        if (route == "typed") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(host, route, cancel.Token)); }
        else
        { QpdfExecutionResult? result = await Run(host, route, cancel.Token); Assert.NotNull(result); Assert.Equal(QpdfExecutionStatus.Canceled, result.Status); }
        Assert.True(memory.Triggered); Assert.Equal(0, host.Created); Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.True(storage.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        storage.Dispose(); owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothTokensCancelDestinationBindingAfterAdmissionWithoutPublishingHeaders(bool caller)
    {
        string actions = string.Join(" ", Enumerable.Range(0, 512).Select(i => "<< /S /GoTo /D [3 0 R /XYZ " + i + " 60 1] >>"));
        byte[] source = Tests.Inspection.CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [10 20 110 220] /Resources << >> /Rotate 90 >>", null),
            (4, "<< /S /GoTo /D [3 0 R /XYZ 25 60 1] /Next [" + actions + "] >>", null)], 1);
        RecordingMemoryBudget root = new(64L << 20);
        using (IMemoryScope owner = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        using (InMemoryQpdfHost storage = new())
        {
            storage.AddInput(Input, source, QpdfInputOwnership.Transfer);
            CancelBudget budget = new(owner, cancellation.Cancel) { Armed = false };
            View host = new(storage, budget, "ordinary");
            ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token, host);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(storage, Input, CancellationToken.None))
            using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
            {
                GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 3, memory);
                var page = document.PageReferences[0]; copier.RegisterPageRoot(page.Source?.ObjectNumber);
                var copied = copier.CopyPageRoot(page.Page, page.Source?.ObjectNumber, new Dictionary<PdfName, QpdfObject>(), false, null);
                var catalog = CatalogCopy.TryCopyExtras(document.Engine, copier, true, false, governor, memory, CancellationToken.None);
                var forms = ParserContainers.CreateDictionary<GraphCopier, CopiedForm?>(memory);
                var rotated = FlattenRotation.Apply([(copier, copied.Id)], forms, false, null, QpdfLimits.Default, governor, new(governor), CancellationToken.None, copier.NextId, memory);
                Assert.NotNull(rotated.Frames);
                Dictionary<int, QpdfObject> pool = ParserContainers.CreateDictionary<int, QpdfObject>(memory);
                foreach (var pair in copier.CopiedObjects) { ParserContainers.EnsureEntryCapacity(memory, pool, IntPtr.Size); pool.Add(pair.Key, pair.Value); }
                QpdfObject original = pool[copied.Id]; int count = pool.Count;
                budget.Armed = true;
                Assert.ThrowsAny<OperationCanceledException>(() => SourceActions.BindRotation(copier, pool, [new(copied.Id, 0)], rotated.Frames,
                    null, null, catalog.Extras, null, governor, memory, caller ? cancellation.Token : CancellationToken.None));
                Assert.True(budget.Triggered); Assert.Same(original, pool[copied.Id]); Assert.Equal(count, pool.Count); Assert.Equal(0, host.Created);
            }
            governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0, root.LiveReservations);
    }

    [Fact]
    public async Task AuthoredUriBytesRemainFundedAfterDocumentAndHostClose()
    {
        RecordingMemoryBudget budget = new(16L << 20); using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost storage = new(QpdfLimits.Default, null, owner);
        storage.AddInput(Input, SourceActionRoleTests.Fixture(true, "(https://source.test/base/)"), QpdfInputOwnership.Transfer);
        await Qpdf.SelectPageAsync(storage, Input, Output, new(1), CancellationToken.None);
        Assert.True(storage.TryGetOutput(Output, out byte[] output)); storage.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        PdfByteString escaped;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(storage, QpdfPath.From("read"), CancellationToken.None))
        {
            QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
            PdfDictionary page = document.PageReferences[0].Page; Assert.True(page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject raw));
            PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(Assert.IsType<PdfArray>(Resolve(raw))[0]));
            Assert.True(widget.TryGet(PdfName.FromAscii("A"), out raw)); PdfDictionary action = Assert.IsType<PdfDictionary>(Resolve(raw));
            Assert.True(action.TryGet(PdfName.FromAscii("URI"), out raw)); escaped = Assert.IsType<PdfByteString>(Resolve(raw));
        }
        storage.Dispose(); Assert.True(budget.ReservedBytes > 0L);
        Assert.Equal("https://source.test/base/item.pdf"u8.ToArray(), escaped.Value);
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    private sealed class View(InMemoryQpdfHost storage, IMemoryBudget memory, string fault) : IQpdfHost, IQpdfMemoryHost
    {
        internal int Created { get; private set; }
        public IMemoryBudget? MemoryBudget => memory;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created++; return new FaultOutput(await storage.CreateOutputAsync(path, token), fault); }
    }

    private sealed class FaultOutput(IQpdfOutputTransaction inner, string fault) : IQpdfOutputTransaction
    {
        public QpdfPath Path => inner.Path;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token) => fault == "write"
            ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed)) : inner.WriteAsync(data, token);
        public ValueTask CommitAsync(CancellationToken token) => fault == "commit"
            ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed)) : inner.CommitAsync(token);
        public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class CancelBudget(IMemoryBudget inner, Action cancel) : IMemoryBudget
    {
        internal bool Triggered { get; private set; }
        internal bool Armed { get; set; } = true;
        public IMemoryScope CreateScope() => inner.CreateScope();
        private void Observe(long bytes) { if (Armed && !Triggered && bytes >= 128L * 1024L) { Triggered = true; cancel(); } }
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!inner.TryReserve(bytes, out IMemoryReservation? admitted)) { reservation = null; return false; }
            reservation = new CancelReservation(admitted, this); Observe(bytes); return true;
        }
        private sealed class CancelReservation(IMemoryReservation inner, CancelBudget parent) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes) { bool accepted = inner.TryResize(bytes); if (accepted) { parent.Observe(bytes); } return accepted; }
            public void Dispose() => inner.Dispose();
        }
    }
}

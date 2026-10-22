using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Tests.Reading;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class TypedWarningOwnershipTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");
    private static readonly string[] Routes = ["typed-page", "typed-page-rotation", "typed-pages", "typed-pages-rotation",
        "typed-pages-options", "typed-split", "typed-json", "typed-json-sidecars",
        "typed-update", "typed-extract", "typed-add", "typed-remove", "typed-copy"];
    private static object Field(object value, string name) => value.GetType().GetField(name, Members)?.GetValue(value)
        ?? throw new InvalidOperationException(name);
    private static object Read(object value, string name) => value.GetType().GetProperty(name, Members)?.GetValue(value)
        ?? throw new InvalidOperationException(name);

    private static byte[] Fixture(bool compressed)
        => (byte[])(typeof(DocumentWarningSnapshotTests).GetMethod("OriginalFixture", Members)?.Invoke(null, [compressed])
            ?? throw new InvalidOperationException("Missing preserved warning fixture."));

    private static async Task<object> Prepare(string route, bool compressed, string boundary, string outcome)
    {
        string originalRoute = route is "typed-split" ? "typed-pages-options"
            : route.StartsWith("typed-json", StringComparison.Ordinal) ? "typed-json" : route;
        Task task = (Task)(typeof(TypedCounterOwnershipTests).GetMethod("Prepare", Members)?.Invoke(null,
            [originalRoute, compressed, boundary, outcome]) ?? throw new InvalidOperationException());
        await task;
        object host = Read(task, "Result");
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Read(host, "Inner");
        byte[] bytes = Fixture(compressed);
        if (route is "typed-extract" or "typed-remove" or "typed-copy")
        {
            List<(string Dict, byte[]? StreamData)> extras = [
                ("<< /Type /EmbeddedFile /Length 4 /Filter /Bogus >>", "q Q\n"u8.ToArray()),
                ("<< /Type /Filespec /F (payload.bin) /EF << /F 6 0 R >> >>", null),
                ("<< /Names [(payload) 7 0 R] >>", null)];
            bytes = (byte[])(typeof(AttachmentTests).GetMethod("BuildPdf", Members)?.Invoke(null,
                [" /Names << /EmbeddedFiles 8 0 R >>", "", extras]) ?? throw new InvalidOperationException());
            int at = bytes.AsSpan().IndexOf("/Count 1"u8); Assert.True(at >= 0); bytes[at + 7] = (byte)'2';
        }
        inner.AddInput(Input, bytes, QpdfInputOwnership.Copy);
        inner.AddInput(QpdfPath.From("second.pdf"), bytes, QpdfInputOwnership.Copy);
        if (route == "typed-split")
        {
            await using IQpdfOutputTransaction seed = await inner.CreateOutputAsync(QpdfPath.From("output-1.pdf"), CancellationToken.None);
            await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        }
        return host;
    }

    private static async Task<object> Start(IQpdfHost host, string route, QpdfLimits limits, CancellationToken token)
    {
        if (route == "typed-split")
        {
            return await Qpdf.SelectPagesAsync(host, [new PdfPageSource(Input, new PdfPageSelectionOptions(1))], Output,
            new PdfPageMergeOptions { SplitPages = 1 }, limits, token);
        }
        if (route == "typed-json-sidecars")
        { return await Qpdf.GetDocumentJsonAsync(host, Input, Output, new QpdfJsonOptions(2, [], "file", "stream", [], false), limits, token); }
        Task<object> task = (Task<object>)(typeof(TypedCounterOwnershipTests).GetMethod("Start", Members)?.Invoke(null,
            [host, route, limits, token]) ?? throw new InvalidOperationException());
        return await task;
    }

    private static void Resume(object host) => host.GetType().GetMethod("Resume", Members)?.Invoke(host, []);
    private static ResourceGovernor Governor(Task task) => (ResourceGovernor)(typeof(ExecutionCounterTests)
        .GetMethod("FindGovernor", Members)?.Invoke(null, [task, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
        ?? throw new InvalidOperationException("Missing complete-job governor."));
    private static Array Backing(object view) => view as Array
        ?? view.GetType().GetFields(Members).Select(f => f.GetValue(view)).OfType<Array>().Single();

    private sealed class Aliases(ResourceGovernor governor, object value, object? sibling,
        QpdfDiagnostic[] elements, bool admitted,
        byte[] output)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal object? Value = value, Sibling = sibling;
        internal readonly QpdfDiagnostic[] Elements = elements;
        internal readonly bool Admitted = admitted;
        internal readonly byte[] Output = output;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Aliases Keep(object result, object host, ResourceGovernor governor, string kind)
    {
        IReadOnlyList<QpdfDiagnostic> warnings = (IReadOnlyList<QpdfDiagnostic>)Read(result, "Warnings");
        Assert.NotEmpty(warnings);
        Array backing = Backing(warnings);
        bool admitted = governor.CallerStorageBytes + governor.PrivateRetainedBytes >= 56L;
        QpdfDiagnostic[] elements = warnings.ToArray();
        object value = kind switch
        {
            "view" => warnings,
            "element" => warnings[0],
            "caller-copy" => elements,
            "record-copy" => warnings[0] with { },
            _ => backing
        };
        object? sibling = kind == "sibling" ? result : null;
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Read(host, "Inner");
        Assert.Equal(0L, inner.OpenLeaseCount); Assert.Equal(0L, governor.Snapshot().OpenHandles);
        QpdfPath path = result is PdfMultiPageSelectionResult multiple ? Assert.Single(multiple.OutputPaths) : Output;
        Assert.True(inner.TryGetOutput(path, out byte[] output));
        return new(governor, value, sibling, elements, admitted, output);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Aliases> Complete(string route, bool compressed, string kind)
    {
        object host = await Prepare(route, compressed, "read", "success");
        Task<object> task = Start((IQpdfHost)host, route, QpdfLimits.Default, CancellationToken.None);
        try
        {
            await ((Task)Read(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = Governor(task); Resume(host);
            object result = await task.WaitAsync(TimeSpan.FromSeconds(10));
            return Keep(result, host, governor, kind);
        }
        finally { Resume(host); await task; }
    }

    public static IEnumerable<object[]> PublicJobs()
    { foreach (string route in Routes) foreach (bool compressed in new[] { false, true }) { yield return [route, compressed]; } }

    [Theory]
    [MemberData(nameof(PublicJobs))]
    [Trait("Layer", "Contract")]
    public async Task CompleteJobsFundWarningBackingIndependently(string route, bool compressed)
    {
        Aliases aliases = await Complete(route, compressed, "backing");
        Assert.True(aliases.Admitted, "Public warning view and backing need a durable coarse owner.");
        long before = aliases.Governor.Snapshot().LiveMemoryBytes;
        Validate(aliases); aliases.Value = null;
        long after = aliases.Governor.Snapshot().LiveMemoryBytes;
        Assert.Equal(before, after);
        Assert.All(aliases.Elements, x => Assert.False(string.IsNullOrEmpty(x.Message)));
        GC.KeepAlive(aliases.Elements); GC.KeepAlive(aliases.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Layer", "Ownership")]
    public async Task WarningElementsSurviveTheirOriginalView(bool compressed)
    {
        Aliases aliases = await Complete("typed-page", compressed, "backing");
        QpdfDiagnostic warning = Assert.Single(aliases.Elements);
        Assert.Equal(QpdfDiagnosticCode.PageCountMismatch, warning.Code);
        Assert.Equal("A /Pages /Count of 2 disagrees with traversal (1); traversal wins.", warning.Message);
        long before = aliases.Governor.Snapshot().LiveMemoryBytes; Validate(aliases); aliases.Value = null;
        long after = aliases.Governor.Snapshot().LiveMemoryBytes;
        Assert.Equal(before, after);
        InMemoryQpdfHost validation = new(); validation.AddInput(Input, aliases.Output, QpdfInputOwnership.Copy);
        QpdfCheckResult check = await Qpdf.CheckAsync(validation, Input, CancellationToken.None);
        Assert.True(check.Passed); Assert.Equal(1, check.PageCount); Assert.Equal(0L, validation.OpenLeaseCount);
        GC.KeepAlive(aliases.Elements);
    }

    private static void Validate(Aliases aliases)
    {
        if (aliases.Value is Array array)
        { for (int i = 0; i < array.Length; i++) { Assert.Same(aliases.Elements[i], array.GetValue(i)); } }
        else if (aliases.Value is IReadOnlyList<QpdfDiagnostic> view)
        { for (int i = 0; i < view.Count; i++) { Assert.Same(aliases.Elements[i], view[i]); } }
        else if (aliases.Value is QpdfDiagnostic diagnostic)
        { Assert.Equal(aliases.Elements[0], diagnostic); Assert.Same(aliases.Elements[0].Message, diagnostic.Message); }
    }

    public static IEnumerable<object[]> IndependentAliases()
    { foreach (bool compressed in new[] { false, true }) foreach (string kind in new[] { "view", "element", "caller-copy", "record-copy", "sibling" }) { yield return [compressed, kind]; } }

    [Theory]
    [MemberData(nameof(IndependentAliases))]
    [Trait("Layer", "Contract")]
    public async Task SurvivingAliasesKeepRecordsTextAndSiblingValid(bool compressed, string kind)
    {
        Aliases aliases = await Complete("typed-page", compressed, kind);
        Assert.True(aliases.Admitted);
        Validate(aliases);
        if (kind == "sibling")
        {
            aliases.Value = null; aliases.Sibling = null;
        }
        else { aliases.Value = null; }
        GC.KeepAlive(aliases.Elements); GC.KeepAlive(aliases.Output);
    }

    public static IEnumerable<object[]> FaultJobs()
    {
        foreach (string route in new[] { "typed-page", "typed-pages", "typed-split", "typed-json", "typed-update", "typed-extract", "typed-add" })
        foreach (bool compressed in new[] { false, true })
        foreach (string boundary in new[] { "write", "commit", "output-close" })
        foreach (string outcome in new[] { "fault", "cancel" }) { yield return [route, compressed, boundary, outcome]; }
    }

    [Theory]
    [MemberData(nameof(FaultJobs))]
    [Trait("Layer", "Contract")]
    public async Task WarningJobsPreserveOutputAliasesAndCloseOnFaultOrCancellation(string route, bool compressed,
        string boundary, string outcome)
    {
        object host = await Prepare(route, compressed, boundary, outcome);
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Read(host, "Inner");
        Assert.True(inner.TryGetOutput(Output, out byte[] prior));
        using CancellationTokenSource cancellation = new();
        Task<object> task = Start((IQpdfHost)host, route, QpdfLimits.Default, cancellation.Token);
        try
        {
            await ((Task)Read(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = Governor(task);
            if (outcome == "cancel") { cancellation.Cancel(); } else { Resume(host); }
            if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); }
            else { await Assert.ThrowsAnyAsync<QpdfException>(() => task); }
            Assert.Equal(0L, inner.OpenLeaseCount); Assert.Equal(0L, governor.Snapshot().OpenHandles);
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            Assert.True(inner.TryGetOutput(route == "typed-split" ? QpdfPath.From("output-1.pdf") : Output, out byte[] output));
            if (boundary != "output-close") { Assert.Equal(prior, output); }
            else if (route == "typed-extract") { Assert.Equal("q Q\n"u8.ToArray(), output); }
            else if (route == "typed-json") { Assert.StartsWith("{", Encoding.UTF8.GetString(output).TrimStart()); }
            else { Assert.True(output.AsSpan().StartsWith("%PDF-"u8)); }
        }
        finally { Resume(host); try { await task; } catch (QpdfException) { } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Layer", "Contract")]
    public async Task JsonCreationBorrowsEmptyWarningStorage(bool compressed)
    {
        object host = await Prepare("typed-import", compressed, "read", "success");
        Task<object> task = Start((IQpdfHost)host, "typed-import", QpdfLimits.Default, CancellationToken.None);
        try
        {
            await ((Task)Read(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = Governor(task); Resume(host);
            object result = await task;
            IReadOnlyList<QpdfDiagnostic> warnings = (IReadOnlyList<QpdfDiagnostic>)Read(result, "Warnings");
            Assert.Same(Array.Empty<QpdfDiagnostic>(), warnings);
            Assert.Equal(0L, ((InMemoryQpdfHost)Read(host, "Inner")).OpenLeaseCount);
        }
        finally { Resume(host); await task; }
    }

    public static IEnumerable<object[]> AdmissionJobs()
    { foreach (bool compressed in new[] { false, true }) foreach (bool shortBudget in new[] { false, true }) { yield return [compressed, shortBudget]; } }

    [Theory]
    [MemberData(nameof(AdmissionJobs))]
    [Trait("Layer", "Contract")]
    public async Task CompleteJobsHonorExistingAdmissionCeilings(bool compressed, bool shortBudget)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shortBudget ? 16384L : 32768L };
        object host = await Prepare("typed-page", compressed, "read", "success");
        Task<object> task = Start((IQpdfHost)host, "typed-page", limits, CancellationToken.None);
        try
        {
            await ((Task)Read(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = Governor(task); Resume(host);
            object? result = null;
            QpdfLimitException? failure = null;
            try { result = await task; }
            catch (QpdfLimitException error) { failure = error; }
            if (failure is not null)
            {
                Assert.True(shortBudget);
                Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
                Assert.Equal(0, Field(host, "Commits"));
                Assert.True(((InMemoryQpdfHost)Read(host, "Inner")).TryGetOutput(Output, out byte[] prior));
                Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            }
            else
            {
                IReadOnlyList<QpdfDiagnostic> warnings = (IReadOnlyList<QpdfDiagnostic>)Read(Assert.IsAssignableFrom<object>(result), "Warnings");
                Assert.Single(warnings);
                Assert.Equal(1, Field(host, "Commits"));
                Assert.True(((InMemoryQpdfHost)Read(host, "Inner")).TryGetOutput(Output, out byte[] published));
                using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(published);
                Assert.Equal(1, independent.NumberOfPages);
            }
            Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
            Assert.Equal(0L, governor.Snapshot().OpenHandles);
            Assert.Equal(0L, ((InMemoryQpdfHost)Read(host, "Inner")).OpenLeaseCount);
        }
        finally
        {
            Resume(host); try { await task; } catch (QpdfException) { }
        }
    }

    private sealed class ClosingCancellationHost(IQpdfHost inner, CancellationTokenSource cancellation) : IQpdfHost
    {
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new OutputLease(await inner.CreateOutputAsync(path, token), cancellation);
        private sealed class OutputLease(IQpdfOutputTransaction inner, CancellationTokenSource cancellation) : IQpdfOutputTransaction
        {
            private bool _committed;
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) => inner.WriteAsync(bytes, token);
            public async ValueTask CommitAsync(CancellationToken token) { await inner.CommitAsync(token); _committed = true; }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync()
            { await inner.DisposeAsync(); if (_committed) { cancellation.Cancel(); } }
        }
    }

    public static IEnumerable<object[]> PostCommitJobs()
    { foreach (string route in Routes.Where(x => x != "typed-json-sidecars")) foreach (bool compressed in new[] { false, true }) { yield return [route, compressed]; } }

    [Theory]
    [MemberData(nameof(PostCommitJobs))]
    [Trait("Layer", "Contract")]
    public async Task CompletedPublicationDoesNotAddANewCancellationPoll(string route, bool compressed)
    {
        object host = await Prepare(route, compressed, "read", "success");
        using CancellationTokenSource cancellation = new();
        Task<object> task = Start(new ClosingCancellationHost((IQpdfHost)host, cancellation), route, QpdfLimits.Default, cancellation.Token);
        try
        {
            await ((Task)Read(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = Governor(task); Resume(host);
            object result = await task;
            Assert.True(cancellation.IsCancellationRequested);
            Assert.NotEmpty((IReadOnlyList<QpdfDiagnostic>)Read(result, "Warnings"));
            Assert.Equal(0L, governor.Snapshot().OpenHandles);
            Assert.Equal(0L, ((InMemoryQpdfHost)Read(host, "Inner")).OpenLeaseCount);
        }
        finally { Resume(host); try { await task; } catch (OperationCanceledException) { } }
    }

    private static DiagnosticBag? FindBag(object value, int depth, HashSet<object> seen)
    {
        if (depth > 96 || !seen.Add(value)) { return null; }
        if (value is DiagnosticBag bag) { return bag; }
        if (value is Task)
        {
            object? state = value.GetType().GetField("StateMachine", Members)?.GetValue(value);
            return state is null ? null : FindBag(state, depth + 1, seen);
        }
        foreach (FieldInfo field in value.GetType().GetFields(Members))
        { if (field.GetValue(value) is DiagnosticBag direct) { return direct; } }
        foreach (FieldInfo field in value.GetType().GetFields(Members))
        {
            if (field.GetValue(value) is object nested && (nested is Task || nested.GetType().IsValueType
                && !nested.GetType().IsPrimitive && !nested.GetType().IsEnum))
            {
                DiagnosticBag? found = FindBag(nested, depth + 1, seen); if (found is not null) { return found; }
            }
        }
        return null;
    }

    private sealed class PreparationWarningHost(IQpdfHost inner, bool onInputClose) : IQpdfHost
    {
        private readonly bool _onInputClose = onInputClose;
        internal Action? Accept;
        private void Add() { Action? accept = Accept; Accept = null; accept?.Invoke(); }
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new InputLease(this, await inner.OpenInputAsync(path, token));
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new OutputLease(this, await inner.CreateOutputAsync(path, token));
        private sealed class InputLease(PreparationWarningHost host, IQpdfInput inner) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token) => inner.ReadAsync(offset, count, token);
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); if (host._onInputClose) { host.Add(); } }
        }
        private sealed class OutputLease(PreparationWarningHost host, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            { if (!host._onInputClose) { host.Add(); } await inner.WriteAsync(bytes, token); }
            public ValueTask CommitAsync(CancellationToken token) => inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("typed-page", false)]
    [InlineData("typed-page", true)]
    [InlineData("typed-json", false)]
    [InlineData("typed-json", true)]
    [Trait("Layer", "Contract")]
    public async Task FinalPreparationPreservesEveryAcceptedWarningInOrder(string route, bool compressed)
    {
        object host = await Prepare(route, compressed, "read", "success");
        PreparationWarningHost observer = new((IQpdfHost)host, route == "typed-json");
        Task<object> task = Start(observer, route, QpdfLimits.Default, CancellationToken.None);
        try
        {
            await ((Task)Read(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = Governor(task);
            DiagnosticBag bag = FindBag(task, 0, new HashSet<object>(ReferenceEqualityComparer.Instance))
                ?? throw new InvalidOperationException("Missing actual public diagnostic bag.");
            observer.Accept = () => bag.Add(QpdfDiagnosticCode.UnsupportedFeature, QpdfDiagnosticSeverity.Warning, "Late preparation warning.");
            Resume(host); object result = await task;
            IReadOnlyList<QpdfDiagnostic> warnings = (IReadOnlyList<QpdfDiagnostic>)Read(result, "Warnings");
            Assert.Equal(2, warnings.Count); Assert.Equal(QpdfDiagnosticCode.PageCountMismatch, warnings[0].Code);
            Assert.Equal("Late preparation warning.", warnings[1].Message);
            for (int i = 0; i < warnings.Count; i++) { Assert.Same(bag.Entries[i], warnings[i]); }
            Assert.True(governor.CallerStorageBytes + governor.PrivateRetainedBytes >= 24L + IntPtr.Size * warnings.Count);
            Assert.Equal(0L, ((InMemoryQpdfHost)Read(host, "Inner")).OpenLeaseCount);
        }
        finally { Resume(host); await task; }
    }

}

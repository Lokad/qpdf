using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Reading;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class DocumentWarningSnapshotTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private const string Message = "A /Pages /Count of 2 disagrees with traversal (1); traversal wins.";

    private static object Field(object value, string name) => value.GetType().GetField(name, Members)?.GetValue(value)
        ?? throw new InvalidOperationException(name);

    private static byte[] OriginalFixture(bool compressed)
    {
        byte[] bytes = (byte[])(typeof(PdfVersionOwnershipTests).GetMethod("DocumentBytes", Members)?.Invoke(null,
            [compressed, "none"]) ?? throw new InvalidOperationException("Missing preserved public fixture."));
        ReadOnlySpan<byte> marker = "/Count 1"u8;
        int at = bytes.AsSpan().IndexOf(marker);
        Assert.True(at >= 0);
        bytes[at + marker.Length - 1] = (byte)'2';
        Assert.Equal(compressed ? "6CADFAF14B1B13BEAF037018E714711F256561B7A83C36373FCBBEAF81C6987C"
            : "E5ED2A5F82933E43037A90E3DADC7A6233863ECF5C23E6DBA0D2B59841E4B0F9",
            Convert.ToHexString(SHA256.HashData(bytes)));
        return bytes;
    }

    private static InMemoryQpdfHost Host(bool compressed)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(Input, OriginalFixture(compressed), QpdfInputOwnership.Copy);
        return host;
    }

    private static ResourceGovernor Governor(QpdfDocument document) => (ResourceGovernor)Field(document, "_governor");

    private static void Validate(IReadOnlyList<QpdfDiagnostic> warnings)
    {
        QpdfDiagnostic warning = Assert.Single(warnings);
        Assert.Equal(QpdfDiagnosticCode.PageCountMismatch, warning.Code);
        Assert.Equal(QpdfDiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(Message, warning.Message);
    }

    public static IEnumerable<object[]> Originals()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (bool disposed in new[] { false, true }) { yield return [compressed, disposed]; }
    }

    [Theory]
    [MemberData(nameof(Originals))]
    public async Task Original4096HeldSnapshotsRejectAtTheSame64KiBLimit(bool compressed, bool disposed)
    {
        InMemoryQpdfHost host = Host(compressed);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 65536L };
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, limits, CancellationToken.None);
        if (disposed) { await document.DisposeAsync(); }
        ResourceGovernor governor = Governor(document);
        IReadOnlyList<QpdfDiagnostic> first = document.Warnings;
        Validate(first);
        IReadOnlyList<QpdfDiagnostic>?[] roots = new IReadOnlyList<QpdfDiagnostic>?[4096];
        QpdfExecutionCounters before = governor.Snapshot();
        QpdfLimitException? rejection = null;
        int completed = 0;
        Assert.True(GC.TryStartNoGCRegion(16L * 1024L * 1024L));
        try
        {
            for (; completed < roots.Length; completed++)
            {
                try { roots[completed] = document.Warnings; }
                catch (QpdfLimitException error) { rejection = error; break; }
            }
        }
        finally { GC.EndNoGCRegion(); }
        Assert.NotNull(rejection);
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), rejection.LimitName);
        Assert.InRange(completed, 1, roots.Length - 1);
        QpdfExecutionCounters after = governor.Snapshot();
        Assert.True(after.LiveMemoryBytes > before.LiveMemoryBytes);
        Assert.True(after.TotalWork > before.TotalWork);
        Assert.True(after.PeakMemoryBytes <= limits.MaxMemoryBytes);
        for (int i = 0; i < completed; i++)
        {
            IReadOnlyList<QpdfDiagnostic> value = roots[i] ?? throw new InvalidOperationException();
            Validate(value); Assert.Same(first[0], value[0]); Assert.NotSame(first, value);
        }
        await document.DisposeAsync();
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, governor.Snapshot().OpenHandles);
        Validate(first); GC.KeepAlive(roots);
    }

    [Theory]
    [MemberData(nameof(Originals))]
    public async Task HeldSnapshotsRejectFiniteWorkAfterPublicOpening(bool compressed, bool disposed)
    {
        InMemoryQpdfHost host = Host(compressed);
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 4096L };
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, limits, CancellationToken.None);
        if (disposed) { await document.DisposeAsync(); }
        ResourceGovernor governor = Governor(document);
        List<IReadOnlyList<QpdfDiagnostic>> roots = [];
        QpdfLimitException? rejection = null;
        Assert.True(GC.TryStartNoGCRegion(16L * 1024L * 1024L));
        try
        {
            for (int i = 0; i < 4096; i++)
            {
                try { roots.Add(document.Warnings); }
                catch (QpdfLimitException error) { rejection = error; break; }
            }
        }
        finally { GC.EndNoGCRegion(); }
        Assert.NotNull(rejection); Assert.Equal(nameof(QpdfLimits.MaxWork), rejection.LimitName);
        Assert.NotEmpty(roots); Assert.All(roots, Validate);
        Assert.True(governor.Snapshot().TotalWork <= limits.MaxWork);
        await document.DisposeAsync();
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, governor.Snapshot().OpenHandles);
        GC.KeepAlive(roots);
    }

    public static IEnumerable<object[]> Cancellations()
    {
        foreach (object[] row in Originals())
        foreach (bool empty in new[] { false, true }) { yield return [.. row, empty]; }
    }

    [Theory]
    [MemberData(nameof(Cancellations))]
    public async Task CancellationAfterOpeningRejectsFreshSnapshotsAndPreservesPriorAliases(bool compressed, bool disposed, bool empty)
    {
        InMemoryQpdfHost host = new();
        byte[] bytes = OriginalFixture(compressed);
        if (empty) { bytes[bytes.AsSpan().IndexOf("/Count 2"u8) + 7] = (byte)'1'; }
        host.AddInput(Input, bytes, QpdfInputOwnership.Copy);
        using CancellationTokenSource cancellation = new();
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, QpdfLimits.Default, cancellation.Token);
        IReadOnlyList<QpdfDiagnostic> prior = document.Warnings;
        if (empty) { Assert.Empty(prior); } else { Validate(prior); }
        if (disposed) { await document.DisposeAsync(); }
        ResourceGovernor governor = Governor(document);
        QpdfExecutionCounters before = governor.Snapshot();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => document.Warnings);
        Assert.Equal(before, governor.Snapshot());
        if (empty) { Assert.Empty(prior); } else { Validate(prior); }
        await document.DisposeAsync();
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, governor.Snapshot().OpenHandles);
    }

    private static RetainedValueOwners.Receipt? Receipt(ResourceGovernor governor, object value)
    {
        object? owners = governor.GetType().GetField("_valueOwners", Members)?.GetValue(governor);
        for (object? segment = owners?.GetType().GetField("_segments", Members)?.GetValue(owners); segment is not null;
            segment = segment.GetType().GetField("Next", Members)?.GetValue(segment))
        {
            ConditionalWeakTable<object, RetainedValueOwners.Receipt> values =
                (ConditionalWeakTable<object, RetainedValueOwners.Receipt>)Field(segment, "Values");
            if (values.TryGetValue(value, out RetainedValueOwners.Receipt? receipt)) { return receipt; }
        }
        return null;
    }

    private static Array Backing(IReadOnlyList<QpdfDiagnostic> warnings) => warnings.GetType().GetFields(Members)
        .Select(field => field.GetValue(warnings)).OfType<Array>().Single();

    private sealed class Aliases(ResourceGovernor governor, object value, IReadOnlyList<QpdfDiagnostic> sibling,
        WeakReference document, WeakReference host, WeakReference view, WeakReference backing,
        WeakReference viewReceipt, WeakReference backingReceipt, long minimum, bool admitted)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal object? Value = value;
        internal IReadOnlyList<QpdfDiagnostic>? Sibling = sibling;
        internal readonly WeakReference Document = document, Host = host, View = view, Backing = backing;
        internal readonly WeakReference ViewReceipt = viewReceipt, BackingReceipt = backingReceipt;
        internal readonly long Minimum = minimum;
        internal readonly bool Admitted = admitted;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Aliases Keep(QpdfDocument document, InMemoryQpdfHost host, string kind)
    {
        ResourceGovernor governor = Governor(document);
        IReadOnlyList<QpdfDiagnostic> first = document.Warnings, second = document.Warnings;
        Validate(first); Validate(second); Assert.Same(first[0], second[0]); Assert.NotSame(first, second);
        Array backing = Backing(first);
        RetainedValueOwners.Receipt? viewReceipt = Receipt(governor, first), arrayReceipt = Receipt(governor, backing);
        bool admitted = viewReceipt is not null && arrayReceipt is not null
            && viewReceipt.Bytes == 24L && arrayReceipt.Bytes == 32L
            && viewReceipt.PrepaidRetirement && arrayReceipt.PrepaidRetirement
            && !ReferenceEquals(viewReceipt, arrayReceipt);
        object value = kind switch
        {
            "view" => first,
            "backing" => backing,
            "element" => first[0],
            _ => first.ToArray(),
        };
        if (kind == "caller-copy") { Assert.Null(Receipt(governor, value)); }
        long receiptBytes = PdfVersionOwnershipTests.MeasurePhysicalSize(typeof(RetainedValueOwners.Receipt));
        long minimum = kind == "view" ? 56L + 2L * receiptBytes : kind == "backing" ? 32L + receiptBytes : 0L;
        return new(governor, value, second, new(document), new(host), new(first), new(backing),
            new(viewReceipt), new(arrayReceipt), minimum, admitted);
    }

    private static async Task<Aliases> CompleteAliases(bool compressed, bool disposed, string kind)
    {
        InMemoryQpdfHost host = Host(compressed);
        QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        if (disposed) { await document.DisposeAsync(); }
        Aliases aliases = Keep(document, host, kind);
        await document.DisposeAsync();
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, aliases.Governor.Snapshot().OpenHandles);
        return aliases;
    }

    private static QpdfExecutionCounters Collect(Aliases aliases)
    {
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        for (int i = 0; i < 4; i++) { aliases.Governor.ReserveMemory(0L); }
        return aliases.Governor.Snapshot();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Check(Aliases aliases, string kind)
    {
        Assert.True(aliases.Admitted, "The completed public job did not admit independent warning view/backing receipts and retirement work.");
        object value = aliases.Value ?? throw new InvalidOperationException();
        if (value is QpdfDiagnostic diagnostic) { Assert.Equal(Message, diagnostic.Message); }
        else { Validate((IReadOnlyList<QpdfDiagnostic>)value); }
        bool view = kind == "view", array = view || kind == "backing";
        Assert.Equal(view, aliases.View.IsAlive); Assert.Equal(array, aliases.Backing.IsAlive);
        Assert.Equal(view, aliases.ViewReceipt.IsAlive); Assert.Equal(array, aliases.BackingReceipt.IsAlive);
        if (view)
        {
            IList mutable = (IList)value;
            Assert.True(mutable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutable[0] = new QpdfDiagnostic(QpdfDiagnosticCode.CheckFailed,
                QpdfDiagnosticSeverity.Error, "caller mutation"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckSibling(Aliases aliases) => Validate(aliases.Sibling ?? throw new InvalidOperationException());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Drop(Aliases aliases) => aliases.Value = null;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropSibling(Aliases aliases) => aliases.Sibling = null;

    public static IEnumerable<object[]> AliasCases()
    {
        foreach (object[] row in Originals())
        foreach (string kind in new[] { "view", "backing", "element", "caller-copy" }) { yield return [.. row, kind]; }
    }

    [Theory]
    [MemberData(nameof(AliasCases))]
    public async Task WarningAliasesOwnIndependentPacketsAndReleaseDeadSiblings(bool compressed, bool disposed, string kind)
    {
        Aliases aliases = await CompleteAliases(compressed, disposed, kind);
        await Task.Yield();
        QpdfExecutionCounters held = Collect(aliases);
        Assert.False(aliases.Document.IsAlive); Assert.False(aliases.Host.IsAlive);
        Check(aliases, kind);
        Drop(aliases);
        QpdfExecutionCounters dropped = Collect(aliases);
        Assert.False(aliases.View.IsAlive); Assert.False(aliases.Backing.IsAlive);
        Assert.False(aliases.ViewReceipt.IsAlive); Assert.False(aliases.BackingReceipt.IsAlive);
        Assert.True(held.LiveMemoryBytes - dropped.LiveMemoryBytes >= aliases.Minimum);
        CheckSibling(aliases);
        DropSibling(aliases);
        QpdfExecutionCounters final = Collect(aliases);
        Assert.Equal(0L, final.LiveMemoryBytes); Assert.Equal(0L, final.OpenHandles);
        GC.KeepAlive(aliases);
    }

    [Theory]
    [MemberData(nameof(Originals))]
    public async Task EmptySnapshotsBorrowStorageWithoutConsumingMemoryOrWork(bool compressed, bool disposed)
    {
        InMemoryQpdfHost host = new();
        byte[] bytes = OriginalFixture(compressed);
        bytes[bytes.AsSpan().IndexOf("/Count 2"u8) + 7] = (byte)'1';
        host.AddInput(Input, bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input,
            QpdfLimits.Default with { MaxMemoryBytes = 65536L }, CancellationToken.None);
        if (disposed) { await document.DisposeAsync(); }
        ResourceGovernor governor = Governor(document);
        IReadOnlyList<QpdfDiagnostic> first = document.Warnings; Assert.Empty(first);
        IReadOnlyList<QpdfDiagnostic>[] roots = new IReadOnlyList<QpdfDiagnostic>[4096];
        QpdfExecutionCounters before = governor.Snapshot();
        Assert.True(GC.TryStartNoGCRegion(16L * 1024L * 1024L));
        long allocated;
        try
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < roots.Length; i++) { roots[i] = document.Warnings; }
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        }
        finally { GC.EndNoGCRegion(); }
        Assert.Equal(0L, allocated);
        Assert.All(roots, Assert.Empty); Assert.Equal(before, governor.Snapshot());
        await document.DisposeAsync(); Assert.Empty(document.Warnings);
        Assert.Equal(0L, host.OpenLeaseCount); GC.KeepAlive(roots);
    }

    [Fact]
    public async Task OldSnapshotsKeepTheirPrefixWhenLaterObjectLoadingAddsAWarning()
    {
        List<(int, string, byte[]?)> objects = CheckTests.SinglePage("<< /Length 0 >>", []);
        objects[1] = (2, "<< /Type /Pages /Kids [3 0 R] /Count 2 >>", null);
        objects.Add((6, "<< /K 1 /K 2 >>", null));
        InMemoryQpdfHost host = new(); host.AddInput(Input, CheckTests.Assemble(objects, 1), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        IReadOnlyList<QpdfDiagnostic> first = document.Warnings; Validate(first);
        document.GetObject(6);
        IReadOnlyList<QpdfDiagnostic> second = document.Warnings;
        Assert.Single(first); Assert.Equal(2, second.Count); Assert.Same(first[0], second[0]);
        Assert.Equal(QpdfDiagnosticCode.DuplicateKey, second[1].Code);
        await document.DisposeAsync();
        Assert.Equal(2, document.Warnings.Count); Validate(first); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private sealed class CloseHost(InMemoryQpdfHost source, string mode, CancellationTokenSource cancellation) : IQpdfHost
    {
        internal int Reads, Closes, Outputs;
        private string Mode => mode;
        private CancellationTokenSource Cancellation => cancellation;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new CloseInput(await source.OpenInputAsync(path, token), this);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Outputs++; return source.CreateOutputAsync(path, token); }

        private sealed class CloseInput(IQpdfInput inner, CloseHost host) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
            { host.Reads++; return inner.ReadAsync(offset, count, token); }
            public async ValueTask DisposeAsync()
            {
                host.Closes++;
                await inner.DisposeAsync();
                if (host.Mode == "fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed); }
                host.Cancellation.Cancel();
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckAfterClose(QpdfDocument document, string mode)
    {
        if (mode == "fault") { Validate(document.Warnings); }
        else { Assert.ThrowsAny<OperationCanceledException>(() => document.Warnings); }
    }

    private static async Task<Aliases> CompleteClose(bool compressed, string mode, string kind)
    {
        InMemoryQpdfHost inner = Host(compressed);
        QpdfPath output = QpdfPath.From("prior.pdf");
        await using (IQpdfOutputTransaction seed = await inner.CreateOutputAsync(output, CancellationToken.None))
        {
            await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        Assert.True(inner.TryGetOutput(output, out byte[] prior));
        using CancellationTokenSource cancellation = new();
        CloseHost host = new(inner, mode, cancellation);
        QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, QpdfLimits.Default, cancellation.Token);
        Aliases aliases = Keep(document, inner, kind);
        if (mode == "fault")
        {
            QpdfHostException error = await Assert.ThrowsAsync<QpdfHostException>(() => document.DisposeAsync().AsTask());
            Assert.Equal(QpdfHostFailureReason.ReadFailed, error.Reason);
        }
        else { await document.DisposeAsync(); }
        CheckAfterClose(document, mode);
        await document.DisposeAsync();
        Assert.True(host.Reads > 0); Assert.Equal(1, host.Closes); Assert.Equal(0, host.Outputs);
        Assert.Equal(0L, inner.OpenLeaseCount); Assert.Equal(0L, aliases.Governor.Snapshot().OpenHandles);
        Assert.True(inner.TryGetOutput(output, out byte[] retained));
        Assert.Equal(new byte[] { 17, 23, 41 }, prior); Assert.Equal(prior, retained);
        return aliases;
    }

    public static IEnumerable<object[]> CloseCases()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (string mode in new[] { "fault", "cancel" })
        foreach (string kind in new[] { "view", "backing" }) { yield return [compressed, mode, kind]; }
    }

    [Theory]
    [MemberData(nameof(CloseCases))]
    public async Task CloseFaultAndCancellationPreserveFundedAliasesAndPriorOutputs(bool compressed, string mode, string kind)
    {
        Aliases aliases = await CompleteClose(compressed, mode, kind);
        await Task.Yield();
        QpdfExecutionCounters held = Collect(aliases);
        Assert.False(aliases.Document.IsAlive); Assert.False(aliases.Host.IsAlive);
        Check(aliases, kind); CheckSibling(aliases);
        Drop(aliases);
        QpdfExecutionCounters dropped = Collect(aliases);
        Assert.False(aliases.View.IsAlive); Assert.False(aliases.Backing.IsAlive);
        Assert.False(aliases.ViewReceipt.IsAlive); Assert.False(aliases.BackingReceipt.IsAlive);
        Assert.True(held.LiveMemoryBytes - dropped.LiveMemoryBytes >= aliases.Minimum);
        CheckSibling(aliases); DropSibling(aliases);
        QpdfExecutionCounters final = Collect(aliases);
        Assert.Equal(0L, final.LiveMemoryBytes); Assert.Equal(0L, final.OpenHandles);
        GC.KeepAlive(aliases);
    }


    private sealed record Unrooted(IReadOnlyList<QpdfDiagnostic> Warnings, WeakReference Document,
        WeakReference Host, WeakReference Governor, WeakReference Ledger);

    private static async Task<Unrooted> CompleteUnrooted(bool compressed)
    {
        InMemoryQpdfHost host = Host(compressed);
        QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        ResourceGovernor governor = Governor(document);
        IReadOnlyList<QpdfDiagnostic> warnings = document.Warnings;
        await document.DisposeAsync();
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, governor.Snapshot().OpenHandles);
        return new(warnings, new(document), new(host), new(governor), new(Field(governor, "_valueOwners")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SurvivingWarningsDoNotRootCompletedGovernorOrLedger(bool compressed)
    {
        Unrooted aliases = await CompleteUnrooted(compressed);
        await Task.Yield();
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(aliases.Document.IsAlive); Assert.False(aliases.Host.IsAlive);
        Assert.False(aliases.Governor.IsAlive); Assert.False(aliases.Ledger.IsAlive);
        Validate(aliases.Warnings); GC.KeepAlive(aliases);
    }

}

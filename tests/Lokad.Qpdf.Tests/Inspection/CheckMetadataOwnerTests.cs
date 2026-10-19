using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

// New owner/primitive types are excluded from the preceding-DLL lane; public
// and private-context-compatible controls live in CheckMetadataLookupTests.
[Collection(nameof(NameTableCollection))]
public sealed class CheckMetadataOwnerTests
{
    private static PdfStream Stream() => new(new PdfDictionary(new Dictionary<PdfName, QpdfObject>()), new byte[2 * 1024 * 1024]);
    private static object Field(object value, string name)
        => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value)
            ?? throw new InvalidOperationException("Missing owned checker field: " + name);

    [Fact]
    public void StreamReferencesStayFundedUntilTheOwnerDropsThemAndBorrowPayloads()
    {
        PdfStream borrowed = Stream(); ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1024L }, CancellationToken.None);
        using CheckStreamList owner = CheckStreamList.Create(governor); Assert.Equal(160L, governor.Snapshot().LiveMemoryBytes);
        owner.Add(borrowed, CancellationToken.None); Assert.Same(borrowed, owner.Get(0, CancellationToken.None));
        Assert.Equal(224L, governor.Snapshot().LiveMemoryBytes); owner.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => owner.Count); Assert.Throws<ObjectDisposedException>(() => owner.Get(0, CancellationToken.None));
        Assert.Equal(2 * 1024 * 1024, borrowed.Data.Length);
    }

    [Theory]
    [InlineData(63L)]
    [InlineData(127L)]
    [InlineData(159L)]
    public void StreamOwnerAdmissionFailureLeavesNoPartialReceipt(long maximum)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = maximum }, CancellationToken.None);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => CheckStreamList.Create(governor));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void StreamCapacityFailurePreservesEarlierReferencesAndRetriesTheSameOwner()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None); PdfStream borrowed = Stream();
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using CheckStreamList owner = CheckStreamList.Create(governor);
        for (int index = 0; index < 16; index++) { owner.Add(borrowed, CancellationToken.None); }
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor); filler.Reserve(4096L - governor.Snapshot().LiveMemoryBytes - 1L);
        long before = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => owner.Add(borrowed, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(16, owner.Count); Assert.Equal(97L, prior.ReservedBytes); Assert.Same(borrowed, owner.Get(15, CancellationToken.None));
        filler.Dispose(); owner.Add(borrowed, CancellationToken.None); Assert.Equal(17, owner.Count);
        Assert.Same(borrowed, owner.Get(16, CancellationToken.None)); owner.Dispose(); Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherStreamCancellationContextPreservesExistingAdmission(bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using CheckStreamList owner = CheckStreamList.Create(governor); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => owner.Add(Stream(), governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(0, owner.Count); Assert.Equal(160L, governor.Snapshot().LiveMemoryBytes); owner.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class Alias(object owner, PdfStream? borrowed, WeakReference[] metadata)
    {
        internal readonly object Owner = owner;
        internal readonly PdfStream? Borrowed = borrowed;
        internal readonly WeakReference[] Metadata = metadata;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias CompletedStreams()
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, source.Token);
        CheckStreamList owner = CheckStreamList.Create(governor); PdfStream borrowed = Stream();
        for (int index = 0; index < 17; index++) { owner.Add(borrowed, CancellationToken.None); }
        object scope = Field(owner, "_memory"), items = Field(owner, "_items"), backing = Field(items, "_items");
        WeakReference[] metadata = [new(governor), new(source), new(scope), new(items), new(backing)];
        owner.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); return new(owner, borrowed, metadata);
    }

    [Fact]
    public void RootedDisposedStreamOwnerAndPayloadRetainNoBackingScopeOrExecutionState()
    {
        Alias alias = CompletedStreams(); Collect(); Assert.All(alias.Metadata, reference => Assert.False(reference.IsAlive));
        Assert.Equal(2 * 1024 * 1024, alias.Borrowed?.Data.Length); GC.KeepAlive(alias);
    }

    private static object CreateForm(ResourceGovernor governor)
    {
        Type type = typeof(DocumentCheck).GetNestedType("FormCheckState", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing form owner.");
        return type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, [governor])
            ?? throw new InvalidOperationException("Missing form owner factory.");
    }

    [Fact]
    public void InitialFormIndexesAdmitTheirFullEmptyGeometry()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 480L }, CancellationToken.None);
        object owner = CreateForm(governor); Assert.Equal(480L, governor.Snapshot().LiveMemoryBytes);
        ((IDisposable)owner).Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); ((IDisposable)owner).Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias CompletedForm()
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, source.Token); object owner = CreateForm(governor);
        MethodInfo visit = owner.GetType().GetMethod("Visit", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing form visit.");
        for (int index = 0; index < 17; index++) { Assert.True(visit.Invoke(owner, [index, CancellationToken.None]) is true); }
        object scope = Field(owner, "_memory"), visited = Field(owner, "_visited"), widgets = Field(owner, "_widgets"), indexOwner = Field(visited, "_index");
        WeakReference[] metadata = [new(governor), new(source), new(scope), new(visited), new(widgets), new(indexOwner), new(Field(indexOwner, "_entries")), new(Field(indexOwner, "_buckets"))];
        ((IDisposable)owner).Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); return new(owner, null, metadata);
    }

    [Fact]
    public void RootedDisposedFormOwnerRetainsNoIndexesScopeOrExecutionState()
    { Alias alias = CompletedForm(); Collect(); Assert.All(alias.Metadata, reference => Assert.False(reference.IsAlive)); GC.KeepAlive(alias); }

    private static void Collect()
    { for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); } }

    private sealed class Resolver(QpdfObject? value, bool found, Action? callback) : IIndirectResolver
    {
        internal int Calls;
        public bool TryResolve(PdfReference reference, out QpdfObject? result) { Calls++; callback?.Invoke(); result = value; return found; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SharedResolutionPreservesBooleanAndNullResultContracts(bool found, bool hasValue)
    {
        QpdfObject? borrowed = hasValue ? new PdfArray([]) : null; Resolver resolver = new(borrowed, found, null);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(found, PdfObjectLookup.TryResolve(new PdfReference(42, 7), resolver, governor, CancellationToken.None, out QpdfObject? result));
        Assert.Same(borrowed, result); Assert.Equal(1, resolver.Calls); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

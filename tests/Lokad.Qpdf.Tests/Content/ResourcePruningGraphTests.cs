using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class ResourcePruningGraphTests
{
    private static readonly Func<GraphCopier, int, PdfDictionary, QpdfLimits, ResourceGovernor, DiagnosticBag, CancellationToken, PdfDictionary> Prune =
        (typeof(ContentTransforms).GetMethod("PrunePage", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing pruning."))
        .CreateDelegate<Func<GraphCopier, int, PdfDictionary, QpdfLimits, ResourceGovernor, DiagnosticBag, CancellationToken, PdfDictionary>>();
    private static readonly Action<GraphCopier, int?, PdfDictionary?, ReadOnlyMemory<byte>, PdfDictionary?, bool, ResourceNameSet, QpdfLimits, ResourceGovernor, DiagnosticBag, CancellationToken> Scan =
        (typeof(ContentTransforms).GetMethod("ScanAndPrune", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing scan."))
        .CreateDelegate<Action<GraphCopier, int?, PdfDictionary?, ReadOnlyMemory<byte>, PdfDictionary?, bool, ResourceNameSet, QpdfLimits, ResourceGovernor, DiagnosticBag, CancellationToken>>();

    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    private sealed class Prepared : IDisposable
    {
        private readonly DocumentEngine _engine;
        private readonly ParserMemoryScope _copyMemory;
        internal readonly GraphCopier Copier;
        internal readonly PdfName FontKey = PdfName.FromAscii("F"), BinaryKey = new([0, 255, 35]);
        internal readonly PdfByteString Borrowed = new(new byte[8192], false);
        internal readonly PdfDictionary Resources, Fonts, Page;
        internal readonly PdfStream Content;

        internal Prepared(ResourceGovernor governor, bool indirect, bool stored)
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None); PdfDictionary trailer = Dictionary();
            _engine = new(ReadOnlyMemory<byte>.Empty, new XRefTable(0L, new Dictionary<int, XrefEntry>(), trailer, [trailer]),
                QpdfLimits.Default, construction, new DiagnosticBag(construction), QpdfRecoveryMode.Strict);
            _copyMemory = ParserMemoryScope.Create(governor);
            Copier = GraphCopier.CreateOwned(_engine, governor, QpdfLimits.Default, 1, _copyMemory);
            Fonts = new(new Dictionary<PdfName, QpdfObject> { [FontKey] = PdfNull.Instance, [PdfName.FromAscii("Unused")] = PdfNull.Instance });
            Resources = new(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("ProcSet")] = new PdfArray([PdfName.FromAscii("PDF")]),
                [PdfName.FromAscii("Font")] = Fonts,
                [BinaryKey] = Borrowed
            });
            Content = new(Dictionary(), "/F 12 Tf"u8.ToArray());
            Page = Dictionary(("Type", PdfName.FromAscii("Page")), ("Resources", indirect ? new PdfReference(7, 3) : Resources), ("Contents", Content));
            IDictionary<int, QpdfObject> pool = (IDictionary<int, QpdfObject>)Copier.CopiedObjects;
            if (stored) { pool.Add(1, Page); }
            if (indirect) { pool.Add(7, Resources); }
        }

        public void Dispose() { ((IDictionary<int, QpdfObject>)Copier.CopiedObjects).Clear(); _engine.Dispose(); _copyMemory.Dispose(); }
    }

    private static PdfDictionary ResourcesOf(PdfDictionary page)
    { Assert.True(page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resources)); return Assert.IsType<PdfDictionary>(resources); }

    private static void Verify(Prepared source, PdfDictionary replacement)
    {
        PdfDictionary resources = ResourcesOf(replacement);
        Assert.IsType<PdfNameTable<QpdfObject>>(replacement.AllocationCarrier);
        Assert.IsType<PdfNameTable<QpdfObject>>(resources.AllocationCarrier);
        Assert.True(resources.TryGet(PdfName.FromAscii("Font"), out QpdfObject value)); PdfDictionary fonts = Assert.IsType<PdfDictionary>(value);
        Assert.IsType<PdfNameTable<QpdfObject>>(fonts.AllocationCarrier);
        Assert.Same(source.FontKey, Assert.Single(fonts.Keys)); Assert.True(fonts.TryGet(source.FontKey, out value)); Assert.Same(PdfNull.Instance, value);
        Assert.Equal(source.Resources.Keys, resources.Keys); Assert.Same(source.BinaryKey, resources.Keys.Last());
        Assert.True(resources.TryGet(source.BinaryKey, out value)); Assert.Same(source.Borrowed, value);
        Assert.True(replacement.TryGet(PdfName.FromAscii("Contents"), out value)); Assert.Same(source.Content, value);
        Assert.Equal(2, source.Fonts.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PagePublicationOwnsFreshContainersAndBorrowsDirectOrIndirectResourceValues(bool indirect)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        using Prepared source = new(governor, indirect, true);
        PdfDictionary replacement = Prune(source.Copier, 1, source.Page, QpdfLimits.Default, governor, new DiagnosticBag(governor), CancellationToken.None);
        Assert.NotSame(source.Page, replacement); Assert.Same(replacement, source.Copier.CopiedObjects[1]); Verify(source, replacement);
        Assert.Equal(61L, prior.ReservedBytes); Assert.True(governor.Snapshot().LiveMemoryBytes > 125L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FormPublicationUsesTheSameImmutableRawPayload(bool indirect)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using Prepared source = new(governor, indirect, false);
        PdfDictionary header = Dictionary(("Subtype", PdfName.FromAscii("Form")), ("Resources", indirect ? new PdfReference(7, 3) : source.Resources));
        PdfStream form = new(header, "/F 12 Tf"u8.ToArray());
        ((IDictionary<int, QpdfObject>)source.Copier.CopiedObjects).Add(1, form);
        using ResourceNameSet unresolved = ResourceNameSet.Create(governor, CancellationToken.None);
        Scan(source.Copier, 1, null, form.RawMemory, source.Resources, false, unresolved, QpdfLimits.Default, governor, new DiagnosticBag(governor), CancellationToken.None);
        PdfStream replacement = Assert.IsType<PdfStream>(source.Copier.CopiedObjects[1]); Assert.NotSame(form, replacement);
        Assert.Same(form.AllocationCarrier, replacement.AllocationCarrier); Assert.Equal(form.RawMemory.ToArray(), replacement.RawMemory.ToArray());
        PdfDictionary resources = ResourcesOf(replacement.Dictionary);
        Assert.IsType<PdfNameTable<QpdfObject>>(resources.AllocationCarrier);
        Assert.True(resources.TryGet(PdfName.FromAscii("Font"), out QpdfObject fonts)); Assert.Single(Assert.IsType<PdfDictionary>(fonts).Keys);
        Assert.Equal(2, source.Fonts.Count); Assert.True(header.TryGet(PdfName.FromAscii("Resources"), out QpdfObject original));
        if (indirect) { Assert.IsType<PdfReference>(original); } else { Assert.Same(source.Resources, original); }
    }

    [Fact]
    public void MissingPageTargetFailsBeforeReceiptActivationAndPreservesPriorFunding()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        using Prepared source = new(governor, false, false); long live = governor.Snapshot().LiveMemoryBytes;
        QpdfException failure = Assert.Throws<QpdfException>(() => Prune(source.Copier, 1, source.Page, QpdfLimits.Default, governor, new DiagnosticBag(governor), CancellationToken.None));
        Assert.Equal("Destination object 1 does not exist.", failure.Message);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(61L, prior.ReservedBytes);
        Assert.Empty(source.Copier.CopiedObjects); Assert.Equal(2, source.Fonts.Count);
    }

    [Fact]
    public void ChangedFormWhoseCurrentPoolValueIsNotAStreamRetiresItsUnpublishedGraph()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using Prepared source = new(governor, false, false);
        PdfInteger stored = new("1", 1L); ((IDictionary<int, QpdfObject>)source.Copier.CopiedObjects).Add(1, stored);
        using ResourceNameSet unresolved = ResourceNameSet.Create(governor, CancellationToken.None);
        long live = governor.Snapshot().LiveMemoryBytes;
        Scan(source.Copier, 1, null, source.Content.RawMemory, source.Resources, false, unresolved, QpdfLimits.Default, governor, new DiagnosticBag(governor), CancellationToken.None);
        Assert.Same(stored, source.Copier.CopiedObjects[1]); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(2048L)]
    [InlineData(4096L)]
    [InlineData(6144L)]
    [InlineData(8192L)]
    public void MemoryInterruptionPreservesTheOriginalPoolAndIndependentReceipt(long maximum)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = maximum };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        using Prepared source = new(governor, false, true); long live = governor.Snapshot().LiveMemoryBytes;
        try { PdfDictionary replacement = Prune(source.Copier, 1, source.Page, limits, governor, new DiagnosticBag(governor), CancellationToken.None); Verify(source, replacement); }
        catch (QpdfLimitException failure)
        {
            Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
            Assert.Same(source.Page, source.Copier.CopiedObjects[1]); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(61L, prior.ReservedBytes); Assert.Equal(2, source.Fonts.Count);
    }

    private sealed class Escaping(object value, WeakReference governor, WeakReference copier, WeakReference page)
    { internal object? Value = value; internal readonly WeakReference Governor = governor, Copier = copier, Page = page; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Escaping Capture(ResourceGovernor governor, bool keys)
    {
        using Prepared source = new(governor, false, true);
        PdfDictionary replacement = Prune(source.Copier, 1, source.Page, QpdfLimits.Default, governor, new DiagnosticBag(governor), CancellationToken.None);
        PdfDictionary resources = ResourcesOf(replacement);
        return new(keys ? resources.Keys : resources, new(governor), new(source.Copier), new(source.Page));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublishedResourceAliasesKeepFundingAfterThePoolAndOldPageDie(bool keys)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); Escaping alias = Capture(governor, keys);
        Collect(); Assert.False(alias.Copier.IsAlive); Assert.False(alias.Page.IsAlive);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 96L);
        VerifyPublishedAlias(alias, keys);
        alias.Value = null; Collect(); governor.CompletePrivateStorage(); Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(alias); GC.KeepAlive(governor);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Escaping Complete(bool keys) => Capture(new(QpdfLimits.Default, CancellationToken.None), keys);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyPublishedAlias(Escaping alias, bool keys)
    {
        if (keys) { Assert.Equal(3, Assert.IsAssignableFrom<IEnumerable<PdfName>>(alias.Value).Count()); }
        else { Assert.Equal(3, Assert.IsType<PdfDictionary>(alias.Value).Count); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublishedResourceAliasesDoNotKeepCompletedExecutionState(bool keys)
    {
        Escaping alias = Complete(keys); Collect();
        Assert.False(alias.Governor.IsAlive); Assert.False(alias.Copier.IsAlive); Assert.False(alias.Page.IsAlive);
        VerifyPublishedAlias(alias, keys);
        GC.KeepAlive(alias);
    }

    private static void Collect()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}

using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class ResourceRebuildOwnershipTests
{
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderRewriteBorrowsLargeImmutablePayloadAndValues(bool existingResources)
    {
        byte[] raw = new byte[2 * 1024 * 1024]; raw[17] = 91;
        PdfByteString borrowed = new(new byte[2 * 1024 * 1024], false);
        PdfDictionary resources = Dictionary(("Opaque", borrowed));
        PdfDictionary sourceHeader = existingResources
            ? Dictionary(("Subtype", PdfName.FromAscii("Form")), ("Resources", Dictionary()), ("Meta", borrowed))
            : Dictionary(("Subtype", PdfName.FromAscii("Form")), ("Meta", borrowed));
        PdfStream source = new(sourceHeader, raw);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        PdfStream replacement = ResourceRebuild.ReplaceResources(graph, source, resources); graph.Retain();
        Assert.Same(source.AllocationCarrier, replacement.AllocationCarrier);
        Assert.Equal(91, replacement.RawMemory.Span[17]);
        Assert.True(replacement.Dictionary.TryGet(PdfName.FromAscii("Resources"), out QpdfObject value)); Assert.Same(resources, value);
        Assert.True(replacement.Dictionary.TryGet(PdfName.FromAscii("Meta"), out value)); Assert.Same(borrowed, value);
        Assert.IsType<PdfNameTable<QpdfObject>>(replacement.Dictionary.AllocationCarrier);
        Assert.InRange(graph.Memory.ReservedBytes, 1L, 4096L);
        Assert.Equal(existingResources ? new[] { "Subtype", "Resources", "Meta" } : new[] { "Subtype", "Meta", "Resources" },
            replacement.Dictionary.Keys.Select(key => key.DisplayText));
        if (existingResources) { Assert.Same(sourceHeader.Keys.Single(key => key.DisplayText == "Resources"), replacement.Dictionary.Keys.Single(key => key.DisplayText == "Resources")); }
        else { Assert.False(sourceHeader.TryGet(PdfName.FromAscii("Resources"), out _)); }
        GC.KeepAlive(source); GC.KeepAlive(replacement);
    }

    private sealed class Alias(object value, WeakReference[] metadata, WeakReference[] wrappers, long minimum)
    {
        internal object? Value = value;
        internal readonly WeakReference[] Metadata = metadata, Wrappers = wrappers;
        internal readonly long Minimum = minimum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Capture(ResourceGovernor governor, string kind)
    {
        byte[] raw = new byte[64]; raw[17] = 91;
        PdfDictionary sourceHeader = Dictionary(("Length", new PdfInteger("64", 64L)));
        PdfStream source = new(sourceHeader, raw); PdfDictionary resources = Dictionary();
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        PdfStream replacement = ResourceRebuild.ReplaceResources(graph, source, resources);
        graph.Retain();
        PdfName name = replacement.Dictionary.Keys.Single(key => key.DisplayText == "Resources");
        object value = kind switch
        {
            "stream" => replacement,
            "header" => replacement.Dictionary,
            "keys" => replacement.Dictionary.Keys,
            "name" => name,
            "name-bytes" => name.AllocationCarrier,
            "payload" => replacement.AllocationCarrier,
            _ => throw new InvalidOperationException("Unknown alias fixture.")
        };
        long minimum = kind switch { "stream" => 365L, "header" => 333L, "keys" => 309L, "name" => 65L, "name-bytes" => 41L, _ => 0L };
        return new(value, [new(governor), new(graph), new(graph.Memory)],
            [new(source), new(sourceHeader), new(replacement), new(replacement.Dictionary), new(replacement.Dictionary.AllocationCarrier)], minimum);
    }

    private static void Verify(Alias alias, string kind)
    {
        object value = alias.Value ?? throw new InvalidOperationException("Alias is missing.");
        if (kind == "payload") { Assert.Equal(91, Assert.IsType<byte[]>(value)[17]); }
        else if (kind == "name-bytes") { Assert.Equal("Resources"u8.ToArray(), Assert.IsType<byte[]>(value)); }
        else if (kind == "name") { Assert.Equal("Resources", Assert.IsType<PdfName>(value).DisplayText); }
        else if (kind == "keys") { Assert.Equal(new[] { "Length", "Resources" }, Assert.IsAssignableFrom<IEnumerable<PdfName>>(value).Select(key => key.DisplayText)); }
        else
        {
            PdfDictionary header = kind == "stream" ? Assert.IsType<PdfStream>(value).Dictionary : Assert.IsType<PdfDictionary>(value);
            Assert.True(header.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resources)); Assert.Equal(0, Assert.IsType<PdfDictionary>(resources).Count);
            if (kind == "stream") { Assert.Equal(91, Assert.IsType<PdfStream>(value).RawMemory.Span[17]); }
        }
    }

    private static void Collect(ResourceGovernor? governor)
    { for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); governor?.ReserveMemory(0L); } }

    [Theory]
    [InlineData("stream")]
    [InlineData("header")]
    [InlineData("keys")]
    [InlineData("name")]
    [InlineData("name-bytes")]
    [InlineData("payload")]
    public void ActiveAliasesKeepOnlyTheirFreshStorageFundedUntilCollection(string kind)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        Alias alias = Capture(governor, kind); Collect(governor);
        Assert.False(alias.Wrappers[0].IsAlive); Assert.False(alias.Wrappers[1].IsAlive);
        Assert.False(alias.Metadata[1].IsAlive); Assert.False(alias.Metadata[2].IsAlive);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= alias.Minimum);
        if (kind == "payload") { governor.CompletePrivateStorage(); Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes); }
        if (kind is "name" or "name-bytes" or "payload") { Assert.False(alias.Wrappers[4].IsAlive); }
        Verify(alias, kind); alias.Value = null; Collect(governor);
        governor.CompletePrivateStorage();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(governor); GC.KeepAlive(alias);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Complete(string kind) => Capture(new(QpdfLimits.Default, CancellationToken.None), kind);

    [Theory]
    [InlineData("stream")]
    [InlineData("header")]
    [InlineData("keys")]
    [InlineData("name")]
    [InlineData("name-bytes")]
    [InlineData("payload")]
    public void EscapingAliasesDoNotRetainCompletedExecutionMetadata(string kind)
    {
        Alias alias = Complete(kind); Collect(null);
        Assert.All(alias.Metadata, witness => Assert.False(witness.IsAlive));
        Verify(alias, kind); GC.KeepAlive(alias);
    }

    [Fact]
    public void ConstructionFailureDropsOnlyFreshStorageAndPreservesBorrowedInputs()
    {
        PdfDictionary source = new(Enumerable.Range(0, 512).ToDictionary(index => PdfName.FromAscii(index.ToString(System.Globalization.CultureInfo.InvariantCulture)), _ => (QpdfObject)PdfNull.Instance));
        PdfDictionary resources = Dictionary();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        long live = governor.Snapshot().LiveMemoryBytes;
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None))
        {
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => ResourceRebuild.ReplaceResources(graph, source, resources));
            Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        }
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(61L, prior.ReservedBytes);
        Assert.Equal(512, source.Count); Assert.Equal(0, resources.Count);
    }
}

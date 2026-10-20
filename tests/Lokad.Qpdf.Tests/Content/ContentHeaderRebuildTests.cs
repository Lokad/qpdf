using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class ContentHeaderRebuildTests
{
    private static PdfDictionary Build(PdfDictionary source, PdfName key, QpdfObject value, ResourceGovernor governor, bool strip)
    {
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        PdfDictionary result = strip ? PdfDictionaryRebuild.StripStreamEncoding(graph, source)
            : PdfDictionaryRebuild.ReplaceEntry(graph, source, key.RawSpan, value);
        graph.Retain(); return result;
    }

    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HelpersBorrowOriginalNamesAndLargeValuesAndOwnTheirBacking(bool strip)
    {
        PdfName key = PdfName.FromAscii("Contents"), binary = new([0, 255, 35]);
        PdfByteString borrowed = new(new byte[2 * 1024 * 1024], false);
        PdfDictionary source = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode"), [key] = borrowed, [binary] = borrowed, [PdfName.FromAscii("Length")] = PdfNull.Instance });
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 8192L }, CancellationToken.None);
        PdfDictionary result = Build(source, key, PdfNull.Instance, governor, strip);
        Assert.IsType<PdfNameTable<QpdfObject>>(result.AllocationCarrier);
        Assert.Same(binary, result.Keys.Single(name => name.RawSpan.SequenceEqual(binary.RawSpan)));
        Assert.Same(key, result.Keys.Single(name => name.RawSpan.SequenceEqual(key.RawSpan)));
        Assert.True(result.TryGet(binary, out QpdfObject value)); Assert.Same(borrowed, value);
        Assert.True(result.TryGet(key, out value)); Assert.Same(strip ? borrowed : PdfNull.Instance, value);
        Assert.Equal(strip ? 2 : 4, result.Count); Assert.Equal(4, source.Count);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 120L);
        GC.KeepAlive(result); GC.KeepAlive(source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullEntriesAndEncodingNamesKeepLegacyOrderAndMeaning(bool strip)
    {
        PdfName key = PdfName.FromAscii("Contents"), binary = new([255, 0, 70]);
        Dictionary<PdfName, QpdfObject?> entries = new()
        {
            [key] = null,
            [PdfName.FromAscii("Filter")] = PdfNull.Instance,
            [binary] = PdfNull.Instance,
            [PdfName.FromAscii("DecodeParms")] = PdfNull.Instance,
            [PdfName.FromAscii("FilterSuffix")] = PdfNull.Instance,
            [PdfName.FromAscii("Length")] = PdfNull.Instance
        };
        PdfDictionary source = Activator.CreateInstance(typeof(PdfDictionary), [entries]) as PdfDictionary ?? throw new InvalidOperationException();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary result = Build(source, key, PdfNull.Instance, governor, strip);
        Assert.Equal(strip ? 2 : 6, result.Count);
        Assert.Same(binary, result.Keys.First(name => name.RawSpan.SequenceEqual(binary.RawSpan)));
        Assert.Equal(strip ? new[] { binary, PdfName.FromAscii("FilterSuffix") } : new[] { PdfName.FromAscii("Filter"), binary, PdfName.FromAscii("DecodeParms"), PdfName.FromAscii("FilterSuffix"), PdfName.FromAscii("Length"), key }, result.Keys);
        Assert.Equal(6, source.Count); GC.KeepAlive(result);
    }

    [Theory]
    [InlineData(false, 2048L)]
    [InlineData(false, 4096L)]
    [InlineData(false, 8192L)]
    [InlineData(true, 2048L)]
    [InlineData(true, 4096L)]
    [InlineData(true, 8192L)]
    public void MemoryFailurePreservesPriorAcceptedCarrierAndOriginalDictionary(bool strip, long maximum)
    {
        PdfDictionary source = new(Enumerable.Range(0, 512).ToDictionary(index => PdfName.FromAscii("N" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)), _ => (QpdfObject)PdfNull.Instance));
        PdfName key = PdfName.FromAscii("Contents"); byte[] accepted = new byte[61];
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = maximum }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(93L); prior.RetainAllocation(93L);
        long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => Build(source, key, PdfNull.Instance, governor, strip));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(512, source.Count);
        Assert.Equal(61, accepted.Length); GC.KeepAlive(accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GovernorCancellationPreventsConstruction(bool strip)
    {
        PdfDictionary source = Dictionary(("Keep", PdfNull.Instance)); PdfName key = PdfName.FromAscii("Contents");
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => Build(source, key, PdfNull.Instance, governor, strip));
        governor.CompletePrivateStorage();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class Alias(object value, WeakReference[] metadata)
    { internal object? Value = value; internal readonly WeakReference[] Metadata = metadata; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Capture(ResourceGovernor governor, bool strip, string kind)
    {
        PdfDictionary source = Dictionary(("Keep", PdfNull.Instance), ("Filter", PdfNull.Instance));
        PdfName key = PdfName.FromAscii("Contents"); PdfDictionary result = Build(source, key, PdfNull.Instance, governor, strip);
        object value = kind switch
        { "dictionary" => result, "keys" => result.Keys, "name" => result.Keys.Single(name => name.Equals(key)), "name-bytes" => result.Keys.Single(name => name.Equals(key)).AllocationCarrier, _ => throw new InvalidOperationException() };
        return new(value, [new(governor), new(source), new(result), new(result.AllocationCarrier)]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyAlias(Alias alias, string kind)
    {
        object value = alias.Value ?? throw new InvalidOperationException();
        if (kind == "dictionary") { Assert.True(Assert.IsType<PdfDictionary>(value).TryGet(PdfName.FromAscii("Keep"), out _)); }
        else if (kind == "keys") { Assert.NotEmpty(Assert.IsAssignableFrom<IEnumerable<PdfName>>(value)); }
        else if (kind == "name") { Assert.Equal("Contents", Assert.IsType<PdfName>(value).DisplayText); }
        else { Assert.Equal("Contents"u8.ToArray(), Assert.IsType<byte[]>(value)); }
    }

    private static void Collect()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    [Theory]
    [InlineData(false, "dictionary")]
    [InlineData(false, "keys")]
    [InlineData(false, "name")]
    [InlineData(false, "name-bytes")]
    [InlineData(true, "dictionary")]
    [InlineData(true, "keys")]
    public void ActiveAliasesKeepFundingUntilTheirLastReferenceDies(bool strip, string kind)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Alias alias = Capture(governor, strip, kind); Collect();
        Assert.False(alias.Metadata[1].IsAlive); Assert.True(governor.Snapshot().LiveMemoryBytes > 0L);
        if (kind is "name" or "name-bytes") { Assert.False(alias.Metadata[2].IsAlive); Assert.False(alias.Metadata[3].IsAlive); }
        VerifyAlias(alias, kind); alias.Value = null; Collect();
        governor.CompletePrivateStorage();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(alias); GC.KeepAlive(governor);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Alias Complete(bool strip, string kind) => Capture(new(QpdfLimits.Default, CancellationToken.None), strip, kind);

    [Theory]
    [InlineData(false, "dictionary")]
    [InlineData(false, "keys")]
    [InlineData(false, "name")]
    [InlineData(false, "name-bytes")]
    [InlineData(true, "dictionary")]
    [InlineData(true, "keys")]
    public void EscapingAliasesDoNotAnchorCompletedGovernors(bool strip, string kind)
    {
        Alias alias = Complete(strip, kind); Collect();
        Assert.False(alias.Metadata[0].IsAlive); Assert.False(alias.Metadata[1].IsAlive);
        VerifyAlias(alias, kind); GC.KeepAlive(alias);
    }
}

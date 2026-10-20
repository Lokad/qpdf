using System.Runtime.CompilerServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class InlineImageDictionaryTests
{
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));
    private static QpdfObject Get(PdfDictionary dictionary, PdfName key)
        => dictionary.TryGet(key, out QpdfObject? value) && value is not null ? value : throw new InvalidOperationException("Missing fixture key.");
    private static PdfDictionary ConvertWith(ResourceGovernor governor, PdfDictionary raw, PdfDictionary resources, CancellationToken token)
        => InlineImageDictionary.Convert(raw, resources, governor, new(governor), token);

    [Theory]
    [InlineData("BPC", "BitsPerComponent")]
    [InlineData("CS", "ColorSpace")]
    [InlineData("D", "Decode")]
    [InlineData("DP", "DecodeParms")]
    [InlineData("F", "Filter")]
    [InlineData("H", "Height")]
    [InlineData("IM", "ImageMask")]
    [InlineData("I", "Interpolate")]
    [InlineData("W", "Width")]
    public void AbbreviatedKeysExpandAndBorrowTheirValues(string abbreviated, string expected)
    {
        PdfByteString value = new(new byte[8192], false); PdfDictionary raw = Dictionary((abbreviated, value));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary result = ConvertWith(governor, raw, Dictionary(), CancellationToken.None);
        Assert.Equal(1, result.Count); Assert.True(result.TryGet(PdfName.FromAscii(expected), out QpdfObject? found));
        Assert.Same(value, found); Assert.Same(value, Get(raw, PdfName.FromAscii(abbreviated)));
    }

    [Theory]
    [InlineData("G", "DeviceGray")]
    [InlineData("RGB", "DeviceRGB")]
    [InlineData("CMYK", "DeviceCMYK")]
    [InlineData("I", "Indexed")]
    public void ColorspaceAbbreviationsExpand(string abbreviated, string expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary result = ConvertWith(governor, Dictionary(("CS", PdfName.FromAscii(abbreviated))), Dictionary(), CancellationToken.None);
        Assert.Equal(PdfName.FromAscii(expected), Assert.IsType<PdfName>(Get(result, PdfName.FromAscii("ColorSpace"))));
    }

    [Theory]
    [InlineData("AHx", "ASCIIHexDecode")]
    [InlineData("A85", "ASCII85Decode")]
    [InlineData("LZW", "LZWDecode")]
    [InlineData("Fl", "FlateDecode")]
    [InlineData("RL", "RunLengthDecode")]
    [InlineData("CCF", "CCITTFaxDecode")]
    [InlineData("DCT", "DCTDecode")]
    public void FilterAbbreviationsExpand(string abbreviated, string expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary result = ConvertWith(governor, Dictionary(("F", PdfName.FromAscii(abbreviated))), Dictionary(), CancellationToken.None);
        Assert.Equal(PdfName.FromAscii(expected), Assert.IsType<PdfName>(Get(result, PdfName.FromAscii("Filter"))));
    }

    [Fact]
    public void UnknownBinaryKeyKeepsItsExactBytesAndBorrowedValue()
    {
        PdfName key = new([66, 255, 35]); PdfByteString value = new(new byte[2 * 1024 * 1024], false);
        PdfDictionary raw = new(new Dictionary<PdfName, QpdfObject> { [key] = value });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary result = ConvertWith(governor, raw, Dictionary(), CancellationToken.None);
        Assert.True(result.TryGet(key, out QpdfObject? found)); Assert.Same(value, found);
        Assert.Same(key, Assert.Single(result.Keys)); Assert.Equal(1, raw.Count);
        Assert.True(governor.Snapshot().PeakMemoryBytes < 16384L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalKeyWinsRegardlessOfSourceInsertionOrder(bool canonicalFirst)
    {
        PdfInteger abbreviated = new("1", 1L), canonical = new("2", 2L);
        PdfDictionary raw = canonicalFirst ? Dictionary(("Width", canonical), ("W", abbreviated)) : Dictionary(("W", abbreviated), ("Width", canonical));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary result = ConvertWith(governor, raw, Dictionary(), CancellationToken.None);
        Assert.Same(canonical, Get(result, PdfName.FromAscii("Width"))); Assert.Equal(1, result.Count); Assert.Equal(2, raw.Count);
    }

    [Fact]
    public void NullCanonicalValueDoesNotHideItsAbbreviation()
    {
        PdfInteger value = new("1", 1L); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary result = ConvertWith(governor, Dictionary(("Width", PdfNull.Instance), ("W", value), ("Other", PdfNull.Instance)), Dictionary(), CancellationToken.None);
        Assert.Equal(1, result.Count); Assert.Same(value, Get(result, PdfName.FromAscii("Width")));
    }

    [Fact]
    public void BinaryNamedColorspaceBorrowsTheResolvedValue()
    {
        PdfName name = new([67, 255, 35]); PdfByteString lookup = new(new byte[8192], false);
        PdfDictionary spaces = new(new Dictionary<PdfName, QpdfObject> { [name] = lookup });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary result = ConvertWith(governor, Dictionary(("CS", name)), Dictionary(("ColorSpace", spaces)), CancellationToken.None);
        Assert.Same(lookup, Get(result, PdfName.FromAscii("ColorSpace"))); Assert.Same(lookup, Get(spaces, name));
    }

    [Fact]
    public void MissingColorspacePreservesNameAndOriginalWarning()
    {
        PdfName name = new([67, 255, 35]); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        PdfDictionary result = InlineImageDictionary.Convert(Dictionary(("CS", name)), Dictionary(), governor, diagnostics, CancellationToken.None);
        Assert.Same(name, Get(result, PdfName.FromAscii("ColorSpace")));
        QpdfDiagnostic warning = Assert.Single(diagnostics.Entries);
        Assert.Equal("Unable to resolve image colorspace; keeping the original name.", warning.Message);
    }

    [Fact]
    public void FilterArrayExpandsOnlyKnownNamesAndBorrowsOtherItems()
    {
        PdfName unknown = new([255, 35]); PdfByteString borrowed = new(new byte[8192], false);
        PdfArray filters = new([PdfName.FromAscii("AHx"), unknown, borrowed, PdfName.FromAscii("Fl")]);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary result = ConvertWith(governor, Dictionary(("F", filters)), Dictionary(), CancellationToken.None);
        PdfArray expanded = Assert.IsType<PdfArray>(Get(result, PdfName.FromAscii("Filter")));
        Assert.Equal(PdfName.FromAscii("ASCIIHexDecode"), expanded[0]); Assert.Same(unknown, expanded[1]); Assert.Same(borrowed, expanded[2]);
        Assert.Equal(PdfName.FromAscii("FlateDecode"), expanded[3]); Assert.Equal(PdfName.FromAscii("AHx"), filters[0]);
    }

    [Fact]
    public void FilterArrayWithoutAbbreviationsRemainsBorrowed()
    {
        PdfArray filters = new([new PdfName([255, 35]), new PdfByteString(new byte[8192], false)]);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary result = ConvertWith(governor, Dictionary(("Filter", filters)), Dictionary(), CancellationToken.None);
        Assert.Same(filters, Get(result, PdfName.FromAscii("Filter"))); Assert.True(governor.Snapshot().PeakMemoryBytes < 4096L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothCanceledTokensInterruptBeforeConversionAndPreserveIndependentAdmission(bool caller)
    {
        using CancellationTokenSource cancellation = new(); ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        PdfDictionary raw = Dictionary(("Unknown", new PdfByteString(new byte[8192], false))); long live = governor.Snapshot().LiveMemoryBytes;
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ConvertWith(governor, raw, Dictionary(), caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(61L, prior.ReservedBytes); Assert.Equal(1, raw.Count);
    }

    [Theory]
    [InlineData(63L)]
    [InlineData(127L)]
    [InlineData(511L)]
    [InlineData(1023L)]
    public void FailedMemoryAdmissionPreservesBorrowedInputsAndIndependentReceipt(long maximum)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = maximum + 125L };
        ResourceGovernor governor = new(limits, CancellationToken.None); using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L);
        PdfByteString borrowed = new(new byte[8192], false); PdfDictionary raw = Dictionary(("W", borrowed), ("F", new PdfArray([PdfName.FromAscii("AHx"), PdfName.FromAscii("Fl")])));
        raw = new PdfDictionary(raw.Keys.Select(key => new KeyValuePair<PdfName, QpdfObject>(key, Get(raw, key)))
            .Concat(Enumerable.Range(0, 128).Select(index => new KeyValuePair<PdfName, QpdfObject>(PdfName.FromAscii("Other" + index), borrowed)))
            .ToDictionary(entry => entry.Key, entry => entry.Value));
        long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => ConvertWith(governor, raw, Dictionary(), CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(61L, prior.ReservedBytes); Assert.Same(borrowed, Get(raw, PdfName.FromAscii("W")));
    }

    private sealed class Escaping(object value, WeakReference governor, WeakReference source)
    { internal object? Value = value; internal readonly WeakReference Governor = governor, Source = source; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Escaping Capture(ResourceGovernor governor, bool keys)
    {
        PdfDictionary raw = Dictionary(("W", new PdfInteger("1", 1L)), ("F", new PdfArray([PdfName.FromAscii("AHx"), PdfName.FromAscii("Fl")])));
        PdfDictionary result = ConvertWith(governor, raw, Dictionary(), CancellationToken.None);
        return new(keys ? result.Keys : result, new(governor), new(raw));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SurvivingDictionaryOrKeyViewKeepsFundingUntilItDies(bool keys)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); Escaping value = Capture(governor, keys); Collect(governor);
        Assert.False(value.Source.IsAlive); Assert.True(governor.Snapshot().LiveMemoryBytes >= 96L); Verify(value, keys);
        value.Value = null; Collect(governor); governor.CompletePrivateStorage(); Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(value); GC.KeepAlive(governor);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Escaping Completed(bool keys) => Capture(new(QpdfLimits.Default, CancellationToken.None), keys);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapingDictionaryOrKeyViewDoesNotKeepCompletedExecutionState(bool keys)
    {
        Escaping value = Completed(keys); Collect(null); Assert.False(value.Governor.IsAlive); Assert.False(value.Source.IsAlive); Verify(value, keys); GC.KeepAlive(value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Escaping CaptureFilterAlias(ResourceGovernor governor, bool nameOnly)
    {
        PdfDictionary result = ConvertWith(governor, Dictionary(("F", new PdfArray([PdfName.FromAscii("AHx"), PdfName.FromAscii("Fl")]))), Dictionary(), CancellationToken.None);
        PdfArray filters = Assert.IsType<PdfArray>(Get(result, PdfName.FromAscii("Filter")));
        return new(nameOnly ? filters[0] : filters, new(governor), new(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpandedArrayAndNameAliasesStayFundedAfterDictionaryCollection(bool nameOnly)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); Escaping alias = CaptureFilterAlias(governor, nameOnly);
        Collect(governor); Assert.False(alias.Source.IsAlive); Assert.True(governor.Snapshot().LiveMemoryBytes >= 24L); VerifyFilterAlias(alias, nameOnly);
        alias.Value = null; Collect(governor); governor.CompletePrivateStorage(); Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes); GC.KeepAlive(alias); GC.KeepAlive(governor);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyFilterAlias(Escaping alias, bool nameOnly)
    {
        if (nameOnly) { Assert.Equal(PdfName.FromAscii("ASCIIHexDecode"), Assert.IsType<PdfName>(alias.Value)); }
        else { Assert.Equal(2, Assert.IsType<PdfArray>(alias.Value).Count); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Verify(Escaping value, bool keys)
    {
        if (keys) { Assert.Equal(2, Assert.IsAssignableFrom<IEnumerable<PdfName>>(value.Value).Count()); }
        else { Assert.Equal(2, Assert.IsType<PdfDictionary>(value.Value).Count); }
    }
    private static void Collect(ResourceGovernor? governor)
    { for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); governor?.ReserveMemory(0L); } }
}

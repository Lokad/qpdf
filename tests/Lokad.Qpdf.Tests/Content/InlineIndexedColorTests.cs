using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Content;

public sealed class InlineIndexedColorTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string key in new[] { "CS", "ColorSpace" })
        foreach (string family in new[] { "I", "Indexed" })
        foreach ((string alias, string full) in new[] { ("G", "DeviceGray"), ("DeviceGray", "DeviceGray"),
            ("RGB", "DeviceRGB"), ("DeviceRGB", "DeviceRGB"), ("CMYK", "DeviceCMYK"), ("DeviceCMYK", "DeviceCMYK") })
        { yield return [key, family, alias, full]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void IndexedArraysExpandOnlyInlineFamilyAndDeviceBaseNames(
        string key, string family, string baseName, string expectedBase)
    {
        PdfByteString lookup = new(new byte[2 * 1048576], false);
        PdfInteger maximum = new("1", 1);
        PdfArray colors = new([PdfName.FromAscii(family), PdfName.FromAscii(baseName), maximum, lookup]);
        PdfDictionary raw = Dictionary((key, colors));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None))
        {
            PdfDictionary result = Assert.IsType<PdfDictionary>(InlineImageDictionary.BuildWithResources(graph, raw, null, new(governor), null));
            Assert.True(result.TryGet(PdfName.FromAscii("ColorSpace"), out QpdfObject found));
            PdfArray expanded = Assert.IsType<PdfArray>(found);
            Assert.Equal(PdfName.FromAscii("Indexed"), expanded[0]); Assert.Equal(PdfName.FromAscii(expectedBase), expanded[1]);
            Assert.Same(maximum, expanded[2]); Assert.Same(lookup, expanded[3]);
            if (family == "Indexed" && baseName == expectedBase) { Assert.Same(colors, expanded); }
            else { Assert.NotSame(colors, expanded); }
            Assert.Equal(PdfName.FromAscii(family), colors[0]); Assert.Equal(PdfName.FromAscii(baseName), colors[1]);
            Assert.True(governor.Snapshot().PeakMemoryBytes < 4096L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("Separation", 4)]
    [InlineData("Private", 4)]
    [InlineData("I", 3)]
    public void OtherArrayGrammarsRemainBorrowed(string family, int count)
    {
        QpdfObject[] values = [PdfName.FromAscii(family), PdfName.FromAscii("RGB"), new PdfInteger("1", 1), new PdfByteString([1, 2, 3], false)];
        PdfArray colors = new(values[..count]);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        PdfDictionary result = Assert.IsType<PdfDictionary>(InlineImageDictionary.BuildWithResources(graph, Dictionary(("CS", colors)), null, new(governor), null));
        Assert.True(result.TryGet(PdfName.FromAscii("ColorSpace"), out QpdfObject found)); Assert.Same(colors, found);
    }

    [Fact]
    public void ResourceDefinedColorArraysKeepTheirOriginalGrammarAndIdentity()
    {
        PdfArray colors = new([PdfName.FromAscii("I"), PdfName.FromAscii("RGB"), new PdfInteger("1", 1), new PdfByteString([1, 2, 3], false)]);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None);
        PdfDictionary result = Assert.IsType<PdfDictionary>(InlineImageDictionary.BuildWithResources(graph, Dictionary(("CS", PdfName.FromAscii("Named"))),
            Dictionary(("ColorSpace", Dictionary(("Named", colors)))), new(governor), null));
        Assert.True(result.TryGet(PdfName.FromAscii("ColorSpace"), out QpdfObject found)); Assert.Same(colors, found);
    }

    [Theory]
    [InlineData("refuse")]
    [InlineData("caller")]
    [InlineData("governor")]
    public void RefusalAndBothCancellationRoutesLeaveTheOriginalArrayAndRefundConstruction(string failure)
    {
        PdfByteString lookup = new(new byte[1048576], false);
        PdfArray colors = new([PdfName.FromAscii("I"), PdfName.FromAscii("RGB"), new PdfInteger("1", 1), lookup]);
        PdfDictionary raw = Dictionary(("CS", colors));
        ResourceGovernor observation = new(QpdfLimits.Default, CancellationToken.None);
        using (OwnedAllocationBatch sample = OwnedAllocationBatch.Create(observation, CancellationToken.None))
        { Assert.NotNull(InlineImageDictionary.BuildWithResources(sample, raw, null, new(observation), null)); }
        using CancellationTokenSource caller = new(), operation = new();
        ResourceGovernor governor = new(QpdfLimits.Default with
            { MaxMemoryBytes = failure == "refuse" ? observation.Snapshot().PeakMemoryBytes - 1 : 4096L }, operation.Token);
        Action convert = () =>
        {
            using OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, caller.Token);
            if (failure == "caller") { caller.Cancel(); }
            if (failure == "governor") { operation.Cancel(); }
            _ = InlineImageDictionary.BuildWithResources(graph, raw, null, new(governor), null);
        };
        if (failure == "refuse") { Assert.Throws<QpdfLimitException>(convert); }
        else { Assert.ThrowsAny<OperationCanceledException>(convert); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(PdfName.FromAscii("I"), colors[0]); Assert.Equal(PdfName.FromAscii("RGB"), colors[1]); Assert.Same(lookup, colors[3]);
    }

    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));
}

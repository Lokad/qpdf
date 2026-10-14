using System.Globalization;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Syntax;

public sealed class NameCollisionTests
{
    private static string Name(int index) => new string('a', 120) + index.ToString("D8", CultureInfo.InvariantCulture);
    private static byte[] Input(bool duplicate)
    {
        StringBuilder text = new("<< ");
        for (int index = 0; index < 32; index++) { text.Append('/').Append(Name(index)).Append(' ').Append(index).Append(' '); }
        if (duplicate) { text.Append('/').Append(Name(7)).Append(" 777 "); }
        text.Append(">>"); return Encoding.ASCII.GetBytes(text.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParserKeepsSamePrefixKeysAndDuplicateLastValue(bool duplicate)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 10000000L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor); PdfParser parser = new(limits, governor, diagnostics, null);
        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(parser.ParseObjectValue(new PdfTokenizer(Input(duplicate)), 0));
        Assert.Equal(32, dictionary.Count); Assert.Equal(32, dictionary.Keys.Count());
        for (int index = 0; index < 32; index++)
        {
            Assert.True(dictionary.TryGet(PdfName.FromAscii(Name(index)), out QpdfObject value));
            Assert.Equal(duplicate && index == 7 ? 777L : index, Assert.IsType<PdfInteger>(value).Value);
        }
        Assert.Equal(duplicate ? 1 : 0, diagnostics.Entries.Count);
        GC.KeepAlive(dictionary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParserCollisionWorkExhaustionDropsNewStorageAndPreservesPriorOwner(bool repair)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 100000L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        DiagnosticBag diagnostics = new(governor); PdfParser parser = new(limits, governor, diagnostics, null) { TolerateObjectErrors = repair };
        long before = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => parser.ParseObjectValue(new PdfTokenizer(Input(false)), 0));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(97L, prior.ReservedBytes); Assert.Empty(diagnostics.Entries);
    }

    [Fact]
    public void WriterCollisionWorkExhaustionPreservesBorrowedDictionaryAndPriorFunding()
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        for (int index = 0; index < 32; index++) { entries.Add(PdfName.FromAscii(Name(index)), PdfNull.Instance); }
        // Unchanged dictionaries can be borrowed without building a name table.
        // Require generation normalization so colliding keys are really inserted.
        PdfReference changed = new(1, 7);
        PdfName first = PdfName.FromAscii(Name(0)); entries[first] = changed;
        PdfDictionary input = new(entries);
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 50000L }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using WriterIndex<int> map = WriterIndex<int>.Create(sizeof(int), governor, CancellationToken.None);
        map.Add(1, 1);
        long before = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => WriterValue.Remap(input, map, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(97L, prior.ReservedBytes); Assert.Equal(32, input.Count);
        Assert.All(input.Keys, key => { Assert.True(input.TryGet(key, out QpdfObject value)); Assert.Same(key.Equals(first) ? changed : PdfNull.Instance, value); });
        Assert.Equal(7, changed.Generation);
    }
}

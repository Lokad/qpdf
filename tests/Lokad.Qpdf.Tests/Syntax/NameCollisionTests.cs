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
        QpdfLimits limits = QpdfLimits.Default; ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor); PdfParser parser = new(limits, governor, diagnostics, null);
        using ParserMemoryScope parsedMemory0 = ParserMemoryScope.Create(governor);
        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(parser.ParseObjectValue(new PdfTokenizer(Input(duplicate)), 0, parsedMemory0));
        parsedMemory0.RetainGraph(); parsedMemory0.Dispose();
        Assert.Equal(32, dictionary.Count); Assert.Equal(32, dictionary.Keys.Count());
        for (int index = 0; index < 32; index++)
        {
            Assert.True(dictionary.TryGet(PdfName.FromAscii(Name(index)), out QpdfObject value));
            Assert.Equal(duplicate && index == 7 ? 777L : index, Assert.IsType<PdfInteger>(value).Value);
        }
        Assert.Equal(duplicate ? 1 : 0, diagnostics.Entries.Count);
        GC.KeepAlive(dictionary);
    }
}

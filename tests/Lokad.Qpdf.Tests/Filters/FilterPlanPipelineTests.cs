using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Filters;

// Exercise the live checker decoder with its explicit storage, cancellation
// and decode-level classification.
public sealed class FilterPlanPipelineTests
{
    private sealed class Resolver : IIndirectResolver
    { public bool TryResolve(PdfReference reference, out QpdfObject? value) { value = null; return false; } }

    private static (object? Bytes, bool AboveLevel) Decode(PdfDictionary dictionary, QpdfLimits limits, ResourceGovernor governor)
    {
        MethodInfo method = typeof(DocumentCheck).GetMethod("DecodeCheckStream", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Checker stream decoder is missing.");
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        object?[] arguments = [new PdfStream(dictionary, [7]), new Resolver(), QpdfStreamDecodeLevel.Generalized,
            limits, governor, memory, CancellationToken.None, false];
        object? bytes = method.Invoke(null, arguments);
        return (bytes, Assert.IsType<bool>(arguments[7]));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("above-count")]
    public void CheckerDistinguishesEmptyMalformedAndAboveLevelArrays(string kind)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        if (kind != "empty")
        {
            entries.Add(PdfName.FromAscii("Filter"), kind == "malformed" ? new PdfArray([PdfNull.Instance])
                : new PdfArray(Enumerable.Repeat<QpdfObject>(PdfName.FromAscii("DCT"), 32).ToArray()));
        }
        QpdfLimits limits = QpdfLimits.Default with { MaxStreamFilters = 1 }; ResourceGovernor governor = new(limits, CancellationToken.None);
        if (kind == "malformed")
        {
            TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() => Decode(new PdfDictionary(entries), limits, governor));
            Assert.IsType<QpdfParseException>(failure.InnerException);
        }
        else
        {
            (object? bytes, bool aboveLevel) = Decode(new PdfDictionary(entries), limits, governor);
            if (kind == "empty") { Assert.Equal(new byte[] { 7 }, Assert.IsType<byte[]>(bytes)); Assert.False(aboveLevel); }
            else { Assert.Null(bytes); Assert.True(aboveLevel); }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

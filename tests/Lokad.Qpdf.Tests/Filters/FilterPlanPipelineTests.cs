using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Filters;

// Preserve the original filter fixtures and ceilings while supplying the
// checker's explicit decoded-storage scope and cancellation token.
public sealed class FilterPlanPipelineTests
{
    private sealed class Resolver : IIndirectResolver
    { public bool TryResolve(PdfReference reference, out QpdfObject? value) { value = null; return false; } }

    private static object? Decode(PdfDictionary dictionary, QpdfLimits limits, ResourceGovernor governor)
    {
        MethodInfo method = typeof(DocumentCheck).GetMethod("DecodeCheckStream", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Checker stream decoder is missing.");
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        return method.Invoke(null, [new PdfStream(dictionary, [7]), new Resolver(), QpdfStreamDecodeLevel.Generalized,
            limits, governor, memory, CancellationToken.None]);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("above-count")]
    public void CheckerPreservesEmptyMalformedAndUndecodedArrayBehavior(string kind)
    {
        Dictionary<PdfName, QpdfObject> entries = new();
        if (kind != "empty")
        {
            entries.Add(PdfName.FromAscii("Filter"), kind == "malformed" ? new PdfArray([PdfNull.Instance])
                : new PdfArray(Enumerable.Repeat<QpdfObject>(PdfName.FromAscii("DCT"), 32).ToArray()));
        }
        QpdfLimits limits = QpdfLimits.Default with { MaxStreamFilters = 1 }; ResourceGovernor governor = new(limits, CancellationToken.None);
        object? result = Decode(new PdfDictionary(entries), limits, governor);
        if (kind == "empty") { Assert.Equal(new byte[] { 7 }, Assert.IsType<byte[]>(result)); }
        else { Assert.Null(result); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

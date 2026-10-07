using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Filters;

// The checker helper signature and these object constructors precede FilterPlan.
// These cases can run unchanged against the preceding DLL without new owner types.
public sealed class FilterPlanPipelineTests
{
    private sealed class Resolver : IIndirectResolver
    { public bool TryResolve(PdfReference reference, out QpdfObject? value) { value = null; return false; } }

    private static object? Decode(PdfDictionary dictionary, QpdfLimits limits, ResourceGovernor governor)
    {
        MethodInfo method = typeof(DocumentCheck).GetMethod("DecodeCheckStream", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Checker stream decoder is missing.");
        return method.Invoke(null, [new PdfStream(dictionary, [7]), new Resolver(), QpdfStreamDecodeLevel.Generalized, limits, governor]);
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("name")]
    [InlineData("capacity")]
    public void CheckerResolutionHonorsFiniteWorkAndStorageBeforeDecode(string kind)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = kind == "capacity" ? 100000L : 64L, MaxMemoryBytes = kind == "capacity" ? 512L : 100000L };
        Dictionary<PdfName, QpdfObject> entries = new();
        if (kind == "lookup")
        { for (int index = 0; index < 512; index++) { entries.Add(PdfName.FromAscii("other" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)), PdfNull.Instance); } }
        else
        { entries.Add(PdfName.FromAscii("Filter"), kind == "name" ? new PdfName(new byte[8192]) : new PdfArray(Enumerable.Repeat<QpdfObject>(PdfName.FromAscii("DCT"), 128).ToArray())); }
        ResourceGovernor governor = new(limits, CancellationToken.None);
        TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() => Decode(new PdfDictionary(entries), limits, governor));
        Assert.Equal(kind == "capacity" ? nameof(QpdfLimits.MaxMemoryBytes) : nameof(QpdfLimits.MaxWork), Assert.IsType<QpdfLimitException>(failure.InnerException).LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.True(governor.Snapshot().TotalWork > 0L);
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

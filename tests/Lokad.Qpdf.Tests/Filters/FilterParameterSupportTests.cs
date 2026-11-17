using System.Globalization;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FilterParameterSupportTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return ["ASCIIHexDecode", "none", true, false];
        yield return ["ASCIIHexDecode", "empty", false, true];
        yield return ["ASCII85Decode", "none", true, false];
        yield return ["ASCII85Decode", "empty", false, false];
        yield return ["RunLengthDecode", "none", true, false];
        yield return ["RunLengthDecode", "empty", false, false];
        yield return ["FlateDecode", "none", true, false];
        yield return ["FlateDecode", "empty", true, true];
        yield return ["FlateDecode", "dormant", true, false];
        yield return ["FlateDecode", "tiff", true, false];
        yield return ["FlateDecode", "png", true, true];
        yield return ["FlateDecode", "predictor", false, false];
        yield return ["FlateDecode", "columns", false, true];
        yield return ["FlateDecode", "colors", false, false];
        yield return ["FlateDecode", "bits", false, false];
        yield return ["FlateDecode", "type", false, false];
        yield return ["FlateDecode", "range", false, false];
        yield return ["FlateDecode", "overflow", false, false];
        yield return ["FlateDecode", "row-capacity", false, false];
        yield return ["FlateDecode", "early2", true, false];
        yield return ["LZWDecode", "none", true, false];
        yield return ["LZWDecode", "early0", true, true];
        yield return ["LZWDecode", "early1", true, false];
        yield return ["LZWDecode", "early2", false, false];
        yield return ["LZWDecode", "early-type", false, true];
        yield return ["LZWDecode", "early-reference", true, false];
        yield return ["LZWDecode", "early-null", true, false];
        yield return ["LZWDecode", "early-missing", false, false];
        yield return ["LZWDecode", "predictor-reference", true, false];
        yield return ["LZWDecode", "predictor", false, true];
        yield return ["LZWDecode", "png", true, false];
        yield return ["DCTDecode", "none", true, false];
        yield return ["DCTDecode", "empty", false, true];
        yield return ["CCITTFaxDecode", "none", false, false];
    }

    private static PdfInteger Integer(long value) => new(value.ToString(CultureInfo.InvariantCulture), value);

    private static QpdfObject Parameters(string kind)
    {
        if (kind == "none") { return PdfNull.Instance; }
        Dictionary<PdfName, QpdfObject> entries = new();
        void Add(string key, QpdfObject value) => entries.Add(PdfName.FromAscii(key), value);
        if (kind.StartsWith("early", StringComparison.Ordinal))
        {
            Add("EarlyChange", kind switch
            {
                "early0" => Integer(0), "early1" => Integer(1), "early2" => Integer(2),
                "early-type" => PdfName.FromAscii("bad"),
                _ => new PdfReference(7, 3)
            });
        }
        else if (kind != "empty")
        {
            Add("Predictor", kind switch
            {
                "dormant" => Integer(1), "png" => Integer(15), "predictor" => Integer(99),
                "type" => PdfName.FromAscii("bad"), "range" => Integer(long.MaxValue),
                "predictor-reference" => new PdfReference(7, 3), _ => Integer(2)
            });
            if (kind is "dormant" or "columns") { Add("Columns", Integer(0)); }
            if (kind == "colors") { Add("Colors", Integer(0)); }
            if (kind == "bits") { Add("BitsPerComponent", Integer(3)); }
            if (kind is "overflow" or "row-capacity")
            {
                Add("Columns", Integer(int.MaxValue));
                Add("Colors", Integer(kind == "overflow" ? int.MaxValue : 2));
                Add("BitsPerComponent", Integer(16));
            }
        }
        return new PdfDictionary(entries);
    }

    private sealed class Resolver(QpdfObject parameters, QpdfObject? scalar, Action? observe) : IIndirectResolver
    {
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        {
            if (reference.ObjectNumber == 1) { value = parameters; return true; }
            observe?.Invoke(); value = scalar; return scalar is not null;
        }
    }

    private static FilterPlan Plan(string name, QpdfObject parameters, bool indirect, IIndirectResolver resolver, ResourceGovernor governor)
        => FilterChain.ResolveScoped(new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii(name),
            [PdfName.FromAscii("DecodeParms")] = indirect ? new PdfReference(1, 0) : parameters
        }), resolver, governor, CancellationToken.None);

    private static byte[] Data(string name, bool png) => Convert.FromHexString(name switch
    {
        "ASCIIHexDecode" => "34313E", "ASCII85Decode" => "356C7E3E", "RunLengthDecode" => "004180",
        "FlateDecode" => png ? "789C6370040000430042" : "789C73040000420042",
        "LZWDecode" => png ? "8000083010" : "80106020", _ => ""
    });

    [Theory]
    [MemberData(nameof(Cases))]
    public void MetadataAndDecoderUseTheSameParameterRules(string name, string kind, bool supported, bool indirect)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope prior = ParserMemoryScope.Create(governor))
        {
            prior.Reserve(97L); QpdfObject parameters = Parameters(kind);
            QpdfObject? scalar = kind == "early-null" ? PdfNull.Instance : kind == "early-missing" ? null : Integer(kind == "predictor-reference" ? 2 : 0);
            using FilterPlan stages = Plan(name, parameters, indirect, new Resolver(parameters, scalar, null), governor);
            long before = governor.Snapshot().LiveMemoryBytes;
            Assert.Equal(supported, FilterChain.SupportsParameters(stages, CancellationToken.None));
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
            Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes);
            if (parameters is PdfDictionary dictionary) { Assert.Same(dictionary, stages[0].Parms); }
            using ParserMemoryScope decoded = ParserMemoryScope.Create(governor);
            if (!supported)
            { Assert.ThrowsAny<QpdfException>(() => FilterChain.Decode([], stages, QpdfLimits.Default, governor, decoded)); }
            else if (name != "DCTDecode")
            { Assert.Equal("A"u8.ToArray(), FilterChain.Decode(Data(name, kind == "png"), stages, QpdfLimits.Default, governor, decoded)); }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ResolverCancellationIsFatalForEitherToken(bool governorToken, bool lzw)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        QpdfObject parameters = Parameters(lzw ? "early-reference" : "predictor-reference");
        using FilterPlan stages = Plan(lzw ? "LZWDecode" : "FlateDecode", parameters, false, new Resolver(parameters, Integer(2), cancellation.Cancel), governor);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.ThrowsAny<OperationCanceledException>(() => FilterChain.SupportsParameters(stages, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RefusedParameterStorageIsFatalAndPreservesPriorOwners(bool shared, bool predictor)
    {
        using RecordingMemoryBudget root = new(shared ? 2048 : 8192);
        using (IMemoryScope caller = root.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, caller))
        {
            QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shared ? 8192 : 2048 };
            ResourceGovernor governor = new(limits, CancellationToken.None, host);
            using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
            QpdfObject parameters = Parameters("tiff");
            using FilterPlan stages = Plan("FlateDecode", parameters, false, new Resolver(parameters, null, null), governor);
            using (ParserMemoryScope blocker = ParserMemoryScope.Create(governor))
            {
                // Cross the shared owner's current capacity on the selected
                // allocation, rather than filling the root's unused block slack.
                long remaining = (shared ? root.ReservedBytes : limits.MaxMemoryBytes) - governor.Snapshot().LiveMemoryBytes;
                blocker.Reserve(remaining - (predictor ? 64 : 32));
                long before = governor.Snapshot().LiveMemoryBytes, hostBefore = root.ReservedBytes;
                if (shared) { Assert.Throws<QpdfMemoryAdmissionException>(() => FilterChain.SupportsParameters(stages, CancellationToken.None)); }
                else { Assert.Throws<QpdfLimitException>(() => FilterChain.SupportsParameters(stages, CancellationToken.None)); }
                Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(hostBefore, root.ReservedBytes);
                Assert.Equal(97L, prior.ReservedBytes);
            }
            Assert.True(FilterChain.SupportsParameters(stages, CancellationToken.None));
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public void ParameterSupportDoesNotRequireAJpegCodecOrChangeDecodeLevels()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using FilterPlan jpeg = Plan("DCTDecode", PdfNull.Instance, false, new Resolver(PdfNull.Instance, null, null), governor);
        Assert.True(FilterChain.SupportsParameters(jpeg, CancellationToken.None));
        Assert.False(FilterChain.AllStagesInLevel(jpeg, "generalized")); Assert.True(FilterChain.AllStagesInLevel(jpeg, "all"));
        QpdfObject parameters = Parameters("predictor");
        using FilterPlan invalid = Plan("FlateDecode", parameters, false, new Resolver(parameters, null, null), governor);
        Assert.True(FilterChain.AllStagesInLevel(invalid, "generalized")); Assert.False(FilterChain.SupportsParameters(invalid, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChainedStagesKeepIndependentParametersAndRetireEachPreparation(bool invalid)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            QpdfObject parameters = Parameters(invalid ? "predictor" : "empty");
            using FilterPlan stages = FilterChain.ResolveScoped(new(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("Filter")] = new PdfArray([PdfName.FromAscii("AHx"), PdfName.FromAscii("Fl")]),
                [PdfName.FromAscii("DecodeParms")] = new PdfArray([PdfNull.Instance, parameters])
            }), new Resolver(parameters, null, null), governor, CancellationToken.None);
            long before = governor.Snapshot().LiveMemoryBytes;
            Assert.Equal(!invalid, FilterChain.SupportsParameters(stages, CancellationToken.None));
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, governor.Snapshot().TotalDecodedBytes);
            byte[] input = "789C73040000420042>"u8.ToArray();
            if (invalid) { Assert.Throws<QpdfFilterException>(() => FilterChain.Decode(input, stages, QpdfLimits.Default, governor, memory)); }
            else { Assert.Equal("A"u8.ToArray(), FilterChain.Decode(input, stages, QpdfLimits.Default, governor, memory)); }
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

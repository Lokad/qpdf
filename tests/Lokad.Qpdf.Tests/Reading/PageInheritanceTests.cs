using System.Reflection;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Reading;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class PageInheritanceTests
{
    private static readonly PdfName[] Names = [PdfName.FromAscii("CropBox"), PdfName.FromAscii("MediaBox"), PdfName.FromAscii("Resources"), PdfName.FromAscii("Rotate")];
    // Observe our own finite work notifications, with periodic resets disabled.
    private static readonly FieldInfo Work = typeof(ResourceGovernor).GetField("_operationsSinceCheck", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing work observation.");

    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(x => PdfName.FromAscii(x.Key), x => x.Value));

    private static Dictionary<int, QpdfObject> Chain(int depth)
    {
        Dictionary<int, QpdfObject> objects = new();
        for (int id = 1; id <= depth; id++) { objects[id] = Dict(("Parent", new PdfReference(id + 1, 0))); }
        objects[depth + 1] = Dict(("CropBox", new PdfArray([new PdfInteger("0", 0)])),
            ("MediaBox", new PdfArray([new PdfInteger("1", 1)])), ("Resources", Dict()), ("Rotate", new PdfInteger("90", 90)));
        return objects;
    }

    private static DocumentEngine Engine(IReadOnlyDictionary<int, QpdfObject> objects)
    {
        DocumentEngine engine = TestDocumentEngine.Create(Dict(), new(QpdfLimits.Default, CancellationToken.None));
        engine.SetInspectionObjects(objects); return engine;
    }

    private static PageLocation Leaf(IReadOnlyDictionary<int, QpdfObject> objects)
        => new(new PdfReference(1, 0), Assert.IsType<PdfDictionary>(objects[1]));

    private static QpdfObject?[] Read(DocumentEngine engine, PageLocation leaf, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        using PageInheritance.Lookup lookup = new(engine, leaf, limits, governor, token);
        return [lookup.Find("CropBox"u8), lookup.Find("MediaBox"u8), lookup.Find("Resources"u8), lookup.Find("Rotate"u8)];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(128)]
    public void StagedLookupReusesParentTraversalAndKeepsRawIdentities(int depth)
    {
        Dictionary<int, QpdfObject> objects = Chain(depth); using DocumentEngine engine = Engine(objects);
        PageLocation leaf = Leaf(objects); QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = long.MaxValue };
        ResourceGovernor separate = new(limits, CancellationToken.None), staged = new(limits, CancellationToken.None);
        QpdfObject?[] expected = Names.Select(name => PageInheritance.Find(engine, leaf, name, limits, separate, CancellationToken.None)).ToArray();
        QpdfObject?[] actual = Read(engine, leaf, limits, staged, CancellationToken.None);
        for (int index = 0; index < Names.Length; index++) { Assert.Same(expected[index], actual[index]); Assert.NotNull(actual[index]); }
        long original = (long)(Work.GetValue(separate) ?? throw new InvalidOperationException());
        long reused = (long)(Work.GetValue(staged) ?? throw new InvalidOperationException());
        Assert.InRange(reused, 1L, original * 3L / 4L);
        Assert.Equal(0L, separate.CounterValues.LiveMemoryBytes); Assert.Equal(0L, staged.CounterValues.LiveMemoryBytes);
        if (depth == 1) { Assert.Equal(0L, staged.CounterValues.PeakMemoryBytes); }
        else { Assert.InRange(staged.CounterValues.PeakMemoryBytes, 1L, 32768L); }
    }

    [Fact]
    public void DifferentAncestorDepthsNullsAndLeafOverridesKeepTheNearestRawValue()
    {
        PdfReference crop = new(10, 0), media = new(11, 0), resources = new(12, 0);
        PdfBoolean rotate = PdfBoolean.True;
        Dictionary<int, QpdfObject> objects = new()
        {
            [1] = Dict(("Parent", new PdfReference(2, 0)), ("CropBox", new PdfReference(13, 0)),
                ("MediaBox", new PdfReference(11, 1)), ("Rotate", rotate)),
            [2] = Dict(("Parent", new PdfReference(3, 0)), ("CropBox", crop), ("Resources", PdfNull.Instance)),
            [3] = Dict(("Parent", new PdfReference(4, 0)), ("Resources", resources), ("MediaBox", new PdfReference(99, 0))),
            [4] = Dict(("MediaBox", media), ("CropBox", new PdfInteger("2", 2)), ("Rotate", new PdfInteger("180", 180))),
            [10] = new PdfArray([]), [11] = new PdfArray([]), [12] = Dict(), [13] = PdfNull.Instance,
        };
        using DocumentEngine engine = Engine(objects); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfObject?[] values = Read(engine, Leaf(objects), QpdfLimits.Default, governor, CancellationToken.None);
        Assert.Same(crop, values[0]); Assert.Same(media, values[1]); Assert.Same(resources, values[2]); Assert.Same(rotate, values[3]);
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public void DirectAncestorsAndEndedMissesKeepTheirLookupOrder()
    {
        PdfDictionary root = Dict(("MediaBox", new PdfArray([])), ("Rotate", new PdfInteger("90", 90)));
        PdfDictionary parent = Dict(("Parent", root), ("CropBox", new PdfArray([])));
        PdfDictionary page = Dict(("Parent", parent));
        using DocumentEngine engine = Engine(new Dictionary<int, QpdfObject>());
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (PageInheritance.Lookup lookup = new(engine, new(null, page), QpdfLimits.Default, governor, CancellationToken.None))
        {
            Assert.Null(lookup.Find("Resources"u8)); Assert.Same(Get(parent, "CropBox"), lookup.Find("CropBox"u8));
            Assert.Same(Get(root, "MediaBox"), lookup.Find("MediaBox"u8)); Assert.Null(lookup.Find("Resources"u8));
            Assert.Same(Get(root, "Rotate"), lookup.Find("Rotate"u8));
        }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public void CompleteLeafValuesDoNotInspectAnUnusedInvalidParent()
    {
        PdfDictionary page = Dict(("Parent", new PdfInteger("17", 17)), ("CropBox", new PdfArray([])),
            ("MediaBox", new PdfArray([])), ("Resources", Dict()), ("Rotate", new PdfInteger("0", 0)));
        using DocumentEngine engine = Engine(new Dictionary<int, QpdfObject>());
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfObject?[] values = Read(engine, new(null, page), QpdfLimits.Default, governor, CancellationToken.None);
        for (int index = 0; index < Names.Length; index++) { Assert.Same(Get(page, Names[index].DisplayText), values[index]); }
        Assert.Equal(0L, governor.CounterValues.PeakMemoryBytes);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("stale")]
    [InlineData("missing")]
    [InlineData("invalid")]
    public void AnExtendedLookupStillRejectsDamagedAncestryAndRefunds(string shape)
    {
        QpdfObject next = shape switch { "cycle" => new PdfReference(1, 0), "stale" => new PdfReference(3, 1),
            "missing" => new PdfReference(99, 0), _ => new PdfInteger("1", 1) };
        Dictionary<int, QpdfObject> objects = Chain(2);
        objects[2] = Dict(("Parent", next), ("CropBox", new PdfArray([])));
        using DocumentEngine engine = Engine(objects); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Throws<QpdfParseException>(() => Read(engine, Leaf(objects), QpdfLimits.Default, governor, CancellationToken.None));
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(128)]
    public void AnExtendedLookupEnforcesTheConfiguredDepth(int maximum)
    {
        Dictionary<int, QpdfObject> objects = Chain(maximum + 1);
        objects[2] = Dict(("Parent", new PdfReference(3, 0)), ("CropBox", new PdfArray([])));
        using DocumentEngine engine = Engine(objects); QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = maximum };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => Read(engine, Leaf(objects), limits, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxNestingDepth), error.LimitName); Assert.Equal(maximum + 1L, error.Requested); Assert.Equal(maximum, error.Allowed);
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterTheFirstStageRefundsTheCachedPath(bool callerToken)
    {
        Dictionary<int, QpdfObject> objects = Chain(32); using DocumentEngine engine = Engine(objects);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, callerToken ? CancellationToken.None : cancellation.Token);
        CancellationToken token = callerToken ? cancellation.Token : CancellationToken.None;
        bool canceled = false;
        using (PageInheritance.Lookup lookup = new(engine, Leaf(objects), QpdfLimits.Default, governor, token))
        {
            Assert.NotNull(lookup.Find("CropBox"u8)); Assert.True(governor.CounterValues.LiveMemoryBytes > 0);
            cancellation.Cancel();
            try { _ = lookup.Find("MediaBox"u8); } catch (OperationCanceledException) { canceled = true; }
        }
        Assert.True(canceled); Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public void AdmissionRefusalRefundsOnlyTheLookupOwner()
    {
        Dictionary<int, QpdfObject> objects = Chain(128); using DocumentEngine engine = Engine(objects);
        using InMemoryQpdfHost storage = new(); int growths = 0;
        for (int refuse = 0; refuse <= growths; refuse++)
        {
            using RecordingMemoryBudget root = new(1L << 20) { RefuseOnGrowth = refuse == 0 ? 0 : refuse + 1 };
            using IMemoryScope owner = root.CreateScope();
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, new MemoryBudgetHost(storage, owner));
            using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(17L);
            long baseline = governor.CounterValues.LiveMemoryBytes;
            if (refuse == 0) { Read(engine, Leaf(objects), QpdfLimits.Default, governor, CancellationToken.None); growths = root.GrowthCalls - 1; Assert.InRange(growths, 1, 64); }
            else { Assert.Throws<QpdfMemoryAdmissionException>(() => Read(engine, Leaf(objects), QpdfLimits.Default, governor, CancellationToken.None)); }
            Assert.Equal(17L, prior.ReservedBytes); Assert.Equal(baseline, governor.CounterValues.LiveMemoryBytes);
            // The shared owner can retain block slack between phases, then trims
            // it at completion while the independent prior storage remains funded.
            governor.CompleteStorage(); Assert.Equal(baseline, root.ReservedBytes);
            prior.Dispose(); owner.Dispose(); Assert.Equal(0L, root.ReservedBytes);
        }
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L };
        ResourceGovernor local = new(limits, CancellationToken.None);
        using ParserMemoryScope retained = ParserMemoryScope.Create(local); retained.Reserve(17L);
        long localBaseline = local.CounterValues.LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => Read(engine, Leaf(objects), limits, local, CancellationToken.None));
        Assert.Equal(17L, retained.ReservedBytes); Assert.Equal(localBaseline, local.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public void DisposalRetiresCachedStorageAndRejectsFurtherUse()
    {
        Dictionary<int, QpdfObject> objects = Chain(8); using DocumentEngine engine = Engine(objects);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PageInheritance.Lookup lookup = new(engine, Leaf(objects), QpdfLimits.Default, governor, CancellationToken.None);
        Assert.NotNull(lookup.Find("CropBox"u8)); lookup.Dispose(); lookup.Dispose();
        bool disposed = false;
        try { lookup.Find("MediaBox"u8); } catch (ObjectDisposedException) { disposed = true; }
        Assert.True(disposed); Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    private static QpdfObject Get(PdfDictionary dictionary, string name)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return value; }
}

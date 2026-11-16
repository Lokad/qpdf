using System.Collections;
using System.Text.Json;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OptionalContentStateTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input"), Source = QpdfPath.From("source"), Output = QpdfPath.From("output");

    public static IEnumerable<object[]> States()
    {
        foreach (bool retained in new[] { false, true })
        foreach ((string basis, string on, string off, bool a, bool b) in new[]
        {
            ("absent", "absent", "absent", true, true), ("ON", "null", "null", true, true),
            ("OFF", "absent", "absent", false, false), ("OFF", "[5 0 R]", "absent", true, false),
            ("OFF", "[6 0 R]", "absent", false, true), ("ON", "absent", "[5 0 R]", false, true),
            ("OFF", "[5 0 R 6 0 R]", "absent", true, true), ("ON", "absent", "[5 0 R 6 0 R]", false, false),
            ("OFF", "[5 0 R 6 0 R]", "[6 0 R]", true, false),
        }) { yield return [retained, basis, on, off, a, b]; }
    }

    [Theory]
    [MemberData(nameof(States))]
    public async Task StaticDefaultsKeepIndependentGroupStates(bool retained, string basis, string on, string off, bool a, bool b)
    {
        using InMemoryQpdfHost host = Prepare(Fixture("ON", "absent", "absent", true, true, "P"),
            Fixture(basis, on, off, retained, retained, "S"));
        await Merge(host, CancellationToken.None);
        await using QpdfDocument document = await OpenOutput(host, Output);
        AssertCombined(document, true, true, a, b);
        AssertPrivate(document, 0, "ON", true, true);
        if (retained) { AssertPrivate(document, 1, basis, a, b); }
    }

    [Theory]
    [InlineData("argv", true, false)]
    [InlineData("job", false, false)]
    [InlineData("overlay", true, false)]
    [InlineData("underlay", false, false)]
    [InlineData("job", false, true)]
    public async Task SourceStatesFollowSelectionPlacementAndSplitWiring(string route, bool packed, bool split)
    {
        using InMemoryQpdfHost host = Prepare(Fixture("OFF", "[5 0 R]", "absent", true, true, "P"),
            Fixture("OFF", "[6 0 R]", "absent", true, true, "S"));
        QpdfExecutionResult result = await Execute(host, route, packed, split, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        await using QpdfDocument document = await OpenOutput(host, split ? QpdfPath.From("output-1") : Output);
        AssertCombined(document, true, false, false, true);
        if (split)
        {
            await using QpdfDocument second = await OpenOutput(host, QpdfPath.From("output-2"));
            AssertCombined(second, true, false, false, true);
        }
        else { AssertPrivate(document, 0, "OFF", true, false); }
    }

    [Theory]
    [InlineData("absent", false, false)]
    [InlineData("[5 0 R]", true, false)]
    public async Task OneRetainedStaticConfigurationKeepsItsOriginalHeader(string on, bool a, bool b)
    {
        using InMemoryQpdfHost host = Prepare(Fixture("OFF", on, "absent", true, true, "P"),
            Fixture("ON", "absent", "absent", false, false, "S"));
        await Qpdf.SelectPageAsync(host, Input, Output, new(1), CancellationToken.None);
        await using QpdfDocument document = await OpenOutput(host, Output);
        PdfDictionary properties = Properties(document);
        Assert.Equal("OFF", Assert.IsType<PdfName>(Read(document, Dict(document, Read(document, properties, "D")), "BaseState")).DisplayText);
        Assert.Equal(new Dictionary<string, bool> { ["PA"] = a, ["PB"] = b }, Visibility(document, properties));
        AssertPrivate(document, 0, "OFF", a, b);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverrideMembersOnlyAffectLiveRegisteredGroupReferences(bool disable)
    {
        const string members = "[null 5 1 R 99 0 R 13 0 R << /Type /OCG >> 6 0 R]";
        using InMemoryQpdfHost host = Prepare(Fixture("ON", "absent", "absent", true, true, "P"),
            Fixture(disable ? "ON" : "OFF", disable ? "absent" : members, disable ? members : "absent", false, false, "S"));
        await Merge(host, CancellationToken.None);
        await using QpdfDocument document = await OpenOutput(host, Output);
        AssertCombined(document, true, true, disable, !disable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedOnArraysRefuseBeforeReplacingOutput(bool retained)
    {
        using InMemoryQpdfHost host = Prepare(Fixture("ON", "absent", "absent", true, true, "P"),
            Fixture("OFF", "42", "absent", retained, retained, "S"));
        await Seed(host);
        QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(() => Merge(host, CancellationToken.None));
        Assert.Contains("ON states must be an array", error.Message, StringComparison.Ordinal);
        Assert.True(host.TryGetOutput(Output, out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BothCopyPathsRefundEveryAdmissionAndPreservePublication(bool retained)
    {
        async Task<InMemoryQpdfHost> PrepareAsync()
        {
            InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(Fixture("ON", "absent", "absent", true, true, "P"), Output);
            host.AddInput(Source, Fixture("OFF", "[6 0 R]", "[5 0 R]", retained, retained, "S"), QpdfInputOwnership.Transfer);
            return host;
        }
        return PreparationAdmissionChecks.VerifyAsync(PrepareAsync,
            (host, token) => Execute(host, "argv", false, false, token), Output, 160, ["write", "cancel"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherTokenStopsOffBaseInterpretationAfterResolvingTheBase(bool caller)
    {
        using InMemoryQpdfHost host = Prepare(Fixture("OFF", "[5 0 R]", "absent", false, true, "P"),
            Fixture("ON", "absent", "absent", false, false, "S"));
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor); owner.Reserve(71L);
        long prior = governor.Snapshot().LiveMemoryBytes;
        GraphCopier first = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 1, owner);
        GraphCopier second = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 1, owner);
        first.OptionalContent = new(new PdfReference(7, 0), OptionalContentFailure.None);
        second.OptionalContent = new(new PdfReference(7, 0), OptionalContentFailure.None);
        Dictionary<int, QpdfObject> values = Enumerable.Range(1, 13).ToDictionary(number => number, document.GetObject);
        CancelPool pool = new(values, cancellation.Cancel);
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.ThrowsAny<OperationCanceledException>(() => OptionalContent.Combine(null, [first, second], [], null, pool,
            governor, caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(1, pool.BaseReads); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        owner.Dispose(); Assert.True(prior > 0); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    internal static byte[] Fixture(string basis, string on, string off, bool retained, bool indirect, string tag)
    {
        string Entry(string key, string value, int number) => value == "absent" ? "" : " /" + key + " " + (indirect ? number + " 0 R" : value);
        string defaults = "<<" + Entry("BaseState", basis == "absent" ? "absent" : "/" + basis, 10)
            + Entry("ON", on, 11) + Entry("OFF", off, 12) + " >>";
        string properties = "<< /OCGs " + (indirect ? "8 0 R" : "[5 0 R 6 0 R]") + " /D " + (indirect ? "9 0 R" : defaults) + " >>";
        byte[] program = "/OC /A BDC 1 0 0 rg 0 0 40 40 re f EMC /OC /B BDC 0 0 1 rg 40 0 40 40 re f EMC"u8.ToArray();
        return PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /OCProperties " + (indirect ? "7 0 R" : properties) + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << /Properties << /A 5 0 R /B 6 0 R >> >> /Contents 4 0 R" + (retained ? " /Private 7 0 R" : "") + " >>", null),
            (4, "<< /Length " + program.Length + " >>", program),
            (5, "<< /Type /OCG /Name (Same) /Tag (" + tag + "A) >>", null),
            (6, "<< /Type /OCG /Name (Same) /Tag (" + tag + "B) >>", null),
            (7, properties, null), (8, "[5 0 R 6 0 R]", null), (9, defaults, null),
            (10, basis == "absent" ? "null" : "/" + basis, null),
            (11, on == "absent" ? "null" : on, null), (12, off == "absent" ? "null" : off, null),
            (13, "<< /Type /OCG /Name (Unused) >>", null)], 1, null, "1.5", "");
    }

    private static InMemoryQpdfHost Prepare(byte[] input, byte[] source)
    {
        InMemoryQpdfHost host = new(); host.AddInput(Input, input, QpdfInputOwnership.Transfer);
        host.AddInput(Source, source, QpdfInputOwnership.Transfer); return host;
    }

    private static async Task Merge(IQpdfHost host, CancellationToken token)
        => _ = await Qpdf.SelectPagesAsync(host, [new(Input, new(1)), new(Source, new(1))], Output, new PdfPageMergeOptions(), token);

    private static ValueTask<QpdfExecutionResult> Execute(IQpdfHost host, string route, bool packed, bool split, CancellationToken token)
    {
        List<string> argv = ["input"]; Dictionary<string, object> job = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
        if (route is "overlay" or "underlay") { argv.AddRange(["--" + route, "source", "--"]); job[route] = new { file = "source" }; }
        else { argv.AddRange(["--pages", ".", "1", "source", "1", "--"]); job["pages"] = new[] { new { file = ".", range = "1" }, new { file = "source", range = "1" } }; }
        if (packed) { argv.Add("--object-streams=generate"); job["objectStreams"] = "generate"; }
        if (split) { argv.Add("--split-pages=1"); job["splitPages"] = "1"; }
        argv.Add("output");
        return route is "job" or "underlay" ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), token) : Qpdf.ExecuteAsync(host, new(argv), token);
    }

    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    private static async ValueTask<QpdfDocument> OpenOutput(InMemoryQpdfHost host, QpdfPath path)
    {
        Assert.True(host.TryGetOutput(path, out byte[] bytes));
        QpdfPath read = QpdfPath.From("read-" + path.Value); host.AddInput(read, bytes, QpdfInputOwnership.Transfer);
        return await QpdfDocument.OpenAsync(host, read, CancellationToken.None);
    }

    private static PdfDictionary Dict(QpdfDocument document, QpdfObject value)
        => Assert.IsType<PdfDictionary>(value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value);
    private static QpdfObject Read(QpdfDocument document, PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)) { return PdfNull.Instance; }
        return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    }
    private static PdfDictionary Properties(QpdfDocument document)
        => Dict(document, Read(document, Dict(document, Read(document, document.Engine.Trailer, "Root")), "OCProperties"));

    private static Dictionary<string, bool> Visibility(QpdfDocument document, PdfDictionary properties)
    {
        PdfDictionary defaults = Dict(document, Read(document, properties, "D"));
        bool basis = Read(document, defaults, "BaseState") is not PdfName name || name.DisplayText != "OFF";
        PdfArray groups = Assert.IsType<PdfArray>(Read(document, properties, "OCGs"));
        Dictionary<string, bool> states = new();
        foreach (QpdfObject group in groups.Items)
        {
            Assert.IsType<PdfReference>(group);
            bool value = basis;
            if (Read(document, defaults, "ON") is PdfArray on && on.Items.Contains(group)) { value = true; }
            if (Read(document, defaults, "OFF") is PdfArray off && off.Items.Contains(group)) { value = false; }
            states.Add(System.Text.Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Read(document, Dict(document, group), "Tag")).ValueSpan), value);
        }
        return states;
    }

    private static void AssertCombined(QpdfDocument document, bool pa, bool pb, bool sa, bool sb)
    {
        PdfDictionary properties = Properties(document), defaults = Dict(document, Read(document, properties, "D"));
        Assert.Equal("ON", Assert.IsType<PdfName>(Read(document, defaults, "BaseState")).DisplayText);
        Assert.False(defaults.TryGet(PdfName.FromAscii("ON"), out _));
        PdfArray groups = Assert.IsType<PdfArray>(Read(document, properties, "OCGs"));
        Assert.Equal(4, groups.Count); Assert.Equal(4, groups.Items.Distinct().Count());
        Assert.All(groups.Items, group => Assert.Equal("Same", System.Text.Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Read(document, Dict(document, group), "Name")).ValueSpan)));
        Assert.Equal(new Dictionary<string, bool> { ["PA"] = pa, ["PB"] = pb, ["SA"] = sa, ["SB"] = sb }, Visibility(document, properties));
    }

    private static void AssertPrivate(QpdfDocument document, int page, string basis, bool a, bool b)
    {
        PdfDictionary properties = Dict(document, Read(document, document.PageReferences[page].Page, "Private"));
        QpdfObject value = Read(document, Dict(document, Read(document, properties, "D")), "BaseState");
        if (basis == "absent") { Assert.IsType<PdfNull>(value); }
        else { Assert.Equal(basis, Assert.IsType<PdfName>(value).DisplayText); }
        bool[] expected = [a, b]; Assert.Equal(expected, Visibility(document, properties).Values);
    }

    private sealed class CancelPool(IReadOnlyDictionary<int, QpdfObject> values, Action cancel) : IReadOnlyDictionary<int, QpdfObject>
    {
        internal int BaseReads { get; private set; }
        public bool TryGetValue(int key, out QpdfObject value)
        {
            if (key == 10) { BaseReads++; cancel(); }
            if (values.TryGetValue(key, out QpdfObject? found)) { value = found; return true; }
            value = PdfNull.Instance; return false;
        }
        public QpdfObject this[int key] => values[key];
        public IEnumerable<int> Keys => values.Keys;
        public IEnumerable<QpdfObject> Values => values.Values;
        public int Count => values.Count;
        public bool ContainsKey(int key) => values.ContainsKey(key);
        public IEnumerator<KeyValuePair<int, QpdfObject>> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

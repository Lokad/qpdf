using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayParentTests
{
    private static PdfDictionary Dict(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));

    private sealed class Parents(int count, Action<int> resolved) : IIndirectResolver
    {
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        {
            resolved(reference.ObjectNumber);
            value = reference.ObjectNumber <= count
                ? Dict(("T", new PdfByteString("Field"u8.ToArray(), false)), ("Parent", new PdfReference(reference.ObjectNumber + 1, 0)))
                : Dict(("FT", PdfName.FromAscii("Tx")));
            return true;
        }
    }

    private sealed class Generations(IReadOnlyDictionary<int, (int Generation, PdfDictionary Value)> nodes) : IIndirectResolver
    {
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        {
            value = nodes.TryGetValue(reference.ObjectNumber, out var node) && node.Generation == reference.Generation
                ? node.Value : PdfNull.Instance;
            return true;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SourceGenerationIsPreservedAndDeadBackLinksAreNotCycles(bool parented, bool liveBackLink)
    {
        PdfReference widget = new(1, 2), parent = new(2, 3);
        PdfDictionary annotation = Dict(("FT", PdfName.FromAscii("Tx")),
            ("Parent", parented ? parent : new PdfReference(1, liveBackLink ? 2 : 0)));
        PdfDictionary field = Dict(("FT", PdfName.FromAscii("Tx")), ("Parent", new PdfReference(1, liveBackLink ? 2 : 0)));
        Generations resolver = new(new Dictionary<int, (int, PdfDictionary)> { [1] = (2, annotation), [2] = (3, field) });
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        var top = AcroFormCopy.FindOverlayTopField(annotation, widget, true, resolver, governor, CancellationToken.None);
        Assert.NotNull(top); Assert.Same(parented ? parent : widget, top.Reference);
        Assert.Equal(parented && liveBackLink, top.Cycled); Assert.False(top.DirectTop);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(256)]
    [InlineData(499)]
    public void ParentEdgesUseConfiguredBoundaryAndRefundStorage(int maximum)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxNestingDepth = maximum }, CancellationToken.None);
        PdfDictionary annotation = Dict(("Parent", new PdfReference(2, 0)));
        var top = AcroFormCopy.FindOverlayTopField(annotation, new PdfReference(1, 0), true, new Parents(maximum, _ => { }), governor, CancellationToken.None);
        Assert.NotNull(top); Assert.Equal(maximum + 1, top.Reference?.ObjectNumber); Assert.False(top.Cycled); Assert.False(top.DirectTop);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => AcroFormCopy.FindOverlayTopField(
            annotation, new PdfReference(1, 0), true, new Parents(maximum + 1, _ => { }), governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxNestingDepth), failure.LimitName);
        Assert.Equal(maximum + 1L, failure.Requested); Assert.Equal(maximum, failure.Allowed);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class Objects(Dictionary<int, QpdfObject> objects) : IIndirectResolver
    {
        public bool TryResolve(PdfReference reference, out QpdfObject? value) => objects.TryGetValue(reference.ObjectNumber, out value);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("cycle")]
    [InlineData("direct")]
    [InlineData("missing")]
    [InlineData("scalar")]
    [InlineData("null")]
    [InlineData("no-identity")]
    [InlineData("no-form")]
    [InlineData("direct-annotation")]
    public void MalformedAndCyclicParentsRetainTheirFallbacks(string kind)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary field = Dict(("FT", PdfName.FromAscii("Tx")));
        QpdfObject parent = kind switch
        {
            "self" => new PdfReference(1, 0),
            "cycle" or "no-identity" => new PdfReference(2, 0),
            "direct" => field,
            "scalar" => new PdfInteger("17", 17),
            "null" => PdfNull.Instance,
            _ => new PdfReference(9, 0)
        };
        PdfDictionary annotation = Dict(("T", new PdfByteString("Widget"u8.ToArray(), false)), ("Parent", parent));
        Objects resolver = new(new() { [1] = annotation, [2] = kind == "cycle" ? Dict(("FT", PdfName.FromAscii("Tx")), ("Parent", new PdfReference(1, 0))) : Dict() });
        var top = AcroFormCopy.FindOverlayTopField(annotation, kind == "direct-annotation" ? null : new PdfReference(1, 0),
            kind != "no-form", resolver, governor, CancellationToken.None);
        if (kind is "no-identity" or "no-form" or "direct-annotation") { Assert.Null(top); }
        else
        {
            Assert.NotNull(top); Assert.Equal(kind == "direct", top.DirectTop); Assert.Equal(kind == "cycle", top.Cycled);
            Assert.Equal(kind == "direct" ? null : kind == "cycle" ? 2 : 1, top.Reference?.ObjectNumber);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, 33)]
    [InlineData(true, 33)]
    [InlineData(false, 65)]
    [InlineData(true, 65)]
    public void CancellationDuringParentResolutionRefundsTraversal(bool caller, int cancelAt)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(17L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Parents resolver = new(64, number => { if (number == cancelAt) { cancellation.Cancel(); } });
        Assert.ThrowsAny<OperationCanceledException>(() => AcroFormCopy.FindOverlayTopField(
            Dict(("Parent", new PdfReference(2, 0))), new PdfReference(1, 0), true, resolver, governor, caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(17L, prior.ReservedBytes);
    }

    [Fact]
    public void ParentVisitationRefusesGrowthAndRefundsTraversal()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(17L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => AcroFormCopy.FindOverlayTopField(Dict(("Parent", new PdfReference(2, 0))),
            new PdfReference(1, 0), true, new Parents(256, _ => { }), governor, CancellationToken.None));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(17L, prior.ReservedBytes);
    }

    public static IEnumerable<object[]> ImportCases()
    {
        foreach (int count in new[] { 16, 33, 34, 64, 96 })
        foreach (bool job in new[] { false, true })
        foreach (string mode in new[] { "overlay", "underlay" }) { yield return [count, job, mode]; }
    }

    [Theory]
    [MemberData(nameof(ImportCases))]
    public async Task ImportsCompleteFieldNamesAndInheritedType(int count, bool job, string mode)
    {
        byte[] output;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("base"), Fixture(0), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("stamp"), Fixture(count), QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = await Import(host, job, mode, QpdfLimits.Default);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out output)); Assert.Equal(0L, host.OpenLeaseCount);
        }
        // Read the escaped output after the source host and all its leases close.
        using InMemoryQpdfHost reader = new(); reader.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(reader, QpdfPath.From("read"), CancellationToken.None))
        {
            QpdfObject Resolve(QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
            QpdfObject Get(PdfDictionary dictionary, string key)
            { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(value); }
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            PdfDictionary form = Assert.IsType<PdfDictionary>(Get(catalog, "AcroForm"));
            PdfArray fields = Assert.IsType<PdfArray>(Get(form, "Fields")); Assert.Equal(1, fields.Count);
            PdfDictionary cursor = Assert.IsType<PdfDictionary>(Resolve(fields[0]));
            Assert.Equal("Tx", Encoding.ASCII.GetString(Assert.IsType<PdfName>(Get(cursor, "FT")).RawSpan));
            Assert.False(cursor.TryGet(PdfName.FromAscii("Parent"), out _));
            List<string> names = new();
            for (int index = 0; index < count; index++)
            {
                names.Add(Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(cursor, "T")).Value));
                if (index + 1 < count)
                { PdfArray kids = Assert.IsType<PdfArray>(Get(cursor, "Kids")); Assert.Equal(1, kids.Count); cursor = Assert.IsType<PdfDictionary>(Resolve(kids[0])); }
            }
            Assert.Equal(string.Join('.', Enumerable.Range(0, count).Select(index => "F" + index)), string.Join('.', names));
            Assert.Equal("Widget", Encoding.ASCII.GetString(Assert.IsType<PdfName>(Get(cursor, "Subtype")).RawSpan));
        }
        Assert.Equal(0L, reader.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, "overlay")]
    [InlineData(true, "overlay")]
    [InlineData(false, "underlay")]
    [InlineData(true, "underlay")]
    public async Task ParentLimitFailurePreservesExistingOutput(bool job, string mode)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("base"), Fixture(0), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), Fixture(34), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Import(host, job, mode, QpdfLimits.Default with { MaxNestingDepth = 32 });
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains(nameof(QpdfLimits.MaxNestingDepth), StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal(new byte[] { 17, 23, 41 }, output);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static ValueTask<QpdfExecutionResult> Import(InMemoryQpdfHost host, bool job, string mode, QpdfLimits limits)
        => job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object>
        { ["inputFile"] = "base", ["outputFile"] = "output", [mode] = new { file = "stamp" } }), new(limits), CancellationToken.None)
        : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["base", "--" + mode, "stamp", "--", "output"]), new(limits), CancellationToken.None);

    private static byte[] Fixture(int count)
    {
        List<(int, string, byte[]?)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R" + (count == 0 ? "" : " /AcroForm << /Fields [5 0 R] >>") + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> /Contents 4 0 R"
                + (count == 0 ? "" : " /Annots [" + (count + 4) + " 0 R]") + " >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray())];
        for (int index = 0; index < count; index++)
        {
            int number = index + 5;
            objects.Add((number, "<< /T (F" + index + ")" + (index == 0 ? " /FT /Tx" : " /Parent " + (number - 1) + " 0 R")
                + (index + 1 < count ? " /Kids [" + (number + 1) + " 0 R]" : " /Type /Annot /Subtype /Widget /P 3 0 R /Rect [0 0 10 10]") + " >>", null));
        }
        return CheckTests.Assemble(objects, 1);
    }
}

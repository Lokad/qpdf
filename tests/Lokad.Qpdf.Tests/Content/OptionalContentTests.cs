using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OptionalContentTests
{
    public static IEnumerable<object[]> MembershipPolicies()
    {
        foreach (string policy in new[] { "AnyOn", "AllOn", "AnyOff", "AllOff" })
        foreach (string placement in new[] { "overlay", "underlay" })
        foreach (string route in new[] { "argv", "job" }) { yield return [policy, placement, route]; }
    }

    [Theory]
    [MemberData(nameof(MembershipPolicies))]
    public async Task PlacementKeepsOneMembershipIdentityForContentFormsAndAnnotations(string policy, string placement, string route)
    {
        byte[] program = "/OC /M BDC 1 0 0 rg 10 10 60 60 re f EMC /Inner Do"u8.ToArray();
        byte[] innerProgram = "0 0 1 rg 70 70 20 20 re f"u8.ToArray();
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /BaseState /ON /OFF [5 0 R] >> >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Properties << /M 7 0 R >> /XObject << /Inner 8 0 R >> >> /Contents 4 0 R /Annots [9 0 R] >>", null),
            (4, "<< /Length " + program.Length + " >>", program),
            (5, "<< /Type /OCG /Name (Same) >>", null), (6, "<< /Type /OCG /Name (Same) >>", null),
            (7, "<< /Type /OCMD /OCGs [5 0 R 6 0 R] /P /" + policy + " >>", null),
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << >> /OC 7 0 R /Length " + innerProgram.Length + " >>", innerProgram),
            (9, "<< /Type /Annot /Subtype /Link /Rect [1 1 10 10] /OC 7 0 R /A << /S /URI /URI (https://example.test/) >> >>", null)], 1);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("source"), source, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new(["input", "--" + placement, "source", "--", "output"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object> { ["inputFile"] = "input", ["outputFile"] = "output", [placement] = new { file = "source" } }), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Read(PdfDictionary dictionary, string key) { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
        PdfDictionary Dict(QpdfObject value) => Assert.IsType<PdfDictionary>(Resolve(value));
        PdfDictionary page = Assert.Single(document.PageReferences).Page;
        PdfDictionary xobjects = Dict(Read(Dict(Read(page, "Resources")), "XObject"));
        PdfStream form = Assert.Single(xobjects.Keys.Select(key => Assert.IsType<PdfStream>(Resolve(Read(xobjects, key.DisplayText)))),
            candidate => candidate.Dictionary.TryGet(PdfName.FromAscii("Resources"), out QpdfObject raw)
                && Resolve(raw) is PdfDictionary resource && resource.TryGet(PdfName.FromAscii("Properties"), out raw)
                && Resolve(raw) is PdfDictionary properties && properties.TryGet(PdfName.FromAscii("M"), out _));
        PdfDictionary resources = Dict(Read(form.Dictionary, "Resources"));
        PdfReference membership = Assert.IsType<PdfReference>(Read(Dict(Read(resources, "Properties")), "M"));
        PdfStream inner = Assert.IsType<PdfStream>(Resolve(Read(Dict(Read(resources, "XObject")), "Inner")));
        Assert.Equal(membership, Read(inner.Dictionary, "OC"));
        PdfArray annotations = Assert.IsType<PdfArray>(Resolve(Read(page, "Annots")));
        Assert.Equal(membership, Read(Dict(Assert.Single(annotations.Items)), "OC"));
        PdfDictionary member = Dict(membership); Assert.Equal(policy, Assert.IsType<PdfName>(Read(member, "P")).DisplayText);
        PdfDictionary properties = Dict(Read(Assert.IsType<PdfDictionary>(document.GetObject(1)), "OCProperties"));
        PdfArray groups = Assert.IsType<PdfArray>(Resolve(Read(properties, "OCGs")));
        PdfArray members = Assert.IsType<PdfArray>(Resolve(Read(member, "OCGs")));
        Assert.Equal(2, groups.Count); Assert.NotEqual(groups[0], groups[1]);
        Assert.All(members.Items, group => Assert.Contains(group, groups.Items));
        PdfArray off = Assert.IsType<PdfArray>(Resolve(Read(Dict(Read(properties, "D")), "OFF")));
        Assert.Equal(members[0], Assert.Single(off.Items));
    }

    [Theory]
    [InlineData("argv", false, false)]
    [InlineData("argv", false, true)]
    [InlineData("argv", true, false)]
    [InlineData("argv", true, true)]
    [InlineData("job", false, false)]
    [InlineData("job", false, true)]
    [InlineData("job", true, false)]
    [InlineData("job", true, true)]
    public async Task RegistrationNegotiatesVersionForOrdinaryAndSplitOutputUnlessForced(string route, bool split, bool force)
    {
        byte[] source = Fixture(true, "static", false); source[7] = (byte)'4';
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        List<string> argv = ["input"]; Dictionary<string, object> job = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
        if (split) { argv.Add("--split-pages=1"); job["splitPages"] = "1"; }
        if (force) { argv.Add("--force-version=1.4"); job["forceVersion"] = "1.4"; }
        argv.Add("output");
        QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From(split ? "output-1" : "output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal(force ? "1.4" : "1.5", document.HeaderVersion.ToString());
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("OCProperties"), out QpdfObject raw));
        PdfDictionary properties = Assert.IsType<PdfDictionary>(raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw);
        Assert.True(properties.TryGet(PdfName.FromAscii("OCGs"), out _));
    }

    internal static byte[] Fixture(bool indirect, string policy, bool alias)
    {
        byte[] program = "/OC /Layer BDC 1 0 0 rg 20 20 40 40 re f EMC"u8.ToArray();
        string defaults = "<< /BaseState /ON /OFF " + (indirect ? "8 0 R" : "[5 0 R]")
            + (policy == "automatic" ? " /AS [<< /Event /View /Category [/View] /OCGs [5 0 R] >>]" : "") + " >>";
        string properties = "<< /OCGs " + (indirect ? "9 0 R" : "[5 0 R]")
            + " /D " + (indirect ? "7 0 R" : defaults)
            + (policy == "alternate" ? " /Configs [<< /Name (Alternate) /BaseState /OFF >>]" : "") + " >>";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /OCProperties " + (indirect ? "6 0 R" : properties) + (alias ? " /Audit 6 0 R" : "") + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Properties << /Layer 5 0 R >> >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + program.Length + " >>", program),
            (5, "<< /Type /OCG /Name (Layer) /Usage << /View << /ViewState /OFF >> >>" + (policy == "intent" ? " /Intent /Design" : "") + " >>", null),
            (6, properties, null), (7, defaults, null), (8, "[5 0 R]", null), (9, "[5 0 R]", null)], 1);
    }

    private static async Task Execute(InMemoryQpdfHost host, string route, bool packed, bool merge)
    {
        List<string> argv = ["input"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
        if (packed) { argv.Add("--object-streams=generate"); settings["objectStreams"] = "generate"; }
        if (merge)
        {
            argv.AddRange(["--pages", ".", "1", "source", "1", "--"]);
            settings["pages"] = new[] { new { file = ".", range = "1" }, new { file = "source", range = "1" } };
        }
        argv.Add("output");
        QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", false)]
    [InlineData("argv", true)]
    [InlineData("job", false)]
    [InlineData("job", true)]
    public async Task UnchangedCatalogAndPrivateAliasRetainOneIndirectRoot(string route, bool packed)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(true, "static", true), QpdfInputOwnership.Transfer);
        await Execute(host, route, packed, false);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("OCProperties"), out QpdfObject properties));
        Assert.True(catalog.TryGet(PdfName.FromAscii("Audit"), out QpdfObject alias));
        Assert.Equal(Assert.IsType<PdfReference>(alias), Assert.IsType<PdfReference>(properties));
    }

    [Theory]
    [InlineData("argv", false)]
    [InlineData("argv", true)]
    [InlineData("job", false)]
    [InlineData("job", true)]
    public async Task CombinedDefaultsResolveIndirectArraysAndKeepOriginalPrivateHeader(string route, bool indirect)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(indirect, "static", true), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("source"), Fixture(true, "static", false), QpdfInputOwnership.Transfer);
        await Execute(host, route, false, true);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
        QpdfObject Read(PdfDictionary dictionary, string key)
        { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(value); }
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        PdfDictionary properties = Assert.IsType<PdfDictionary>(Read(catalog, "OCProperties"));
        PdfArray groups = Assert.IsType<PdfArray>(Read(properties, "OCGs")); Assert.Equal(2, groups.Count);
        Assert.NotEqual(groups[0], groups[1]);
        PdfArray off = Assert.IsType<PdfArray>(Read(Assert.IsType<PdfDictionary>(Read(properties, "D")), "OFF"));
        Assert.Equal(2, off.Count); Assert.All(groups.Items, item => Assert.Contains(item, off.Items));
        PdfDictionary original = Assert.IsType<PdfDictionary>(Read(catalog, "Audit"));
        Assert.Single(Assert.IsType<PdfArray>(Read(original, "OCGs")).Items);
    }

    [Theory]
    [InlineData("typed", "automatic")]
    [InlineData("argv", "automatic")]
    [InlineData("job", "automatic")]
    [InlineData("typed", "intent")]
    [InlineData("argv", "intent")]
    [InlineData("job", "intent")]
    [InlineData("typed", "alternate")]
    [InlineData("argv", "alternate")]
    [InlineData("job", "alternate")]
    public async Task OneComplexSourceRetainsItsConfiguration(string route, string policy)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(true, policy, true), QpdfInputOwnership.Transfer);
        if (route == "typed") { await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(1), CancellationToken.None); }
        else { await Execute(host, route, false, false); }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference link ? document.GetObject(link.ObjectNumber) : value;
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("OCProperties"), out QpdfObject raw));
        PdfDictionary properties = Assert.IsType<PdfDictionary>(Resolve(raw));
        if (policy == "automatic")
        {
            Assert.True(properties.TryGet(PdfName.FromAscii("D"), out raw));
            PdfDictionary defaults = Assert.IsType<PdfDictionary>(Resolve(raw));
            Assert.True(defaults.TryGet(PdfName.FromAscii("AS"), out raw)); Assert.Single(Assert.IsType<PdfArray>(Resolve(raw)).Items);
        }
        else if (policy == "alternate")
        { Assert.True(properties.TryGet(PdfName.FromAscii("Configs"), out raw)); Assert.Single(Assert.IsType<PdfArray>(Resolve(raw)).Items); }
        else
        {
            Assert.True(properties.TryGet(PdfName.FromAscii("OCGs"), out raw));
            PdfDictionary group = Assert.IsType<PdfDictionary>(Resolve(Assert.Single(Assert.IsType<PdfArray>(Resolve(raw)).Items)));
            Assert.True(group.TryGet(PdfName.FromAscii("Intent"), out raw)); Assert.Equal("Design", Assert.IsType<PdfName>(Resolve(raw)).DisplayText);
        }
    }

    [Theory]
    [InlineData("argv", "automatic")]
    [InlineData("job", "automatic")]
    [InlineData("argv", "intent")]
    [InlineData("job", "intent")]
    [InlineData("argv", "alternate")]
    [InlineData("job", "alternate")]
    [InlineData("typed", "automatic")]
    [InlineData("typed", "intent")]
    [InlineData("typed", "alternate")]
    public async Task UnsupportedCombinedStatesRefuseBeforePublication(string route, string policy)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(true, "static", false), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("source"), Fixture(true, policy, false), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        string[] argv = ["input", "--pages", ".", "1", "source", "1", "--", "output"];
        string job = "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"pages\":[{\"file\":\".\",\"range\":\"1\"},{\"file\":\"source\",\"range\":\"1\"}]}";
        if (route == "typed")
        {
            await Assert.ThrowsAsync<QpdfUnsupportedFeatureException>(() => Qpdf.SelectPagesAsync(host,
                [new(QpdfPath.From("input"), new(1)), new(QpdfPath.From("source"), new(1))], QpdfPath.From("output"), new PdfPageMergeOptions(), CancellationToken.None).AsTask());
        }
        else
        {
            QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> OffStateCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (bool retained in new[] { false, true })
        foreach (bool malformed in new[] { false, true }) { yield return [route, retained, malformed]; }
    }

    [Theory]
    [MemberData(nameof(OffStateCases))]
    public async Task RequiredOffStateInterpretationKeepsMalformedDataDistinctFromHostFailures(string route, bool retained, bool malformed)
    {
        byte[] States(bool indirect, bool bad) => CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /OCProperties " + (indirect ? "6 0 R" : "<< /OCGs [5 0 R] /D << /BaseState /ON /OFF " + (bad ? "42" : "[5 0 R]") + " >> >>") + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Properties << /L 5 0 R >> " + (indirect ? "/Private 6 0 R" : "") + " >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + "/OC /L BDC 0 0 20 20 re f EMC"u8.Length + " >>", "/OC /L BDC 0 0 20 20 re f EMC"u8.ToArray()),
            (5, "<< /Type /OCG /Name (layer) >>", null),
            (6, "<< /OCGs [5 0 R] /D << /BaseState /ON /OFF " + (bad ? "42" : "[5 0 R]") + " >> >>", null)], 1);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), States(retained, retained && malformed), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("source"), States(false, !retained && malformed), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        if (route == "typed")
        {
            Task<PdfMultiPageSelectionResult> Run() => Qpdf.SelectPagesAsync(host,
                [new(QpdfPath.From("input"), new(1)), new(QpdfPath.From("source"), new(1))], QpdfPath.From("output"), new PdfPageMergeOptions(), CancellationToken.None).AsTask();
            if (malformed) { await Assert.ThrowsAsync<QpdfParseException>(Run); } else { _ = await Run(); }
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1", "source", "1", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"pages\":[{\"file\":\".\",\"range\":\"1\"},{\"file\":\"source\",\"range\":\"1\"}]}", CancellationToken.None);
            Assert.Equal(malformed ? QpdfExitCode.Error : QpdfExitCode.Success, result.ExitCode);
            if (malformed) { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments); }
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        if (malformed) { Assert.Equal("prior"u8.ToArray(), output); }
        else { Assert.NotEqual("prior"u8.ToArray(), output); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

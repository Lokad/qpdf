using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class FormNullProjectionTests
{
    private static readonly string[] Keys = ["T", "FT", "TU", "TM", "Ff", "V", "DV", "DA", "Q", "Opt", "MaxLen"];
    private static readonly string[] Routes = ["typed", "argv", "job"];

    private static byte[] Fixture(string key, string shape, string formShape)
    {
        string value = key is "Ff" or "Q" or "MaxLen" ? "7" : key == "FT" ? "/Tx" : key == "Opt" ? "[(child)]" : key == "DA" ? "(/F1 10 Tf)" : "(child)";
        string entry = shape == "absent" ? "" : "/" + key + " " + (shape == "null" ? "null" : shape == "nullref" ? "7 0 R" : shape == "value" ? value : "8 0 R");
        string parent = key == "Parent" ? "/T (child) /FT /Tx /V (child) " + entry : "/Parent 5 0 R " + entry;
        string form = formShape switch
        {
            "absent" => "", "null" => "/AcroForm null", "reference-null" => "/AcroForm 7 0 R",
            "stale" => "/AcroForm 7 1 R", "free" => "/AcroForm 7 0 R", "integer" => "/AcroForm 42",
            "reference-scalar" => "/AcroForm 8 0 R", "missing" => "/AcroForm 99 0 R",
            _ => "/AcroForm << /Fields [" + (key == "Parent" ? "4" : "5") + " 0 R] >>",
        };
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R " + form + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Widget /Rect [10 20 80 40] " + parent + " >>", null),
            (5, "<< /T (parent) /FT " + (key == "Opt" ? "/Ch" : "/Tx") + " /V (inherited) /DV (default) /Ff 2 /Q 1 /TU (alternative) /TM (mapping) /Opt [(inherited option)] /DA (/F1 10 Tf) /MaxLen 12 /Kids [4 0 R] >>", null),
            (6, "null", null), (7, "null", null), (8, value, null)], 1);
        if (formShape == "free")
        {
            int xref = source.AsSpan().LastIndexOf("xref\n0 9\n"u8); Assert.True(xref >= 0);
            int row = xref + "xref\n0 9\n"u8.Length + 7 * 20; Assert.Equal((byte)'n', source[row + 17]);
            "0000000000 65535 f \n"u8.CopyTo(source.AsSpan(row, 20));
        }
        return source;
    }

    private static async Task<JsonElement> Read(byte[] source, int version, string route)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        string json;
        if (route == "typed")
        { json = (await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(version, ["acroform"]), CancellationToken.None)).Json; }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--json=" + version, "--json-key=acroform"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", json = version.ToString(System.Globalization.CultureInfo.InvariantCulture), jsonKey = new[] { "acroform" } }), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); json = Assert.IsType<string>(result.OutputText);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        using JsonDocument report = JsonDocument.Parse(json); return report.RootElement.GetProperty("acroform").Clone();
    }

    public static IEnumerable<object[]> Cases()
    {
        for (int index = 0; index < Keys.Length; index++)
        foreach (string shape in new[] { "null", "nullref", "value", "valueref" })
        foreach (int version in new[] { 1, 2 }) { yield return [Keys[index], shape, version, Routes[index % Routes.Length]]; }
        foreach (string key in new[] { "T", "V", "Opt" })
        foreach (string shape in new[] { "null", "nullref", "value", "valueref" })
        foreach (int version in new[] { 1, 2 })
        foreach (string route in Routes)
        {
            if (route != Routes[Array.IndexOf(Keys, key) % Routes.Length]) { yield return [key, shape, version, route]; }
        }
        foreach (string shape in new[] { "null", "nullref" })
        foreach (int version in new[] { 1, 2 })
        foreach (string route in Routes) { yield return ["Parent", shape, version, route]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OptionalFieldEntriesUseResolvedValuesWithoutChangingTheirTerminalIdentity(string key, string shape, int version, string route)
    {
        JsonElement expected = await Read(Fixture(key, shape is "null" or "nullref" ? "absent" : "value", "normal"), version, route);
        JsonElement actual = await Read(Fixture(key, shape, "normal"), version, route);
        Assert.True(JsonElement.DeepEquals(expected, actual), key + ": " + actual.GetRawText());
    }

    public static IEnumerable<object[]> RootCases()
    {
        foreach (string shape in new[] { "absent", "null", "reference-null", "stale", "free" })
        foreach (string route in Routes)
        foreach (string operation in new[] { "inspect", "whole", "select", "packed", "linear" })
        {
            if (route != "typed" || operation is "inspect" or "select") { yield return [shape, route, operation]; }
        }
    }

    private static async Task<QpdfExitCode> Write(InMemoryQpdfHost host, string route, string operation)
    {
        if (route == "typed")
        {
            await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("input"), new(1))], QpdfPath.From("output"), CancellationToken.None);
            return QpdfExitCode.Success;
        }
        List<string> argv = ["input"]; Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
        if (operation == "select") { argv.AddRange(["--pages", ".", "1", "--"]); settings["pages"] = new[] { new { file = ".", range = "1" } }; }
        if (operation == "packed") { argv.Add("--object-streams=generate"); settings["objectStreams"] = "generate"; }
        if (operation == "linear") { argv.Add("--linearize"); settings["linearize"] = true; }
        argv.Add("output");
        QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None);
        return result.ExitCode;
    }

    [Theory]
    [MemberData(nameof(RootCases))]
    public async Task NullCatalogFormRemainsAbsentWhileUnregisteredWidgetsSurvive(string shape, string route, string operation)
    {
        byte[] source = Fixture("T", "absent", shape);
        if (operation != "inspect")
        {
            using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
            Assert.Equal(QpdfExitCode.Success, await Write(host, route, operation));
            Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out source));
        }
        JsonElement form = await Read(source, 2, route);
        Assert.False(form.GetProperty("hasacroform").GetBoolean()); Assert.Single(form.GetProperty("fields").EnumerateArray());
    }

    [Theory]
    [InlineData("integer", "typed")]
    [InlineData("integer", "argv")]
    [InlineData("integer", "job")]
    [InlineData("reference-scalar", "typed")]
    [InlineData("reference-scalar", "argv")]
    [InlineData("reference-scalar", "job")]
    [InlineData("missing", "typed")]
    [InlineData("missing", "argv")]
    [InlineData("missing", "job")]
    public async Task MalformedCatalogFormStillRejectsAndPreservesTheTarget(string shape, string route)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("T", "absent", shape), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        if (route == "typed") { await Assert.ThrowsAsync<QpdfParseException>(async () => await Write(host, route, "whole")); }
        else { Assert.Equal(QpdfExitCode.Error, await Write(host, route, "whole")); }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private sealed class PublicationHost(InMemoryQpdfHost storage, IMemoryBudget caller, bool cancel, CancellationTokenSource cancellation)
        : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => caller;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return ValueTask.FromException<IQpdfOutputTransaction>(new QpdfHostException(path, QpdfHostFailureReason.WriteFailed));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ResolvedNullsRefundPrivateStorageWhenPublicationFails(bool selection, bool cancel)
    {
        RecordingMemoryBudget root = new(16777216L);
        using (IMemoryScope durable = root.CreateScope())
        using (IMemoryScope caller = root.CreateScope())
        using (InMemoryQpdfHost storage = new(QpdfLimits.Default, null, durable))
        {
            storage.AddInput(QpdfPath.From("input"), selection ? Fixture("T", "absent", "reference-null")
                : Fixture("V", "nullref", "normal"), QpdfInputOwnership.Transfer);
            await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
            { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
            long before = root.ReservedBytes;
            using CancellationTokenSource cancellation = new();
            PublicationHost host = new(storage, caller, cancel, cancellation);
            async Task Run()
            {
                if (selection) { await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("input"), new(1))], QpdfPath.From("output"), cancellation.Token); }
                else { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(2, ["acroform"]), cancellation.Token); }
            }
            if (cancel) { await Assert.ThrowsAnyAsync<OperationCanceledException>(Run); }
            else { await Assert.ThrowsAsync<QpdfHostException>(Run); }
            Assert.Equal(before, root.ReservedBytes); Assert.Equal(0L, storage.OpenLeaseCount);
            using IMemoryScope export = root.CreateScope();
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), export, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task ResolvedInheritedProjectionSurvivesItsHostUnderTheCallerOwner()
    {
        RecordingMemoryBudget root = new(16777216L);
        using (IMemoryScope caller = root.CreateScope())
        {
            QpdfJsonResult result;
            using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, caller))
            {
                host.AddInput(QpdfPath.From("input"), Fixture("V", "nullref", "normal"), QpdfInputOwnership.Transfer);
                result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(2, ["acroform"]), CancellationToken.None);
                Assert.Equal(0L, host.OpenLeaseCount);
            }
            Assert.True(root.ReservedBytes >= 2L * result.Json.Length);
            using JsonDocument json = JsonDocument.Parse(result.Json);
            JsonElement field = json.RootElement.GetProperty("acroform").GetProperty("fields")[0];
            Assert.Equal("5 0 R", field.GetProperty("object").GetString()); Assert.Equal("u:inherited", field.GetProperty("value").GetString());
            Assert.Equal(result.Json, (result with { }).Json);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}

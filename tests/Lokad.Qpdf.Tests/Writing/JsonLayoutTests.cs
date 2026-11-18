using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonLayoutTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Json = QpdfPath.From("input.json"), Output = QpdfPath.From("output.pdf");
    private static readonly byte[] Payload = "q\nQ\n"u8.ToArray();

    private static byte[] Source() => PdfTestDocument.Assemble([
        (1, "(private-value)", null), (2, "<< /Title (kept-info) >>", null),
        (3, "<< /Type /Page /Parent 7 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>", null),
        (4, "<< /Length 4 >>", "q Q\n"u8.ToArray()),
        (5, "<< /Type /Catalog /Pages 7 0 R /Probe 1 0 R /Scalar 6 0 R /Marker (old) >>", null),
        (6, "17", null), (7, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null)
    ], 5, null, "1.3", "/Info 2 0 R");

    private static byte[] Model(bool update, bool directRoot)
    {
        Dictionary<string, object> catalog = new() { ["/Type"] = "/Catalog", ["/Pages"] = "7 0 R", ["/Probe"] = "1 0 R", ["/Scalar"] = "6 0 R", ["/Marker"] = "u:patched" };
        Dictionary<string, object> objects = new()
        {
            ["obj:5 0 R"] = new { value = catalog },
            ["obj:4 0 R"] = new { stream = new { dict = new Dictionary<string, object>(), data = Convert.ToBase64String(Payload) } },
        };
        if (!update)
        {
            objects["obj:1 0 R"] = new { value = "u:private-value" };
            objects["obj:2 0 R"] = new { value = new Dictionary<string, object> { ["/Title"] = "u:kept-info" } };
            objects["obj:3 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Page", ["/Parent"] = "7 0 R", ["/MediaBox"] = new[] { 0, 0, 100, 100 }, ["/Resources"] = new Dictionary<string, object>(), ["/Contents"] = "4 0 R" } };
            objects["obj:6 0 R"] = new { value = 17 };
            objects["obj:7 0 R"] = new { value = new Dictionary<string, object> { ["/Type"] = "/Pages", ["/Count"] = 1, ["/Kids"] = new[] { "3 0 R" } } };
        }
        if (!update || directRoot)
        { objects["trailer"] = new { value = new Dictionary<string, object> { ["/Root"] = directRoot ? catalog : "5 0 R", ["/Info"] = "2 0 R", ["/Size"] = 8 } }; }
        return JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2, pdfversion = "1.3" }, objects } });
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference) : value;
    private static PdfDictionary Catalog(QpdfDocument document)
        => Assert.IsType<PdfDictionary>(Resolve(document, Get(document.Engine.Trailer, "Root")));

    public static IEnumerable<object[]> LayoutCases()
    {
        foreach (bool update in new[] { false, true })
        foreach (bool directRoot in new[] { false, true })
        foreach (string mode in new[] { "disable", "preserve", "generate" })
        foreach (bool linearize in new[] { false, true }) { yield return [update, directRoot, mode, linearize]; }
    }

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public async Task ImportedValuesAndNoncanonicalRootsSurviveIndependentLayouts(bool update, bool directRoot, string mode, bool linearize)
    {
        byte[] source = Source(), before = (byte[])source.Clone(), json = Model(update, directRoot), jsonBefore = (byte[])json.Clone();
        using InMemoryQpdfHost host = new(); host.AddInput(Json, json, QpdfInputOwnership.Transfer);
        if (update) { host.AddInput(Input, source, QpdfInputOwnership.Transfer); }
        QpdfJsonImportOptions options = new(mode, linearize);
        QpdfJsonImportResult result = update
            ? await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, options, null, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None)
            : await Qpdf.CreatePdfFromJsonAsync(host, Json, Output, options, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, result.PageCount); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(Output, out byte[] output)); host.AddInput(Output, output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, CancellationToken.None))
        {
            PdfDictionary catalog = Catalog(document);
            Assert.Equal("patched"u8.ToArray(), Assert.IsType<PdfByteString>(Get(catalog, "Marker")).Value);
            PdfReference probe = Assert.IsType<PdfReference>(Get(catalog, "Probe"));
            Assert.Equal("private-value"u8.ToArray(), Assert.IsType<PdfByteString>(document.GetObject(probe)).Value);
            Assert.Equal(17L, Assert.IsType<PdfInteger>(Resolve(document, Get(catalog, "Scalar"))).Value);
            PdfDictionary info = Assert.IsType<PdfDictionary>(Resolve(document, Get(document.Engine.Trailer, "Info")));
            Assert.Equal("kept-info"u8.ToArray(), Assert.IsType<PdfByteString>(Get(info, "Title")).Value);
            PdfDictionary pages = Assert.IsType<PdfDictionary>(Resolve(document, Get(catalog, "Pages")));
            PdfDictionary page = Assert.IsType<PdfDictionary>(Resolve(document, Assert.IsType<PdfArray>(Get(pages, "Kids"))[0]));
            Assert.Equal(Payload, Assert.IsType<PdfStream>(Resolve(document, Get(page, "Contents"))).Data);
            bool compressed = document.Engine.XRefEntries.Values.Any(entry => entry.Kind == XrefEntryKind.Compressed);
            Assert.Equal(mode == "generate", compressed);
            if (mode == "generate" && !linearize)
            { Assert.Equal(XrefEntryKind.Compressed, document.Engine.XRefEntries[probe.ObjectNumber].Kind); }
            Assert.Equal(mode == "generate" ? "1.5" : "1.3", document.Version.ToString());
        }
        await Check(host, output, linearize);
        Assert.Equal(before, source); Assert.Equal(jsonBefore, json); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static async Task Check(InMemoryQpdfHost host, byte[] output, bool linearize)
    {
        QpdfCheckResult checkedOutput = await Qpdf.CheckAsync(host, Output, CancellationToken.None);
        Assert.True(checkedOutput.Passed); Assert.Equal(1, checkedOutput.PageCount);
        if (linearize)
        {
            Assert.Contains("/Linearized", Encoding.Latin1.GetString(output.AsSpan(0, Math.Min(1024, output.Length))), StringComparison.Ordinal);
            Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(checkedOutput.Checks, item => item.Name == "linearization").Status);
        }
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(1, independent.NumberOfPages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandCapturesFirstPassFromTheBoundGraph(bool update)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Json, Model(update, false), QpdfInputOwnership.Transfer);
        if (update) { host.AddInput(Input, Source(), QpdfInputOwnership.Transfer); }
        QpdfExecutionResult result = update
            ? await Qpdf.ExecuteJobAsync(host, """
                {"inputFile":"input.pdf","updateFromJson":"input.json","outputFile":"output.pdf",
                 "linearize":"","linearizePass1":"pass1.bin","compressStreams":"n"}
                """, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new([
                Json.Value, "--json-input", "--linearize", "--linearize-pass1=pass1.bin",
                "--compress-streams=n", Output.Value]), CancellationToken.None);

        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(Output, out byte[] pdf));
        Assert.True(host.TryGetOutput(QpdfPath.From("pass1.bin"), out byte[] pass1));
        LinearizedWriterTests.AssertFirstPassMatchesLayout(pdf, pass1);
        Assert.Contains("(patched)", Encoding.ASCII.GetString(pass1), StringComparison.Ordinal);
        Assert.True(pass1.AsSpan().IndexOf(Payload) >= 0);
        host.AddInput(Output, pdf, QpdfInputOwnership.Transfer);
        await Check(host, pdf, true);
    }

    [Fact]
    public async Task InactiveCaptureDoesNotProbeOrReplaceTheJsonInput()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        host.Inner.AddInput(Json, Model(false, false), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(Json, CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }

        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([
            Json.Value, "--json-input", "--linearize-pass1=input.json", Output.Value]), CancellationToken.None);

        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal([Json, Output], host.OpenedPaths);
        Assert.Equal([Output], host.CreatedPaths);
        Assert.True(host.Inner.TryGetOutput(Json, out byte[] intact));
        Assert.Equal("prior"u8.ToArray(), intact);
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveCaptureProtectsTheJsonDeclarationBeforeAcquisition(bool replace)
    {
        using RecordingJobHost host = new(null, null, "success", true);
        string[] argv = replace
            ? [Input.Value, "--update-from-json=input.json", "--replace-input"]
            : [Json.Value, "--json-input", Output.Value];
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([
            .. argv, "--linearize", "--linearize-pass1=input.json"]), CancellationToken.None);

        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, diagnostic.Code);
        Assert.Contains("first-pass", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(host.OpenedPaths);
        Assert.Empty(host.CreatedPaths);
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    private static void PackedInputs(InMemoryQpdfHost host)
    {
        host.AddInput(Input, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "objstm-1page.pdf")), QpdfInputOwnership.Transfer);
        host.AddInput(Json, """{"qpdf":[{"jsonversion":2},{"obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/Probe":"6 0 R","/Marker":"u:patched"}}}]}"""u8.ToArray(), QpdfInputOwnership.Transfer);
    }

    [Theory]
    [InlineData("disable", false)]
    [InlineData("disable", true)]
    [InlineData("preserve", false)]
    [InlineData("preserve", true)]
    [InlineData("generate", false)]
    [InlineData("generate", true)]
    public async Task UpdatesPreserveActualSourceMembershipWhenRequested(string mode, bool linearize)
    {
        using InMemoryQpdfHost host = new(); PackedInputs(host);
        await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, new QpdfJsonImportOptions(mode, linearize), null,
            QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None);
        await VerifyPacked(host, mode != "disable", linearize);
    }

    private static async Task VerifyPacked(InMemoryQpdfHost host, bool packed, bool linearize)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] output)); host.AddInput(Output, output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, CancellationToken.None))
        {
            PdfDictionary catalog = Catalog(document);
            Assert.Equal("patched"u8.ToArray(), Assert.IsType<PdfByteString>(Get(catalog, "Marker")).Value);
            PdfReference probe = Assert.IsType<PdfReference>(Get(catalog, "Probe"));
            PdfDictionary font = Assert.IsType<PdfDictionary>(document.GetObject(probe));
            Assert.Equal(PdfName.FromAscii("Helvetica"), Get(font, "BaseFont"));
            Assert.Equal(packed, document.Engine.XRefEntries.Values.Any(entry => entry.Kind == XrefEntryKind.Compressed));
            if (packed && !linearize) { Assert.Equal(XrefEntryKind.Compressed, document.Engine.XRefEntries[probe.ObjectNumber].Kind); }
        }
        await Check(host, output, linearize); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandUpdatesKeepNativeDefaultPacking(bool job)
    {
        using InMemoryQpdfHost host = new(); PackedInputs(host);
        QpdfExecutionResult command = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input.pdf","outputFile":"output.pdf","updateFromJson":"input.json"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([Input.Value, "--update-from-json=" + Json.Value, Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, command.ExitCode); await VerifyPacked(host, true, false);
    }

    [Fact]
    public async Task ExistingTypedOverloadsKeepFlatOutput()
    {
        using InMemoryQpdfHost host = new(); PackedInputs(host);
        await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, CancellationToken.None);
        await VerifyPacked(host, false, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JobAdaptersProjectBothLayoutSettings(bool update)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Input, Source(), QpdfInputOwnership.Transfer);
        host.AddInput(Json, Model(update, false), QpdfInputOwnership.Transfer);
        Dictionary<string, object> job = new() { ["inputFile"] = update ? Input.Value : Json.Value,
            ["outputFile"] = Output.Value, ["objectStreams"] = "generate", ["linearize"] = true };
        if (update) { job["updateFromJson"] = Json.Value; } else { job["jsonInput"] = true; }
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(Output, out byte[] output)); host.AddInput(Output, output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, CancellationToken.None))
        {
            Assert.Equal("patched"u8.ToArray(), Assert.IsType<PdfByteString>(Get(Catalog(document), "Marker")).Value);
            Assert.Contains(document.Engine.XRefEntries.Values, entry => entry.Kind == XrefEntryKind.Compressed);
        }
        await Check(host, output, true);
    }

    [Theory]
    [InlineData(false, "--force-version=1.3", "1.3", false)]
    [InlineData(false, "--force-version=1.4", "1.4", false)]
    [InlineData(false, "--min-version=1.3", "1.5", true)]
    [InlineData(true, "--force-version=1.4", "1.4", false)]
    [InlineData(true, "--min-version=1.3", "1.5", true)]
    public async Task HeaderSettersKeepPackingFloorsAndForcedSuppression(bool update, string versionFlag, string version, bool packed)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Json, Model(update, false), QpdfInputOwnership.Transfer);
        host.AddInput(Input, Source(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(update
            ? [Input.Value, "--update-from-json=" + Json.Value, versionFlag, "--object-streams=generate", Output.Value]
            : [Json.Value, "--json-input", versionFlag, "--object-streams=generate", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.True(host.TryGetOutput(Output, out byte[] output));
        host.AddInput(Output, output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, CancellationToken.None);
        Assert.Equal(version, document.Version.ToString());
        Assert.Equal(packed, document.Engine.XRefEntries.Values.Any(entry => entry.Kind == XrefEntryKind.Compressed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationLayoutsApplyFreshOrIndependentTemplateProtection(bool template)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Json, Model(false, false), QpdfInputOwnership.Transfer);
        QpdfPath templatePath = QpdfPath.From("template");
        host.AddInput(templatePath, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r4-ascii.pdf")), QpdfInputOwnership.Transfer);
        QpdfPassword reader = QpdfPassword.FromText("reader");
        QpdfOutputEncryption encryption = template ? QpdfOutputEncryption.CopyFrom(templatePath, QpdfPassword.FromText("fixture-owner"))
            : QpdfOutputEncryption.CreateAes256(reader, QpdfPassword.FromText("owner"), -4, false);
        QpdfJsonImportResult result = await Qpdf.CreatePdfFromJsonAsync(host, Json, Output, new("generate", true), encryption, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(1, result.PageCount); Assert.Equal(0L, result.Counters.OpenHandles);
        await ProtectedLayout(host, reader, template ? 4 : 6, "patched");
    }

    [Fact]
    public async Task UpdatingAnAuthenticatedSourceKeepsProtectionAcrossCombinedLayouts()
    {
        byte[] source = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r4-ascii.pdf")), before = (byte[])source.Clone();
        using InMemoryQpdfHost host = new(); host.AddInput(Input, source, QpdfInputOwnership.Transfer);
        QpdfPassword reader = QpdfPassword.FromText("reader");
        QpdfJsonResult exported = await Qpdf.GetDocumentJsonAsync(host, Input, new QpdfJsonOptions(2, ["qpdf"], "inline", null, [], false) { Password = reader }, CancellationToken.None);
        System.Text.Json.Nodes.JsonNode model = System.Text.Json.Nodes.JsonNode.Parse(exported.Json) ?? throw new InvalidOperationException();
        System.Text.Json.Nodes.JsonNode table = model["qpdf"]?[1] ?? throw new InvalidOperationException();
        string root = table["trailer"]?["value"]?["/Root"]?.GetValue<string>() ?? throw new InvalidOperationException();
        System.Text.Json.Nodes.JsonNode catalog = table["obj:" + root]?["value"] ?? throw new InvalidOperationException();
        catalog["/Marker"] = "u:secured";
        host.AddInput(Json, Encoding.UTF8.GetBytes(model.ToJsonString()), QpdfInputOwnership.Transfer);
        await Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, new("generate", true), reader, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None);
        await ProtectedLayout(host, reader, 4, "secured"); Assert.Equal(before, source);
    }

    private static async Task ProtectedLayout(InMemoryQpdfHost host, QpdfPassword reader, int revision, string marker)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] output)); host.AddInput(Output, output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Output, reader, QpdfLimits.Default, CancellationToken.None))
        {
            Assert.Equal(revision, document.Encryption?.Revision); Assert.Equal(1, document.PageCount);
            Assert.Equal(Encoding.ASCII.GetBytes(marker), Assert.IsType<PdfByteString>(Get(Catalog(document), "Marker")).Value);
            Assert.Contains(document.Engine.XRefEntries.Values, entry => entry.Kind == XrefEntryKind.Compressed);
        }
        QpdfCheckResult check = await Qpdf.CheckAsync(host, Output, new QpdfCheckOptions { Password = reader }, QpdfLimits.Default, CancellationToken.None);
        Assert.True(check.Passed); Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, item => item.Name == "linearization").Status);
        Assert.Contains("/Linearized", Encoding.Latin1.GetString(output.AsSpan(0, Math.Min(1024, output.Length))), StringComparison.Ordinal);
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output, new UglyToad.PdfPig.ParsingOptions { Password = "reader" });
        Assert.Equal(1, independent.NumberOfPages); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LayoutPreparationRetiresOnEveryAdmissionAndPublicationFailure(bool update)
        => PreparationAdmissionChecks.VerifyAsync(async () =>
        {
            InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(Source(), Output);
            host.AddInput(Json, Model(update, false), QpdfInputOwnership.Transfer); return host;
        }, (host, token) => Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(update
            ? ["input", "--update-from-json=" + Json.Value, "--object-streams=generate", "--linearize", Output.Value]
            : [Json.Value, "--json-input", "--object-streams=generate", "--linearize", "--encrypt", "reader", "owner", "256", "--", Output.Value]), token),
            Output, 256, ["create", "write", "commit", "cancel", "close"]);

    private sealed class ObservedHost(InMemoryQpdfHost storage) : IQpdfHost
    {
        internal int Created;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created++; return storage.CreateOutputAsync(path, token); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CombinedLayoutsRequireValidPrimaryAndTemplateCredentialsBeforeOutput(bool update)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r4-ascii.pdf")), before = (byte[])source.Clone();
        using InMemoryQpdfHost storage = await PreparationAdmissionChecks.StorageAsync(source, Output);
        storage.AddInput(Input, source, QpdfInputOwnership.Copy); storage.AddInput(Json, Model(update, false), QpdfInputOwnership.Transfer);
        ObservedHost host = new(storage); QpdfPassword wrong = QpdfPassword.FromText("wrong");
        QpdfSecurityException error = await Assert.ThrowsAsync<QpdfSecurityException>(() => update
            ? Qpdf.UpdatePdfFromJsonAsync(host, Input, Json, Output, new("generate", true), wrong, QpdfOutputEncryption.Preserve, QpdfLimits.Default, CancellationToken.None).AsTask()
            : Qpdf.CreatePdfFromJsonAsync(host, Json, Output, new("generate", true), QpdfOutputEncryption.CopyFrom(Input, wrong), QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Equal(QpdfSecurityFailure.InvalidPassword, error.Reason); Assert.Equal(0, host.Created);
        Assert.True(storage.TryGetOutput(Output, out byte[] prior)); Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        Assert.Equal(before, source); Assert.Equal(0L, storage.OpenLeaseCount);
    }
}

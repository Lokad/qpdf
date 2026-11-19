using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonContentTransformTests
{
    private static readonly byte[] First = "q % FIRST"u8.ToArray(), Second = "Q % SECOND"u8.ToArray();
    private static readonly byte[] Inline = [.. "q BI /W 1 /H 1 /BPC 8 /CS /Pixel ID "u8, 255, 0, 0, .. " EI Q"u8];
    private static readonly byte[] Samples = [255, 0, 0, 32];
    private static readonly QpdfPath Input = QpdfPath.From("input"), Output = QpdfPath.From("output");

    internal static byte[] Fixture(string shape, int generation, bool indirect, bool inherited, string content)
    {
        string Ref(int number) => number + " " + generation + " R";
        bool image = content is "inline" or "forms", forms = content == "forms";
        string resources = "<< /ColorSpace << /Pixel /DeviceRGB >> /XObject " + Ref(6) + " >>";
        string page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Contents "
            + (indirect ? Ref(7) : "[" + Ref(4) + (image ? "" : " " + Ref(5)) + "]")
            + (inherited ? "" : " /Resources " + Ref(8)) + " >>";
        string kids = shape switch { "direct" => page, "repeated" => Ref(3) + " " + Ref(3),
            "shared" => Ref(3) + " " + Ref(9), _ => Ref(3) };
        byte[] first = forms ? "q /F Do Q"u8.ToArray() : content == "both" ? Inline[..^2] : image ? Inline : First;
        List<(int, string, byte[]?)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /AuditContents " + Ref(7) + " /AuditResources " + Ref(8)
                + (forms ? " /AuditForm " + Ref(11) : "") + " /Dead ["
                + string.Join(" ", Enumerable.Range(forms ? 12 : 10, 20).Select(number => number + " 0 R")) + "] >>", null),
            (2, "<< /Type /Pages /Count " + (shape is "repeated" or "shared" ? "2" : "1") + " /Kids [" + kids + "]"
                + (inherited ? " /Resources " + Ref(8) : "") + " >>", null),
            (3, page, null), (4, "<< /Length " + first.Length + " >>", first),
            (5, "<< /Length " + Second.Length + " >>", Second),
            (6, forms ? "<< /F " + Ref(10) + " /Stale 10 " + (generation + 1) + " R >>" : "<< >>", null),
            (7, "[" + Ref(4) + (image ? "" : " " + Ref(5)) + "]", null), (8, resources, null), (9, page, null)];
        if (forms)
        {
            byte[] nested = "q /Nested Do Q"u8.ToArray();
            objects.Add((10, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 200] /Resources << /XObject << /Nested "
                + Ref(11) + " /Stale 11 " + (generation + 1) + " R >> >> /Length " + nested.Length + " >>", nested));
            objects.Add((11, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 200] /Resources " + Ref(8)
                + " /Length " + Inline.Length + " >>", Inline));
        }
        Dictionary<int, int> generations = objects.Where(item => item.Item1 > 2).ToDictionary(item => item.Item1, _ => generation);
        return PdfTestDocument.Assemble(objects, 1, generations, "1.7", "");
    }

    public static IEnumerable<object[]> CoalescingCases()
    {
        foreach (string shape in new[] { "ordinary", "direct", "repeated", "shared" })
        foreach (int generation in new[] { 0, 17 })
        foreach (bool indirect in new[] { false, true }) { yield return [shape, generation, indirect]; }
    }

    [Fact]
    public async Task RotationAfterPageSelectionPreservesTheCoalescedContentsAndPrivateArray()
    {
        using InMemoryQpdfHost host = Host(Fixture("ordinary", 17, true, true, "coalesce"));
        using Graph graph = await Inspect(host, ["--pages", ".", "1,1", "--", "--rotate=+90:2", "--coalesce-contents"]);
        List<JsonElement> pages = graph.Pages();
        Assert.Equal(2, pages.Count);
        Assert.False(pages[0].TryGetProperty("/Rotate", out _));
        Assert.Equal(90, pages[1].GetProperty("/Rotate").GetInt32());
        Assert.Equal(pages[0].GetProperty("/Contents").GetString(), pages[1].GetProperty("/Contents").GetString());
        string active = Assert.IsType<string>(pages[1].GetProperty("/Contents").GetString());
        Assert.Equal([.. First, (byte)'\n', .. Second], graph.Bytes(active));
        JsonElement original = graph.Resolve(graph.Catalog.GetProperty("/AuditContents"));
        Assert.Equal(new[] { "4 17 R", "5 17 R" }, original.EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(First, graph.Bytes("4 17 R"));
        Assert.Equal(Second, graph.Bytes("5 17 R"));
    }

    [Theory]
    [MemberData(nameof(CoalescingCases))]
    public async Task CoalescingChangesVisiblePageEdgesWithoutChangingOriginalArrays(string shape, int generation, bool indirect)
    {
        byte[] source = Fixture(shape, generation, indirect, false, "coalesce"), snapshot = [.. source];
        using InMemoryQpdfHost host = Host(source);
        using Graph graph = await Inspect(host, ["--coalesce-contents"]);
        List<JsonElement> pages = graph.Pages();
        Assert.Equal(shape is "repeated" or "shared" ? 2 : 1, pages.Count);
        string? first = null;
        foreach (JsonElement page in pages)
        {
            string reference = page.GetProperty("/Contents").GetString() ?? throw new InvalidOperationException();
            if (first is not null)
            {
                if (shape == "shared" && !indirect) { Assert.NotEqual(first, reference); }
                else { Assert.Equal(first, reference); }
            }
            first = reference;
            Assert.Equal([.. First, (byte)'\n', .. Second], graph.Bytes(reference));
        }
        JsonElement original = graph.Resolve(graph.Catalog.GetProperty("/AuditContents"));
        Assert.Equal(2, original.GetArrayLength());
        Assert.Equal(First, graph.Bytes(original[0].GetString() ?? throw new InvalidOperationException()));
        Assert.Equal(Second, graph.Bytes(original[1].GetString() ?? throw new InvalidOperationException()));
        Assert.All(graph.Catalog.GetProperty("/Dead").EnumerateArray(), value => Assert.Equal(JsonValueKind.Null, value.ValueKind));
        Assert.Equal(snapshot, source);
        graph.Metadata(false);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, false, true)]
    [InlineData(0, true, false)]
    [InlineData(0, true, true)]
    [InlineData(17, false, false)]
    [InlineData(17, false, true)]
    [InlineData(17, true, false)]
    [InlineData(17, true, true)]
    public async Task ExternalizationBindsNamedColorsAndReusesSharedContents(int generation, bool inherited, bool indirect)
    {
        using InMemoryQpdfHost host = Host(Fixture("shared", generation, indirect, inherited, "inline"));
        using Graph graph = await Inspect(host, ["--externalize-inline-images", "--ii-min-bytes=0"]);
        List<JsonElement> pages = graph.Pages();
        Assert.Equal(2, pages.Count);
        string? first = pages[0].GetProperty("/Contents").GetString(), second = pages[1].GetProperty("/Contents").GetString();
        if (indirect) { Assert.Equal(first, second); }
        else { Assert.NotEqual(first, second); }
        foreach (JsonElement page in pages) { graph.Externalized(page); }
        JsonElement oldResources = graph.Resolve(graph.Catalog.GetProperty("/AuditResources"));
        Assert.Empty(graph.Resolve(oldResources.GetProperty("/XObject")).EnumerateObject());
        JsonElement oldContents = graph.Resolve(graph.Catalog.GetProperty("/AuditContents"));
        Assert.Equal(Inline, graph.Bytes(oldContents[0].GetString() ?? throw new InvalidOperationException()));
        Assert.All(graph.Catalog.GetProperty("/Dead").EnumerateArray(), value => Assert.Equal(JsonValueKind.Null, value.ValueKind));
        graph.Metadata(false);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task NestedFormReplacementsKeepBorrowedParentsAndStaleAliasesSeparate(int generation)
    {
        using InMemoryQpdfHost host = Host(Fixture("ordinary", generation, true, true, "forms"));
        using Graph graph = await Inspect(host, ["--externalize-inline-images", "--ii-min-bytes=0"]);
        JsonElement resources = graph.Resolve(Assert.Single(graph.Pages()).GetProperty("/Resources"));
        JsonElement objects = graph.Resolve(resources.GetProperty("/XObject"));
        Assert.False(objects.TryGetProperty("/Stale", out _));
        JsonElement parent = graph.Header(objects.GetProperty("/F").GetString() ?? throw new InvalidOperationException());
        JsonElement nested = graph.Resolve(graph.Resolve(parent.GetProperty("/Resources")).GetProperty("/XObject"));
        Assert.False(nested.TryGetProperty("/Stale", out _));
        string child = nested.GetProperty("/Nested").GetString() ?? throw new InvalidOperationException();
        graph.Externalized(graph.Header(child));
        Assert.Equal(Inline, graph.Bytes(graph.Catalog.GetProperty("/AuditForm").GetString() ?? throw new InvalidOperationException()));
        Assert.All(graph.Catalog.GetProperty("/Dead").EnumerateArray(), value => Assert.Equal(JsonValueKind.Null, value.ValueKind));
        graph.Metadata(false);
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("threshold")]
    public async Task InactivePoliciesLeaveTheOriginalProgram(string mode)
    {
        using InMemoryQpdfHost host = Host(Fixture("ordinary", 17, true, true, "inline"));
        string[] options = mode == "threshold" ? ["--externalize-inline-images", "--ii-min-bytes=1024"] : [];
        using Graph graph = await Inspect(host, options);
        JsonElement contents = graph.Resolve(Assert.Single(graph.Pages()).GetProperty("/Contents"));
        Assert.Equal(Inline, graph.Bytes(contents[0].GetString() ?? throw new InvalidOperationException()));
        JsonElement resources = graph.Resolve(graph.Catalog.GetProperty("/AuditResources"));
        Assert.Empty(graph.Resolve(resources.GetProperty("/XObject")).EnumerateObject());
        Assert.Equal(mode == "threshold", graph.Report.RootElement.GetProperty("qpdf")[0].GetProperty("calledgetallpages").GetBoolean());
    }

    private static readonly byte[] OriginalProgram = "q\r\n0 0 1 1 re f\r\nQ\r\n"u8.ToArray();
    private static readonly byte[] PatchedProgram = "q\r\n0 0 3 5 re f\r\nQ\r\n"u8.ToArray();

    private static byte[] NormalizationFixture() => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /Audit 4 7 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 7 R >>", null),
        (4, $"<< /Length {OriginalProgram.Length} >>", OriginalProgram)
    ], 1, new Dictionary<int, int> { [4] = 7 }, "1.7", "");

    [Theory]
    [InlineData("n")]
    [InlineData("y")]
    public async Task NormalizationReportsActiveBytesAndKeepsTheOriginalAlias(string setting)
    {
        using InMemoryQpdfHost host = Host(NormalizationFixture());
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-stream-data=inline",
            "--normalize-content=" + setting]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        using Graph report = new(Assert.IsType<string>(result.OutputText), host);
        AssertNormalizedReport(report, OriginalProgram, "q\n0 0 1 1 re f\nQ\n"u8, setting == "y");
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundNormalizationExportsPatchedBytesAndRecreatesTheEditedGraph(bool create)
    {
        using InMemoryQpdfHost host = Host(NormalizationFixture());
        if (create)
        {
            using Graph source = await Inspect(host, []);
            host.AddInput(QpdfPath.From("bound"), Encoding.UTF8.GetBytes(source.Report.RootElement.GetRawText()), QpdfInputOwnership.Transfer);
        }
        host.AddInput(QpdfPath.From("patch"), NormalizationPatch(), QpdfInputOwnership.Transfer);
        Dictionary<string, object> job = new()
        {
            ["inputFile"] = create ? "bound" : "input", ["updateFromJson"] = "patch",
            ["normalizeContent"] = "y", ["json"] = "2", ["jsonStreamData"] = "inline"
        };
        if (create) { job["jsonInput"] = ""; }
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        string json = Assert.IsType<string>(result.OutputText);
        using Graph report = new(json, host);
        AssertNormalizedReport(report, PatchedProgram, "q\n0 0 3 5 re f\nQ\n"u8, true);
        host.AddInput(QpdfPath.From("normalized"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Transfer);
        QpdfExecutionResult publication = await Qpdf.ExecuteAsync(host,
            new(["normalized", "--json-input", "--compress-streams=n", "recreated"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, publication.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("recreated"), out byte[] pdf));
        QpdfPath recreated = QpdfPath.From("read-recreated");
        host.AddInput(recreated, pdf, QpdfInputOwnership.Transfer);
        await using (QpdfDocument read = await QpdfDocument.OpenAsync(host, recreated, CancellationToken.None))
        {
            PdfDictionary page = Assert.Single(read.PageReferences).Page;
            Assert.True(page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject active));
            PdfStream stream = Assert.IsType<PdfStream>(read.GetObject(Assert.IsType<PdfReference>(active)));
            Assert.Equal("q\n0 0 3 5 re f\nQ\n"u8.ToArray(), stream.Data);
            Assert.True(read.Engine.Trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject root));
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(read.GetObject(Assert.IsType<PdfReference>(root)));
            Assert.True(catalog.TryGet(PdfName.FromAscii("Audit"), out QpdfObject alias));
            Assert.NotEqual(active, alias);
            Assert.Equal(PatchedProgram, Assert.IsType<PdfStream>(read.GetObject(Assert.IsType<PdfReference>(alias))).Data);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    private static byte[] NormalizationPatch() => Encoding.UTF8.GetBytes($$$$"""
        {"qpdf":[{"jsonversion":2},{"obj:4 7 R":{"stream":{"dict":{},"data":"{{{{Convert.ToBase64String(PatchedProgram)}}}}"}}}]}
        """);

    private static void AssertNormalizedReport(Graph report, byte[] original, ReadOnlySpan<byte> normalized, bool changed)
    {
        string active = Assert.IsType<string>(Assert.Single(report.Pages()).GetProperty("/Contents").GetString());
        string alias = Assert.IsType<string>(report.Catalog.GetProperty("/Audit").GetString());
        Assert.Equal(!changed, active == alias);
        Assert.Equal(original, report.Bytes(alias));
        Assert.Equal(changed ? normalized.ToArray() : original, report.Bytes(active));
        JsonElement contents = Assert.Single(report.Report.RootElement.GetProperty("pages")[0].GetProperty("contents").EnumerateArray());
        Assert.Equal(active, contents.GetString());
    }

    [Fact]
    public Task NormalizationRefusalAndCancellationPreserveThePriorReport()
        => PreparationAdmissionChecks.VerifyAsync(
            () => PreparationAdmissionChecks.StorageAsync(NormalizationFixture(), Output),
            (host, token) => Qpdf.ExecuteAsync(host, new(["input", "--json-output=2", "--normalize-content=y", "output"]), token),
            Output, 256, ["write", "cancel"]);

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task JobReturnedAndSidecarReportsUseTheSameTransformedGraph(bool job, bool returned, bool sidecars)
    {
        using InMemoryQpdfHost host = Host(Fixture("ordinary", 17, true, true, "inline"));
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["json"] = "2", ["decodeLevel"] = "none",
            ["jsonStreamData"] = sidecars ? "file" : "inline", ["externalizeInlineImages"] = true, ["iiMinBytes"] = "0" };
        List<string> arguments = ["input", "--json=2", "--decode-level=none", "--json-stream-data=" + (sidecars ? "file" : "inline"),
            "--externalize-inline-images", "--ii-min-bytes=0"];
        if (sidecars) { arguments.Add("--json-stream-prefix=side"); settings["jsonStreamPrefix"] = "side"; }
        if (!returned) { arguments.Add("output"); settings["outputFile"] = "output"; }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        using Graph graph = new(returned ? result.OutputText ?? throw new InvalidOperationException() : Report(host), host);
        graph.Externalized(Assert.Single(graph.Pages()));
        JsonElement descriptor = graph.Report.RootElement.GetProperty("pages")[0];
        Assert.Single(descriptor.GetProperty("images").EnumerateArray());
        Assert.Equal(Assert.Single(graph.Pages()).GetProperty("/Contents").GetString(), descriptor.GetProperty("contents")[0].GetString());
    }

    [Fact]
    public async Task ForeignPageAssemblyTransformsAfterSourceAcquisition()
    {
        using InMemoryQpdfHost host = Host(Fixture("ordinary", 0, true, false, "coalesce"));
        host.AddInput(QpdfPath.From("other"), Fixture("ordinary", 17, true, true, "coalesce"), QpdfInputOwnership.Transfer);
        using Graph graph = await Inspect(host, ["--pages", "other", "1", "--", "--coalesce-contents"]);
        Assert.Equal([.. First, (byte)'\n', .. Second], graph.Bytes(Assert.Single(graph.Pages()).GetProperty("/Contents").GetString() ?? throw new InvalidOperationException()));
        graph.Metadata(true);
    }

    [Fact]
    public async Task CoalescingAndExternalizationComposeBeforeProjection()
    {
        using InMemoryQpdfHost host = Host(Fixture("direct", 17, true, true, "both"));
        using Graph graph = await Inspect(host, ["--coalesce-contents", "--externalize-inline-images", "--ii-min-bytes=0"]);
        graph.Externalized(Assert.Single(graph.Pages()));
        Assert.Equal(2, graph.Resolve(graph.Catalog.GetProperty("/AuditContents")).GetArrayLength());
        graph.Metadata(false);
    }

    [Fact]
    public async Task VersionOneDescriptorsAddressTheCoalescedObject()
    {
        using InMemoryQpdfHost host = Host(Fixture("ordinary", 17, true, true, "coalesce"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--json=1", "--coalesce-contents"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        using JsonDocument report = JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException());
        string reference = Assert.Single(report.RootElement.GetProperty("pages")[0].GetProperty("contents").EnumerateArray()).GetString()
            ?? throw new InvalidOperationException();
        Assert.Equal(First.Length + Second.Length + 1, report.RootElement.GetProperty("objects").GetProperty(reference).GetProperty("/Length").GetInt32());
    }

    [Fact]
    public async Task StaleContentsDoNotAliasTheLiveGeneration()
    {
        byte[] source = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents [4 0 R 4 17 R 5 0 R] >>", null),
            (4, "<< /Length " + First.Length + " >>", First), (5, "<< /Length " + Second.Length + " >>", Second)],
            1, new Dictionary<int, int> { [4] = 17 }, "1.7", "");
        using InMemoryQpdfHost host = Host(source);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--json-output=2", "--coalesce-contents", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        using Graph graph = new(Report(host), host);
        JsonElement contents = graph.Resolve(Assert.Single(graph.Pages()).GetProperty("/Contents"));
        Assert.Equal(3, contents.GetArrayLength()); Assert.Equal(JsonValueKind.Null, contents[0].ValueKind);
        Assert.Equal(First, graph.Bytes(contents[1].GetString() ?? throw new InvalidOperationException()));
        Assert.Equal(Second, graph.Bytes(contents[2].GetString() ?? throw new InvalidOperationException()));
    }

    [Fact]
    public async Task GeneratedIdentityDoesNotReviveAnEqualParsedDanglingReference()
    {
        using InMemoryQpdfHost host = Host(Fixture("ordinary", 0, true, false, "coalesce"));
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        Assert.True(catalog.TryGet(PdfName.FromAscii("Dead"), out QpdfObject raw));
        PdfReference parsed = Assert.IsType<PdfReference>(Assert.IsType<PdfArray>(raw)[0]);
        PdfReference generated = new(parsed.ObjectNumber, parsed.Generation);
        Assert.Equal(parsed, generated); Assert.Equal(parsed.GetHashCode(), generated.GetHashCode());
        document.Engine.SetInspectionObjects(new Dictionary<int, QpdfObject> { [generated.ObjectNumber] = new PdfInteger("123", 123) });
        try
        {
            Assert.IsType<PdfNull>(document.GetObject(parsed));
            Assert.Equal(123, Assert.IsType<PdfInteger>(document.GetObject(generated)).Value);
            Assert.False(document.Engine.HasLiveReference(parsed)); Assert.True(document.Engine.HasLiveReference(generated));
        }
        finally { document.Engine.SetInspectionObjects(null); }
        Assert.IsType<PdfNull>(document.GetObject(generated));
    }

    [Theory]
    [InlineData("coalesce")]
    [InlineData("inline")]
    [InlineData("forms")]
    public Task PreparationRefusalsAndPendingWritesPreserveTheActualPriorReport(string content)
        => PreparationAdmissionChecks.VerifyAsync(
            () => PreparationAdmissionChecks.StorageAsync(Fixture("shared", 17, true, true, content), Output),
            (host, token) => Qpdf.ExecuteAsync(host, new(["input", "--json-output=2", "--ii-min-bytes=0",
                content == "coalesce" ? "--coalesce-contents" : "--externalize-inline-images", "output"]), token),
            Output, 256, ["write", "cancel"]);

    private static InMemoryQpdfHost Host(byte[] input)
    { InMemoryQpdfHost host = new(); host.AddInput(Input, input, QpdfInputOwnership.Copy); return host; }

    private static string Report(InMemoryQpdfHost host)
    { Assert.True(host.TryGetOutput(Output, out byte[] bytes)); return Encoding.UTF8.GetString(bytes); }

    private static async Task<Graph> Inspect(InMemoryQpdfHost host, IReadOnlyList<string> options)
    {
        List<string> arguments = ["input", "--json-output=2", "--decode-level=none"];
        arguments.AddRange(options); arguments.Add("output");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        return new(Report(host), host);
    }

    private sealed class Graph(string json, InMemoryQpdfHost host) : IDisposable
    {
        internal JsonDocument Report { get; } = JsonDocument.Parse(json);
        private JsonElement Table => Report.RootElement.GetProperty("qpdf")[1];
        internal JsonElement Catalog => Resolve(Table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));
        internal JsonElement Resolve(JsonElement value) => value.ValueKind == JsonValueKind.String
            ? Table.GetProperty("obj:" + value.GetString()).GetProperty("value") : value;
        internal JsonElement Header(string reference) => Table.GetProperty("obj:" + reference).GetProperty("stream").GetProperty("dict");
        internal byte[] Bytes(string reference)
        {
            JsonElement stream = Table.GetProperty("obj:" + reference).GetProperty("stream");
            if (stream.TryGetProperty("data", out JsonElement data)) { return Convert.FromBase64String(data.GetString() ?? throw new InvalidOperationException()); }
            Assert.True(host.TryGetOutput(QpdfPath.From(stream.GetProperty("datafile").GetString() ?? throw new InvalidOperationException()), out byte[] bytes));
            return bytes;
        }
        internal List<JsonElement> Pages() => Resolve(Catalog.GetProperty("/Pages")).GetProperty("/Kids").EnumerateArray().Select(Resolve).ToList();
        internal void Metadata(bool pushed)
        {
            JsonElement metadata = Report.RootElement.GetProperty("qpdf")[0];
            Assert.True(metadata.GetProperty("calledgetallpages").GetBoolean());
            Assert.Equal(pushed, metadata.GetProperty("pushedinheritedpageresources").GetBoolean());
        }
        internal void Externalized(JsonElement page)
        {
            byte[] content;
            if (page.TryGetProperty("/Contents", out JsonElement contents)) { content = Bytes(contents.GetString() ?? throw new InvalidOperationException()); }
            else
            {
                string reference = Table.EnumerateObject().Single(entry => entry.Value.TryGetProperty("stream", out JsonElement stream)
                    && stream.GetProperty("dict").GetRawText() == page.GetRawText()).Name[4..];
                content = Bytes(reference);
            }
            Assert.DoesNotContain("BI", Encoding.ASCII.GetString(content)); Assert.Contains(" Do", Encoding.ASCII.GetString(content));
            JsonElement resources = Resolve(page.GetProperty("/Resources"));
            JsonProperty image = Assert.Single(Resolve(resources.GetProperty("/XObject")).EnumerateObject(),
                entry => Header(entry.Value.GetString() ?? throw new InvalidOperationException())
                    .GetProperty("/Subtype").GetString() == "/Image");
            Assert.Contains(image.Name + " Do", Encoding.ASCII.GetString(content));
            string imageReference = image.Value.GetString() ?? throw new InvalidOperationException();
            Assert.Equal("/DeviceRGB", Header(imageReference).GetProperty("/ColorSpace").GetString());
            Assert.Equal(Samples, Bytes(imageReference));
        }
        public void Dispose() => Report.Dispose();
    }
}

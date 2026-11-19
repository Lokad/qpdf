using System.Reflection;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AcroFormJsonTests
{
    internal static byte[] Fixture(string widget, string terminal, string group)
        => Fixture(widget, terminal, group, Array.Empty<(int, string, byte[]?)>());

    private static byte[] Fixture(string widget, string terminal, string group,
        IReadOnlyList<(int Number, string Dict, byte[]? StreamData)> extra) => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /Q 2 >> >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Annots [7 0 R] >>", null),
        (4, "null", null),
        (5, "<< /T (group) /Kids [6 0 R] " + group + " >>", null),
        (6, "<< /Parent 5 0 R /Kids [7 0 R] " + terminal + " >>", null),
        (7, "<< /Type /Annot /Subtype /Widget /P 3 0 R /Rect [0 0 10 10] " + widget + " >>", null), .. extra], 1);

    [Theory]
    [InlineData("typed", 2, false)]
    [InlineData("typed", 2, true)]
    [InlineData("argv", 1, false)]
    [InlineData("job", 2, true)]
    public async Task TerminalIdentityDoesNotDependOnRedundantFieldType(string route, int version, bool ownType)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Parent 6 0 R", "/T (field) /V (value)" + (ownType ? " /FT /Tx" : ""), "/FT /Tx /Ff 4096"), QpdfInputOwnership.Transfer);
        if (route == "typed") { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(version, ["acroform"]), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","json":"2","jsonKey":["acroform"]}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", $"--json={version}", "--json-key=acroform", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.Equal(0L, host.OpenLeaseCount); host.Dispose();
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement field = json.RootElement.GetProperty("acroform").GetProperty("fields")[0];
        Assert.Equal("6 0 R", field.GetProperty("object").GetString());
        Assert.Equal("5 0 R", field.GetProperty("parent").GetString());
        Assert.Equal("7 0 R", field.GetProperty("annotation").GetProperty("object").GetString());
        Assert.Equal("group.field", field.GetProperty("fullname").GetString());
        Assert.Equal("field", field.GetProperty("partialname").GetString());
        Assert.Equal("/Tx", field.GetProperty("fieldtype").GetString());
        Assert.Equal(4096, field.GetProperty("fieldflags").GetInt32());
        Assert.Equal(version == 1 ? "value" : "u:value", field.GetProperty("value").GetString());
        Assert.Equal(2, field.GetProperty("quadding").GetInt32());
    }

    [Theory]
    [InlineData("/T (merged)", "7 0 R", "group.field.merged", "merged")]
    [InlineData("/V (merged)", "7 0 R", "group.field", "")]
    [InlineData("/FT /Tx", "7 0 R", "group.field", "")]
    [InlineData("", "6 0 R", "group.field", "field")]
    public async Task MergedAndSeparateWidgetsRetainTheirIdentity(string ownEntries, string identity, string full, string partial)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Parent 6 0 R " + ownEntries, "/T (field)", "/FT /Tx"), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["acroform"]), CancellationToken.None);
        host.Dispose(); using JsonDocument json = JsonDocument.Parse(result.Json);
        JsonElement field = json.RootElement.GetProperty("acroform").GetProperty("fields")[0];
        Assert.Equal(identity, field.GetProperty("object").GetString());
        Assert.Equal(ownEntries.Length == 0 ? "5 0 R" : "6 0 R", field.GetProperty("parent").GetString());
        Assert.Equal("7 0 R", field.GetProperty("annotation").GetProperty("object").GetString());
        Assert.Equal(full, field.GetProperty("fullname").GetString()); Assert.Equal(partial, field.GetProperty("partialname").GetString());
        Assert.True(field.GetProperty("istext").GetBoolean());
    }

    [Theory]
    [InlineData("EFBBBFC3A9F09F9880", "é😀")]
    [InlineData("FEFF00E9D83DDE00", "é😀")]
    [InlineData("FF FEE9003DD800DE", "é😀")]
    [InlineData("E918", "é˘")]
    [InlineData("EFBBBFE282", "�")]
    public async Task LocalNamesAndInheritedChoicesKeepTextEncoding(string hex, string expected)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Parent 6 0 R /AS /A#23B", "/T <" + hex + "> /TU <" + hex + "> /TM <" + hex + ">",
            "/FT /Ch /Opt [<" + hex + "> [(export) <" + hex + ">] 42]"), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["acroform"]), CancellationToken.None);
        host.Dispose(); using JsonDocument json = JsonDocument.Parse(result.Json);
        JsonElement field = json.RootElement.GetProperty("acroform").GetProperty("fields")[0];
        Assert.Equal("group." + expected, field.GetProperty("fullname").GetString());
        foreach (string property in new[] { "partialname", "alternativename", "mappingname" }) { Assert.Equal(expected, field.GetProperty(property).GetString()); }
        Assert.Equal(new[] { expected, expected }, field.GetProperty("choices").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("/A#B", field.GetProperty("annotation").GetProperty("appearancestate").GetString());
    }

    [Theory]
    [InlineData("", "group.field.widget", "group.field.widget")]
    [InlineData("/TU null /TM null", "group.field.widget", "group.field.widget")]
    [InlineData("/TU (local)", "local", "local")]
    [InlineData("/TM (mapping)", "group.field.widget", "mapping")]
    [InlineData("/TU ()", "", "")]
    [InlineData("/TU (local) /TM (mapping)", "local", "mapping")]
    [InlineData("/TU 8 0 R /TM 9 0 R", "indirect-local", "indirect-mapping")]
    [InlineData("/TU 10 0 R /TM 10 0 R", "group.field.widget", "group.field.widget")]
    [InlineData("/TU 42 /TM /Wrong", "group.field.widget", "group.field.widget")]
    public async Task LocalNamesDoNotInheritAncestorNames(string own, string alternative, string mapping)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Parent 6 0 R /FT /Tx /T (widget) " + own,
            "/T (field) /TU (parent-alternative) /TM (parent-mapping)", "/FT /Tx /TU (root-alternative) /TM (root-mapping)",
            [(8, "(indirect-local)", null), (9, "(indirect-mapping)", null), (10, "null", null)]), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), new QpdfJsonOptions(2, ["acroform"]), CancellationToken.None);
        host.Dispose(); using JsonDocument json = JsonDocument.Parse(result.Json);
        JsonElement field = json.RootElement.GetProperty("acroform").GetProperty("fields")[0];
        Assert.Equal(alternative, field.GetProperty("alternativename").GetString());
        Assert.Equal(mapping, field.GetProperty("mappingname").GetString());
        Assert.Equal("group.field.widget", field.GetProperty("fullname").GetString());
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task FieldLocalNamesUseTheSamePublicProjection(bool job, int version)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Parent 6 0 R /FT /Tx /T (widget) /TU <FEFF00E9D83DDE00>",
            "/T (field)", "/FT /Tx /TU (ancestor) /TM (ancestor-map)"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", json = version.ToString(System.Globalization.CultureInfo.InvariantCulture), jsonKey = new[] { "acroform" } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--json=" + version, "--json-key=acroform"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(0L, host.OpenLeaseCount);
        using JsonDocument json = JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException());
        JsonElement field = json.RootElement.GetProperty("acroform").GetProperty("fields")[0];
        Assert.Equal("é😀", field.GetProperty("alternativename").GetString()); Assert.Equal("é😀", field.GetProperty("mappingname").GetString());
    }

    [Theory]
    [InlineData("T", false)]
    [InlineData("TU", false)]
    [InlineData("TM", false)]
    [InlineData("Opt", false)]
    [InlineData("T", true)]
    [InlineData("AS", false)]
    [InlineData("AS", true)]
    [InlineData("FT", false)]
    [InlineData("FT", true)]
    public async Task RefusalAndCancellationPrecedeLargeMetadataCopies(string key, bool canceled)
    {
        string text = key is "AS" or "FT" ? "/" + new string('N', 1048576) : "(" + new string('N', 1048576) + ")";
        string entry = "/" + key + " " + (key == "Opt" ? "[" + text + "]" : text);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Parent 6 0 R" + (key == "AS" ? " " + entry : ""),
            key == "AS" ? "" : entry, "/FT /Ch"), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        for (int number = 1; number <= 7; number++) { document.GetObject(number); }
        _ = document.PageReferences;
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(71L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        void Inspect()
        {
            using PreparedJsonOutput output = PreparedJsonOutput.Create(governor);
            using DisplayBuffer textOutput = DocumentJson.WriteDocument(document, new(2, ["acroform"], "none", "none", null, [], false), output,
                limits, governor, new(governor), canceled ? new CancellationToken(true) : CancellationToken.None);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        if (canceled) { Assert.ThrowsAny<OperationCanceledException>(Inspect); }
        else { Assert.Throws<QpdfLimitException>(Inspect); }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("/Parent 6 0 R", "group.field")]
    [InlineData("/Parent 99 0 R", "")]
    public async Task CyclicAndMissingParentsKeepBoundedLenientInspection(string parent, string name)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(parent, "/T (field)", "/FT /Tx /Parent 6 0 R"), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["acroform"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(result.Json);
        Assert.Equal(name, json.RootElement.GetProperty("acroform").GetProperty("fields")[0].GetProperty("fullname").GetString());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task UnnamedDirectTerminalRetainsItsObjectAndInheritsType()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Parent << /Parent 5 0 R /V (value) >>", "", "/FT /Tx"), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["acroform"]), CancellationToken.None);
        using JsonDocument json = JsonDocument.Parse(result.Json);
        JsonElement field = json.RootElement.GetProperty("acroform").GetProperty("fields")[0];
        Assert.Equal("5 0 R", field.GetProperty("object").GetProperty("/Parent").GetString());
        Assert.Equal("5 0 R", field.GetProperty("parent").GetString());
        Assert.Equal("group", field.GetProperty("fullname").GetString());
        Assert.Equal("", field.GetProperty("partialname").GetString()); Assert.True(field.GetProperty("istext").GetBoolean());
    }

    [Fact]
    public async Task AncestorDepthExhaustionRefundsInspectionScratch()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Parent 6 0 R", "/T (field)", "/FT /Tx"), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 1 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(71L);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        using (PreparedJsonOutput output = PreparedJsonOutput.Create(governor))
        {
            QpdfLimitException exception = Assert.Throws<QpdfLimitException>(() =>
            {
                using DisplayBuffer buffer = DocumentJson.WriteDocument(document, new(2, ["acroform"], "none", "none", null, [], false), output, limits, governor, new(governor), CancellationToken.None);
            });
            Assert.Equal(nameof(QpdfLimits.MaxNestingDepth), exception.LimitName);
            Assert.Equal(2L, exception.Requested); Assert.Equal(1L, exception.Allowed);
        }
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(1, document.PageCount);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task PublicAncestorLimitKeepsCompleteValuesOrThePriorReport(int ancestors)
    {
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(DeepFixture(ancestors), QpdfPath.From("report"));
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 3 };
        Task<QpdfJsonResult> running = Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("report"), new QpdfJsonOptions(2, ["acroform"]), limits, CancellationToken.None).AsTask();
        if (ancestors == 4)
        {
            QpdfLimitException exception = await Assert.ThrowsAsync<QpdfLimitException>(() => running);
            Assert.Equal(nameof(QpdfLimits.MaxNestingDepth), exception.LimitName); Assert.Equal(4L, exception.Requested); Assert.Equal(3L, exception.Allowed);
            Assert.True(host.TryGetOutput(QpdfPath.From("report"), out byte[] prior)); Assert.Equal(new byte[] { 17, 23, 41 }, prior);
        }
        else
        {
            QpdfJsonResult result = await running; using JsonDocument json = JsonDocument.Parse(result.Json);
            JsonElement field = json.RootElement.GetProperty("acroform").GetProperty("fields")[0];
            Assert.Equal("root.n1.n2.leaf", field.GetProperty("fullname").GetString());
            Assert.Equal("/Tx", field.GetProperty("fieldtype").GetString()); Assert.Equal("u:root-value", field.GetProperty("value").GetString());
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task CommandAncestorExhaustionReportsBudgetExceededBeforePublication(bool job, int version)
    {
        using InMemoryQpdfHost host = await PreparationAdmissionChecks.StorageAsync(DeepFixture(4), QpdfPath.From("report"));
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "report", json = version.ToString(System.Globalization.CultureInfo.InvariantCulture), jsonKey = new[] { "acroform" }, @global = new { parserMaxNesting = "3" } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--json=" + version, "--json-key=acroform", "--parser-max-nesting=3", "report"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(host.TryGetOutput(QpdfPath.From("report"), out byte[] bytes)); Assert.Equal(new byte[] { 17, 23, 41 }, bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedTerminalKeepsLocalOverridesAndAnnotationOccurrences(bool cyclic)
    {
        string groupParent = cyclic ? "/Parent 6 4 R" : "";
        byte[] source = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /Q 2 >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Annots [7 0 R 8 0 R] >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Annots [9 0 R 10 0 R 11 0 R] >>", null),
            (5, "<< /T (root) /FT /Ch /Ff 3 /V (inherited) /DV (default) /Opt [(one) (two)] " + groupParent + " >>", null),
            (6, "<< /T (terminal) /Parent 5 0 R /TU (short) /TM (map) >>", null),
            (7, "<< /Subtype /Widget /Parent 6 4 R /AS /First /F 4 >>", null),
            (8, "<< /Subtype /Widget /Parent 6 4 R /AS /Second /F 20 /T 12 0 R /V null >>", null),
            (9, "<< /Subtype /Widget /Parent 6 4 R /T (local) /V (override) /Q /Invalid >>", null),
            (10, "<< /Subtype /Widget /Parent 6 4 R /AS /Later >>", null),
            (11, "<< /Subtype /Widget /Parent 6 0 R /AS /Stale >>", null),
            (12, "null", null)], 1, new Dictionary<int, int> { [6] = 4 });
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), new QpdfJsonOptions(2, ["acroform"]), CancellationToken.None);
        Assert.Equal(0L, host.OpenLeaseCount);
        using JsonDocument report = JsonDocument.Parse(result.Json);
        JsonElement[] fields = report.RootElement.GetProperty("acroform").GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(new[] { "6 4 R", "6 4 R", "9 0 R", "6 4 R", "11 0 R" }, fields.Select(field => field.GetProperty("object").GetString()));
        Assert.Equal(new[] { 1, 1, 2, 2, 2 }, fields.Select(field => field.GetProperty("pageposfrom1").GetInt32()));
        Assert.Equal(new[] { "7 0 R", "8 0 R", "9 0 R", "10 0 R", "11 0 R" },
            fields.Select(field => field.GetProperty("annotation").GetProperty("object").GetString()));
        Assert.Equal("/First", fields[0].GetProperty("annotation").GetProperty("appearancestate").GetString());
        Assert.Equal("/Second", fields[1].GetProperty("annotation").GetProperty("appearancestate").GetString());
        Assert.Equal(20, fields[1].GetProperty("annotation").GetProperty("annotationflags").GetInt32());
        foreach (int index in new[] { 0, 1, 3 })
        {
            JsonElement field = fields[index];
            Assert.Equal("root.terminal", field.GetProperty("fullname").GetString());
            Assert.Equal("short", field.GetProperty("alternativename").GetString());
            Assert.Equal("map", field.GetProperty("mappingname").GetString());
            Assert.Equal("u:inherited", field.GetProperty("value").GetString());
            Assert.Equal("u:default", field.GetProperty("defaultvalue").GetString());
            Assert.Equal(new[] { "one", "two" }, field.GetProperty("choices").EnumerateArray().Select(choice => choice.GetString()));
        }
        Assert.Equal("root.terminal.local", fields[2].GetProperty("fullname").GetString());
        Assert.Equal("u:override", fields[2].GetProperty("value").GetString());
        Assert.Equal(2, fields[2].GetProperty("quadding").GetInt32());
        Assert.Equal(JsonValueKind.Null, fields[4].GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task DeepSharedTerminalsNeedLessWorkThanEqualIndependentTerminals()
    {
        FieldInfo notifications = typeof(ResourceGovernor).GetField("_operationsSinceCheck", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing work notification counter");
        long shared = await MeasureAsync(true);
        long independent = await MeasureAsync(false);
        Assert.True(shared * 2 < independent, $"Shared work {shared}, independent work {independent}");

        async Task<long> MeasureAsync(bool sharedParent)
        {
            const int depth = 64;
            const int count = 16;
            int firstTerminal = 4 + depth;
            int firstWidget = firstTerminal + count;
            string[] widgets = Enumerable.Range(firstWidget, count).Select(number => $"{number} 0 R").ToArray();
            List<(int, string, byte[]?)> objects = [
                (1, $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Annots [" + string.Join(' ', widgets) + "] >>", null),
                (4, "<< /T (root) /FT /Tx /V (value) >>", null)];
            for (int number = 5; number < firstTerminal; number++)
            { objects.Add((number, $"<< /Parent {number - 1} 0 R >>", null)); }
            for (int index = 0; index < count; index++)
            {
                objects.Add((firstTerminal + index, $"<< /T (leaf) /Parent {firstTerminal - 1} 0 R >>", null));
                int parent = sharedParent ? firstTerminal : firstTerminal + index;
                objects.Add((firstWidget + index, $"<< /Subtype /Widget /Parent {parent} 0 R >>", null));
            }

            using InMemoryQpdfHost host = new();
            host.AddInput(QpdfPath.From("input"), PdfTestDocument.Assemble(objects, 1), QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
            for (int number = 1; number < firstWidget + count; number++) { document.GetObject(number); }
            _ = document.PageReferences;
            QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = long.MaxValue };
            ResourceGovernor governor = new(limits, CancellationToken.None);
            using (PreparedJsonOutput output = PreparedJsonOutput.Create(governor))
            using (DisplayBuffer projection = DocumentJson.WriteDocument(document, new(2, ["acroform"], "none", "none", null, [], false), output,
                limits, governor, new(governor), CancellationToken.None))
            using (GovernedText text = projection.Finish())
            using (JsonDocument report = JsonDocument.Parse(text.Value))
            {
                JsonElement[] fields = report.RootElement.GetProperty("acroform").GetProperty("fields").EnumerateArray().ToArray();
                Assert.Equal(count, fields.Length);
                for (int i = 0; i < count; i++)
                {
                    Assert.Equal($"{(sharedParent ? firstTerminal : firstTerminal + i)} 0 R", fields[i].GetProperty("object").GetString());
                    Assert.Equal("root.leaf", fields[i].GetProperty("fullname").GetString());
                    Assert.Equal("u:value", fields[i].GetProperty("value").GetString());
                }
            }
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            return (long)(notifications.GetValue(governor) ?? throw new InvalidOperationException());
        }
    }

    internal static byte[] DeepFixture(int ancestors)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ancestors, 1);
        int widget = 5 + ancestors;
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Annots [" + widget + " 0 R] >>", null),
            (4, "null", null)];
        for (int index = 0; index < ancestors; index++)
        {
            int number = 5 + index;
            objects.Add((number, "<< /T (" + (index == 0 ? "root" : "n" + index) + ") /Kids [" + (number + 1) + " 0 R] "
                + (index == 0 ? "/FT /Tx /V (root-value)" : "/Parent " + (number - 1) + " 0 R") + " >>", null));
        }
        objects.Add((widget, "<< /Type /Annot /Subtype /Widget /T (leaf) /P 3 0 R /Parent " + (widget - 1) + " 0 R /Rect [0 0 60 20] >>", null));
        return PdfTestDocument.Assemble(objects, 1);
    }
}

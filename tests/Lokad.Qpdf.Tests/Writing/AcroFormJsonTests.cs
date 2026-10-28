using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AcroFormJsonTests
{
    internal static byte[] Fixture(string widget, string terminal, string group) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /Q 2 >> >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Annots [7 0 R] >>", null),
        (4, "null", null),
        (5, "<< /T (group) /Kids [6 0 R] " + group + " >>", null),
        (6, "<< /Parent 5 0 R /Kids [7 0 R] " + terminal + " >>", null),
        (7, "<< /Type /Annot /Subtype /Widget /P 3 0 R /Rect [0 0 10 10] " + widget + " >>", null)], 1);

    [Theory]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("typed", true)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task TerminalIdentityDoesNotDependOnRedundantFieldType(string route, bool ownType)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Parent 6 0 R", "/T (field) /V (value)" + (ownType ? " /FT /Tx" : ""), "/FT /Tx /Ff 4096"), QpdfInputOwnership.Transfer);
        if (route == "typed") { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["acroform"]), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","json":"2","jsonKey":["acroform"]}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--json=2", "--json-key=acroform", "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.Equal(0L, host.OpenLeaseCount); host.Dispose();
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement field = json.RootElement.GetProperty("acroform").GetProperty("fields")[0];
        Assert.Equal("6 0 R", field.GetProperty("object").GetString());
        Assert.Equal("5 0 R", field.GetProperty("parent").GetString());
        Assert.Equal("group.field", field.GetProperty("fullname").GetString());
        Assert.Equal("field", field.GetProperty("partialname").GetString());
        Assert.Equal("/Tx", field.GetProperty("fieldtype").GetString());
        Assert.Equal(4096, field.GetProperty("fieldflags").GetInt32());
        Assert.Equal("u:value", field.GetProperty("value").GetString());
        Assert.Equal(2, field.GetProperty("quadding").GetInt32());
    }

    [Theory]
    [InlineData("/T (merged)", "7 0 R", "group.field.merged", "merged")]
    [InlineData("/V (merged)", "7 0 R", "group.field", "")]
    [InlineData("", "6 0 R", "group.field", "field")]
    public async Task MergedAndSeparateWidgetsRetainTheirIdentity(string ownEntries, string identity, string full, string partial)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Parent 6 0 R " + ownEntries, "/T (field)", "/FT /Tx"), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["acroform"]), CancellationToken.None);
        host.Dispose(); using JsonDocument json = JsonDocument.Parse(result.Json);
        JsonElement field = json.RootElement.GetProperty("acroform").GetProperty("fields")[0];
        Assert.Equal(identity, field.GetProperty("object").GetString());
        Assert.Equal(full, field.GetProperty("fullname").GetString()); Assert.Equal(partial, field.GetProperty("partialname").GetString());
        Assert.True(field.GetProperty("istext").GetBoolean());
    }

    [Theory]
    [InlineData("EFBBBFC3A9F09F9880", "é😀")]
    [InlineData("FEFF00E9D83DDE00", "é😀")]
    [InlineData("FF FEE9003DD800DE", "é😀")]
    [InlineData("E918", "é˘")]
    [InlineData("EFBBBFE282", "�")]
    public async Task NamesAndChoicesKeepTextEncodingAndInheritedValues(string hex, string expected)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Parent 6 0 R /AS /A#23B", "/T <" + hex + ">",
            "/FT /Ch /TU <" + hex + "> /TM <" + hex + "> /Opt [<" + hex + "> [(export) <" + hex + ">] 42]"), QpdfInputOwnership.Transfer);
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, ["acroform"]), CancellationToken.None);
        host.Dispose(); using JsonDocument json = JsonDocument.Parse(result.Json);
        JsonElement field = json.RootElement.GetProperty("acroform").GetProperty("fields")[0];
        Assert.Equal("group." + expected, field.GetProperty("fullname").GetString());
        foreach (string property in new[] { "partialname", "alternativename", "mappingname" }) { Assert.Equal(expected, field.GetProperty(property).GetString()); }
        Assert.Equal(new[] { expected, expected }, field.GetProperty("choices").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("/A#B", field.GetProperty("annotation").GetProperty("appearancestate").GetString());
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
    public async Task AncestorDepthStillTruncatesLeniently()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("/Parent 6 0 R", "/T (field)", "/FT /Tx"), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 1 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using PreparedJsonOutput output = PreparedJsonOutput.Create(governor);
        using DisplayBuffer buffer = DocumentJson.WriteDocument(document, new(2, ["acroform"], "none", "none", null, [], false), output, limits, governor, new(governor), CancellationToken.None);
        using GovernedText text = buffer.Finish(); using JsonDocument json = JsonDocument.Parse(text.Value);
        JsonElement field = json.RootElement.GetProperty("acroform").GetProperty("fields")[0];
        Assert.Equal("field", field.GetProperty("fullname").GetString()); Assert.Equal("", field.GetProperty("fieldtype").GetString());
    }
}

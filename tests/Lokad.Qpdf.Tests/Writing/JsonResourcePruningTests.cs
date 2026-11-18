using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonResourcePruningTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output");

    internal static byte[] Fixture(int generation, string shape)
    {
        string Ref(int number) => number + " " + generation + " R";
        string fonts = "<< /Font << /F1 " + Ref(8) + " /F2 " + Ref(9) + " >> >>";
        bool inherited = shape is "inherited" or "coalesce" or "damaged";
        string local = shape == "independent" ? fonts : shape == "form"
            ? "<< /XObject << /Fm " + Ref(10) + " >> >>" : Ref(5);
        byte[] program = Encoding.ASCII.GetBytes(shape == "damaged" ? "(" : shape == "form" ? "/Fm Do"
            : "BT /F1 12 Tf 10 10 Td (USED) Tj ET");
        byte[] first = shape == "coalesce" ? "BT /F1 12 Tf 10 10 Td (USED) Tj"u8.ToArray() : program;
        byte[] second = shape == "coalesce" ? "ET"u8.ToArray() : program;
        string Page(int content) => "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents "
            + (shape == "coalesce" ? "[" + Ref(6) + " " + Ref(7) + "]" : Ref(content))
            + (inherited ? "" : " /Resources " + local) + " >>";
        List<(int, string, byte[]?)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /PrivateResources " + Ref(5)
                + (shape == "form" ? " /PrivateForm " + Ref(10) : "") + " /Dead ["
                + string.Join(" ", Enumerable.Range(shape == "form" ? 11 : 10, 16).Select(number => number + " 0 R")) + "] >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [" + Ref(3) + " " + Ref(4) + "]"
                + (inherited ? " /Resources " + Ref(5) : "") + " >>", null),
            (3, Page(6), null), (4, Page(7), null), (5, fonts, null),
            (6, "<< /Length " + first.Length + " >>", first), (7, "<< /Length " + second.Length + " >>", second),
            (8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (9, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null)];
        if (shape == "form")
        {
            byte[] form = "BT /F1 12 Tf 10 10 Td (USED) Tj ET"u8.ToArray();
            objects.Add((10, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources " + Ref(5)
                + " /Length " + form.Length + " >>", form));
        }
        return PdfTestDocument.Assemble(objects, 1,
            objects.Where(item => item.Item1 > 2).ToDictionary(item => item.Item1, _ => generation), "1.7", "");
    }

    public static IEnumerable<object[]> Policies()
    {
        foreach (int generation in new[] { 0, 17 })
        foreach (string shape in new[] { "inherited", "shared", "independent" })
        foreach (string mode in new[] { "default", "auto", "yes", "no" })
        { yield return [generation, shape, mode]; }
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task SelectedResourcesFollowSourcePolicyWithoutChangingPrivateBindings(int generation, string shape, string mode)
    {
        using InMemoryQpdfHost host = Host(Fixture(generation, shape));
        List<string> arguments = ["input", "--json-output=2", "--pages", ".", "1", "--"];
        if (mode != "default") { arguments.Add("--remove-unreferenced-resources=" + mode); }
        using JsonDocument report = await Run(host, arguments);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        JsonElement page = Assert.Single(Pages(table).EnumerateArray());
        bool pruned = mode == "yes" || mode != "no" && shape != "independent";
        JsonElement active = FontBindings(table, Value(table, page));
        AssertFonts(active, pruned);
        Assert.Equal("8 " + generation + " R", active.GetProperty("/F1").GetString());
        AssertPrivate(table, generation);
        JsonElement metadata = report.RootElement.GetProperty("qpdf")[0];
        Assert.True(metadata.GetProperty("calledgetallpages").GetBoolean());
        Assert.True(metadata.GetProperty("pushedinheritedpageresources").GetBoolean());
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("yes")]
    [InlineData("coalesce")]
    public async Task PruningDoesNotActivateWithoutPageSelection(string mode)
    {
        using InMemoryQpdfHost host = Host(Fixture(17, "inherited"));
        List<string> arguments = ["input", "--json-output=2"];
        if (mode != "baseline") { arguments.Add("--remove-unreferenced-resources=yes"); }
        if (mode == "coalesce") { arguments.Add("--coalesce-contents"); }
        using JsonDocument report = await Run(host, arguments);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        Assert.All(Pages(table).EnumerateArray(), page => AssertFonts(FontBindings(table, Value(table, page)), false));
        AssertPrivate(table, 17);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrimaryAndForeignSourcesKeepIndependentAutoDecisions(bool sharedPrimary)
    {
        using InMemoryQpdfHost host = Host(Fixture(17, sharedPrimary ? "inherited" : "independent"));
        host.AddInput(QpdfPath.From("foreign"), Fixture(0, sharedPrimary ? "independent" : "inherited"), QpdfInputOwnership.Transfer);
        using JsonDocument report = await Run(host, ["input", "--json-output=2", "--pages", ".", "1", "foreign", "1", "--"]);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1], pages = Pages(table);
        Assert.Equal(2, pages.GetArrayLength());
        AssertFonts(FontBindings(table, Value(table, pages[0])), sharedPrimary);
        AssertFonts(FontBindings(table, Value(table, pages[1])), !sharedPrimary);
        AssertPrivate(table, 17);
    }

    [Fact]
    public async Task EmptyPrimaryUsesTheForeignSourcePolicy()
    {
        using InMemoryQpdfHost host = Host(Fixture(17, "inherited"));
        using JsonDocument report = await Run(host, ["--empty", "--json-output=2", "--pages", "input", "1", "--"]);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        AssertFonts(FontBindings(table, Value(table, Assert.Single(Pages(table).EnumerateArray()))), true);
    }

    [Fact]
    public async Task RepeatedPagesRetainIndependentHeadersAndSharedPrunedResources()
    {
        using InMemoryQpdfHost host = Host(Fixture(17, "shared"));
        using JsonDocument report = await Run(host, ["input", "--json-output=2", "--pages", ".", "1,1", "--", "--remove-unreferenced-resources=yes"]);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1], pages = Pages(table);
        Assert.NotEqual(pages[0].GetString(), pages[1].GetString());
        JsonElement first = Value(table, pages[0]), second = Value(table, pages[1]);
        AssertFonts(FontBindings(table, first), true); AssertFonts(FontBindings(table, second), true);
        Assert.Equal(first.GetProperty("/Resources").GetRawText(), second.GetProperty("/Resources").GetRawText());
        AssertPrivate(table, 17);
    }

    [Fact]
    public async Task FormReplacementsLeaveThePrivateFormAndResourcesIntact()
    {
        using InMemoryQpdfHost host = Host(Fixture(17, "form"));
        using JsonDocument report = await Run(host, ["input", "--json-output=2", "--pages", ".", "1", "--", "--remove-unreferenced-resources=yes"]);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        JsonElement page = Value(table, Pages(table)[0]), resources = Value(table, page.GetProperty("/Resources"));
        JsonElement reference = Value(table, resources.GetProperty("/XObject")).GetProperty("/Fm");
        Assert.NotEqual("10 17 R", reference.GetString());
        JsonElement active = table.GetProperty("obj:" + reference.GetString()).GetProperty("stream").GetProperty("dict");
        AssertFonts(FontBindings(table, active), true);
        JsonElement original = table.GetProperty("obj:10 17 R").GetProperty("stream").GetProperty("dict");
        AssertFonts(FontBindings(table, original), false);
        AssertPrivate(table, 17);
    }

    [Fact]
    public async Task CoalescingDoesNotChangeTheOriginalSourceAutoDecision()
    {
        using InMemoryQpdfHost host = Host(Fixture(17, "coalesce"));
        using JsonDocument report = await Run(host, ["input", "--json-output=2", "--pages", ".", "1", "--", "--coalesce-contents"]);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1], page = Value(table, Pages(table)[0]);
        Assert.Equal(JsonValueKind.String, page.GetProperty("/Contents").ValueKind);
        AssertFonts(FontBindings(table, page), true);
        AssertPrivate(table, 17);
    }

    [Fact]
    public async Task DamagedProgramsRetainResourcesAndReturnThePruningWarning()
    {
        using InMemoryQpdfHost host = Host(Fixture(17, "damaged"));
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--json-output=2", "--pages", ".", "1", "--", "--remove-unreferenced-resources=yes", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, entry => entry.Code == QpdfDiagnosticCode.UnsupportedFeature
            && entry.Message.StartsWith("Skipped resource pruning", StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        using JsonDocument report = JsonDocument.Parse(bytes);
        JsonElement table = report.RootElement.GetProperty("qpdf")[1];
        AssertFonts(FontBindings(table, Value(table, Pages(table)[0])), false);
    }

    [Fact]
    public Task PreparationAndPublicationFailuresProtectTheActualReportAndRefundOwners()
        => PreparationAdmissionChecks.VerifyAsync(
            () => PreparationAdmissionChecks.StorageAsync(Fixture(17, "inherited"), Output),
            (host, token) => Qpdf.ExecuteAsync(host,
                new(["input", "--json-output=2", "--pages", ".", "1,1", "--", "--remove-unreferenced-resources=yes", "output"]), token),
            Output, 512, ["write", "cancel"]);

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    [InlineData(false, 1)]
    public async Task MinimalJobAndReturnedVersionsCarryThePrunedBindings(bool job, int version)
    {
        using InMemoryQpdfHost host = Host(Fixture(17, "inherited"));
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
            {
                inputFile = "input", outputFile = "output", jsonOutput = "2", removeUnreferencedResources = "yes",
                pages = new[] { new { file = ".", range = "1" } },
            }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--json=" + version, "--pages", ".", "1", "--",
                "--remove-unreferenced-resources=yes"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        byte[] bytes;
        if (job) { Assert.True(host.TryGetOutput(Output, out bytes)); }
        else { bytes = Encoding.UTF8.GetBytes(result.OutputText ?? throw new InvalidOperationException()); }
        using JsonDocument report = JsonDocument.Parse(bytes);
        if (version == 2)
        {
            JsonElement table = report.RootElement.GetProperty("qpdf")[1];
            AssertFonts(FontBindings(table, Value(table, Pages(table)[0])), true);
        }
        else
        {
            JsonElement table = report.RootElement.GetProperty("objects");
            JsonElement Resolve(JsonElement value) => value.ValueKind == JsonValueKind.String
                ? table.GetProperty(value.GetString() ?? throw new InvalidOperationException()) : value;
            JsonElement catalog = Resolve(table.GetProperty("trailer").GetProperty("/Root"));
            JsonElement page = Resolve(Resolve(catalog.GetProperty("/Pages")).GetProperty("/Kids")[0]);
            JsonElement resources = Resolve(page.GetProperty("/Resources"));
            AssertFonts(Resolve(resources.GetProperty("/Font")), true);
        }
    }

    private static InMemoryQpdfHost Host(byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), bytes, QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task<JsonDocument> Run(InMemoryQpdfHost host, IReadOnlyList<string> arguments)
    {
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([.. arguments, "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); return JsonDocument.Parse(bytes);
    }

    private static JsonElement Value(JsonElement table, JsonElement value)
        => value.ValueKind == JsonValueKind.String ? table.GetProperty("obj:" + value.GetString()).GetProperty("value") : value;

    private static JsonElement Catalog(JsonElement table)
        => Value(table, table.GetProperty("trailer").GetProperty("value").GetProperty("/Root"));

    private static JsonElement Pages(JsonElement table) => Value(table, Catalog(table).GetProperty("/Pages")).GetProperty("/Kids");

    private static JsonElement FontBindings(JsonElement table, JsonElement page)
    {
        for (int depth = 0; depth < 4; depth++)
        {
            if (page.TryGetProperty("/Resources", out JsonElement resources))
            { return Value(table, Value(table, resources).GetProperty("/Font")); }
            page = Value(table, page.GetProperty("/Parent"));
        }
        throw new InvalidOperationException("Fixture resources are missing.");
    }

    private static void AssertFonts(JsonElement fonts, bool pruned)
    {
        Assert.True(fonts.TryGetProperty("/F1", out _));
        Assert.Equal(!pruned, fonts.TryGetProperty("/F2", out _));
    }

    private static void AssertPrivate(JsonElement table, int generation)
    {
        JsonElement catalog = Catalog(table);
        JsonElement fonts = Value(table, Value(table, catalog.GetProperty("/PrivateResources")).GetProperty("/Font"));
        Assert.Equal("8 " + generation + " R", fonts.GetProperty("/F1").GetString());
        Assert.Equal("9 " + generation + " R", fonts.GetProperty("/F2").GetString());
        Assert.All(catalog.GetProperty("/Dead").EnumerateArray(), entry => Assert.Equal(JsonValueKind.Null, entry.ValueKind));
    }
}

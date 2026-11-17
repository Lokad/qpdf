using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonImportReaderLimitTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output");

    public static IEnumerable<object[]> Policies()
    {
        foreach (bool update in new[] { false, true })
        foreach (string policy in new[] { "parserMaxNesting", "parserMaxErrors", "parserMaxContainerSizeDamaged" })
        { yield return [update, policy]; }
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task ReaderSettingsPreserveImportedValues(bool update, string policy)
    {
        using InMemoryQpdfHost host = await Prepare("17", JsonValue("17"), false);
        QpdfExecutionResult result = await Run(host, update, "job", new() { [policy] = 32 }, new(), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(17L, Assert.IsType<PdfInteger>(await ReadAudit(host)).Value);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task ReaderSettingsCannotRaiseExplicitHostCeilings(bool update, string policy)
    {
        using InMemoryQpdfHost host = await Prepare("17", JsonValue("17"), false);
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 16, MaxParserErrors = 16, MaxContainerSizeDamaged = 16 };
        QpdfExecutionResult result = await Run(host, update, "job", new() { [policy] = 32 }, new(limits), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.Equal(0L, result.Counters.HostOperations); Assert.Equal(0L, result.Counters.OpenHandles);
        AssertPrior(host);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnlimitedErrorsCannotLiftAFiniteHostCeiling(bool update)
    {
        using InMemoryQpdfHost host = await Prepare("17", JsonValue("17"), false);
        QpdfExecutionResult result = await Run(host, update, "argv", new() { ["parserMaxErrors"] = 0 },
            new(QpdfLimits.Default with { MaxParserErrors = 2 }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded && d.Message.Contains(nameof(QpdfLimits.MaxParserErrors), StringComparison.Ordinal));
        Assert.Equal(0L, result.Counters.HostOperations); AssertPrior(host);
    }

    [Theory]
    [InlineData("create-json", 4)]
    [InlineData("create-json", 32)]
    [InlineData("update-json", 4)]
    [InlineData("update-json", 32)]
    [InlineData("update-pdf", 4)]
    [InlineData("update-pdf", 32)]
    public async Task NestingSettingsReachBothJsonValuesAndTheUpdateReader(string source, int limit)
    {
        string nested = new string('[', 8) + "17" + new string(']', 8);
        using InMemoryQpdfHost host = await Prepare(source == "update-pdf" ? nested : "17",
            source == "update-pdf" ? EmptyUpdate() : JsonValue(nested), false);
        QpdfExecutionResult result = await Run(host, source != "create-json", "job", new() { ["parserMaxNesting"] = limit }, new(), CancellationToken.None);
        if (limit == 4)
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded && d.Message.Contains(nameof(QpdfLimits.MaxNestingDepth), StringComparison.Ordinal));
            Assert.True(result.Counters.HostOperations > 0); AssertPrior(host);
        }
        else
        {
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            QpdfObject value = await ReadAudit(host);
            for (int index = 0; index < 8; index++) { value = Assert.Single(Assert.IsType<PdfArray>(value).Items); }
            Assert.Equal(17L, Assert.IsType<PdfInteger>(value).Value);
        }
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData(false, 10, false)]
    [InlineData(true, 10, false)]
    [InlineData(true, 100, false)]
    [InlineData(true, 10, true)]
    public async Task DamagedContainerLimitOnlyAppliesDuringRecovery(bool damaged, int limit, bool noDefaults)
    {
        string array = "[" + string.Join(' ', Enumerable.Range(0, 40)) + "]";
        using InMemoryQpdfHost host = await Prepare(array, EmptyUpdate(), damaged);
        Dictionary<string, object> global = new() { ["parserMaxContainerSizeDamaged"] = limit };
        if (noDefaults) { global["noDefaultLimits"] = ""; }
        QpdfExecutionResult result = await Run(host, true, "job", global, new(), CancellationToken.None);
        if (damaged && limit == 10)
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded
                && d.Message.Contains("MaxContainerSize exceeded: requested 11, allowed 10", StringComparison.Ordinal));
            AssertPrior(host);
        }
        else
        {
            Assert.Equal(damaged ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
            PdfArray value = Assert.IsType<PdfArray>(await ReadAudit(host));
            Assert.Equal(40, value.Count); Assert.Equal(39L, Assert.IsType<PdfInteger>(value[39]).Value);
        }
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ErrorThresholdGovernsTheUpdatedSourceObject(int threshold)
    {
        using InMemoryQpdfHost host = await Prepare("[ @ 17 ]", EmptyUpdate(), false);
        QpdfExecutionResult result = await Run(host, true, "argv", new() { ["parserMaxErrors"] = threshold }, new(), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        QpdfObject value = await ReadAudit(host);
        if (threshold == 1) { Assert.IsType<PdfNull>(value); }
        else
        {
            PdfArray array = Assert.IsType<PdfArray>(value); Assert.Equal(2, array.Count);
            Assert.Equal("@"u8.ToArray(), Assert.IsType<PdfByteString>(array[0]).ValueMemory.ToArray());
            Assert.Equal(17L, Assert.IsType<PdfInteger>(array[1]).Value);
        }
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Fact]
    public async Task NestedFileSettingsUseTheSameGovernance()
    {
        using InMemoryQpdfHost host = await Prepare("17", JsonValue("17"), false);
        host.AddInput(QpdfPath.From("settings"), "{\"global\":{\"parserMaxNesting\":32,\"parserMaxErrors\":2,\"parserMaxContainerSizeDamaged\":100}}"u8.ToArray(), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, "{\"jobJsonFile\":\"settings\",\"inputFile\":\"json\",\"jsonInput\":\"\",\"outputFile\":\"output\"}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(17L, Assert.IsType<PdfInteger>(await ReadAudit(host)).Value);
    }

    private static byte[] EmptyUpdate() => "{\"qpdf\":[{\"jsonversion\":2,\"pdfversion\":\"1.4\"},{}]}"u8.ToArray();

    private static byte[] JsonValue(string audit) => Encoding.UTF8.GetBytes("{\"qpdf\":[{\"jsonversion\":2,\"pdfversion\":\"1.4\"},{"
        + "\"obj:1 0 R\":{\"value\":{\"/Type\":\"/Catalog\",\"/Pages\":\"2 0 R\",\"/Audit\":\"4 0 R\"}},"
        + "\"obj:2 0 R\":{\"value\":{\"/Type\":\"/Pages\",\"/Count\":1,\"/Kids\":[\"3 0 R\"]}},"
        + "\"obj:3 0 R\":{\"value\":{\"/Type\":\"/Page\",\"/Parent\":\"2 0 R\",\"/MediaBox\":[0,0,80,40]}},"
        + "\"obj:4 0 R\":{\"value\":" + audit + "},\"trailer\":{\"value\":{\"/Root\":\"1 0 R\"}}}]}");

    private static async Task<InMemoryQpdfHost> Prepare(string audit, byte[] json, bool damaged)
    {
        InMemoryQpdfHost host = new();
        byte[] pdf = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 4 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] >>", null),
            (4, audit, null)], 1);
        if (damaged)
        {
            string text = Encoding.Latin1.GetString(pdf); int start = text.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length;
            int end = text.IndexOf('\n', start); pdf = Encoding.Latin1.GetBytes(text[..start] + new string('0', end - start) + text[end..]);
        }
        host.AddInput(QpdfPath.From("input"), pdf, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("json"), json, QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
        return host;
    }

    private static async Task<QpdfExecutionResult> Run(InMemoryQpdfHost host, bool update, string route,
        Dictionary<string, object> global, QpdfExecutionOptions options, CancellationToken token)
    {
        if (route == "argv")
        {
            List<string> argv = ["--global"];
            foreach (var setting in global)
            {
                string flag = setting.Key switch
                {
                    "parserMaxErrors" => "parser-max-errors",
                    "parserMaxNesting" => "parser-max-nesting",
                    "parserMaxContainerSizeDamaged" => "parser-max-container-size-damaged",
                    _ => throw new ArgumentException("Unknown test setting.", nameof(global)),
                };
                argv.Add("--" + flag + "=" + setting.Value);
            }
            argv.Add("--"); argv.AddRange(update ? ["input", "--update-from-json=json", "output"] : ["json", "--json-input", "output"]);
            return await Qpdf.ExecuteAsync(host, new(argv), options, token);
        }
        Dictionary<string, object> job = new() { ["inputFile"] = update ? "input" : "json", ["outputFile"] = "output", ["global"] = global };
        job[update ? "updateFromJson" : "jsonInput"] = update ? "json" : "";
        return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), options, token);
    }

    private static async Task<QpdfObject> ReadAudit(InMemoryQpdfHost host)
    {
        Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(Output, out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfReference root = Assert.IsType<PdfReference>(Get(document.Engine.Trailer, "Root"));
        QpdfObject value = Get(Assert.IsType<PdfDictionary>(document.GetObject(root)), "Audit");
        return value is PdfReference reference ? document.GetObject(reference) : value;
    }

    private static QpdfObject Get(PdfDictionary node, string key) => node.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? value : PdfNull.Instance;

    private static void AssertPrior(InMemoryQpdfHost host)
    { Assert.True(host.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output); Assert.Equal(0L, host.OpenLeaseCount); }
}

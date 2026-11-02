using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class AttachmentOptionTests
{
    private static readonly string[] Routes = ["argv", "mixed", "job"];
    private static readonly byte[] Payload = "data"u8.ToArray();

    public static IEnumerable<object[]> ParserCases()
    {
        foreach (string route in Routes)
        foreach (string kind in new[] { "depth", "container", "damaged" })
        { yield return [route, kind]; }
    }

    public static IEnumerable<object[]> WarningCases()
    {
        foreach (string route in Routes)
        foreach (bool noWarn in new[] { false, true })
        foreach (bool exitZero in new[] { false, true })
        { yield return [route, noWarn, exitZero]; }
    }

    public static IEnumerable<object[]> CeilingCases()
    {
        foreach (string route in Routes)
        foreach (string kind in new[] { "filters", "depth", "container", "damaged", "errors" })
        { yield return [route, kind]; }
    }

    [Theory]
    [MemberData(nameof(ParserCases))]
    public async Task ParserOverridesRejectBeforeReplacingOutput(string route, string kind)
    {
        using InMemoryQpdfHost host = await Host(Fixture(Payload, string.Empty, kind == "depth", kind == "damaged"));
        (string Flag, string Property) setting = kind switch
        {
            "depth" => ("--parser-max-nesting=2", "\"parserMaxNesting\":2"),
            "damaged" => ("--parser-max-container-size-damaged=3", "\"parserMaxContainerSizeDamaged\":3"),
            _ => ("--parser-max-container-size=3", "\"parserMaxContainerSize\":3")
        };
        QpdfExecutionResult result = await Execute(host, route, [setting.Flag], "\"global\":{" + setting.Property + "}", new());
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments);
        AssertOutput(host, result, "prior"u8.ToArray());
    }

    [Theory]
    [MemberData(nameof(WarningCases))]
    public async Task FilterLimitAndWarningPolicyReachExtraction(string route, bool noWarn, bool exitZero)
    {
        byte[] once = Encoding.ASCII.GetBytes(Convert.ToHexString(Payload) + ">");
        byte[] twice = Encoding.ASCII.GetBytes(Convert.ToHexString(once) + ">");
        using InMemoryQpdfHost host = await Host(Fixture(twice, " /Filter [/ASCIIHexDecode /ASCIIHexDecode]", false, false));
        string[] flags = ["--max-stream-filters=1", .. noWarn ? new[] { "--no-warn" } : [], .. exitZero ? new[] { "--warning-exit-0" } : []];
        string fields = "\"global\":{\"maxStreamFilters\":1},\"noWarn\":" + Bool(noWarn) + ",\"warningExit0\":" + Bool(exitZero);
        QpdfExecutionResult result = await Execute(host, route, flags, fields, new());
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(exitZero ? QpdfExitCode.Success : QpdfExitCode.Warning, result.ExitCode);
        Assert.Equal(!noWarn, result.Diagnostics.Any(item => item.Code == QpdfDiagnosticCode.UnsupportedFeature));
        AssertOutput(host, result, twice);
    }

    [Theory]
    [MemberData(nameof(WarningCases))]
    public async Task RecoveryWarningSuppressionPrecedesDiagnosticStorage(string route, bool noWarn, bool exitZero)
    {
        using InMemoryQpdfHost host = await Host(Fixture(Payload, string.Empty, false, true));
        string[] flags = [.. noWarn ? new[] { "--no-warn" } : [], .. exitZero ? new[] { "--warning-exit-0" } : []];
        string fields = "\"noWarn\":" + Bool(noWarn) + ",\"warningExit0\":" + Bool(exitZero);
        QpdfLimits limits = noWarn ? QpdfLimits.Default with { MaxDiagnosticBytes = 1 } : QpdfLimits.Default;
        QpdfExecutionResult result = await Execute(host, route, flags, fields, new(limits));
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(exitZero ? QpdfExitCode.Success : QpdfExitCode.Warning, result.ExitCode);
        Assert.Equal(!noWarn, result.Diagnostics.Any(item => item.Code == QpdfDiagnosticCode.RecoveryEngaged));
        AssertOutput(host, result, Payload);
    }

    [Theory]
    [MemberData(nameof(CeilingCases))]
    public async Task InvocationCannotRaiseExplicitHostCeilings(string route, string kind)
    {
        using InMemoryQpdfHost host = await Host(Fixture(Payload, string.Empty, false, false));
        (QpdfLimits Limits, string Flag, string Property) setting = kind switch
        {
            "filters" => (QpdfLimits.Default with { MaxStreamFilters = 1 }, "--max-stream-filters=2", "\"maxStreamFilters\":2"),
            "depth" => (QpdfLimits.Default with { MaxNestingDepth = 2 }, "--parser-max-nesting=3", "\"parserMaxNesting\":3"),
            "container" => (QpdfLimits.Default with { MaxContainerSize = 3 }, "--parser-max-container-size=4", "\"parserMaxContainerSize\":4"),
            "damaged" => (QpdfLimits.Default with { MaxContainerSizeDamaged = 3 }, "--parser-max-container-size-damaged=4", "\"parserMaxContainerSizeDamaged\":4"),
            _ => (QpdfLimits.Default with { MaxParserErrors = 1 }, "--parser-max-errors=2", "\"parserMaxErrors\":2")
        };
        QpdfExecutionResult result = await Execute(host, route, [setting.Flag], "\"global\":{" + setting.Property + "}", new(setting.Limits));
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code);
        AssertOutput(host, result, "prior"u8.ToArray());
    }

    [Theory]
    [InlineData("argv", false)]
    [InlineData("mixed", false)]
    [InlineData("job", false)]
    [InlineData("argv", true)]
    [InlineData("mixed", true)]
    [InlineData("job", true)]
    public async Task NoDefaultLimitsLiftsOnlyInvocationDefaults(string route, bool explicitHost)
    {
        byte[] data = Payload;
        for (int index = 0; index < 26; index++)
        {
            using MemoryStream compressed = new();
            using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, true)) { zlib.Write(data); }
            data = compressed.ToArray();
        }
        string filters = " /Filter [" + string.Join(' ', Enumerable.Repeat("/FlateDecode", 26)) + "]";
        using InMemoryQpdfHost host = await Host(Fixture(data, filters, false, false));
        QpdfExecutionOptions options = explicitHost ? new(QpdfLimits.Default) : new();
        QpdfExecutionResult result = await Execute(host, route, ["--no-default-limits"], "\"global\":{\"noDefaultLimits\":true}", options);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(explicitHost ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
        AssertOutput(host, result, explicitHost ? data : Payload);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static async Task<InMemoryQpdfHost> Host(byte[] input)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input.pdf"), input, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("empty.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output.bin"), CancellationToken.None);
        await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
        await seed.CommitAsync(CancellationToken.None);
        return host;
    }

    private static ValueTask<QpdfExecutionResult> Execute(InMemoryQpdfHost host, string route, string[] flags, string fields, QpdfExecutionOptions options)
    {
        if (route == "job")
        {
            return Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input.pdf\",\"outputFile\":\"output.bin\",\"showAttachment\":\"payload\"," + fields + "}", options, CancellationToken.None);
        }
        return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input.pdf", .. flags, "--show-attachment=payload", .. route == "mixed" ? new[] { "--job-json-file=empty.json" } : [], "--", "output.bin"]), options, CancellationToken.None);
    }

    private static void AssertOutput(InMemoryQpdfHost host, QpdfExecutionResult result, byte[] expected)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From("output.bin"), out byte[] output));
        Assert.Equal(expected, output);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    private static byte[] Fixture(byte[] data, string filters, bool deep, bool brokenXref)
    {
        byte[] pdf = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names 6 0 R" + (deep ? " /Audit [[[[0]]]]" : "") + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null),
            (4, "<< /Type /Filespec /F (p.bin) /EF << /F 5 0 R >> >>", null),
            (5, "<< /Type /EmbeddedFile /Length " + data.Length + filters + " >>", data),
            (6, "<< /EmbeddedFiles 7 0 R >>", null),
            (7, "<< /Names [(payload) 4 0 R] >>", null)], 1);
        if (brokenXref)
        {
            int marker = pdf.AsSpan().LastIndexOf("startxref\n"u8);
            Assert.True(marker >= 0);
            for (int at = marker + "startxref\n"u8.Length; at < pdf.Length && pdf[at] is >= (byte)'0' and <= (byte)'9'; at++) { pdf[at] = (byte)'9'; }
        }
        return pdf;
    }
}

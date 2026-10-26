using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class MixedJobArgvTests
{
    public static IEnumerable<object?[]> ConfigurationCases()
    {
        yield return Case("partial-before", ["--job-json-file=settings.json", "inputs/a.pdf", "outputs/a.pdf"], 0, "1.5");
        yield return Case("partial-between", ["inputs/a.pdf", "--job-json-file=settings.json", "outputs/a.pdf"], 0, "1.5");
        yield return Case("partial-after", ["inputs/a.pdf", "outputs/a.pdf", "--job-json-file=settings.json"], 0, "1.5");
        yield return Case("force-before", ["inputs/a.pdf", "outputs/a.pdf", "--force-version=1.6", "--job-json-file=settings.json"], 0, "1.5");
        yield return Case("force-after", ["inputs/a.pdf", "outputs/a.pdf", "--job-json-file=settings.json", "--force-version=1.6"], 0, "1.6");
        yield return Case("min-before", ["inputs/a.pdf", "outputs/a.pdf", "--min-version=1.6", "--job-json-file=min.json"], 0, "1.5");
        yield return Case("min-after", ["inputs/a.pdf", "outputs/a.pdf", "--job-json-file=min.json", "--min-version=1.6"], 0, "1.6");
        yield return Case("file-input-count", ["--job-json-file=input.json", "--show-npages"], 0, null);
        yield return Case("file-input-check", ["--job-json-file=input.json", "--check"], 0, null);
        yield return Case("argv-input-file-count", ["inputs/a.pdf", "--job-json-file=count.json"], 0, null);
        yield return Case("two-partials", ["--job-json-file=input.json", "--job-json-file=count.json"], 0, null);
        yield return Case("two-inputs", ["--job-json-file=input.json", "--job-json-file=input.json", "--show-npages"], 2, null);
        yield return Case("two-settings", ["inputs/a.pdf", "outputs/a.pdf", "--job-json-file=settings.json", "--job-json-file=other.json"], 0, "1.6");
        yield return Case("positional-cursor", ["--job-json-file=input.json", "outputs/a.pdf"], 2, null);
        yield return Case("argv-input-file-output", ["inputs/a.pdf", "--job-json-file=output.json"], 0, "1.4");
        yield return Case("output-collision", ["--job-json-file=output.json", "inputs/a.pdf", "outputs/a.pdf"], 2, null);
        yield return Case("output-inspection", ["--job-json-file=complete.json", "--show-npages"], 2, null);
        yield return Case("global-context", ["inputs/a.pdf", "--global", "--job-json-file=count.json", "--"], 2, null);
        yield return Case("pages-context", ["inputs/a.pdf", "--pages", "--job-json-file=count.json", ".", "1", "--", "outputs/a.pdf"], 2, null);
        yield return Case("empty-file-cursor", ["--job-json-file=empty.json", "outputs/a.pdf"], 2, null);
        yield return Case("argv-empty-output-file", ["--empty", "--job-json-file=output.json"], 0, "1.3");
        yield return Case("file-before-global", ["inputs/a.pdf", "--job-json-file=global.json", "--global", "--max-stream-filters=0", "--", "--show-npages"], 0, null);
        yield return Case("global-before-file", ["inputs/a.pdf", "--global", "--max-stream-filters=0", "--", "--job-json-file=global.json", "--show-npages"], 0, null);
        yield return Case("file-empty-two-positionals", ["--job-json-file=empty.json", "inputs/a.pdf", "outputs/a.pdf"], 0, "1.4");
        yield return Case("file-empty-argv-count", ["--job-json-file=empty.json", "inputs/a.pdf", "--show-npages"], 0, null);
        yield return Case("argv-input-file-empty", ["inputs/a.pdf", "--job-json-file=empty.json", "--show-npages"], 2, null);
        yield return Case("whole-rotation", ["inputs/a.pdf", "outputs/a.pdf", "--job-json-file=settings.json", "--rotate=+90:1"], 0, "1.5");
        yield return Case("whole-labels", ["inputs/a.pdf", "outputs/a.pdf", "--job-json-file=settings.json", "--set-page-labels", "1:D/8/prefix", "--"], 0, "1.5");
        yield return Case("whole-collate", ["inputs/a.pdf", "outputs/a.pdf", "--job-json-file=settings.json", "--collate"], 0, "1.5");
        yield return Case("whole-replace", ["--job-json-file=input.json", "--replace-input"], 0, "1.4");
        yield return Case("bad-force-overwritten", ["inputs/a.pdf", "outputs/a.pdf", "--force-version=bogus", "--job-json-file=settings.json"], 0, "1.5");
        yield return Case("json-objects", ["inputs/a.pdf", "--job-json-file=settings.json", "--json=2", "--json-object=trailer", "--json-object=1"], 0, null);
        // The managed bare-compression extension also applies after job files.
        yield return Case("bare-compress", ["inputs/a.pdf", "outputs/a.pdf", "--job-json-file=settings.json", "--compress-streams"], 0, "1.5");
        yield return Case("separate-form", ["inputs/a.pdf", "--job-json-file", "settings.json", "outputs/a.pdf"], 0, "1.5");
        yield return Case("nested-scalar", ["inputs/a.pdf", "outputs/a.pdf", "--force-version=1.7", "--job-json-file=nested.json"], 0, "1.5");
        yield return Case("pages", ["inputs/a.pdf", "--job-json-file=settings.json", "--pages", "inputs/b.pdf", "2,1", "--", "outputs/a.pdf"], 0, "1.5");
        yield return Case("overlay", ["inputs/a.pdf", "--job-json-file=settings.json", "--overlay", "inputs/a.pdf", "--", "--", "outputs/a.pdf"], 0, "1.5");
        yield return Case("attachment-add", ["inputs/a.pdf", "--job-json-file=settings.json", "--add-attachment", "inputs/payload.bin", "--key=payload", "--", "--", "outputs/a.pdf"], 0, "1.5");
        yield return Case("overlay-context", ["inputs/a.pdf", "--overlay", "inputs/a.pdf", "--job-json-file=settings.json", "--", "outputs/a.pdf"], 2, null);
        yield return Case("attachment-context", ["inputs/a.pdf", "--add-attachment", "inputs/payload.bin", "--job-json-file=settings.json", "--", "outputs/a.pdf"], 2, null);
        yield return Case("global-terminator", ["inputs/a.pdf", "--job-json-file=count.json", "--global", "--parser-max-errors=10"], 2, null);
        yield return Case("rotation-replaces", ["inputs/a.pdf", "outputs/a.pdf", "--rotate=+180:1", "--job-json-file=rotation.json"], 0, "1.4");
    }

    private static object?[] Case(string name, string[] argv, int exit, string? version) => [name, argv, exit, version];

    [Theory]
    [MemberData(nameof(ConfigurationCases))]
    public async Task ConfigurationOrderAndPositionalState(string name, string[] argv, int expectedExit, string? version)
    {
        InMemoryQpdfHost host = Host();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(expectedExit, (int)result.ExitCode);
        Assert.Equal(expectedExit == 0 ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
        if (name == "json-objects")
        {
            using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(ReadOutputText(result)));
            Assert.Equal(2, json.RootElement.GetProperty("version").GetInt32());
            JsonElement objects = json.RootElement.GetProperty("qpdf")[1];
            Assert.True(objects.TryGetProperty("obj:1 0 R", out _));
            Assert.True(objects.TryGetProperty("trailer", out _));
            Assert.False(objects.TryGetProperty("obj:2 0 R", out _));
        }
        QpdfPath output = QpdfPath.From(name == "whole-replace" ? "inputs/a.pdf" : "outputs/a.pdf");
        bool written = host.TryGetOutput(output, out byte[] bytes);
        Assert.Equal(version is not null, written);
        if (version is not null)
        {
            Assert.StartsWith("%PDF-" + version, Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
            host.AddInput(output, bytes, QpdfInputOwnership.Copy);
            await using QpdfDocument reopened = await QpdfDocument.OpenAsync(host, output, CancellationToken.None);
            Assert.Equal(name == "argv-empty-output-file" ? 0 : name == "pages" ? 2 : 1, reopened.PageCount);
            if (name is "whole-rotation" or "rotation-replaces")
            {
                Assert.True(reopened.PageReferences[0].Page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject rotation));
                Assert.Equal(90L, Assert.IsType<PdfInteger>(rotation).Value);
            }
            if (name == "whole-labels") { Assert.Contains("prefix", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal); }
            if (name == "overlay") { Assert.Contains("/Subtype /Form", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal); }
            if (name == "attachment-add")
            {
                QpdfPath extracted = QpdfPath.From("outputs/payload.bin");
                await Qpdf.ExtractAttachmentAsync(host, output, "payload", extracted, CancellationToken.None);
                Assert.True(host.TryGetOutput(extracted, out byte[] payload));
                Assert.Equal(new byte[] { 0, 1, 2, 127, 128, 255 }, payload);
            }
        }
        else if (expectedExit == 0 && result.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.PageCount))
        {
            Assert.EndsWith(": 1", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RepeatedCompletedIncludesAreNotIdentityCycles()
    {
        InMemoryQpdfHost host = Host();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["inputs/a.pdf", "outputs/a.pdf", "--job-json-file=settings.json", "--job-json-file=settings.json"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] bytes));
        Assert.StartsWith("%PDF-1.5", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectJsonTextObeysOutputLimitsAndPreservesTargets(bool bounded)
    {
        InMemoryQpdfHost host = Host();
        QpdfPath prior = QpdfPath.From("outputs/prior.json");
        await using (IQpdfOutputTransaction transaction = await host.CreateOutputAsync(prior, CancellationToken.None))
        {
            await transaction.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        QpdfLimits limits = bounded ? QpdfLimits.Default with { MaxOutputBytes = 1L } : QpdfLimits.Default;
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"inputs/a.pdf","json":"2","jsonObject":["1","trailer"],"testJsonSchema":""}""",
            new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(bounded ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
        if (bounded)
        {
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.Null(ReadOutputText(result));
        }
        else
        {
            using JsonDocument json = JsonDocument.Parse(Assert.IsType<string>(ReadOutputText(result)));
            Assert.Equal(2, json.RootElement.GetProperty("version").GetInt32());
            Assert.True(json.RootElement.GetProperty("qpdf")[1].TryGetProperty("obj:1 0 R", out _));
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.JsonSchemaCheck);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(prior, out byte[] previous));
        Assert.Equal("prior"u8.ToArray(), previous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArgvCryptoRejectsBeforeConfigurationAccess(bool first)
    {
        string[] argv = first
            ? ["--password-file=secret-must-not-echo", "--job-json-file=input.json", "--check"]
            : ["--job-json-file=input.json", "--check", "--password-file=secret-must-not-echo"];
        InMemoryQpdfHost host = Host();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.UnsupportedCryptoOperation, Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("secret-must-not-echo", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Equal(0L, result.Counters.HostOperations);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    internal static InMemoryQpdfHost Host()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/b.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf")), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("inputs/payload.bin"), new byte[] { 0, 1, 2, 127, 128, 255 }, QpdfInputOwnership.Copy);
        Add(host, "settings.json", """{"forceVersion":"1.5"}""");
        Add(host, "other.json", """{"forceVersion":"1.6"}""");
        Add(host, "min.json", """{"minVersion":"1.5"}""");
        Add(host, "input.json", """{"inputFile":"inputs/a.pdf"}""");
        Add(host, "output.json", """{"outputFile":"outputs/a.pdf"}""");
        Add(host, "count.json", """{"showNpages":""}""");
        Add(host, "complete.json", """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf"}""");
        Add(host, "empty.json", """{"empty":""}""");
        Add(host, "global.json", """{"global":{"maxStreamFilters":"1"}}""");
        Add(host, "nested.json", """{"forceVersion":"1.6","jobJsonFile":"settings.json"}""");
        Add(host, "rotation.json", """{"rotate":["+90:1"]}""");
        return host;
    }

    // Keep final regressions executable against the preceding assembly: its
    // behavior must fail before touching the newly introduced text property.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string? ReadOutputText(QpdfExecutionResult result) => result.OutputText;

    internal static void Add(InMemoryQpdfHost host, string name, string text)
        => host.AddInput(QpdfPath.From(name), Encoding.UTF8.GetBytes(text), QpdfInputOwnership.Copy);
}

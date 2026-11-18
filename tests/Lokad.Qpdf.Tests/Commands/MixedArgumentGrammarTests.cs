using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class MixedArgumentGrammarTests
{
    private static readonly string[] Targets = ["output.pdf", "output.json", "output-1.pdf", "output-2.pdf", "output-3.pdf", "output-1-2.pdf"];

    private static byte[] Input() => PdfTestDocument.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null),
        (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null),
        (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null)], 1);

    public static IEnumerable<object[]> PlainCases()
    {
        (string Name, string[] Arguments, bool Valid)[] cases = [
            ("json-spaced", ["input", "--json", "1", "--json-key=pages", "--", "output.json"], true),
            ("json-equals", ["input", "--json=1", "--json-key=pages", "--", "output.json"], true),
            ("json-bare", ["input", "--json", "--json-key=pages", "--", "output.json"], true),
            ("json-empty", ["input", "--json=", "--json-key=pages", "--", "output.json"], false),
            ("json-output-spaced", ["input", "--json-output", "2", "--", "output.json"], true),
            ("json-output-empty", ["input", "--json-output=", "--", "output.json"], false),
            ("json-output-v1", ["input", "--json-output=1", "--", "output.json"], false),
            ("split-spaced", ["input", "--split-pages", "2", "output.pdf"], true),
            ("split-equals", ["input", "--split-pages=2", "output.pdf"], true),
            ("split-empty", ["input", "--split-pages=", "output.pdf"], true),
            ("split-bare", ["input", "--split-pages", "--", "output.pdf"], true),
            ("split-invalid", ["input", "--split-pages=x", "output.pdf"], false),
            ("compress-bare", ["input", "--compress-streams", "--", "output.pdf"], true),
            ("compress-spaced", ["input", "--compress-streams", "n", "output.pdf"], true),
            ("compress-empty", ["input", "--compress-streams=", "--", "output.pdf"], false),
            ("keep-spaced", ["input", "--keep-files-open", "y", "output.pdf"], false),
            ("keep-bare", ["input", "--keep-files-open", "--", "output.pdf"], false),
            ("keep-empty", ["input", "--keep-files-open=", "output.pdf"], false),
            ("keep-invalid", ["input", "--keep-files-open=maybe", "output.pdf"], false),
            ("keep-equals", ["input", "--keep-files-open=y", "output.pdf"], true),
            ("collate-bare", ["input", "--collate", "--pages", ".", "1-3", "--", "output.pdf"], true),
            ("collate-positional", ["--collate", "input", "--pages", ".", "1-3", "--", "output.pdf"], true),
            ("collate-empty", ["input", "--collate=", "--pages", ".", "1-3", "--", "output.pdf"], true)];
        foreach (var item in cases)
        { yield return [item.Name, item.Arguments, item.Valid]; }
    }

    private static async Task<(QpdfExecutionResult Result, Dictionary<string, byte[]> Outputs)> Execute(string[] arguments)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("empty.json"), "{}"u8.ToArray(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("version.json"), """{"forceVersion":"1.6"}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        foreach (string target in new[] { "output.pdf", "output.json" })
        {
            await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From(target), CancellationToken.None);
            await prior.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Dictionary<string, byte[]> outputs = new(StringComparer.Ordinal);
        foreach (string target in Targets)
        { if (host.TryGetOutput(QpdfPath.From(target), out byte[] bytes)) { outputs.Add(target, bytes); } }
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        return (result, outputs);
    }

    [Theory]
    [MemberData(nameof(PlainCases))]
    public async Task PlainArgumentsHaveExpectedGrammarAndOutput(string name, string[] arguments, bool valid)
    {
        var run = await Execute(arguments);
        AssertOutcome(name, valid, run.Result, run.Outputs);
    }

    [Theory]
    [InlineData("json-bare", "--job-json-file=empty.json", "--json", "input", "output.json")]
    [InlineData("json-output-bare", "input", "--json-output", "--job-json-file", "empty.json", "output.json")]
    [InlineData("split-spaced", "input", "--split-pages", "2", "output.pdf", "--job-json-file=empty.json")]
    public async Task EmptyPartialFilesPreserveDistinctPositionalForms(string name, params string[] arguments)
    {
        var run = await Execute(arguments);
        AssertOutcome(name, true, run.Result, run.Outputs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonemptyPartialFileParticipatesInSetterOrder(bool includeFirst)
    {
        string[] arguments = includeFirst
            ? ["--job-json-file=version.json", "input", "--force-version=1.5", "output.pdf"]
            : ["input", "--force-version=1.5", "output.pdf", "--job-json-file", "version.json"];
        var run = await Execute(arguments);
        Assert.Equal(QpdfExitCode.Success, run.Result.ExitCode);
        byte[] pdf = run.Outputs["output.pdf"];
        Assert.True(pdf.AsSpan().StartsWith(includeFirst ? "%PDF-1.5"u8 : "%PDF-1.6"u8));
    }

    [Fact]
    public async Task InvalidExplicitChoiceAfterAnIncludePreservesThePriorTargets()
    {
        var run = await Execute(["input", "--job-json-file", "empty.json", "--json-output=1", "output.json"]);
        AssertOutcome("json-output-v1", false, run.Result, run.Outputs);
    }

    private static void AssertOutcome(string name, bool valid, QpdfExecutionResult result, Dictionary<string, byte[]> outputs)
    {
        Assert.Equal(valid ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(valid ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
        byte[] prior = [17, 23, 41];
        if (!valid)
        {
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.InvalidArguments);
            Assert.Equal(2, outputs.Count);
            Assert.Equal(prior, outputs["output.pdf"]);
            Assert.Equal(prior, outputs["output.json"]);
        }
        else if (name.StartsWith("json", StringComparison.Ordinal))
        {
            Assert.Equal(2, outputs.Count);
            Assert.Equal(prior, outputs["output.pdf"]);
            using JsonDocument json = JsonDocument.Parse(outputs["output.json"]);
            if (name.StartsWith("json-output", StringComparison.Ordinal))
            { Assert.Equal(2, json.RootElement.GetProperty("qpdf")[0].GetProperty("jsonversion").GetInt32()); }
            else { Assert.Equal(name == "json-bare" ? 2 : 1, json.RootElement.GetProperty("version").GetInt32()); }
        }
        else if (name.StartsWith("split", StringComparison.Ordinal))
        {
            string[] parts = name is "split-spaced" or "split-equals"
                ? ["output-1-2.pdf", "output-3.pdf"] : ["output-1.pdf", "output-2.pdf", "output-3.pdf"];
            Assert.Equal(parts.Length + 2, outputs.Count);
            Assert.Equal(prior, outputs["output.pdf"]);
            Assert.Equal(prior, outputs["output.json"]);
            foreach (string part in parts) { Assert.True(outputs[part].AsSpan().StartsWith("%PDF-"u8)); }
        }
        else
        {
            Assert.Equal(2, outputs.Count);
            Assert.Equal(prior, outputs["output.json"]);
            Assert.True(outputs["output.pdf"].AsSpan().StartsWith("%PDF-"u8));
        }
    }
}

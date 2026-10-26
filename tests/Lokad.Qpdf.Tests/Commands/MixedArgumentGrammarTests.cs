using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class MixedArgumentGrammarTests
{
    private static readonly string[] Targets = ["output.pdf", "output.json", "output-1.pdf", "output-2.pdf", "output-3.pdf", "output-1-2.pdf"];

    private static byte[] Input() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null),
        (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null),
        (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null)], 1);

    public static IEnumerable<object[]> Cases()
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
        foreach (int position in new[] { 0, 1, 2 })
        foreach (bool separateFile in new[] { false, true })
        { yield return [item.Name, item.Arguments, item.Valid, position, separateFile]; }
    }

    private static async Task<(QpdfExecutionResult Result, Dictionary<string, byte[]> Outputs)> Execute(string[] arguments)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("empty.json"), "{}"u8.ToArray(), QpdfInputOwnership.Transfer);
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
    [MemberData(nameof(Cases))]
    public async Task EmptyPartialFilePreservesGrammarAndOutput(string name, string[] arguments, bool valid, int position, bool separateFile)
    {
        var plain = await Execute(arguments);
        List<string> mixedArguments = new(arguments);
        int at = position == 0 ? 0 : position == 1 ? Array.IndexOf(arguments, "input") + 1 : arguments.Length;
        mixedArguments.InsertRange(at, separateFile ? ["--job-json-file", "empty.json"] : ["--job-json-file=empty.json"]);
        var mixed = await Execute(mixedArguments.ToArray());
        Assert.Equal(valid ? QpdfExitCode.Success : QpdfExitCode.Error, plain.Result.ExitCode);
        Assert.Equal(plain.Result.ExitCode, mixed.Result.ExitCode); Assert.Equal(plain.Result.Status, mixed.Result.Status);
        Assert.Equal(plain.Outputs.Keys, mixed.Outputs.Keys);
        foreach ((string target, byte[] bytes) in plain.Outputs) { Assert.Equal(bytes, mixed.Outputs[target]); }
        if (!valid)
        {
            Assert.Contains(mixed.Result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.InvalidArguments);
            Assert.Equal(new byte[] { 17, 23, 41 }, mixed.Outputs["output.pdf"]);
            Assert.Equal(new byte[] { 17, 23, 41 }, mixed.Outputs["output.json"]);
        }
        else if (name.StartsWith("json", StringComparison.Ordinal))
        {
            using JsonDocument json = JsonDocument.Parse(mixed.Outputs["output.json"]);
            if (name.StartsWith("json-output", StringComparison.Ordinal))
            { Assert.Equal(2, json.RootElement.GetProperty("qpdf")[0].GetProperty("jsonversion").GetInt32()); }
            else { Assert.Equal(name == "json-bare" ? 2 : 1, json.RootElement.GetProperty("version").GetInt32()); }
        }
    }
}

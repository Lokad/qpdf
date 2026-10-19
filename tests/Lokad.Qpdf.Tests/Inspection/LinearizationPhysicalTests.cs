using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

/// <summary>Preserved complete public jobs, independent of the current writer.</summary>
public sealed class LinearizationPhysicalTests
{
    private static readonly JsonObject Corpus = Load();

    private static JsonObject Load()
    {
        using Stream stream = typeof(LinearizationPhysicalTests).Assembly.GetManifestResourceStream(
            "Lokad.Qpdf.Tests.Inspection.LinearizationCheckCases.json") ?? throw new InvalidOperationException();
        return Assert.IsType<JsonObject>(JsonNode.Parse(stream));
    }

    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? throw new InvalidOperationException();

    public static IEnumerable<object[]> Cases()
    {
        foreach (JsonObject row in Assert.IsType<JsonArray>(Corpus["Cases"]).OfType<JsonObject>())
        {
            string[] routes = row["Routes"] is JsonArray selected
                ? selected.Select(Text).ToArray()
                : ["typed", "argv-check", "job-check", "argv-linearization", "job-linearization", "argv-show", "job-show"];
            foreach (string route in routes)
            {
                yield return [Text(row["Id"]), route];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PreservedInvalidHintsRejectAndValidPhysicalLayoutsPass(string id, string route)
    {
        JsonObject row = Assert.IsType<JsonArray>(Corpus["Cases"]).OfType<JsonObject>().Single(row => Text(row["Id"]) == id);
        string digest = Text(row["SHA256"]);
        byte[] input = Convert.FromBase64String(Text(Corpus["Files"]?[digest]));
        Assert.Equal(digest, Convert.ToHexString(SHA256.HashData(input)));
        bool valid = Text(row["Shape"]) != "invalid";
        InMemoryQpdfHost host = new();
        QpdfPath inputPath = QpdfPath.From("inputs/a.pdf");
        QpdfPath outputPath = QpdfPath.From("outputs/a.pdf");
        host.AddInput(inputPath, input, QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(outputPath, CancellationToken.None))
        {
            await prior.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }

        if (route == "typed")
        {
            QpdfCheckResult result = await Qpdf.CheckAsync(host, inputPath, CancellationToken.None);
            QpdfCheckOutcome check = Assert.Single(result.Checks, outcome => outcome.Name == "linearization");
            Assert.Equal(valid ? QpdfCheckStatus.Passed : QpdfCheckStatus.Failed, check.Status);
            if (row["ReasonContains"] is JsonValue reason)
            {
                Assert.Contains(reason.GetValue<string>(), check.Detail, StringComparison.Ordinal);
            }
            Assert.Equal(valid, result.Passed);
            Assert.Equal(0L, result.Counters.OpenHandles);
        }
        else
        {
            string key = route.EndsWith("show", StringComparison.Ordinal) ? "showLinearization"
                : route.EndsWith("linearization", StringComparison.Ordinal) ? "checkLinearization" : "check";
            string flag = key == "showLinearization" ? "--show-linearization"
                : key == "checkLinearization" ? "--check-linearization" : "--check";
            QpdfExecutionResult result = route.StartsWith("job", StringComparison.Ordinal)
                ? await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"inputs/a.pdf\",\"" + key + "\":\"\"}", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", flag]), CancellationToken.None);
            Assert.Equal(valid ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(valid ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
            Assert.Equal(!valid, result.Diagnostics.Any(d => d.Code == QpdfDiagnosticCode.CheckFailed));
            if (row["ReasonContains"] is JsonValue reason)
            {
                Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.CheckFailed
                    && d.Message.Contains(reason.GetValue<string>(), StringComparison.Ordinal));
            }
            if (key == "showLinearization")
            {
                // Invalidity must not erase the original raw field report.
                Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.LinearizationReport
                    && d.Message == "Computed Linearization Part Assignments");
                Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.LinearizationReport
                    && d.Message == "Page Offsets Hint Table");
            }
            Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(outputPath, out byte[] target));
        Assert.Equal(new byte[] { 17, 23, 41 }, target);
    }
}

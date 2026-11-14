using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ObjectStreamPreparationAdmissionTests
{
    private static bool Split(string mode) => mode.StartsWith("os-split", StringComparison.Ordinal);

    private static async Task<InMemoryQpdfHost> Storage(string mode)
    {
        byte[] input = ObjectStreamPlanningTests.Fixture(mode.Contains("qdf", StringComparison.Ordinal) ? 16 : 201);
        if (mode.StartsWith("os-preserve", StringComparison.Ordinal))
        {
            using InMemoryQpdfHost seed = new();
            seed.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
            QpdfExecutionResult generated = await Qpdf.ExecuteAsync(seed, new(["input", "--object-streams=generate", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, generated.Status);
            Assert.True(seed.TryGetOutput(QpdfPath.From("output"), out input));
        }
        return await PreparationAdmissionChecks.StorageAsync(input, QpdfPath.From(Split(mode) ? "output-1" : "output"));
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string mode, CancellationToken token)
    {
        string operation = mode.Replace("-job", "", StringComparison.Ordinal);
        bool preserve = operation == "os-preserve", qdf = operation.Contains("qdf", StringComparison.Ordinal);
        if (mode.EndsWith("-job", StringComparison.Ordinal))
        {
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["objectStreams"] = preserve ? "preserve" : "generate" };
            if (qdf) { settings.Add("qdf", ""); }
            if (operation.Contains("noids", StringComparison.Ordinal)) { settings.Add("noOriginalObjectIds", ""); }
            if (Split(operation)) { settings.Add("splitPages", "1"); }
            if (operation == "os-linearized") { settings.Add("linearize", true); }
            return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), token);
        }
        List<string> arguments = ["input", preserve ? "--object-streams=preserve" : "--object-streams=generate"];
        if (qdf) { arguments.Add("--qdf"); }
        if (operation.Contains("noids", StringComparison.Ordinal)) { arguments.Add("--no-original-object-ids"); }
        if (Split(operation)) { arguments.Add("--split-pages=1"); }
        if (operation == "os-linearized") { arguments.Add("--linearize"); }
        arguments.Add("output");
        return Qpdf.ExecuteAsync(host, new(arguments), token);
    }

    [Theory]
    [InlineData("os-qdf")]
    [InlineData("os-qdf-job")]
    [InlineData("os-qdf-noids")]
    [InlineData("os-qdf-noids-job")]
    [InlineData("os-split-qdf")]
    [InlineData("os-split-qdf-job")]
    [InlineData("os-split-qdf-noids")]
    [InlineData("os-split-qdf-noids-job")]
    [InlineData("os-generate")]
    [InlineData("os-generate-job")]
    [InlineData("os-preserve")]
    [InlineData("os-preserve-job")]
    [InlineData("os-split")]
    [InlineData("os-split-job")]
    [InlineData("os-linearized")]
    [InlineData("os-linearized-job")]
    public Task PreparedObjectStreamsRefundOwnersAndRespectPublication(string mode)
        => PreparationAdmissionChecks.VerifyAsync(() => Storage(mode), (host, token) => Run(host, mode, token),
            QpdfPath.From(Split(mode) ? "output-1" : "output"), mode.Contains("qdf", StringComparison.Ordinal) ? 128 : 96,
            mode.EndsWith("-job", StringComparison.Ordinal) ? [] : ["write"]);
}

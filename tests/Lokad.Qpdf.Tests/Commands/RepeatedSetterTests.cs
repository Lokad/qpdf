using System.Globalization;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Jobs;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class RepeatedSetterTests
{
    // Repetition is a planning contract. Content/layout suites own the behavior
    // of each enabled setting; representative public/native jobs own route wiring.
    [Theory]
    [InlineData("flatten-rotation", "", "", "True")]
    [InlineData("qdf", "", "", "True")]
    [InlineData("no-original-object-ids", "", "", "True")]
    [InlineData("optimize-images", "", "", "True")]
    [InlineData("jpeg-quality", "5", "6", "6")]
    [InlineData("oi-min-width", "1", "2", "2")]
    [InlineData("keep-inline-images", "", "", "True")]
    [InlineData("externalize-inline-images", "", "", "True")]
    [InlineData("object-streams", "disable", "generate", "generate")]
    [InlineData("remove-metadata", "", "", "True")]
    [InlineData("remove-info", "", "", "True")]
    [InlineData("normalize-content", "y", "n", "False")]
    [InlineData("coalesce-contents", "", "", "True")]
    [InlineData("preserve-unreferenced", "", "", "True")]
    [InlineData("generate-appearances", "", "", "True")]
    [InlineData("min-version", "1.5", "1.6", "1.6")]
    [InlineData("static-id", "", "", "True")]
    [InlineData("deterministic-id", "", "", "True")]
    [InlineData("newline-before-endstream", "", "", "True")]
    [InlineData("force-version", "1.5", "1.6", "1.6")]
    [InlineData("json-stream-prefix", "a", "b", "b")]
    [InlineData("json-stream-data", "inline", "file", "file")]
    public async Task OrderedScalarSettersKeepTheirLastValue(string name, string first, string last, string expected)
    {
        using InMemoryQpdfHost host = new();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        List<string> arguments = ["input"];
        if (name.StartsWith("json-", StringComparison.Ordinal)) { arguments.Add("--json=2"); }
        if (name == "no-original-object-ids") { arguments.Add("--qdf"); }
        arguments.Add("--" + name + (first.Length == 0 ? "" : "=" + first));
        arguments.Add("--" + name + (last.Length == 0 ? "" : "=" + last));
        arguments.Add("output");
        using (JobJsonLoader.LoadedJob loaded = await JobJsonLoader.ParseArgvAsync(host, arguments, QpdfLimits.Default, governor, CancellationToken.None))
        {
            JobPlan plan = loaded.Plan;
            object? value = name switch
            {
                "flatten-rotation" => plan.FlattenRotation,
                "qdf" => plan.Qdf,
                "no-original-object-ids" => plan.NoOriginalObjectIds,
                "optimize-images" => plan.OptimizeImages,
                "jpeg-quality" => plan.JpegQuality,
                "oi-min-width" => plan.OiMinWidth,
                "keep-inline-images" => plan.KeepInlineImages,
                "externalize-inline-images" => plan.ExternalizeInlineImages,
                "object-streams" => plan.ObjectStreams,
                "remove-metadata" => plan.RemoveMetadata,
                "remove-info" => plan.RemoveInfo,
                "normalize-content" => plan.NormalizeContent,
                "coalesce-contents" => plan.CoalesceContents,
                "preserve-unreferenced" => plan.PreserveUnreferenced,
                "generate-appearances" => plan.GenerateAppearances,
                "min-version" => plan.MinVersion,
                "static-id" => plan.StaticId,
                // This documented compatibility switch is a validated no-op;
                // writer controls separately qualify its stable output IDs.
                "deterministic-id" => plan.OutputEncryption is null,
                "newline-before-endstream" => plan.NewlineBeforeEndstream,
                "force-version" => plan.ForceVersion,
                "json-stream-prefix" => Assert.IsType<JsonPlan>(plan).StreamPrefix,
                "json-stream-data" => Assert.IsType<JsonPlan>(plan).StreamDataMode,
                _ => throw new InvalidOperationException("Unknown setter case."),
            };
            Assert.Equal(expected, Convert.ToString(value, CultureInfo.InvariantCulture));
            Assert.Equal(0L, governor.Snapshot().HostOperations);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

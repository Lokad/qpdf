using System.Text.Json;

namespace Lokad.Qpdf.Benchmarks;

internal sealed record ComparisonWorkload(
    string Name, string[] Inputs, string[] Arguments, string[] Outputs, string[] ExpectedMarkers);

internal static class ComparisonWorkloads
{
    internal static ComparisonWorkload[] Create()
    {
        string[] policy = ["--stream-data=preserve", "--decode-level=none", "--compress-streams=n",
            "--object-streams=disable", "--force-version=1.5", "--static-id", "--remove-unreferenced-resources=no"];
        ComparisonWorkload Select(string name, string input, int count, string series) =>
            new(name, [input], ["inputs/" + input, ..policy, "--pages", ".", count + "-1", "--", "outputs/out.pdf"],
                ["out.pdf"], Markers(series, count).Reverse().ToArray());
        ComparisonWorkload Rewrite(string name, string input, string series, int count, string[] extra) =>
            new(name, [input], ["inputs/" + input, ..policy, ..extra, "outputs/out.pdf"], ["out.pdf"], Markers(series, count));
        return
        [
            Select("select-S10", "S10.pdf", 10, "S10"),
            Select("select-M500", "M500.pdf", 500, "M500"),
            new("merge-250x2", ["A250.pdf", "B250.pdf"],
                ["inputs/A250.pdf", ..policy, "--pages", ".", "1-z", "inputs/B250.pdf", "1-z", "--", "outputs/out.pdf"],
                ["out.pdf"], [..Markers("A250", 250), ..Markers("B250", 250)]),
            new("split-M500", ["M500.pdf"],
                ["inputs/M500.pdf", ..policy, "--split-pages=50", "--pages", ".", "1-z", "--", "outputs/out.pdf"],
                Enumerable.Range(0, 10).Select(i => "out-" + (1 + (i * 50)).ToString("D3") + "-" + ((i + 1) * 50).ToString("D3") + ".pdf").ToArray(),
                Markers("M500", 500)),
            Rewrite("rewrite-M500", "M500.pdf", "M500", 500, []),
            Rewrite("rewrite-X100", "X100-incremental.pdf", "X100", 100, []),
            Rewrite("object-streams-X100", "X100.pdf", "X100", 100, ["--object-streams=generate"]),
            Rewrite("linearize-M500", "M500.pdf", "M500", 500, ["--linearize"]),
            new("check-X100", ["X100-incremental.pdf"],
                ["inputs/X100-incremental.pdf", "--check"], [], Markers("X100", 100)),
        ];
    }

    internal static int Generate(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "inputs"));
        foreach ((string series, int count, int bytes) in new[]
        {
            ("S10", 10, 128), ("M500", 500, 10 * 1024), ("A250", 250, 10 * 1024),
            ("B250", 250, 10 * 1024), ("X100", 100, 512),
        })
        {
            File.WriteAllBytes(Path.Combine(directory, "inputs", series + ".pdf"),
                PdfScenarioBuilder.BuildComparisonDocument(count, bytes, series));
        }
        File.WriteAllText(Path.Combine(directory, "workloads.json"),
            JsonSerializer.Serialize(Create(), new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Generated five classic files and nine workloads; prepare X100-incremental.pdf with the Linux comparison script.");
        return 0;
    }

    private static string[] Markers(string series, int count) =>
        Enumerable.Range(1, count).Select(page => series + "-page-" + page.ToString("D4")).ToArray();
}

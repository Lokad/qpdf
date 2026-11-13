using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

internal static class DiagnosticTestJobs
{
    // Preserve the original complete fifty-page characterization fixture.
    internal static byte[] WarningPdf()
        => BuildWarningPdf(null, false);

    internal static byte[] StreamWarningPdf(bool compressed)
    {
        byte[] body = "q Q\n"u8.ToArray();
        if (compressed)
        {
            using MemoryStream buffer = new();
            using (ZLibStream encoder = new(buffer, CompressionLevel.Optimal, true)) { encoder.Write(body); }
            body = buffer.ToArray();
        }
        return BuildWarningPdf(body, compressed);
    }

    private static byte[] BuildWarningPdf(byte[]? body, bool compressed)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 50 /Kids [" + string.Join(" ", Enumerable.Range(3, 50).Select(x => x + " 0 R")) + "] >>", null),
        ];
        for (int index = 0; index < 50; index++)
        {
            string key = string.Concat(Enumerable.Range(0, 80).Select(j => "#" + (128 + (index + j) % 128).ToString("X2")));
            objects.Add((3 + index, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10]"
                + (body is null ? "" : " /Contents 53 0 R")
                + " /Resources << /" + key + " 1 /" + key + " 2 >> >>", null));
        }
        if (body is not null)
        { objects.Add((53, "<< /Length " + body.Length + (compressed ? " /Filter /FlateDecode" : "") + " >>", body)); }
        return CheckTests.Assemble(objects, 1);
    }

    internal static async Task<QpdfExecutionResult> RunAsync(RecordingJobHost host, string route, bool summaries, bool noWarn,
        string mode, QpdfLimits limits, CancellationToken token)
    {
        if (route == "job")
        {
            Dictionary<string, object> job = new()
            {
                ["inputFile"] = "input.pdf",
                ["json"] = "2",
                ["testJsonSchema"] = "",
            };
            if (mode != "json-inline") { job.Add("outputFile", "output.pdf"); }
            if (mode == "pages") { job.Add("jsonKey", new[] { "pages" }); }
            else
            {
                job.Add("jsonStreamData", mode == "json-sidecar" ? "file" : "inline");
                if (mode == "json-sidecar") { job.Add("jsonStreamPrefix", "side"); }
            }
            if (summaries) { job.Add("verbose", ""); job.Add("progress", ""); job.Add("reportMemoryUsage", ""); }
            if (noWarn) { job.Add("noWarn", ""); }
            return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new QpdfExecutionOptions(limits), token);
        }
        List<string> argv = ["input.pdf", "--json=2", "--test-json-schema"];
        if (mode == "pages") { argv.Add("--json-key=pages"); }
        else
        {
            argv.Add("--json-stream-data=" + (mode == "json-sidecar" ? "file" : "inline"));
            if (mode == "json-sidecar") { argv.Add("--json-stream-prefix=side"); }
        }
        if (summaries) { argv.AddRange(["--verbose", "--progress", "--report-memory-usage"]); }
        if (noWarn) { argv.Add("--no-warn"); }
        if (mode != "json-inline") { argv.AddRange(["--", "output.pdf"]); }
        if (route == "mixed") { argv.Insert(1, "--job-json-file=settings.json"); }
        if (route == "response")
        {
            host.Inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy);
            argv = ["@args.txt"];
        }
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), new QpdfExecutionOptions(limits), token);
    }

}

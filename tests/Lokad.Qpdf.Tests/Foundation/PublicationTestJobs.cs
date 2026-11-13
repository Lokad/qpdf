using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

internal static class PublicationTestJobs
{
    internal static readonly byte[] Prior = [17, 23, 41];
    internal static readonly string[] Targets = ["output.pdf", "output-1.pdf", "output-2.pdf", "side-4", "pass1.pdf"];

    private static byte[] Source(bool compressed, bool warning)
    {
        byte[] body = "q Q\n"u8.ToArray();
        if (compressed)
        {
            using MemoryStream buffer = new();
            using (ZLibStream encoder = new(buffer, CompressionLevel.Optimal, true)) { encoder.Write(body); }
            body = buffer.ToArray();
        }
        List<(int Number, string Dict, byte[]? StreamData)> objects = CheckTests.SinglePage(
            "<< /Length " + body.Length + (compressed ? " /Filter /FlateDecode" : "") + " >>", body);
        if (warning) { objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Duplicate 1 /Duplicate 2 >>", null); }
        return CheckTests.Assemble(objects, 1);
    }

    internal static async Task<InMemoryQpdfHost> PrepareAsync(string operation, bool compressed, bool warning)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input.pdf"), Source(compressed, warning), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("second.pdf"), Source(compressed, false), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("settings.json"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        if (operation is "json-import" or "json-update")
        {
            QpdfJsonResult json = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input.pdf"),
                QpdfPath.From("setup.json"), new QpdfJsonOptions(2, [], "inline", null, [], false), CancellationToken.None);
            host.AddInput(QpdfPath.From("input.json"), Encoding.UTF8.GetBytes(json.Json), QpdfInputOwnership.Copy);
        }
        foreach (string target in Targets)
        {
            await using IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From(target), CancellationToken.None);
            await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        }
        return host;
    }

    internal static async Task<QpdfExecutionResult> ExecuteAsync(IQpdfHost host, InMemoryQpdfHost storage, string route, string operation, string flags,
        QpdfLimits limits, CancellationToken token)
    {
        List<string> argv = [operation == "empty" ? "--empty" : operation == "json-import" ? "input.json" : "input.pdf"];
        Dictionary<string, object> job = new(StringComparer.Ordinal);
        if (operation == "empty") { job.Add("empty", ""); }
        else { job.Add("inputFile", operation == "json-import" ? "input.json" : "input.pdf"); }
        foreach (string flag in flags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            argv.Add("--" + flag);
            job.Add(flag switch
            {
                "report-memory-usage" => "reportMemoryUsage",
                "no-warn" => "noWarn",
                "warning-exit-0" => "warningExit0",
                _ => flag
            }, "");
        }
        switch (operation)
        {
            case "empty":
                argv.AddRange(["--pages", "input.pdf", "1", "--"]);
                job.Add("pages", new[] { new { file = "input.pdf", range = "1" } }); break;
            case "linearized": argv.Add("--linearize"); job.Add("linearize", ""); break;
            case "qdf": argv.Add("--qdf"); job.Add("qdf", ""); break;
            case "pages":
                argv.AddRange(["--pages", ".", "1", "--"]);
                job.Add("pages", new[] { new { file = ".", range = "1" } }); break;
            case "merge":
                argv.AddRange(["--pages", ".", "1", "second.pdf", "1", "--"]);
                job.Add("pages", new[] { new { file = ".", range = "1" }, new { file = "second.pdf", range = "1" } }); break;
            case "split":
            case "split-linearized":
            case "split-qdf":
            case "split-streamed":
            case "split-pass1":
                if (operation is "split-linearized" or "split-pass1") { argv.Add("--linearize"); job.Add("linearize", ""); }
                if (operation == "split-qdf") { argv.Add("--qdf"); job.Add("qdf", ""); }
                if (operation == "split-streamed") { argv.Add("--object-streams=generate"); job.Add("objectStreams", "generate"); }
                if (operation == "split-pass1") { argv.Add("--linearize-pass1=pass1.pdf"); job.Add("linearizePass1", "pass1.pdf"); }
                argv.AddRange(["--split-pages=1", "--pages", ".", "1", ".", "1", "--"]);
                job.Add("splitPages", "1");
                job.Add("pages", new[] { new { file = ".", range = "1" }, new { file = ".", range = "1" } }); break;
            case "json-file":
            case "json-inline":
            case "json-sidecar":
                argv.AddRange(["--json=2", "--json-stream-data=" + (operation == "json-sidecar" ? "file" : "inline")]);
                job.Add("json", "2"); job.Add("jsonStreamData", operation == "json-sidecar" ? "file" : "inline");
                if (operation == "json-sidecar") { argv.Add("--json-stream-prefix=side"); job.Add("jsonStreamPrefix", "side"); }
                if (operation != "json-inline") { argv.Add("--"); }
                break;
            case "json-import": argv.AddRange(["--json-input", "--"]); job.Add("jsonInput", ""); break;
            case "json-update": argv.AddRange(["--update-from-json=input.json", "--"]); job.Add("updateFromJson", "input.json"); break;
            case "check": argv.Add("--check"); job.Add("check", ""); break;
            case "show-count": argv.Add("--show-npages"); job.Add("showNpages", ""); break;
            case "filtered":
                argv.AddRange(["--show-object=4", "--filtered-stream-data"]);
                job.Add("showObject", "4"); job.Add("filteredStreamData", ""); break;
        }
        if (operation is not ("check" or "show-count" or "filtered" or "json-inline"))
        { argv.Add("output.pdf"); job.Add("outputFile", "output.pdf"); }
        QpdfExecutionOptions options = new(limits);
        if (route == "job") { return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), options, token); }
        if (route == "mixed") { argv.Insert(1, "--job-json-file=settings.json"); }
        if (route == "response")
        {
            storage.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy);
            argv = ["@args.txt"];
        }
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), options, token);
    }
}

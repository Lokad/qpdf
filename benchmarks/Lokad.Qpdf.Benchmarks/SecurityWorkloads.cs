#if !QPDF_MEMORY_BASELINE
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Benchmarks;

internal sealed record SecurityWorkload(string Name, byte[] Input, string[] Arguments,
    QpdfExitCode ExpectedExitCode, int Revision, int Pages, int OutputPages);

// Prepare randomized encryption once, outside measurement, and reuse the same
// bytes in every worker. Public credentials and hashes make each run reproducible.
internal static class SecurityWorkloads
{
    internal static readonly string[] Profiles = ["plain", "r4", "r6"];
    internal static readonly string[] Operations = ["auth", "open", "rewrite", "select"];
    private const string Password = "reader";
    private static readonly string[] WriterOptions =
        ["--stream-data=preserve", "--compress-streams=n", "--object-streams=disable", "--remove-unreferenced-resources=no"];

    internal static async Task<int> GenerateAsync(string directory)
    {
        if (Directory.Exists(directory) || File.Exists(directory)) { throw new ArgumentException("Security input directory must be fresh."); }
        Directory.CreateDirectory(directory);
        using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("security-r4-ascii.pdf")
            ?? throw new InvalidOperationException("Missing recorded R4 template.");
        using MemoryStream template = new(); resource.CopyTo(template);
        List<object> manifest = [];
        foreach ((string size, int pages, int bytes) in new[] { ("small", 1, 128), ("bulk", 500, 10240) })
        {
            byte[] plain = PdfScenarioBuilder.BuildComparisonDocument(pages, bytes, "SECURITY");
            foreach (string profile in Profiles)
            {
                byte[] input = plain;
                if (profile != "plain")
                {
                    using InMemoryQpdfHost host = new();
                    host.AddInput(QpdfPath.From("plain"), plain, QpdfInputOwnership.Copy);
                    host.AddInput(QpdfPath.From("template"), template.ToArray(), QpdfInputOwnership.Transfer);
                    string[] protection = profile == "r4"
                        ? ["--copy-encryption=template", "--encryption-file-password=" + Password]
                        : ["--encrypt", Password, "fixture-owner", "256", "--"];
                    QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
                        new(["plain", "protected", .. WriterOptions, .. protection]), CancellationToken.None);
                    Require(result, QpdfExitCode.Success, profile);
                    if (!host.TryGetOutput(QpdfPath.From("protected"), out input)) { throw new InvalidOperationException("Missing encrypted input."); }
                }
                string file = profile + "-" + size + ".pdf";
                File.WriteAllBytes(Path.Combine(directory, file), input);
                manifest.Add(new { file, pages, revision = Revision(profile), bytes = input.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(input)) });
            }
        }
        File.WriteAllText(Path.Combine(directory, "inputs.json"), JsonSerializer.Serialize(new
        {
            userPassword = Password, ownerPassword = "fixture-owner", inputs = manifest,
            note = "Generated once with public operations; R4 copies the recorded template. Retain these exact files across workers.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (string profile in Profiles)
        foreach (string operation in Operations)
        { await ValidateAsync(Load(directory, "security-" + profile + "-" + operation)); }
        Console.WriteLine("Prepared and validated six inputs and twelve public security workloads.");
        return 0;
    }

    internal static SecurityWorkload Load(string name)
        => Load(Environment.GetEnvironmentVariable("QPDF_SECURITY_ROOT")
            ?? throw new InvalidOperationException("Set QPDF_SECURITY_ROOT to the prepared security input directory."), name);

    private static SecurityWorkload Load(string directory, string name)
    {
        string[] parts = name.Split('-');
        if (parts.Length is not (3 or 4) || parts[0] != "security" || !Profiles.Contains(parts[1]) || !Operations.Contains(parts[2])
            || parts.Length == 4 && parts[3] is not ("small" or "bulk"))
        { throw new ArgumentException("Unknown security workload: " + name); }
        string profile = parts[1], operation = parts[2];
        string size = parts.Length == 4 ? parts[3] : operation == "auth" ? "small" : "bulk";
        if (size == "small" && operation == "select") { throw new ArgumentException("Selection workload requires the 500-page input."); }
        int pages = size == "small" ? 1 : 500;
        byte[] input = File.ReadAllBytes(Path.Combine(directory, profile + "-" + size + ".pdf"));
        string[] credential = profile == "plain" ? [] : ["--password=" + Password];
        string[] arguments = operation switch
        {
            "auth" => ["input0", .. credential, "--requires-password"],
            "open" => ["input0", .. credential, "--show-npages"],
            "rewrite" => ["input0", .. credential, .. WriterOptions, "output"],
            _ => ["input0", .. credential, .. WriterOptions, "--pages", ".", "1,250,500", "--", "output"],
        };
        QpdfExitCode exit = operation == "auth" ? (QpdfExitCode)(profile == "plain" ? 2 : 3) : QpdfExitCode.Success;
        return new(name, input, arguments, exit, Revision(profile), pages,
            operation == "select" ? 3 : operation == "rewrite" ? pages : 0);
    }

    internal static async Task ExecuteAsync(SecurityWorkload workload)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input0"), workload.Input, QpdfInputOwnership.Copy);
        Require(await Qpdf.ExecuteAsync(host, new(workload.Arguments), CancellationToken.None), workload.ExpectedExitCode, workload.Name);
        if (host.OpenLeaseCount != 0) { throw new InvalidOperationException("Security workload leaked an input lease."); }
    }

    internal static async Task ValidateAsync(SecurityWorkload workload)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input0"), workload.Input, QpdfInputOwnership.Copy);
        QpdfPassword? password = workload.Revision == 0 ? null : QpdfPassword.FromText(Password);
        await using (QpdfDocument input = await QpdfDocument.OpenAsync(host, QpdfPath.From("input0"), password, QpdfLimits.Default, CancellationToken.None))
        {
            if (input.PageCount != workload.Pages || (input.Encryption?.Revision ?? 0) != workload.Revision)
            { throw new InvalidOperationException("Security input differs from its declared scenario."); }
        }
        Require(await Qpdf.ExecuteAsync(host, new(workload.Arguments), CancellationToken.None), workload.ExpectedExitCode, workload.Name);
        if (workload.OutputPages != 0)
        {
            if (!host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)) { throw new InvalidOperationException("Missing benchmark output."); }
            host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), password, QpdfLimits.Default, CancellationToken.None);
            if (output.PageCount != workload.OutputPages || (output.Encryption?.Revision ?? 0) != workload.Revision)
            { throw new InvalidOperationException("Security output changed page count or protection."); }
            string[] credential = workload.Revision == 0 ? [] : ["--password=" + Password];
            Require(await Qpdf.ExecuteAsync(host, new(["read", "plain-output", .. credential, "--decrypt", .. WriterOptions]),
                CancellationToken.None), QpdfExitCode.Success, workload.Name + " validation");
            if (!host.TryGetOutput(QpdfPath.From("plain-output"), out byte[] clear)) { throw new InvalidOperationException("Missing plaintext validation output."); }
            foreach (int page in workload.OutputPages == 1 ? new[] { 1 } : new[] { 1, 250, 500 })
            {
                if (clear.AsSpan().IndexOf(Encoding.ASCII.GetBytes("SECURITY-page-" + page.ToString("D4"))) < 0)
                { throw new InvalidOperationException("Benchmark output lost a selected page's content."); }
            }
            if (workload.OutputPages == 3 && clear.AsSpan().IndexOf("SECURITY-page-0002"u8) >= 0)
            { throw new InvalidOperationException("Page-selection benchmark retained an omitted page."); }
        }
    }

    private static int Revision(string profile) => profile == "r4" ? 4 : profile == "r6" ? 6 : 0;

    private static void Require(QpdfExecutionResult result, QpdfExitCode exit, string name)
    {
        if (result.Status != QpdfExecutionStatus.Completed || result.ExitCode != exit
            || result.Diagnostics.Any(diagnostic => diagnostic.Severity != QpdfDiagnosticSeverity.Info))
        { throw new InvalidOperationException(name + " failed: " + string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))); }
    }
}
#endif

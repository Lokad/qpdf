using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedPageModeTests
{
    public static IEnumerable<object[]> WriterCases()
    {
        foreach (string mode in new[] { "direct", "indirect", "null", "integer", "missing", "cycle" })
        foreach (bool job in new[] { false, true })
        foreach (bool compressed in new[] { false, true })
        { yield return [mode, job, compressed]; }
    }

    private static byte[] Input(string mode)
    {
        string entry = mode == "direct" ? "/PageMode /UseOutlines" : mode == "missing" ? "" : "/PageMode 9 0 R";
        string value = mode switch { "null" => "null", "integer" => "7", "cycle" => "9 0 R", _ => "/UseOutlines" };
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R " + entry + " >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 7 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length 4 >>", Encoding.ASCII.GetBytes("q Q\n")),
            (5, "<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>", null),
            (6, "<< /Title (First page) /Parent 5 0 R /Dest [3 0 R /Fit] >>", null),
            (7, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Resources << >> /Contents 8 0 R >>", null),
            (8, "<< /Length 4 >>", Encoding.ASCII.GetBytes("q Q\n")), (9, value, null)], 1);
    }

    [Theory]
    [MemberData(nameof(WriterCases))]
    public async Task ClassifiesResolvedModeAndPlacesOutlines(string mode, bool job, bool compressed)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input(mode), QpdfInputOwnership.Copy);
        string streams = compressed ? "generate" : "disable";
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", linearize = true, objectStreams = streams }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--linearize", "--object-streams=" + streams, "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] pdf));
        if (!compressed)
        {
            // Measure physical positions independently of the shared classifier.
            string text = Encoding.Latin1.GetString(pdf);
            Match end = Regex.Match(text, @"/E\s+(\d+)", RegexOptions.CultureInvariant);
            Assert.True(end.Success);
            int firstEnd = int.Parse(end.Groups[1].Value, CultureInfo.InvariantCulture);
            int outline = text.IndexOf("/Type /Outlines", StringComparison.Ordinal);
            int title = text.IndexOf("/Title", StringComparison.Ordinal);
            Assert.True(outline >= 0 && title >= 0);
            bool firstPage = mode is "direct" or "indirect";
            Assert.Equal(firstPage, outline < firstEnd);
            Assert.Equal(firstPage, title < firstEnd);
        }
        host.AddInput(QpdfPath.From("reread"), pdf, QpdfInputOwnership.Copy);
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("reread"), CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, x => x.Name == "linearization").Status);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("native-valid", true)]
    [InlineData("managed-invalid", false)]
    public async Task CheckerDistinguishesIndependentPhysicalLayouts(string name, bool valid)
    {
        using Stream stream = typeof(LinearizedPageModeTests).Assembly.GetManifestResourceStream(
            "Lokad.Qpdf.Tests.Writing.LinearizedPageModeCases.json") ?? throw new InvalidOperationException();
        using JsonDocument corpus = JsonDocument.Parse(stream);
        JsonElement entry = corpus.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        byte[] pdf = Convert.FromBase64String(entry.GetProperty("pdf").GetString() ?? throw new InvalidOperationException());
        Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(pdf)));
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), pdf, QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--check-linearization"]), CancellationToken.None);
        Assert.Equal(valid ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
        QpdfCheckResult typed = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.Equal(valid ? QpdfCheckStatus.Passed : QpdfCheckStatus.Failed, Assert.Single(typed.Checks, x => x.Name == "linearization").Status);
        Assert.Equal(0, host.OpenLeaseCount);
    }
}

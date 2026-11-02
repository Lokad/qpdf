using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class InvocationNestingTests
{
    private static byte[] Fields(int count)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "<< /Fields [5 0 R] >>", null)
        ];
        for (int i = 0; i < count; i++)
        {
            string parent = i == 0 ? "" : "/Parent " + (i + 4) + " 0 R ";
            string next = i + 1 < count ? "/Kids [" + (i + 6) + " 0 R]" : "/FT /Tx /V (value)";
            objects.Add((i + 5, "<< /T (field" + i + ") " + parent + next + " >>", null));
        }
        return CheckTests.Assemble(objects, 1);
    }

    private static byte[] Viewer(int depth) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /ViewerPreferences << /Audit " + new string('[', depth) + "1" + new string(']', depth) + " >> >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>", null)], 1);

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (bool hostLimit in new[] { false, true })
        foreach ((string kind, int depth, int maximum, bool success) in new[]
        {
            ("fields", 6, 2, false), ("fields", 6, 6, true),
            ("fields", 270, 256, false), ("fields", 270, 499, true),
            ("viewer", 260, 256, false), ("viewer", 260, 499, true)
        }) { yield return [job, hostLimit, kind, depth, maximum, success]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task InvocationAndHostLimitsGovernTheSameWalks(
        bool job, bool hostLimit, string kind, int depth, int maximum, bool success)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), kind == "fields" ? Fields(depth) : Viewer(depth), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionOptions options = hostLimit ? new(QpdfLimits.Default with { MaxNestingDepth = maximum }) : new();
        List<string> argv = ["input", "--suppress-recovery"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["suppressRecovery"] = true };
        if (!hostLimit)
        {
            argv.Add("--parser-max-nesting=" + maximum);
            settings["global"] = new { parserMaxNesting = maximum.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }
        if (kind == "fields") { argv.Add("--check"); settings["check"] = ""; }
        else
        {
            argv.AddRange(["--qdf", "--", "output"]); settings["qdf"] = true; settings["outputFile"] = "output";
        }
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), options, CancellationToken.None);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(result.ExitCode == (success ? QpdfExitCode.Success : QpdfExitCode.Error),
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(success ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
        if (!success) { Assert.Contains(result.Diagnostics, d => d.Message.Contains("nesting", StringComparison.OrdinalIgnoreCase)); }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        if (kind == "viewer" && success) { Assert.True(output.AsSpan().StartsWith("%PDF-"u8)); }
        else { Assert.Equal("prior"u8.ToArray(), output); }
    }
}

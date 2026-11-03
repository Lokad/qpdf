using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentImportConflictTests
{
    private static byte[] Plain() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1);

    private static byte[] Source(string shape, bool second, bool single)
    {
        string extra = shape is "catalog" or "direct" ? "/Private 1 0 R" : shape == "pages" ? "/Private 2 0 R" : "";
        string specification = "<< /Type /Filespec /F (file) /EF << /F 6 0 R >> " + extra + " >>";
        string value = shape == "direct" ? specification : shape == "missing" ? "99 0 R" : shape == "null" ? "null" : "5 0 R";
        string other = single ? "" : " (" + (second ? "Y" : "Z") + ") 7 0 R";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(K) " + value + other + "] >> >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "null", null), (5, specification, null),
            (6, "<< /Type /EmbeddedFile /Length 4 >>", second ? "GOOD"u8.ToArray() : "ORIG"u8.ToArray()),
            (7, "<< /Type /Filespec /F (other) /EF << /F 6 0 R >> >>", null)], 1);
    }

    private static async Task<InMemoryQpdfHost> Host(string shape, bool repeated)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Plain(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("first"), Source(shape, false, repeated), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("second"), Source("valid", true, false), QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
        return host;
    }

    private static async Task Run(InMemoryQpdfHost host, string route, string shape, bool reverse, bool repeated, string prefix)
    {
        string[] files = repeated ? ["first", "first"] : reverse ? ["second", "first"] : ["first", "second"];
        string?[] prefixes = prefix == "none" ? [null, null] : prefix == "different" ? ["x", "y"] : ["x", "x"];
        bool conflict = prefix != "different";
        if (route == "typed")
        {
            QpdfAttachmentCopy[] copies = [new(QpdfPath.From(files[0]), prefixes[0]), new(QpdfPath.From(files[1]), prefixes[1])];
            async Task Copy() => _ = await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), copies, CancellationToken.None);
            if (conflict)
            {
                QpdfParseException failure = await Assert.ThrowsAsync<QpdfParseException>(Copy);
                Assert.Contains("conflict", failure.Message, StringComparison.OrdinalIgnoreCase);
            }
            else { await Copy(); }
        }
        else
        {
            List<string> arguments = ["input", "--no-warn", "--warning-exit-0", "--object-streams=generate"];
            List<Dictionary<string, object>> copies = [];
            for (int index = 0; index < files.Length; index++)
            {
                arguments.AddRange(["--copy-attachments-from", files[index]]);
                Dictionary<string, object> copy = new() { ["file"] = files[index] };
                if (prefixes[index] is string text) { arguments.Add("--prefix=" + text); copy["prefix"] = text; }
                arguments.Add("--"); copies.Add(copy);
            }
            arguments.Add("output");
            QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", noWarn = true, warningExit0 = true, objectStreams = "generate", copyAttachmentsFrom = copies }), CancellationToken.None);
            Assert.Equal(conflict ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(conflict ? QpdfExitCode.Error : QpdfExitCode.Success, result.ExitCode);
            if (conflict) { Assert.Contains(result.Diagnostics, d => d.Severity == QpdfDiagnosticSeverity.Error && d.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase)); }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        if (conflict) { Assert.Equal("prior"u8.ToArray(), output); return; }
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfAttachmentInfo> entries = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("read"), CancellationToken.None);
        bool valid = shape == "valid";
        Assert.Equal(repeated ? valid ? 2 : 0 : valid ? 4 : 3, entries.Count);
        if (!repeated) { Assert.Contains(entries, e => e.Key == "xZ"); Assert.Contains(entries, e => e.Key == "yY"); }
        Assert.Equal(valid, entries.Any(e => e.Key == "xK")); Assert.Equal(repeated ? valid : true, entries.Any(e => e.Key == "yK"));
        foreach (QpdfAttachmentInfo entry in entries)
        {
            await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("read"), entry.Key, QpdfPath.From("payload"), CancellationToken.None);
            Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] data));
            Assert.Equal(entry.Key.StartsWith('x') || repeated ? "ORIG"u8.ToArray() : "GOOD"u8.ToArray(), data);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "catalog", "direct", "pages", "missing", "null", "valid" })
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (bool reverse in new[] { false, true }) { yield return [shape, route, reverse, false, "none"]; }
        foreach (string shape in new[] { "catalog", "missing", "valid" })
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (bool repeated in new[] { false, true })
        foreach (string prefix in new[] { "same", "different" }) { yield return [shape, route, false, repeated, prefix]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ImportConflictIdentitySurvivesCopyFailure(string shape, string route, bool reverse, bool repeated, string prefix)
    {
        using InMemoryQpdfHost host = await Host(shape, repeated);
        await Run(host, route, shape, reverse, repeated, prefix);
    }
}

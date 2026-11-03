using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentConflictIdentityTests
{
    private static byte[] Fixture(string shape)
    {
        string spec = "<< /Type /Filespec /F (file) /EF << /F 6 0 R >> /Bad 1 0 R >>";
        string entry = shape == "direct" ? spec : shape == "missing" ? "99 0 R" : "5 0 R";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "<< /Names [(key) " + entry + " (unrelated) 7 0 R] >>", null),
            (5, spec, null), (6, "<< /Type /EmbeddedFile /Length 9 >>", "OLD-BYTES"u8.ToArray()),
            (7, "<< /Type /Filespec /F (file) /EF << /F 6 0 R >> >>", null)], 1);
    }

    private static async Task<InMemoryQpdfHost> Host(string shape)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(shape), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("payload"), "NEW-BYTES"u8.ToArray(), QpdfInputOwnership.Transfer);
        // Import a valid key independently of the damaged primary specification.
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(key) 4 0 R] >> >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "<< /Type /Filespec /F (file) /EF << /F 5 0 R >> >>", null),
            (5, "<< /Type /EmbeddedFile /Length 9 >>", "NEW-BYTES"u8.ToArray())], 1);
        host.AddInput(QpdfPath.From("source"), source, QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
        return host;
    }

    public static IEnumerable<object[]> Collisions()
    {
        foreach (string shape in new[] { "reference", "direct", "missing" })
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (bool copy in new[] { false, true })
        foreach (bool quiet in new[] { false, true })
        { if (route != "typed" || !quiet) { yield return [shape, route, copy, quiet]; } }
    }

    [Theory]
    [MemberData(nameof(Collisions))]
    public async Task OriginalKeyConflictsSurviveRecoverableCopyFailures(string shape, string route, bool copy, bool quiet)
    {
        using InMemoryQpdfHost host = await Host(shape);
        if (route == "typed")
        {
            QpdfParseException failure = await Assert.ThrowsAsync<QpdfParseException>(async () =>
            {
                if (copy) { await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), [new(QpdfPath.From("source"), null)], CancellationToken.None); }
                else { await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), [new("payload", "key", null, null, null, null, null, false)], CancellationToken.None); }
            });
            Assert.Contains("conflict", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            QpdfExecutionResult result = await Run(host, route, copy ? "copy" : "add", quiet);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, d => d.Severity == QpdfDiagnosticSeverity.Error && d.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase));
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string route, string operation, bool quiet)
    {
        List<string> args = ["input", "--object-streams=generate"];
        Dictionary<string, object> job = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["objectStreams"] = "generate" };
        if (operation is "copy" or "prefix")
        {
            args.AddRange(["--copy-attachments-from", "source"]); if (operation == "prefix") { args.Add("--prefix=x"); } args.Add("--");
            Dictionary<string, object> source = new() { ["file"] = "source" }; if (operation == "prefix") { source["prefix"] = "x"; }
            job["copyAttachmentsFrom"] = new[] { source };
        }
        else if (operation != "rewrite")
        {
            string key = operation == "different" ? "different" : "key";
            args.AddRange(["--add-attachment", "payload", "--key=" + key]); if (operation == "replace") { args.Add("--replace"); } args.Add("--");
            Dictionary<string, object> add = new() { ["file"] = "payload", ["key"] = key }; if (operation == "replace") { add["replace"] = true; }
            job["addAttachment"] = new[] { add };
            if (operation == "remove-add") { args.Add("--remove-attachment=key"); job["removeAttachment"] = new[] { "key" }; }
        }
        if (quiet) { args.AddRange(["--no-warn", "--warning-exit-0"]); job["noWarn"] = true; job["warningExit0"] = true; }
        args.Add("output");
        return route == "argv" ? Qpdf.ExecuteAsync(host, new(args), CancellationToken.None) : Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
    }

    public static IEnumerable<object[]> Controls()
    {
        foreach (string shape in new[] { "reference", "direct", "missing" })
        foreach (string route in new[] { "argv", "job" })
        foreach (string operation in new[] { "rewrite", "different", "replace", "remove-add", "prefix" }) { yield return [shape, route, operation]; }
    }

    [Theory]
    [MemberData(nameof(Controls))]
    public async Task SkippedGraphsStayAbsentWhileExplicitEditsAndSharedPayloadsSurvive(string shape, string route, string operation)
    {
        using InMemoryQpdfHost host = await Host(shape);
        QpdfExecutionResult result = await Run(host, route, operation, false); Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfAttachmentInfo> entries = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal(operation == "rewrite" ? 1 : 2, entries.Count); Assert.Contains(entries, e => e.Key == "unrelated");
        Assert.Equal(operation is "replace" or "remove-add", entries.Any(e => e.Key == "key"));
        await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("read"), "unrelated", QpdfPath.From("extracted"), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("extracted"), out byte[] payload)); Assert.Equal("OLD-BYTES"u8.ToArray(), payload);
        if (operation != "rewrite")
        {
            string addedKey = operation == "different" ? "different" : operation == "prefix" ? "xkey" : "key";
            Assert.Contains(entries, e => e.Key == addedKey);
            await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("read"), addedKey, QpdfPath.From("added"), CancellationToken.None);
            Assert.True(host.TryGetOutput(QpdfPath.From("added"), out byte[] added)); Assert.Equal("NEW-BYTES"u8.ToArray(), added);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

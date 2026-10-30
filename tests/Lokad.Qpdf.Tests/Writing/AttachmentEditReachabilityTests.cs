using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentEditReachabilityTests
{
    private static byte[] Fixture(string state, bool indirect, bool shared, int size)
    {
        byte[] payload = Encoding.ASCII.GetBytes("REMOVED-PAYLOAD-" + new string('X', size));
        string page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 5 0 R"
            + (shared ? " /Audit 7 0 R" : "") + " >>";
        string spec = state switch
        {
            "missing-ef" => "<< /Type /Filespec /F (seed) >>",
            "null" => "null",
            _ => "<< /Type /Filespec /F (seed) /EF << /F 7 0 R >> >>",
        };
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 9 0 R >> >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>", null),
            (3, page, null), (4, page, null), (5, "<< /Length 0 >>", []),
            (6, spec, null), (7, "<< /Type /EmbeddedFile /Length " + payload.Length + " >>", payload),
            (8, "<< /Type /Filespec /F (keep) /EF << /F 10 0 R >> >>", null),
            (9, indirect ? "<< /Kids [11 0 R] >>" : "<< /Names [(seed) 6 0 R (keep) 8 0 R] >>", null),
            (10, "<< /Type /EmbeddedFile /Length 4 >>", "KEEP"u8.ToArray()),
            (11, "<< /Names 12 0 R >>", null), (12, "[13 0 R 6 0 R (keep) 8 0 R]", null), (13, "(seed)", null)], 1);
    }

    private static async Task<IReadOnlyList<QpdfDiagnostic>> Run(InMemoryQpdfHost host, string route, string operation,
        QpdfLimits limits, bool preserve)
    {
        QpdfAttachmentSpec[] adds = operation == "replace"
            ? [new("first", "added", null, null, null, null, null, false), new("second", "added", null, null, null, null, null, true)] : [];
        if (route == "typed")
        {
            QpdfAttachmentEditResult result = operation == "remove"
                ? await Qpdf.RemoveAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output.pdf"), ["seed"], limits, CancellationToken.None)
                : await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output.pdf"), adds, limits, CancellationToken.None);
            return result.Warnings;
        }
        List<string> argv = ["input", "--compress-streams=n", "--decode-level=none", "--object-streams=disable"];
        Dictionary<string, object> job = new() { ["inputFile"] = "input", ["outputFile"] = "output.pdf",
            ["compressStreams"] = "n", ["decodeLevel"] = "none", ["objectStreams"] = "disable" };
        if (operation == "remove") { argv.Add("--remove-attachment=seed"); job["removeAttachment"] = new[] { "seed" }; }
        else
        {
            foreach (QpdfAttachmentSpec add in adds)
            {
                argv.AddRange(["--add-attachment", add.File, "--key=added"]);
                if (add.Replace) { argv.Add("--replace"); }
                argv.Add("--");
            }
            job["addAttachment"] = adds.Select(add => new { file = add.File, key = add.Key, replace = add.Replace }).ToArray();
        }
        if (preserve) { argv.Add("--preserve-unreferenced"); job["preserveUnreferenced"] = ""; }
        if (route.EndsWith("-split", StringComparison.Ordinal)) { argv.Add("--split-pages=1"); job["splitPages"] = "1"; }
        argv.Add("output.pdf");
        QpdfExecutionResult executed = route.StartsWith("job", StringComparison.Ordinal)
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new QpdfExecutionOptions(limits), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, executed.Status);
        return executed.Diagnostics;
    }

    private static IEnumerable<string> Outputs(string route)
        => route.EndsWith("-split", StringComparison.Ordinal) ? ["output-1.pdf", "output-2.pdf"] : ["output.pdf"];

    public static IEnumerable<object[]> RemovalCases()
    {
        foreach (string route in new[] { "typed", "argv", "job", "argv-split", "job-split" })
        foreach (string state in new[] { "valid", "missing-ef", "null" })
        foreach (bool indirect in new[] { false, true }) { yield return [route, state, indirect]; }
    }

    [Theory]
    [MemberData(nameof(RemovalCases))]
    public async Task RemovalMatchesKeysBeforeInterpretingTheirValues(string route, string state, bool indirect)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(state, indirect, false, 32), QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfDiagnostic> diagnostics = await Run(host, route, "remove", QpdfLimits.Default, false);
        Assert.Empty(diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        foreach (string output in Outputs(route))
        {
            Assert.True(host.TryGetOutput(QpdfPath.From(output), out byte[] bytes));
            host.AddInput(QpdfPath.From("inspect"), bytes, QpdfInputOwnership.Transfer);
            QpdfAttachmentInfo attachment = Assert.Single(await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("inspect"), CancellationToken.None));
            Assert.Equal("keep", attachment.Key);
            await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("inspect"), "keep", QpdfPath.From("payload"), CancellationToken.None);
            Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] payload)); Assert.Equal("KEEP"u8.ToArray(), payload);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("typed", true)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task MissingAndDuplicateRemovalRequestsPreservePriorOutput(string route, bool duplicate)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("missing-ef", true, false, 32), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output.pdf"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        string[] keys = duplicate ? ["seed", "seed"] : ["missing"];
        if (route == "typed")
        {
            QpdfParseException failure = await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.RemoveAttachmentsAsync(host,
                QpdfPath.From("input"), QpdfPath.From("output.pdf"), keys, CancellationToken.None).AsTask());
            Assert.Contains("requested attachment was not found", failure.Message, StringComparison.Ordinal);
        }
        else
        {
            List<string> argv = ["input", .. keys.Select(key => "--remove-attachment=" + key), "output.pdf"];
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output.pdf", removeAttachment = keys }), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments
                && d.Message.Contains("requested attachment was not found", StringComparison.Ordinal));
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes); Assert.Equal(0L, host.OpenLeaseCount);
    }
}

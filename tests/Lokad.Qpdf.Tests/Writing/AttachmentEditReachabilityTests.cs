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
            (9, indirect ? "<< /Kids [11 0 R] >>" : "<< /Names [(keep) 8 0 R (seed) 6 0 R] >>", null),
            (10, "<< /Type /EmbeddedFile /Length 4 >>", "KEEP"u8.ToArray()),
            (11, "<< /Names 12 0 R >>", null), (12, "[(keep) 8 0 R 13 0 R 6 0 R]", null), (13, "(seed)", null)], 1);
    }

    private static async Task<IReadOnlyList<QpdfDiagnostic>> Run(IQpdfHost host, string route, string operation,
        QpdfLimits limits, bool preserve, string mode)
    {
        QpdfAttachmentSpec[] adds = operation == "replace"
            ? [new("first", "added", null, null, null, null, null, false), new("second", "added", null, null, null, null, null, true)]
            : operation == "replace-seed" ? [new("second", "seed", null, null, null, null, null, true)] : [];
        if (route == "typed")
        {
            QpdfAttachmentEditResult result = operation == "remove"
                ? await Qpdf.RemoveAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output.pdf"), ["seed"], limits, CancellationToken.None)
                : await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output.pdf"), adds, limits, CancellationToken.None);
            return result.Warnings;
        }
        string streams = mode is "generate" or "preserve" ? mode : "disable";
        List<string> argv = ["input", "--compress-streams=n", "--decode-level=none", "--object-streams=" + streams];
        Dictionary<string, object> job = new() { ["inputFile"] = "input", ["outputFile"] = "output.pdf",
            ["compressStreams"] = "n", ["decodeLevel"] = "none", ["objectStreams"] = streams };
        if (operation == "remove") { argv.Add("--remove-attachment=seed"); job["removeAttachment"] = new[] { "seed" }; }
        else
        {
            foreach (QpdfAttachmentSpec add in adds)
            {
                argv.AddRange(["--add-attachment", add.File, "--key=" + add.Key]);
                if (add.Replace) { argv.Add("--replace"); }
                argv.Add("--");
            }
            job["addAttachment"] = adds.Select(add => new { file = add.File, key = add.Key, replace = add.Replace }).ToArray();
        }
        if (preserve) { argv.Add("--preserve-unreferenced"); job["preserveUnreferenced"] = ""; }
        if (mode == "linear") { argv.Add("--linearize"); job["linearize"] = true; }
        if (mode == "qdf") { argv.Add("--qdf"); job["qdf"] = ""; }
        if (route.EndsWith("-split", StringComparison.Ordinal)) { argv.Add("--split-pages=1"); job["splitPages"] = "1"; }
        argv.Add("output.pdf");
        QpdfExecutionResult executed = route.StartsWith("job", StringComparison.Ordinal)
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new QpdfExecutionOptions(limits), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), new QpdfExecutionOptions(limits), CancellationToken.None);
        Assert.True(executed.Status == QpdfExecutionStatus.Completed,
            string.Join("; ", executed.Diagnostics.Select(diagnostic => diagnostic.Message)));
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
        IReadOnlyList<QpdfDiagnostic> diagnostics = await Run(host, route, "remove", QpdfLimits.Default, false, "plain");
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

    public static IEnumerable<object[]> ReachabilityCases()
    {
        foreach (string route in new[] { "typed", "argv", "job", "argv-split", "job-split" })
        foreach (string operation in new[] { "remove", "replace", "replace-seed" })
        foreach (bool shared in new[] { false, true })
        foreach (bool preserve in new[] { false, true })
        { if (route != "typed" || !preserve) { yield return [route, operation, shared, preserve]; } }
    }

    [Theory]
    [MemberData(nameof(ReachabilityCases))]
    public async Task OutputRetainsOnlyLiveAttachmentsUnlessUnreferencedObjectsAreRequested(
        string route, string operation, bool shared, bool preserve)
    {
        QpdfLimits limits = QpdfLimits.Default with
        { MaxOutputBytes = operation != "replace" && !shared && !preserve ? 4096L : 1048576L };
        using InMemoryQpdfHost host = new(limits);
        host.AddInput(QpdfPath.From("input"), Fixture("valid", false, shared, 65536), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("first"), Encoding.ASCII.GetBytes("OLD-ADDED-" + new string('Y', 65536)), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("second"), "NEW"u8.ToArray(), QpdfInputOwnership.Copy);
        Assert.Empty(await Run(host, route, operation, limits, preserve, "plain"));
        Assert.Equal(0L, host.OpenLeaseCount);
        foreach (string path in Outputs(route))
        {
            Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
            Assert.Equal(operation == "replace" || shared || preserve, bytes.AsSpan().IndexOf("REMOVED-PAYLOAD-"u8) >= 0);
            Assert.Equal(operation == "replace" && preserve, bytes.AsSpan().IndexOf("OLD-ADDED-"u8) >= 0);
            host.AddInput(QpdfPath.From("inspect"), bytes, QpdfInputOwnership.Transfer);
            IReadOnlyList<QpdfAttachmentInfo> listed = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("inspect"), CancellationToken.None);
            Assert.Equal(operation == "remove" ? ["keep"] : operation == "replace" ? ["added", "keep", "seed"] : new[] { "keep", "seed" }, listed.Select(a => a.Key));
            if (operation != "remove")
            {
                await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("inspect"), operation == "replace" ? "added" : "seed",
                    QpdfPath.From("payload"), CancellationToken.None);
                Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] payload)); Assert.Equal("NEW"u8.ToArray(), payload);
            }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> WriterCases()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (string operation in new[] { "remove", "replace-seed" })
        foreach (string mode in new[] { "generate", "preserve", "qdf", "linear" }) { yield return [route, operation, mode]; }
    }

    [Theory]
    [MemberData(nameof(WriterCases))]
    public async Task PruningKeepsCompressedQdfAndLinearizedModelsCoherent(string route, string operation, string mode)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("valid", false, false, 65536), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("second"), "NEW"u8.ToArray(), QpdfInputOwnership.Copy);
        if (mode == "preserve")
        {
            QpdfExecutionResult packed = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([
                "input", "--object-streams=generate", "--compress-streams=n", "packed"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, packed.Status);
            Assert.True(host.TryGetOutput(QpdfPath.From("packed"), out byte[] source));
            host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        }
        Assert.Empty(await Run(host, route, operation, QpdfLimits.Default, false, mode));
        Assert.True(host.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] bytes));
        Assert.True(bytes.Length < 16384); Assert.DoesNotContain("REMOVED-PAYLOAD-", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        host.AddInput(QpdfPath.From("inspect"), bytes, QpdfInputOwnership.Transfer);
        Assert.Equal(operation == "remove" ? 1 : 2, (await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("inspect"), CancellationToken.None)).Count);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inspect"), CancellationToken.None))
        { Assert.Equal(2, document.PageCount); }
        if (mode == "linear")
        {
            QpdfExecutionResult checkedLayout = await Qpdf.ExecuteAsync(host,
                new QpdfCommandInvocation(["inspect", "--check-linearization"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, checkedLayout.Status); Assert.Equal(QpdfExitCode.Success, checkedLayout.ExitCode);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedPayloadSurvivesRemovalAndOwnerDisposal(bool split)
    {
        PdfStream escaped;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Fixture("valid", false, true, 64), QpdfInputOwnership.Transfer);
            string route = split ? "argv-split" : "argv";
            await Run(host, route, "remove", QpdfLimits.Default, false, "plain");
            Assert.True(host.TryGetOutput(QpdfPath.From(Outputs(route).First()), out byte[] bytes));
            host.AddInput(QpdfPath.From("inspect"), bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inspect"), CancellationToken.None);
            PdfDictionary page = document.PageReferences[0].Page;
            Assert.True(page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject raw));
            escaped = Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(raw).ObjectNumber));
        }
        Assert.StartsWith("REMOVED-PAYLOAD-", Encoding.ASCII.GetString(escaped.Data), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("typed", "write")]
    [InlineData("argv", "write")]
    [InlineData("job", "write")]
    [InlineData("typed", "commit")]
    [InlineData("argv", "commit")]
    [InlineData("job", "commit")]
    [InlineData("typed", "cancel")]
    [InlineData("argv", "cancel")]
    [InlineData("job", "cancel")]
    public async Task RemovalPublicationFailuresPreservePriorOutput(string route, string fault)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 4096L };
        using InMemoryQpdfHost inner = new(limits);
        inner.AddInput(QpdfPath.From("input"), Fixture("valid", false, false, 65536), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await inner.CreateOutputAsync(QpdfPath.From("output.pdf"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        using CancellationTokenSource cancellation = new();
        SelectiveAttachmentExtractionTests.FaultHost host = new(inner, fault, cancellation);
        if (route == "typed")
        {
            Exception? failure = await Record.ExceptionAsync(() => Qpdf.RemoveAttachmentsAsync(host,
                QpdfPath.From("input"), QpdfPath.From("output.pdf"), ["seed"], limits, cancellation.Token).AsTask());
            if (fault == "cancel") { Assert.IsAssignableFrom<OperationCanceledException>(failure); }
            else { Assert.IsType<QpdfHostException>(failure); }
        }
        else
        {
            QpdfExecutionOptions options = new(limits);
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--remove-attachment=seed", "--compress-streams=n", "output.pdf"]), options, cancellation.Token)
                : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output.pdf\",\"removeAttachment\":[\"seed\"],\"compressStreams\":\"n\"}", options, cancellation.Token);
            Assert.Equal(fault == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        }
        Assert.Equal(1, host.Created);
        Assert.True(inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] output));
        Assert.Equal("prior"u8.ToArray(), output); Assert.Equal(0L, inner.OpenLeaseCount);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task SupersededAddsStillReadTheirInputsInOrder(string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("valid", false, false, 32), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("second"), "NEW"u8.ToArray(), QpdfInputOwnership.Copy);
        if (route == "typed")
        {
            QpdfHostException failure = await Assert.ThrowsAsync<QpdfHostException>(() => Qpdf.AddAttachmentsAsync(host,
                QpdfPath.From("input"), QpdfPath.From("output.pdf"),
                [new("first", "added", null, null, null, null, null, false), new("second", "added", null, null, null, null, null, true)], CancellationToken.None).AsTask());
            Assert.Equal(QpdfPath.From("first"), failure.Path);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input", "--add-attachment", "first", "--key=added", "--", "--add-attachment", "second", "--key=added", "--replace", "--", "output.pdf"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output.pdf\",\"addAttachment\":[{\"file\":\"first\",\"key\":\"added\"},{\"file\":\"second\",\"key\":\"added\",\"replace\":true}]}", CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.HostOperationFailed && d.Message.Contains("first", StringComparison.Ordinal));
        }
        Assert.False(host.TryGetOutput(QpdfPath.From("output.pdf"), out _)); Assert.Equal(0L, host.OpenLeaseCount);
    }
}

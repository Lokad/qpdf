using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentKeyAmbiguityTests
{
    private static byte[] Fixture(string shape, bool brokenKid)
    {
        string first = shape == "malformed" ? "<feffd800>" : shape == "empty" ? "()" : "(K)";
        string second = shape switch { "encoded" => "<feff004b>", "malformed" => "<feffd801>", "distinct" => "(k)", _ => first };
        string tree = shape == "nested" ? "<< /Kids [10 0 R 11 0 R] >>"
            : "<< /Names [" + first + " 5 0 R " + second + " 8 0 R]" + (brokenKid ? " /Kids [7 0 R]" : "") + " >>";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, tree, null), (5, "<< /Type /Filespec /UF (first) /EF << /UF 6 0 R >> >>", null),
            (6, "<< /Type /EmbeddedFile /Length 1 >>", "A"u8.ToArray()), (7, "null", null),
            (8, "<< /Type /Filespec /UF (second) /EF << /UF 9 0 R >> >>", null),
            (9, "<< /Type /EmbeddedFile /Length 1 >>", "B"u8.ToArray()),
            (10, "<< /Names [" + first + " 5 0 R] >>", null),
            (11, "<< /Names [" + second + " 8 0 R] >>", null)], 1);
    }

    private static InMemoryQpdfHost Host(string shape, bool brokenKid)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(shape, brokenKid), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("base"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("added"), "N"u8.ToArray(), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task Typed(InMemoryQpdfHost host, string operation, string key)
    {
        QpdfPath input = QpdfPath.From("input"), output = QpdfPath.From("output");
        switch (operation)
        {
            case "list": await Qpdf.ListAttachmentsAsync(host, input, CancellationToken.None); break;
            case "json": await Qpdf.GetDocumentJsonAsync(host, input, output, new QpdfJsonOptions(2, ["attachments"]), CancellationToken.None); break;
            case "rewrite": await Qpdf.SelectPageAsync(host, input, output, new PdfPageSelectionOptions(1), CancellationToken.None); break;
            case "copy": await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("base"), output, [new(input, null)], CancellationToken.None); break;
            case "remove": await Qpdf.RemoveAttachmentsAsync(host, input, output, [key], CancellationToken.None); break;
            case "replace": await Qpdf.AddAttachmentsAsync(host, input, output, [new("added", key, null, null, null, null, null, true)], CancellationToken.None); break;
            default: throw new InvalidOperationException();
        }
    }

    private static ValueTask<QpdfExecutionResult> Execute(InMemoryQpdfHost host, string route, string operation, string key, bool fileless)
    {
        if (route == "job")
        {
            Dictionary<string, object> job = new() { ["inputFile"] = operation == "copy" ? "base" : "input", ["noWarn"] = true, ["warningExit0"] = true };
            if (operation != "list" && !fileless) { job["outputFile"] = "output"; }
            switch (operation)
            {
                case "list": job["listAttachments"] = true; break;
                case "json": job["json"] = "2"; job["jsonKey"] = new[] { "attachments" }; break;
                case "rewrite": job["pages"] = new[] { new { file = ".", range = "1" } }; break;
                case "copy": job["copyAttachmentsFrom"] = new[] { new { file = "input" } }; break;
                case "remove": job["removeAttachment"] = new[] { key }; break;
                case "replace": job["addAttachment"] = new[] { new { file = "added", key, replace = true } }; break;
                default: throw new InvalidOperationException();
            }
            return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
        }
        List<string> argv = [operation == "copy" ? "base" : "input", "--no-warn", "--warning-exit-0"];
        argv.AddRange(operation switch
        {
            "list" => ["--list-attachments"], "json" => ["--json=2", "--json-key=attachments"],
            "rewrite" => ["--pages", ".", "1", "--"], "copy" => ["--copy-attachments-from", "input", "--"],
            "remove" => ["--remove-attachment=" + key], "replace" => ["--add-attachment", "added", "--key=" + key, "--replace", "--"],
            _ => throw new InvalidOperationException(),
        });
        if (operation != "list" && !fileless)
        { if (operation != "rewrite") { argv.Add("--"); } argv.Add("output"); }
        return Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
    }

    public static IEnumerable<object[]> AmbiguousCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        {
            foreach (string operation in new[] { "list", "json", "rewrite", "copy", "remove", "replace" })
            { yield return [route, operation, "same", false]; }
            foreach (string shape in new[] { "encoded", "malformed", "nested", "empty" })
            { yield return [route, shape == "empty" ? "remove" : "rewrite", shape, false]; }
        }
        yield return ["argv", "json", "encoded", true];
        yield return ["job", "json", "malformed", true];
    }

    [Theory]
    [MemberData(nameof(AmbiguousCases))]
    public async Task FullCollectionOperationsRejectAmbiguityBeforePublication(string route, string operation, string shape, bool fileless)
    {
        using InMemoryQpdfHost host = Host(shape, false);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        string key = shape is "malformed" or "empty" ? string.Empty : "K";
        if (route == "typed")
        {
            QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(() => Typed(host, operation, key));
            Assert.Contains("duplicate logical keys", error.Message, StringComparison.Ordinal);
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, route, operation, key, fileless);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("duplicate logical keys", StringComparison.Ordinal));
            Assert.Null(result.OutputBytes); Assert.Null(result.OutputText);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] priorBytes)); Assert.Equal("prior"u8.ToArray(), priorBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> SelectiveCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string shape in new[] { "same", "encoded", "malformed" })
        foreach (bool brokenKid in new[] { false, true }) { yield return [route, shape, brokenKid]; }
    }

    [Theory]
    [MemberData(nameof(SelectiveCases))]
    public async Task SelectiveExtractionKeepsFirstMatchWithoutScanningOtherBranches(string route, string shape, bool brokenKid)
    {
        using InMemoryQpdfHost host = Host(shape, brokenKid);
        // The existing input decoder drops isolated high surrogates.
        string key = shape == "malformed" ? string.Empty : "K";
        if (route == "typed")
        { await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), key, QpdfPath.From("output"), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", showAttachment = key }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--show-attachment=" + key, "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); Assert.Equal("A"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task DistinctCaseSensitiveKeysRetainTheirOwnPayloads(string route)
    {
        using InMemoryQpdfHost host = Host("distinct", false);
        if (route == "typed") { await Typed(host, "rewrite", "K"); }
        else { Assert.Equal(QpdfExitCode.Success, (await Execute(host, route, "rewrite", "K", false)).ExitCode); }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] rewritten));
        host.AddInput(QpdfPath.From("again"), rewritten, QpdfInputOwnership.Transfer);
        Assert.Equal(new[] { "K", "k" }, (await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("again"), CancellationToken.None)).Select(item => item.Key));
        foreach (string key in new[] { "K", "k" })
        {
            await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("again"), key, QpdfPath.From("payload"), CancellationToken.None);
            Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] payload));
            Assert.Equal(key == "K" ? "A"u8.ToArray() : "B"u8.ToArray(), payload);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

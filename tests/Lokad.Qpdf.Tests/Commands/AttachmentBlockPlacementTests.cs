using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class AttachmentBlockPlacementTests
{
    private static byte[] Fixture(string key, string payload, bool keep)
    {
        string names = (keep ? "(keep) 7 0 R " : "") + "(" + key + ") 5 0 R";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "<< /Names [" + names + "] >>", null),
            (5, "<< /Type /Filespec /F (" + key + ") /EF << /F 6 0 R >> >>", null),
            (6, "<< /Type /EmbeddedFile /Length " + payload.Length + " >>", Encoding.ASCII.GetBytes(payload)),
            (7, "<< /Type /Filespec /F (keep) /EF << /F 8 0 R >> >>", null),
            (8, "<< /Type /EmbeddedFile /Length 4 >>", "KEEP"u8.ToArray())], 1);
    }

    public static IEnumerable<object[]> Placements()
    {
        foreach (string route in new[] { "argv", "response", "mixed" })
        foreach (string edit in new[] { "add", "remove", "replace", "copy" })
        foreach (int position in new[] { 0, 1, 2, 3 })
        {
            yield return [route, edit, position, true];
            if (position != 2) { yield return [route, edit, position, false]; }
        }
    }

    [Theory]
    [MemberData(nameof(Placements))]
    public async Task AttachmentBlocksKeepTheirMeaningAtMainArgumentPositions(string route, string edit, int position, bool pages)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("seed", "OLD", true), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("foreign"), Fixture("foreign", "FOREIGN", false), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("payload"), "NEW"u8.ToArray(), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("settings"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        string[] block = edit switch
        {
            "add" => ["--add-attachment", "payload", "--key=added", "--"],
            "remove" => ["--remove-attachment=seed"],
            "replace" => ["--add-attachment", "payload", "--key=seed", "--replace", "--"],
            _ => ["--copy-attachments-from", "foreign", "--prefix=imported-", "--"],
        };
        List<string> args = pages ? ["input", "--pages", ".", position % 2 == 0 ? "1" : "1-1", "--", "output"] : ["input", "output"];
        args.InsertRange(position switch { 0 => 0, 1 => 1, 2 => 5, _ => args.Count }, block);
        if (route == "mixed") { args.Insert(0, "--job-json-file=settings"); }
        if (route == "response")
        {
            host.AddInput(QpdfPath.From("args"), Encoding.UTF8.GetBytes(string.Join('\n', args)), QpdfInputOwnership.Copy);
            args = ["@args"];
        }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("reopen"), output, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reopen"), CancellationToken.None))
        { Assert.Equal(1, document.PageCount); }
        Dictionary<string, string> expected = new() { ["keep"] = "KEEP" };
        if (edit != "remove") { expected["seed"] = edit == "replace" ? "NEW" : "OLD"; }
        if (edit == "add") { expected["added"] = "NEW"; }
        if (edit == "copy") { expected["imported-foreign"] = "FOREIGN"; }
        IReadOnlyList<QpdfAttachmentInfo> attachments = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("reopen"), CancellationToken.None);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), attachments.Select(a => a.Key));
        foreach (var entry in expected)
        {
            await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("reopen"), entry.Key, QpdfPath.From("extracted"), CancellationToken.None);
            Assert.True(host.TryGetOutput(QpdfPath.From("extracted"), out byte[] payload));
            Assert.Equal(Encoding.ASCII.GetBytes(entry.Value), payload);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EmptyPrimaryKeepsExplicitAddsWithoutImportingProviderCollection(bool beforeEmpty, bool pages)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("provider"), Fixture("foreign", "FOREIGN", false), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("payload"), "NEW"u8.ToArray(), QpdfInputOwnership.Copy);
        string[] block = ["--add-attachment", "payload", "--key=added", "--"];
        List<string> args = beforeEmpty ? [.. block, "--empty"] : ["--empty", .. block];
        if (pages) { args.AddRange(["--pages", "provider", "1-1", "--"]); }
        args.Add("output");
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("reopen"), output, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reopen"), CancellationToken.None))
        { Assert.Equal(pages ? 1 : 0, document.PageCount); }
        IReadOnlyList<QpdfAttachmentInfo> attachments = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("reopen"), CancellationToken.None);
        Assert.Equal("added", Assert.Single(attachments).Key);
        await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("reopen"), "added", QpdfPath.From("extracted"), CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("extracted"), out byte[] payload));
        Assert.Equal("NEW"u8.ToArray(), payload); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SupersededPrefixAddStillReadsBeforeLaterAddAndPreservesPriorOutputOnFailure()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("seed", "OLD", true), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new([
            "input", "--add-attachment", "missing-first", "--key=seed", "--replace", "--",
            "--pages", ".", "1", "--", "output", "--add-attachment", "missing-second", "--key=seed", "--replace", "--"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("missing-first", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("missing-second", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.Equal("prior"u8.ToArray(), output); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MalformedBlocksRejectBeforePdfAccessAndPreservePriorTarget(int position)
    {
        using InMemoryQpdfHost host = new();
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        List<string> args = ["missing-input", "--pages", ".", "1", "--", "output"];
        args.InsertRange(position switch { 0 => 0, 1 => 1, 2 => 5, _ => 6 },
            ["--add-attachment", "missing-payload", "--bogus", "--"]);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0L, result.Counters.HostOperations);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.Equal("prior"u8.ToArray(), output); Assert.Equal(0L, host.OpenLeaseCount);
    }
}

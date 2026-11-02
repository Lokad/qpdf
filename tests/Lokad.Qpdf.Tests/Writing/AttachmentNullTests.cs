using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentNullTests
{
    private static byte[] Fixture(string role, bool indirect)
    {
        string nil = indirect ? "8 0 R" : "null";
        string names = role == "names" ? nil : "<< /EmbeddedFiles " + (role == "embedded" ? nil : "7 0 R") + " >>";
        string embedded = role == "preferred" ? "<< /UF " + nil + " /F 6 0 R >>" : role == "ef" ? "8 0 R" : "<< /UF " + (role == "stream" ? "8 0 R" : "6 0 R") + " >>";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names " + names + " >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "null", null),
            (5, "<< /Type /Filespec /UF (file) /EF " + embedded + " >>", null),
            (6, "<< /Type /EmbeddedFile /Length 1" + (role == "parameters" ? " /Params " + nil : "") + " >>", "A"u8.ToArray()),
            (7, "<< /Names [(K) " + (role == "filespec" ? "8 0 R" : "5 0 R") + "] >>", null),
            (8, "null", null)], 1);
    }

    private static async Task AssertContents(InMemoryQpdfHost host, string path, bool files)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] output));
        host.AddInput(QpdfPath.From("again"), output, QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfAttachmentInfo> listing = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("again"), CancellationToken.None);
        Assert.Equal(files ? 1 : 0, listing.Count);
        if (files)
        {
            Assert.Equal("K", listing[0].Key); Assert.Equal("file", listing[0].FileName);
            await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("again"), "K", QpdfPath.From("payload"), CancellationToken.None);
            Assert.True(host.TryGetOutput(QpdfPath.From("payload"), out byte[] payload)); Assert.Equal("A"u8.ToArray(), payload);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string role in new[] { "names", "embedded", "parameters", "preferred" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [role, route]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OptionalDirectAndIndirectNullHaveTheSameMeaning(string role, string route)
    {
        foreach (bool indirect in new[] { false, true })
        {
            using InMemoryQpdfHost host = new();
            host.AddInput(QpdfPath.From("input"), Fixture(role, indirect), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("base"), Fixture("names", false), QpdfInputOwnership.Transfer);
            bool files = role is "parameters" or "preferred";
            if (route == "typed")
            {
                IReadOnlyList<QpdfAttachmentInfo> listing = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("input"), CancellationToken.None);
                Assert.Equal(files ? 1 : 0, listing.Count);
                PdfPageSelectionResult rewritten = await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("rewrite"),
                    new PdfPageSelectionOptions(1), CancellationToken.None);
                Assert.Empty(rewritten.Warnings);
                QpdfAttachmentEditResult copied = await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("base"), QpdfPath.From("copy"),
                    [new(QpdfPath.From("input"), null)], CancellationToken.None);
                Assert.Equal(files ? 1 : 0, copied.AttachmentCount); Assert.Empty(copied.Warnings);
            }
            else
            {
                foreach (string operation in new[] { "list", "rewrite", "copy" })
                {
                    QpdfExecutionResult result;
                    if (route == "argv")
                    {
                        string[] argv = operation switch
                        {
                            "list" => ["input", "--list-attachments"],
                            "rewrite" => ["input", "--pages", ".", "1", "--", "rewrite"],
                            _ => ["base", "--copy-attachments-from", "input", "--", "--", "copy"],
                        };
                        result = await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
                    }
                    else
                    {
                        Dictionary<string, object> config = new() { ["inputFile"] = operation == "copy" ? "base" : "input" };
                        if (operation == "list") { config["listAttachments"] = true; }
                        else { config["outputFile"] = operation; }
                        if (operation == "rewrite") { config["pages"] = new[] { new { file = ".", range = "1" } }; }
                        if (operation == "copy") { config["copyAttachmentsFrom"] = new[] { new { file = "input" } }; }
                        result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(config), CancellationToken.None);
                    }
                    Assert.Equal(QpdfExitCode.Success, result.ExitCode);
                    Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity != QpdfDiagnosticSeverity.Info);
                }
            }
            await AssertContents(host, "rewrite", files); await AssertContents(host, "copy", files);
        }
    }

    [Theory]
    [InlineData("filespec")]
    [InlineData("ef")]
    [InlineData("stream")]
    public async Task RequiredNullReferencesStillFailWithoutPublication(string role)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(role, true), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), "K",
            QpdfPath.From("output"), CancellationToken.None).AsTask());
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

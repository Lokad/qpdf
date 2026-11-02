using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PrimaryAttachmentRetentionTests
{
    private static byte[] Fixture(string key, string payload, bool keep, bool zero, bool indirect, bool pageFile)
    {
        string names = "[" + (keep ? "(keep) 8 0 R " : "") + "(" + key + ") 5 0 R]";
        string page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >>"
            + (pageFile ? " /AF [5 0 R]" : "") + " >>";
        byte[] data = Encoding.ASCII.GetBytes(payload);
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 7 0 R >> >>", null),
            (2, "<< /Type /Pages /Count " + (zero ? "0 /Kids []" : "2 /Kids [3 0 R 4 0 R]") + " >>", null),
            (3, page, null), (4, page, null),
            (5, "<< /Type /Filespec /UF (" + key + ") /EF << /UF 6 0 R >> >>", null),
            (6, "<< /Type /EmbeddedFile /Length " + data.Length + " >>", data),
            (7, "<< /Names " + (indirect ? "10 0 R" : names) + " >>", null),
            (8, "<< /Type /Filespec /UF (keep) /EF << /UF 9 0 R >> >>", null),
            (9, "<< /Type /EmbeddedFile /Length 4 >>", "KEEP"u8.ToArray()),
            (10, names, null)], 1);
    }

    private static InMemoryQpdfHost Host(bool zero, bool indirect)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("primary"), Fixture("seed", "PRIMARY", true, zero, indirect, false), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("secondary"), Fixture("foreign", "SECONDARY", false, false, indirect, true), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("added"), "NEW"u8.ToArray(), QpdfInputOwnership.Transfer);
        return host;
    }

    private static async Task AssertEntries(InMemoryQpdfHost host, string path, IReadOnlyDictionary<string, string> expected)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
        QpdfPath again = QpdfPath.From("reopen"); host.AddInput(again, bytes, QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfAttachmentInfo> entries = await Qpdf.ListAttachmentsAsync(host, again, CancellationToken.None);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), entries.Select(entry => entry.Key));
        foreach (KeyValuePair<string, string> entry in expected)
        {
            QpdfPath output = QpdfPath.From("payload-" + entry.Key);
            await Qpdf.ExtractAttachmentAsync(host, again, entry.Key, output, CancellationToken.None);
            Assert.True(host.TryGetOutput(output, out byte[] payload)); Assert.Equal(Encoding.ASCII.GetBytes(entry.Value), payload);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> SelectionCases()
    {
        foreach (string route in new[] { "typed-single", "typed-multi", "argv", "job" })
        foreach (string range in new[] { "1-z", "2", "2-2" })
        foreach (bool indirect in new[] { false, true }) { yield return [route, range, indirect]; }
    }

    [Theory]
    [MemberData(nameof(SelectionCases))]
    public async Task OrdinarySelectionKeepsPrimaryCollection(string route, string range, bool indirect)
    {
        using InMemoryQpdfHost host = Host(false, indirect);
        PdfPageSelectionOptions selection = PdfPageSelectionOptions.FromPages(range == "1-z" ? [1, 2] : [2]);
        if (route == "typed-single")
        {
            PdfPageSelectionResult result = await Qpdf.SelectPageAsync(host, QpdfPath.From("primary"), QpdfPath.From("output"), selection, CancellationToken.None);
            Assert.Empty(result.Warnings);
        }
        else if (route == "typed-multi")
        {
            PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("primary"), selection)], QpdfPath.From("output"), CancellationToken.None);
            Assert.Empty(result.Warnings);
        }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "primary", outputFile = "output", pages = new[] { new { file = ".", range } } }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["primary", "--pages", ".", range, "--", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        await AssertEntries(host, "output", new Dictionary<string, string> { ["keep"] = "KEEP", ["seed"] = "PRIMARY" });
    }

    [Theory]
    [InlineData(false, "normal")]
    [InlineData(true, "normal")]
    [InlineData(false, "zero")]
    [InlineData(true, "zero")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    public async Task CollectionComesFromPrimaryWithoutRequiringItsPages(bool job, string primary)
    {
        using InMemoryQpdfHost host = Host(primary == "zero", true);
        Dictionary<string, object> config = new() { ["outputFile"] = "output", ["streamData"] = "uncompress", ["pages"] = new[] { new { file = "secondary", range = "1" } } };
        if (primary == "empty") { config["empty"] = true; } else { config["inputFile"] = "primary"; }
        List<string> argv = [primary == "empty" ? "--empty" : "primary", "--stream-data=uncompress", "--pages", "secondary", "1", "--", "output"];
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(config), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Dictionary<string, string> expected = primary == "empty" ? [] : new() { ["keep"] = "KEEP", ["seed"] = "PRIMARY" };
        await AssertEntries(host, "output", expected);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.Contains("SECONDARY", Encoding.ASCII.GetString(output)); // The selected page's AF remains reachable.
    }

    [Theory]
    [InlineData(false, "add")]
    [InlineData(true, "add")]
    [InlineData(false, "remove")]
    [InlineData(true, "remove")]
    [InlineData(false, "replace")]
    [InlineData(true, "replace")]
    [InlineData(false, "copy")]
    [InlineData(true, "copy")]
    public async Task EditsChangeOnlyRequestedEntries(bool job, string edit)
    {
        using InMemoryQpdfHost host = Host(false, false);
        Dictionary<string, string> expected = new() { ["keep"] = "KEEP", ["seed"] = "PRIMARY" };
        Dictionary<string, object> config = new() { ["inputFile"] = "primary", ["outputFile"] = "output", ["pages"] = new[] { new { file = ".", range = "2" } } };
        List<string> argv = ["primary", "--pages", ".", "2", "--"];
        if (edit is "add" or "replace")
        {
            string key = edit == "add" ? "extra" : "seed";
            expected[key] = "NEW";
            config["addAttachment"] = new[] { new { file = "added", key, replace = edit == "replace" } };
            argv.AddRange(["--add-attachment", "added", "--key=" + key]);
            if (edit == "replace") { argv.Add("--replace"); }
            argv.Add("--");
        }
        else if (edit == "remove")
        { expected.Remove("seed"); config["removeAttachment"] = new[] { "seed" }; argv.Add("--remove-attachment=seed"); }
        else
        {
            expected["foreign"] = "SECONDARY";
            config["copyAttachmentsFrom"] = new[] { new { file = "secondary" } };
            argv.AddRange(["--copy-attachments-from", "secondary", "--"]);
        }
        argv.AddRange(["--", "output"]);
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(config), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        await AssertEntries(host, "output", expected);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task SplitRetainsFinalCollectionInEveryOutput(string route)
    {
        using InMemoryQpdfHost host = Host(false, false);
        if (route == "typed")
        {
            _ = await Qpdf.SelectPagesAsync(host, [new(QpdfPath.From("primary"), PdfPageSelectionOptions.FromPages([1, 2]))], QpdfPath.From("output.pdf"),
                new PdfPageMergeOptions { SplitPages = 1 }, CancellationToken.None);
        }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"primary","outputFile":"output.pdf","splitPages":"1"}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["primary", "--split-pages=1", "output.pdf"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        foreach (string output in new[] { "output-1.pdf", "output-2.pdf" })
        { await AssertEntries(host, output, new Dictionary<string, string> { ["keep"] = "KEEP", ["seed"] = "PRIMARY" }); }
    }

    [Theory]
    [InlineData("typed", "memory")]
    [InlineData("argv", "memory")]
    [InlineData("job", "memory")]
    [InlineData("typed", "output")]
    [InlineData("argv", "output")]
    [InlineData("job", "output")]
    public async Task RetentionRefusesAtomicallyInsteadOfDroppingTheCollection(string route, string resource)
    {
        string names = resource == "memory" ? string.Join(' ', Enumerable.Range(0, 1000).Select(index => $"(K-{index:D4}) 5 0 R")) : "(K) 5 0 R";
        byte[] payload = new byte[resource == "memory" ? 1 : 32768]; new Random(42).NextBytes(payload);
        byte[] fixture = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>", null),
            (4, "<< /Names [" + names + "] >>", null),
            (5, "<< /Type /Filespec /UF (file) /EF << /UF 6 0 R >> >>", null),
            (6, "<< /Type /EmbeddedFile /Length " + payload.Length + " >>", payload)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("primary"), fixture, QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction transaction = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await transaction.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await transaction.CommitAsync(CancellationToken.None); }
        QpdfLimits limits = resource == "memory" ? QpdfLimits.Default with { MaxMemoryBytes = 131072L } : QpdfLimits.Default with { MaxOutputBytes = 4096L };
        if (route == "typed")
        {
            await Assert.ThrowsAsync<QpdfLimitException>(() => Qpdf.SelectPageAsync(host, QpdfPath.From("primary"), QpdfPath.From("output"),
                new PdfPageSelectionOptions(1), limits, CancellationToken.None).AsTask());
        }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"primary","outputFile":"output","pages":[{"file":".","range":"1"}]}""", new QpdfExecutionOptions(limits), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["primary", "--pages", ".", "1", "--", "output"]), new QpdfExecutionOptions(limits), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

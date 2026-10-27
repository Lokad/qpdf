using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class CatalogNamesTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool indirect in new[] { false, true })
        foreach (bool attachment in new[] { false, true })
        foreach (string operation in new[] { "rewrite", "qdf", "linearized", "subset", "add", "remove" })
        {
            if (operation == "remove" && !attachment) { continue; }
            foreach (string route in operation is "add" or "remove" ? new[] { "argv", "job", "typed" } : new[] { "argv", "job" })
            { yield return [indirect, attachment, operation, route]; }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CatalogNamesSurviveRewritingAndAttachmentEdits(bool indirect, bool attachment, string operation, string route)
    {
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("input"), output = QpdfPath.From("output");
        host.AddInput(input, Fixture(indirect, attachment), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("payload"), [17], QpdfInputOwnership.Transfer);
        if (route == "typed")
        {
            if (operation == "remove") { await Qpdf.RemoveAttachmentsAsync(host, input, output, ["kept"], CancellationToken.None); }
            else { await Qpdf.AddAttachmentsAsync(host, input, output, [new("payload", "added", null, null, null, null, null, false)], CancellationToken.None); }
        }
        else
        {
            List<string> argv = ["input"];
            Dictionary<string, object> job = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
            if (operation is "qdf" or "linearized")
            { string option = operation == "qdf" ? "qdf" : "linearize"; argv.Add("--" + option); job[option] = ""; }
            if (operation == "subset")
            { argv.AddRange(["--pages", ".", "1", "--"]); job["pages"] = new[] { new { file = ".", range = "1" } }; }
            if (operation == "add")
            { argv.AddRange(["--add-attachment", "payload", "--key=added", "--"]); job["addAttachment"] = new[] { new { file = "payload", key = "added" } }; }
            if (operation == "remove")
            { argv.Add("--remove-attachment=kept"); job["removeAttachment"] = new[] { "kept" }; }
            if (operation != "subset") { argv.Add("--"); }
            argv.Add("output");
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        host.AddInput(QpdfPath.From("readback"), bytes, QpdfInputOwnership.Transfer);
        PdfDictionary escaped;
        PdfByteString text;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Property(document, document.Engine.Trailer, "Root"));
            escaped = Assert.IsType<PdfDictionary>(Property(document, catalog, "Names"));
            PdfDictionary destinations = Assert.IsType<PdfDictionary>(Property(document, escaped, "Dests"));
            PdfArray entries = Assert.IsType<PdfArray>(Property(document, destinations, "Names"));
            PdfArray chapter = Assert.IsType<PdfArray>(Resolve(document, entries[1]));
            Assert.Same(document.PageReferences[0].Page, Resolve(document, chapter[0]));
            PdfArray omitted = Assert.IsType<PdfArray>(Resolve(document, entries[3]));
            if (operation == "subset") { Assert.IsType<PdfNull>(omitted[0]); }
            else { Assert.Same(document.PageReferences[1].Page, Resolve(document, omitted[0])); }
            PdfDictionary privateTree = Assert.IsType<PdfDictionary>(Property(document, escaped, "PrivateTree"));
            Assert.Equal(Entry(privateTree, "Shared"), Entry(privateTree, "Alias"));
            Assert.Same(privateTree, Property(document, privateTree, "Self"));
            text = Assert.IsType<PdfByteString>(Property(document, privateTree, "Shared"));
            int expected = (attachment && operation is not ("remove" or "subset") ? 1 : 0) + (operation == "add" ? 1 : 0);
            Assert.Equal(expected > 0, escaped.TryGet(PdfName.FromAscii("EmbeddedFiles"), out QpdfObject files));
            if (expected > 0)
            {
                PdfDictionary tree = Assert.IsType<PdfDictionary>(Resolve(document, files));
                Assert.Equal(expected * 2, Assert.IsType<PdfArray>(Property(document, tree, "Names")).Count);
            }
        }
        host.Dispose();
        Assert.True(escaped.TryGet(PdfName.FromAscii("Dests"), out _));
        Assert.Equal(new byte[] { 0, 128, 255 }, text.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputFailurePreservesPriorTargetAndClosesInput(bool commit)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), Fixture(true, true), QpdfInputOwnership.Transfer);
        QpdfPath target = QpdfPath.From("output");
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(target, CancellationToken.None))
        { await prior.WriteAsync(new byte[] { 17 }, CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(new FailingOutput(storage, commit), new(["input", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Null(result.OutputBytes);
        Assert.True(storage.TryGetOutput(target, out byte[] priorBytes)); Assert.Equal(new byte[] { 17 }, priorBytes);
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    private sealed class FailingOutput(InMemoryQpdfHost storage, bool commit) : IQpdfHost
    {
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => ValueTask.FromResult<IQpdfOutputTransaction>(commit ? new FailingCommitTransaction(path) : new FailingWriteTransaction(path));
    }

    private static byte[] Fixture(bool indirect, bool attachment)
    {
        string names = "<< /Dests 6 0 R /PrivateTree 12 0 R" + (attachment ? " /EmbeddedFiles << /Names [(kept) 7 0 R] >>" : "") + " >>";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names " + (indirect ? "5 0 R" : names) + " /Outlines 9 0 R >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 11 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /GoTo /D (chapter) >> >>", null),
            (5, names, null),
            (6, "<< /Names [(chapter) [3 0 R /Fit] (omitted) [11 0 R /Fit]] >>", null),
            (7, "<< /Type /Filespec /F (kept.bin) /EF << /F 8 0 R >> >>", null),
            (8, "<< /Type /EmbeddedFile /Length 1 >>", new byte[] { 23 }),
            (9, "<< /Type /Outlines /Count 1 /First 10 0 R /Last 10 0 R >>", null),
            (10, "<< /Title (Jump) /Parent 9 0 R /Dest (chapter) >>", null),
            (11, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (12, "<< /Shared 13 0 R /Alias 13 0 R /Self 12 0 R >>", null),
            (13, "<0080FF>", null)], 1);
    }

    private static QpdfObject Entry(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
    private static QpdfObject Property(QpdfDocument document, PdfDictionary dictionary, string key) => Resolve(document, Entry(dictionary, key));
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
}

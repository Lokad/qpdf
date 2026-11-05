using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class VerbatimLabelTreeTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach ((string route, bool numbered, string writer) in new[]
        {
            ("argv", true, "plain"), ("job", true, "packed"),
            ("argv", false, "qdf"), ("job", false, "linear"),
            ("typed", true, "plain"), ("typed", false, "plain"),
        })
        foreach (bool maximum in new[] { false, true }) { yield return [route, numbered, writer, maximum, 4]; }
        yield return ["argv", true, "plain", true, 1];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task WholeFileRetainsRawTreeAndAliasesWithoutComputingPageCounters(string route, bool numbered, string writer, bool maximum, int pages)
    {
        RecordingMemoryBudget budget = new(32L * 1048576L);
        using (IMemoryScope owner = budget.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        {
            host.AddInput(QpdfPath.From("input"), Source(numbered, maximum, pages, "normal"), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("attachment"), new byte[] { 42 }, QpdfInputOwnership.Transfer);
            QpdfExecutionResult? result = await Run(host, route, writer, QpdfLimits.Default, CancellationToken.None);
            if (result is not null) { Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); }
            Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
            PdfByteString escaped;
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
            {
                Assert.Equal(pages, document.PageCount);
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(Read(document, document.Engine.Trailer, "Root"));
                PdfDictionary tree = Assert.IsType<PdfDictionary>(Read(document, catalog, "PageLabels"));
                foreach (var page in document.PageReferences)
                { Assert.Same(tree, Read(document, page.Page, "PrivateTree")); }
                PdfArray numbers = Assert.IsType<PdfArray>(Read(document, tree, "Nums"));
                Assert.Equal(2, numbers.Count); Assert.Equal(0L, Assert.IsType<PdfInteger>(numbers[0]).Value);
                PdfDictionary label = Assert.IsType<PdfDictionary>(Resolve(document, numbers[1]));
                Assert.Equal(numbered, label.TryGet(PdfName.FromAscii("S"), out _));
                if (numbered) { Assert.Equal("D"u8.ToArray(), Assert.IsType<PdfName>(Read(document, label, "S")).RawSpan.ToArray()); }
                PdfInteger start = Assert.IsType<PdfInteger>(Read(document, label, "St"));
                Assert.Equal(maximum ? long.MaxValue : 7L, start.Value);
                Assert.Equal(maximum ? "+0009223372036854775807" : "+0007", start.Text);
                Assert.Same(start, Read(document, tree, "PrivateCounter"));
                Assert.Equal(new byte[] { 0, 255, 1, 0 }, Assert.IsType<PdfByteString>(Read(document, label, "Opaque")).Value);
                escaped = Assert.IsType<PdfByteString>(Read(document, label, "P"));
                Assert.Equal(new byte[] { 255, 254, 83, 0 }, escaped.Value);
            }
            if (route == "typed")
            {
                await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("readback"), "attachment", QpdfPath.From("extracted"), CancellationToken.None);
                Assert.True(host.TryGetOutput(QpdfPath.From("extracted"), out byte[] bytes)); Assert.Equal(new byte[] { 42 }, bytes);
            }
            host.Dispose(); Assert.Equal(0L, host.OpenLeaseCount);
            Assert.Equal(new byte[] { 255, 254, 83, 0 }, escaped.Value);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData("nums", "argv")]
    [InlineData("nums", "job")]
    [InlineData("cycle", "argv")]
    [InlineData("cycle", "job")]
    public async Task RawCopyPreservesOpaqueLabelStructuresWhileSemanticConsumersRejectThem(string defect, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Source(false, false, 4, defect), QpdfInputOwnership.Transfer);
        QpdfExecutionResult? rewritten = await Run(host, route, "plain", QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, Assert.IsType<QpdfExecutionResult>(rewritten).ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            PdfDictionary tree = Assert.IsType<PdfDictionary>(Read(document, document.PageReferences[0].Page, "PrivateTree"));
            if (defect == "nums") { Assert.Equal("opaque"u8.ToArray(), Assert.IsType<PdfByteString>(Read(document, tree, "Nums")).Value); }
            else
            {
                PdfArray kids = Assert.IsType<PdfArray>(Read(document, tree, "Kids"));
                Assert.Same(tree, Resolve(document, kids[0]));
            }
        }
        foreach (string operation in new[] { "select", "inspect" })
        {
            QpdfExecutionResult result = operation == "select"
                ? await Qpdf.ExecuteAsync(host, new(["readback", "--pages", ".", "1", "--", "semantic"]), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["readback", "--json", "--json-key=pagelabels", "--", "semantic"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.False(host.TryGetOutput(QpdfPath.From("semantic"), out _));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("syntax", "argv")]
    [InlineData("depth", "job")]
    [InlineData("memory", "typed")]
    [InlineData("cancel", "typed")]
    public async Task OpaqueCopyKeepsSyntaxResourceCancellationAndPublicationGuards(string failure, string route)
    {
        RecordingMemoryBudget budget = new(32L * 1048576L);
        using (IMemoryScope owner = budget.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        using (CancellationTokenSource cancel = new())
        {
            host.AddInput(QpdfPath.From("input"), Source(true, true, 4, failure), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("attachment"), new byte[] { 42 }, QpdfInputOwnership.Transfer);
            await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
            { await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
            if (failure == "cancel") { cancel.Cancel(); }
            QpdfLimits limits = QpdfLimits.Default with
            { MaxNestingDepth = failure == "depth" ? 2 : QpdfLimits.Default.MaxNestingDepth,
                MaxMemoryBytes = failure == "memory" ? 1024 : QpdfLimits.Default.MaxMemoryBytes };
            QpdfExecutionResult? result = null;
            Exception? error = await Record.ExceptionAsync(async () => result = await Run(host, route, "plain", limits, cancel.Token));
            if (route == "typed")
            {
                if (failure == "cancel") { Assert.IsAssignableFrom<OperationCanceledException>(error); }
                else { Assert.IsType<QpdfLimitException>(error); }
            }
            else
            {
                Assert.Null(error); QpdfExecutionResult command = Assert.IsType<QpdfExecutionResult>(result);
                Assert.Equal(QpdfExitCode.Error, command.ExitCode);
                if (failure == "depth") { Assert.Contains(command.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded); }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    private static async Task<QpdfExecutionResult?> Run(IQpdfHost host, string route, string writer, QpdfLimits limits, CancellationToken token)
    {
        if (route == "typed")
        {
            await Qpdf.AddAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
                [new("attachment", null, null, null, null, null, null, false)], limits, token);
            return null;
        }
        QpdfExecutionResult result;
        if (route == "job")
        {
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["suppressRecovery"] = true };
            if (writer == "packed") { settings["objectStreams"] = "generate"; }
            if (writer == "linear") { settings["linearize"] = true; }
            result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), new(limits), token);
        }
        else
        {
            string[] flags = writer == "qdf" ? ["--qdf"] : [];
            result = await Qpdf.ExecuteAsync(host, new(["input", "--suppress-recovery", .. flags, "output"]), new(limits), token);
        }
        return result;
    }

    private static byte[] Source(bool numbered, bool maximum, int pages, string defect)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /PageLabels 10 0 R >>", null),
            (2, "<< /Type /Pages /Count " + pages + " /Kids [" + string.Join(' ', Enumerable.Range(3, pages).Select(i => i + " 0 R")) + "] >>", null),
        ];
        for (int index = 0; index < pages; index++)
        { objects.Add((index + 3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /PrivateTree 10 0 R >>", null)); }
        for (int index = pages + 3; index < 10; index++) { objects.Add((index, "null", null)); }
        string tree = defect == "cycle" ? "<< /Kids [10 0 R] >>" : defect == "nums" ? "<< /Nums (opaque) >>" : "<< /Nums 11 0 R /PrivateCounter 15 0 R >>";
        string label = "<< " + (numbered ? "/S 13 0 R " : "") + "/P 14 0 R /St 15 0 R /Opaque <00ff0100> >>";
        if (defect == "syntax") { label = "<< /P (unterminated"; }
        objects.AddRange([(10, tree, null), (11, "[0 12 0 R]", null), (12, label, null), (13, "/D", null),
            (14, "<fffe5300>", null), (15, maximum ? "+0009223372036854775807" : "+0007", null)]);
        return CheckTests.Assemble(objects, 1);
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static QpdfObject Read(QpdfDocument document, PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(document, value); }
}

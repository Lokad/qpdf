using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ExplicitSplitLabelTests
{
    [Theory]
    [InlineData("none", "typed", 1, "plain")]
    [InlineData("none", "argv", 2, "reverse")]
    [InlineData("none", "job", 3, "duplicates")]
    [InlineData("numbered", "typed", 2, "duplicates")]
    [InlineData("numbered", "argv", 3, "plain")]
    [InlineData("numbered", "job", 1, "reverse")]
    [InlineData("prefix", "typed", 3, "reverse")]
    [InlineData("prefix", "argv", 1, "duplicates")]
    [InlineData("prefix", "job", 2, "plain")]
    [InlineData("none", "typed", 0, "plain")]
    [InlineData("numbered", "argv", 0, "plain")]
    [InlineData("prefix", "job", 0, "plain")]
    [InlineData("overflow", "typed", 2, "plain")]
    [InlineData("overflow", "argv", 2, "plain")]
    [InlineData("overflow", "job", 2, "plain")]
    public async Task ExplicitOutputLabelsOverrideSourcesBeforePartitioning(string source, string route, int size, string order)
    {
        RecordingMemoryBudget budget = new(32L * 1048576L);
        using (IMemoryScope owner = budget.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        {
            host.AddInput(QpdfPath.From("input"), Source(source), QpdfInputOwnership.Transfer);
            QpdfExecutionResult? result = await Execute(host, route, size, order, CancellationToken.None);
            if (result is not null) { Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); }
            Assert.Equal(0L, host.OpenLeaseCount);
            PdfByteString? escaped = null;
            int[] selected = Pages(order);
            for (int first = 1; first <= selected.Length; first += size == 0 ? selected.Length : size)
            {
                int last = size == 0 ? selected.Length : Math.Min(selected.Length, first + size - 1);
                Assert.True(host.TryGetOutput(QpdfPath.From(Output(first, last, size)), out byte[] output));
                host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
                Assert.Equal(last - first + 1, document.PageReferences.Count);
                PdfArray labels = LabelIndirectionTests.Numbers(document);
                for (int at = first; at <= last; at++)
                {
                    PdfDictionary page = document.PageReferences[at - first].Page;
                    Assert.Equal((long)selected[at - 1], Assert.IsType<PdfInteger>(Read(document, page, "AuditPage")).Value);
                    AssertLabel(document, labels, at - first, at);
                    PdfDictionary originalTree = Assert.IsType<PdfDictionary>(Read(document, page, "OriginalLabels"));
                    PdfArray originalPairs = Assert.IsType<PdfArray>(Read(document, originalTree, "Nums"));
                    PdfDictionary original = Assert.IsType<PdfDictionary>(Resolve(document, originalPairs[1]));
                    Assert.Equal(source == "overflow" ? long.MaxValue : 7L, Assert.IsType<PdfInteger>(Read(document, original, "St")).Value);
                    Assert.Equal(source == "numbered", original.TryGet(PdfName.FromAscii("S"), out _));
                    escaped = Assert.IsType<PdfByteString>(Read(document, original, "P"));
                    Assert.Equal("Source-"u8.ToArray(), escaped.Value);
                }
            }
            host.Dispose();
            Assert.Equal("Source-"u8.ToArray(), Assert.IsType<PdfByteString>(escaped).Value);
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData("cancel", "typed")]
    [InlineData("refuse", "argv")]
    [InlineData("write", "job")]
    [InlineData("commit", "typed")]
    public async Task FailureAfterFirstSplitKeepsItsExplicitLabelsAndPreservesLaterTargets(string mode, string route)
    {
        RecordingMemoryBudget budget = new(32L * 1048576L);
        using (IMemoryScope owner = budget.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        using (CancellationTokenSource cancel = new())
        {
            host.AddInput(QpdfPath.From("input"), Source("prefix"), QpdfInputOwnership.Transfer);
            for (int page = 1; page <= 4; page++)
            {
                await using IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output-" + page), CancellationToken.None);
                await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
            }
            using SplitFailureHost adapter = new(host, owner, budget, cancel, mode);
            QpdfExecutionResult? result = null;
            Exception? failure = await Record.ExceptionAsync(async () => result = await Execute(adapter, route, 1, "plain", cancel.Token));
            adapter.Dispose();
            if (route == "typed")
            {
                if (mode == "cancel") { Assert.IsAssignableFrom<OperationCanceledException>(failure); }
                else { Assert.IsType<QpdfHostException>(failure); }
            }
            else
            {
                Assert.Null(failure); Assert.Equal(QpdfExitCode.Error, Assert.IsType<QpdfExecutionResult>(result).ExitCode);
            }
            Assert.Equal(0L, host.OpenLeaseCount);
            for (int page = 1; page <= 4; page++)
            {
                Assert.True(host.TryGetOutput(QpdfPath.From("output-" + page), out byte[] output));
                if (page != 1) { Assert.Equal("prior"u8.ToArray(), output); continue; }
                host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
                AssertLabel(document, LabelIndirectionTests.Numbers(document), 0, 1);
            }
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    private static async Task<QpdfExecutionResult?> Execute(IQpdfHost host, string route, int size, string order, CancellationToken token)
    {
        int[] pages = Pages(order);
        if (route == "typed")
        {
            PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host,
                [new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages(pages))], QpdfPath.From("output"),
                new PdfPageMergeOptions { SplitPages = size == 0 ? null : size,
                    Labels = [new(1, PdfPageLabelStyle.Decimal, 41, "A-"), new(3, PdfPageLabelStyle.LowerRoman, 7, "B-")] }, token);
            Assert.Empty(result.Warnings); return null;
        }
        string range = string.Join(',', pages);
        if (route == "job")
        {
            Dictionary<string, object> settings = new()
            { ["inputFile"] = "input", ["outputFile"] = "output", ["setPageLabels"] = new[] { "1:D/41/A-", "3:r/7/B-" } };
            if (size != 0) { settings["splitPages"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            if (order != "plain") { settings["pages"] = new[] { new { file = ".", range } }; }
            return await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), token);
        }
        List<string> arguments = ["input", "--set-page-labels", "1:D/41/A-", "3:r/7/B-", "--"];
        if (size != 0) { arguments.Add("--split-pages=" + size); }
        if (order != "plain") { arguments.AddRange(["--pages", ".", range, "--"]); }
        arguments.Add("output"); return await Qpdf.ExecuteAsync(host, new(arguments), token);
    }

    private static int[] Pages(string order) => order switch
    { "reverse" => [4, 3, 2, 1], "duplicates" => [4, 2, 2, 1], _ => [1, 2, 3, 4] };

    private static string Output(int first, int last, int size)
        => size == 0 ? "output" : "output-" + first + (first == last ? "" : "-" + last);

    private static void AssertLabel(QpdfDocument document, PdfArray labels, int local, int absolute)
    {
        int chosen = 0;
        for (int at = 2; at < labels.Count; at += 2)
        { if (Assert.IsType<PdfInteger>(labels[at]).Value <= local) { chosen = at; } }
        long startAt = Assert.IsType<PdfInteger>(labels[chosen]).Value;
        PdfDictionary label = Assert.IsType<PdfDictionary>(Resolve(document, labels[chosen + 1]));
        string style = absolute < 3 ? "D" : "r", prefix = absolute < 3 ? "A-" : "B-";
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes(style), Assert.IsType<PdfName>(Read(document, label, "S")).RawSpan.ToArray());
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes(prefix), Assert.IsType<PdfByteString>(Read(document, label, "P")).Value);
        long start = Assert.IsType<PdfInteger>(Read(document, label, "St")).Value;
        Assert.Equal(absolute < 3 ? 40L + absolute : 4L + absolute, start + local - startAt);
    }

    private static byte[] Source(string kind)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R" + (kind == "none" ? "" : " /PageLabels 10 0 R") + " >>", null),
            (2, "<< /Type /Pages /Count 4 /Kids [3 0 R 4 0 R 5 0 R 6 0 R] >>", null),
        ];
        for (int page = 1; page <= 4; page++)
        { objects.Add((page + 2, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /OriginalLabels 10 0 R /AuditPage " + page + " >>", null)); }
        objects.AddRange([(7, "null", null), (8, "null", null), (9, "null", null),
            (10, "<< /Nums [0 11 0 R] >>", null),
            (11, "<< " + (kind == "numbered" ? "/S /D " : "") + "/P 12 0 R /St "
                + (kind == "overflow" ? "9223372036854775807" : "7") + " >>", null), (12, "(Source-)", null)]);
        return CheckTests.Assemble(objects, 1);
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static QpdfObject Read(QpdfDocument document, PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(document, value); }

    private sealed class SplitFailureHost(InMemoryQpdfHost host, IMemoryScope owner, RecordingMemoryBudget budget,
        CancellationTokenSource cancel, string mode) : IQpdfHost, IQpdfMemoryHost, IDisposable
    {
        private int _created;
        private IMemoryReservation? _pressure;
        public IMemoryBudget? MemoryBudget => host.MemoryBudget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => host.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            int created = ++_created;
            if (created == 2 && mode == "cancel") { cancel.Cancel(); token.ThrowIfCancellationRequested(); }
            if (created == 2 && mode == "refuse") { Assert.True(owner.TryReserve(budget.LimitBytes - budget.ReservedBytes, out _pressure)); }
            IQpdfOutputTransaction transaction = await host.CreateOutputAsync(path, token);
            return created == 2 ? new SplitFailureOutput(transaction, mode) : transaction;
        }
        public void Dispose() { _pressure?.Dispose(); _pressure = null; }
    }

    private sealed class SplitFailureOutput(IQpdfOutputTransaction inner, string mode) : IQpdfOutputTransaction
    {
        public QpdfPath Path => inner.Path;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
            => mode == "write" ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed)) : inner.WriteAsync(data, token);
        public ValueTask CommitAsync(CancellationToken token)
            => mode == "commit" ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed)) : inner.CommitAsync(token);
        public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

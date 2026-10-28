using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayGenerationTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output");

    private static byte[] Target() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1);

    private static string NextValues(int live, int stale, string order) => order switch
    {
        "stale-first" => $"6 {stale} R 6 {live} R",
        "two-live" => $"6 {live} R 6 {live} R",
        "null" => $"6 {live} R null",
        "shared" => $"6 {live} R 6 {stale} R 6 {live} R",
        _ => $"6 {live} R 6 {stale} R",
    };

    private static byte[] Actions(int live, int stale, string order) => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Annots [5 0 R] >>", null),
        (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
        (5, "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /Named /N /FirstPage /Next [" + NextValues(live, stale, order) + "] >> >>", null),
        (6, "<< /S /Named /N /NextPage >>", null)], 1, new Dictionary<int, int> { [6] = live });

    private static byte[] Fields(int depth, int fieldGeneration, int widgetGeneration)
    {
        string root = $"5 {fieldGeneration} R", widget = depth == 0 ? root : $"6 {widgetGeneration} R";
        string appearance = "/Type /Annot /Subtype /Widget /Rect [0 0 10 10] /AP << /N 9 0 R >>";
        string field = "/FT /Tx /T (Field) /V (kept)";
        byte[] data = "BT /F1 10 Tf 0 0 Td (kept) Tj ET\n"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 10 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Annots [" + widget + "] >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, depth == 0 ? "<< " + appearance + " " + field + " >>" : "<< " + field + " /Kids [" + (depth == 2 ? "7 1 R" : widget) + "] >>", null),
            (6, depth == 0 ? "null" : "<< " + appearance + " /Parent " + (depth == 2 ? "7 1 R" : root) + " >>", null),
            (7, depth == 2 ? "<< /Parent " + root + " /T (Child) /Kids [" + widget + "] >>" : "null", null),
            (8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (9, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /Font << /F1 8 0 R >> >> /Length " + data.Length + " >>", data),
            (10, "<< /Fields [" + root + "] /DR << /Font << /F1 8 0 R >> >> /DA (/F1 10 Tf 0 g) >>", null)],
            1, new Dictionary<int, int> { [5] = fieldGeneration, [6] = widgetGeneration, [7] = 1 });
    }

    private static void Seed(InMemoryQpdfHost host, byte[] source)
    {
        host.AddInput(QpdfPath.From("target"), Target(), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("stamp"), source, QpdfInputOwnership.Copy);
    }

    private static Task<QpdfExecutionResult> Run(IQpdfHost host, bool job, string mode, bool repeated, CancellationToken token)
    {
        string range = repeated ? "1,1" : "1";
        return job
            ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object> {
                ["inputFile"] = "target", ["outputFile"] = "output", [mode] = new { file = "stamp", to = range, from = range } }), token).AsTask()
            : Qpdf.ExecuteAsync(host, new(["target", "--" + mode, "stamp", "--to=" + range, "--from=" + range, "--", "output"]), token).AsTask();
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static QpdfObject Entry(PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value;
    }

    public static IEnumerable<object[]> ActionCases()
    {
        foreach ((int live, int stale) in new[] { (2, 0), (2, 1), (0, 2) })
        foreach (string order in new[] { "live-first", "stale-first", "two-live", "null", "shared" })
        foreach (bool job in new[] { false, true })
        foreach (string mode in new[] { "overlay", "underlay" })
        foreach (bool repeated in new[] { false, true })
        { yield return [live, stale, order, job, mode, repeated]; }
    }

    [Theory]
    [MemberData(nameof(ActionCases))]
    public async Task StalePairsStayNullAndLiveSharingStaysWithinItsPlacement(int live, int stale, string order, bool job, string mode, bool repeated)
    {
        byte[] output;
        using (InMemoryQpdfHost host = new())
        {
            Seed(host, Actions(live, stale, order));
            QpdfExecutionResult result = await Run(host, job, mode, repeated, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(Output, out output)); Assert.Equal(0L, host.OpenLeaseCount);
        }
        using InMemoryQpdfHost reader = new();
        reader.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, QpdfPath.From("read"), CancellationToken.None);
        PdfArray annots = Assert.IsType<PdfArray>(Resolve(document, Entry(document.PageReferences[0].Page, "Annots")));
        Assert.Equal(repeated ? 2 : 1, annots.Count);
        HashSet<int> placementActions = [];
        for (int i = 0; i < annots.Count; i++)
        {
            PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(document, annots[i]));
            PdfDictionary action = Assert.IsType<PdfDictionary>(Resolve(document, Entry(annotation, "A")));
            PdfArray next = Assert.IsType<PdfArray>(Resolve(document, Entry(action, "Next")));
            Assert.Equal(order == "shared" ? 3 : 2, next.Count);
            int liveIndex = order == "stale-first" ? 1 : 0;
            PdfReference valid = Assert.IsType<PdfReference>(next[liveIndex]);
            Assert.Equal(0, valid.Generation); Assert.True(placementActions.Add(valid.ObjectNumber));
            PdfDictionary target = Assert.IsType<PdfDictionary>(Resolve(document, valid));
            Assert.Equal("NextPage", Assert.IsType<PdfName>(Entry(target, "N")).DisplayText);
            if (order == "two-live") { Assert.Equal(valid, Assert.IsType<PdfReference>(next[1])); }
            else { Assert.IsType<PdfNull>(next[order == "stale-first" ? 0 : 1]); }
            if (order == "shared") { Assert.Equal(valid, Assert.IsType<PdfReference>(next[2])); }
        }
    }

    public static IEnumerable<object[]> FieldCases()
    {
        foreach (int depth in new[] { 0, 1, 2 })
        foreach ((int field, int widget) in new[] { (0, 0), (2, 0), (2, 3), (0, 3) })
        foreach (bool job in new[] { false, true })
        foreach (string mode in new[] { "overlay", "underlay" })
        foreach (bool repeated in new[] { false, true })
        {
            if (depth == 0 && widget != 0) { continue; }
            yield return [depth, field, widget, job, mode, repeated];
        }
    }

    [Theory]
    [MemberData(nameof(FieldCases))]
    public async Task FieldRegistrationRetainsSourceIdentityAcrossParentsAndPlacements(int depth, int fieldGeneration, int widgetGeneration, bool job, string mode, bool repeated)
    {
        byte[] output;
        using (InMemoryQpdfHost host = new())
        {
            Seed(host, Fields(depth, fieldGeneration, widgetGeneration));
            QpdfExecutionResult result = await Run(host, job, mode, repeated, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(Output, out output)); Assert.Equal(0L, host.OpenLeaseCount);
        }
        using InMemoryQpdfHost reader = new();
        reader.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Copy);
        QpdfDocument document = await QpdfDocument.OpenAsync(reader, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        PdfDictionary form = Assert.IsType<PdfDictionary>(Resolve(document, Entry(catalog, "AcroForm")));
        PdfArray fields = Assert.IsType<PdfArray>(Resolve(document, Entry(form, "Fields")));
        PdfArray annots = Assert.IsType<PdfArray>(Resolve(document, Entry(document.PageReferences[0].Page, "Annots")));
        Assert.Equal(repeated ? 2 : 1, fields.Count); Assert.Equal(fields.Count, annots.Count);
        HashSet<int> roots = [];
        List<PdfDictionary> escaped = [];
        for (int i = 0; i < fields.Count; i++)
        {
            PdfReference root = Assert.IsType<PdfReference>(fields[i]);
            Assert.True(roots.Add(root.ObjectNumber)); Assert.Equal(0, root.Generation);
            PdfDictionary field = Assert.IsType<PdfDictionary>(Resolve(document, root));
            Assert.Equal("Tx", Assert.IsType<PdfName>(Entry(field, "FT")).DisplayText);
            Assert.Equal("kept"u8.ToArray(), Assert.IsType<PdfByteString>(Entry(field, "V")).Value);
            escaped.Add(field);
        }
        HashSet<int> linked = [];
        for (int i = 0; i < annots.Count; i++)
        {
            PdfReference reference = Assert.IsType<PdfReference>(annots[i]);
            for (int parent = 0; parent < depth; parent++)
            {
                PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(document, reference));
                reference = Assert.IsType<PdfReference>(Entry(widget, "Parent"));
            }
            Assert.Contains(reference.ObjectNumber, roots); Assert.True(linked.Add(reference.ObjectNumber));
        }
        await document.DisposeAsync();
        Assert.Equal(0L, reader.OpenLeaseCount);
        foreach (PdfDictionary field in escaped)
        { Assert.Equal("kept"u8.ToArray(), Assert.IsType<PdfByteString>(Entry(field, "V")).Value); }
    }

    private sealed class Host(string fault, IMemoryBudget? budget, CancellationTokenSource? cancel)
        : IQpdfHost, IQpdfMemoryHost, IDisposable
    {
        internal readonly InMemoryQpdfHost Inner = new();
        public IMemoryBudget? MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => Inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Transaction(await Inner.CreateOutputAsync(path, token), fault, cancel);
        public void Dispose() => Inner.Dispose();
        private sealed class Transaction(IQpdfOutputTransaction inner, string fault, CancellationTokenSource? cancel) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                if (fault == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                if (fault == "cancel") { cancel?.Cancel(); token.ThrowIfCancellationRequested(); }
                return inner.WriteAsync(bytes, token);
            }
            public ValueTask CommitAsync(CancellationToken token)
                => fault == "commit" ? throw new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static byte[] FailureSource(string kind) => kind == "actions" ? Actions(2, 0, "shared") : Fields(kind == "merged" ? 0 : 2, 2, 3);
    private static async Task SeedFailure(Host host, string kind)
    {
        Seed(host.Inner, FailureSource(kind));
        await using IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(Output, CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }
    private static void AssertUnpublished(Host host, QpdfExecutionResult result)
    {
        Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
    }

    [Theory]
    [InlineData("actions")]
    [InlineData("merged")]
    [InlineData("parents")]
    public async Task EveryAdmissionRefusalPreservesPriorOutputAndRefundsPrivateStorage(string kind)
    {
        int growths;
        RecordingMemoryBudget baseline = new(16777216L);
        using (IMemoryScope owner = baseline.CreateScope())
        using (Host host = new("", owner, null))
        {
            await SeedFailure(host, kind);
            QpdfExecutionResult result = await Run(host, false, "overlay", true, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); growths = baseline.GrowthCalls; Assert.True(growths > 0);
        }
        Assert.Equal(0L, baseline.ReservedBytes);
        for (int refused = 1; refused <= growths; refused++)
        {
            RecordingMemoryBudget budget = new(16777216L) { RefuseOnGrowth = refused };
            using (IMemoryScope owner = budget.CreateScope())
            using (Host host = new("", owner, null))
            {
                await SeedFailure(host, kind);
                QpdfExecutionResult result = await Run(host, false, "overlay", true, CancellationToken.None);
                Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
                Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
                AssertUnpublished(host, result);
            }
            Assert.Equal(0L, budget.ReservedBytes);
        }
    }

    public static IEnumerable<object[]> OutputFailures()
    {
        foreach (string kind in new[] { "actions", "merged", "parents" })
        foreach (string fault in new[] { "write", "commit", "cancel" }) { yield return [kind, fault]; }
    }

    [Theory]
    [MemberData(nameof(OutputFailures))]
    public async Task GenerationAwareCopyKeepsRollbackAndCancellation(string kind, string fault)
    {
        using CancellationTokenSource cancel = new();
        using Host host = new(fault, null, cancel);
        await SeedFailure(host, kind);
        QpdfExecutionResult result = await Run(host, true, "underlay", true, cancel.Token);
        Assert.Equal(fault == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        if (fault != "cancel") { Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.HostOperationFailed); }
        AssertUnpublished(host, result);
    }
}

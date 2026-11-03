using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class GenerationReadinessTests
{
    private static byte[] Fixture(string kind, bool needs)
    {
        byte[] stale = "/Tx BMC BT /F1 10 Tf (STALE) Tj ET EMC\n"u8.ToArray();
        if (kind == "malformed") { stale = "/Tx BMC BT /F1 10 Tf (STALE) Tj ET\n"u8.ToArray(); }
        byte[] note = "0 0 1 rg 0 0 8 8 re f % NOTE\n"u8.ToArray();
        string first = kind == "bad" ? "/DA (nonsense)" : "/DA (/F1 10 Tf 0 g)";
        string second = kind is "good" or "malformed" ? "/DA (/F1 10 Tf 0 g)" : "/DA (nonsense)";
        string field = kind is "radio" or "push"
            ? "/FT /Btn /Ff " + (kind == "radio" ? "32768" : "65536") + " /V /On /AS /Off /AP << /N << /Off 5 0 R /On 5 0 R >> >>"
            : "/FT /Tx /V (LIVE2) " + second + " /AP << /N 5 0 R >>";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 8 0 R] /NeedAppearances " + (needs ? "true" : "false") + " /DR << /Font << /F1 6 0 R >> >> >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 7 0 R] /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [4 0 R 9 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Widget /F 4 /FT /Tx /T (first) /V (LIVE1) /Rect [10 20 150 60] " + first + " /AP << /N 5 0 R >> >>", null),
            (5, "<< /Subtype /Form /BBox [0 0 140 40] /Resources << /Font << /F1 6 0 R >> >> /Length " + stale.Length + " >>", stale),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (7, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [8 0 R] >>", null),
            (8, "<< /Type /Annot /Subtype /Widget /F 4 /T (second) /Rect [10 20 150 60] " + field + " >>", null),
            (9, "<< /Type /Annot /Subtype /Text /F 4 /Rect [160 20 180 40] /AP << /N 10 0 R >> >>", null),
            (10, "<< /Subtype /Form /BBox [0 0 8 8] /Resources << >> /Length " + note.Length + " >>", note)], 1);
    }

    private static Task<QpdfExecutionResult> Run(IQpdfHost host, bool job, string path, string mode, bool import,
        QpdfLimits limits, CancellationToken token)
    {
        List<string> argv = ["input", "--generate-appearances", "--compress-streams=n"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["generateAppearances"] = true, ["compressStreams"] = "n" };
        if (mode != "generate") { argv.Add("--flatten-annotations=" + mode); settings["flattenAnnotations"] = mode; }
        if (path is "select" or "merge")
        {
            argv.AddRange(["--pages", ".", "1-z"]);
            List<object> pages = [new { file = ".", range = "1-z" }];
            if (path == "merge") { argv.AddRange(["other", "1-z"]); pages.Add(new { file = "other", range = "1-z" }); }
            argv.Add("--"); settings["pages"] = pages;
        }
        if (path == "split") { argv.Add("--split-pages=1"); settings["splitPages"] = "1"; }
        if (import) { argv.AddRange(["--overlay", "other", "--"]); settings["overlay"] = new { file = "other" }; }
        argv.Add("output");
        return job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), new(limits), token).AsTask()
            : Qpdf.ExecuteAsync(host, new(argv), new(limits), token).AsTask();
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static QpdfObject Get(QpdfDocument document, PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(document, value); }

    private static async Task Verify(byte[] bytes, bool failed, bool flatten, int widgets, bool staleExpected, bool noteExpected)
    {
        List<PdfStream> programs = []; int retained = 0; bool noteStamped = false;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Copy);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
            {
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
                if (catalog.TryGet(PdfName.FromAscii("AcroForm"), out QpdfObject formRaw))
                {
                    PdfDictionary form = Assert.IsType<PdfDictionary>(Resolve(document, formRaw));
                    bool needs = form.TryGet(PdfName.FromAscii("NeedAppearances"), out QpdfObject flag)
                        && Resolve(document, flag) is PdfBoolean boolean && boolean.Value;
                    Assert.Equal(failed, needs);
                }
                else { Assert.False(failed || !flatten); }
                foreach (var location in document.PageReferences)
                {
                    PdfDictionary page = location.Page;
                    if (page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annotsRaw))
                    {
                        PdfArray annots = Assert.IsType<PdfArray>(Resolve(document, annotsRaw));
                        for (int index = 0; index < annots.Count; index++)
                        {
                            PdfDictionary annot = Assert.IsType<PdfDictionary>(Resolve(document, annots[index]));
                            if (Get(document, annot, "Subtype") is not PdfName name || name.DisplayText != "Widget") { continue; }
                            retained++;
                            PdfDictionary appearance = Assert.IsType<PdfDictionary>(Get(document, annot, "AP"));
                            QpdfObject normal = Get(document, appearance, "N");
                            if (normal is PdfDictionary states)
                            { normal = Get(document, states, Assert.IsType<PdfName>(Get(document, annot, "AS")).DisplayText); }
                            programs.Add(Assert.IsType<PdfStream>(normal));
                        }
                    }
                    PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(document, page, "Resources"));
                    if (!resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject objectsRaw)) { continue; }
                    PdfDictionary objects = Assert.IsType<PdfDictionary>(Resolve(document, objectsRaw));
                    foreach (PdfName name in objects.Keys)
                    {
                        PdfStream stream = Assert.IsType<PdfStream>(Get(document, objects, name.DisplayText));
                        string text = Encoding.ASCII.GetString(stream.RawMemory.Span);
                        noteStamped |= text.Contains("NOTE", StringComparison.Ordinal);
                        if (name.DisplayText.StartsWith("Fxo", StringComparison.Ordinal)) { programs.Add(stream); }
                    }
                }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        Assert.Equal(flatten && !failed ? 0 : widgets, retained);
        string[] textPrograms = programs.Select(p => Encoding.ASCII.GetString(p.RawMemory.Span)).ToArray();
        Assert.Equal(staleExpected, textPrograms.Any(p => p.Contains("STALE", StringComparison.Ordinal)));
        Assert.Equal(noteExpected, noteStamped);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool job in new[] { false, true })
        foreach (string path in new[] { "whole", "select", "merge", "split" })
        foreach (string kind in new[] { "good", "bad", "mixed", "radio", "push" })
        foreach (string mode in new[] { "generate", "all" }) { yield return [job, path, kind, mode]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OnlyCompletedGenerationClearsReadiness(bool job, string path, string kind, string mode)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(kind, true), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("other"), Fixture("good", false), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, path, mode, false, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(0L, host.OpenLeaseCount);
        if (kind == "good") { Assert.Empty(result.Diagnostics); } else { Assert.NotEmpty(result.Diagnostics); }
        if (path == "split")
        {
            for (int page = 1; page <= 2; page++)
            {
                Assert.True(host.TryGetOutput(QpdfPath.From("output-" + page), out byte[] output));
                // Each group keeps the conservative document-wide readiness result.
                await Verify(output, kind != "good", mode != "generate", 1,
                    kind != "good" && (kind == "bad" || page == 2), mode != "generate" && page == 1);
            }
        }
        else
        {
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            await Verify(output, kind != "good", mode != "generate", path == "merge" ? 4 : 2, kind != "good", mode != "generate");
        }
    }

    [Theory]
    [InlineData(false, "whole", "print")]
    [InlineData(true, "select", "screen")]
    [InlineData(false, "merge", "screen")]
    [InlineData(true, "whole", "print")]
    public async Task SkippedWidgetsStayWhileNonwidgetsFlatten(bool job, string path, string mode)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("mixed", true), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("other"), Fixture("good", false), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, path, mode, false, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        await Verify(output, true, true, path == "merge" ? 4 : 2, true, true);
    }

    [Theory]
    [InlineData(false, "whole", false)]
    [InlineData(true, "whole", true)]
    [InlineData(false, "select", true)]
    [InlineData(true, "select", false)]
    public async Task ImportedReadinessParticipatesInGenerationAndFlattening(bool job, string path, bool primaryNeeds)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("good", primaryNeeds), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("other"), Fixture("mixed", true), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, path, "all", true, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); await Verify(output, true, true, 4, true, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedProgramsKeepReadinessAndTheirOriginalStorage(bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("malformed", true), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Run(host, job, "whole", "all", false, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("malformed appearance", StringComparison.Ordinal));
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); await Verify(output, true, true, 2, true, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedReadinessGuardsFlatteningWithoutGeneration(bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("good", false), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("other"), Fixture("mixed", true), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", flattenAnnotations = "all", compressStreams = "n", overlay = new { file = "other" } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--compress-streams=n", "--flatten-annotations=all", "--overlay", "other", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); await Verify(output, true, true, 4, true, true);
    }

    private sealed class FailureHost(InMemoryQpdfHost inner, IMemoryBudget budget, string phase, CancellationTokenSource cancellation)
        : IQpdfHost, IQpdfMemoryHost
    {
        internal int Closed;
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Transaction(this, await inner.CreateOutputAsync(path, token), phase, cancellation);
        private sealed class Transaction(FailureHost host, IQpdfOutputTransaction inner, string phase, CancellationTokenSource cancellation) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                if (phase == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                if (phase == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                return inner.WriteAsync(bytes, token);
            }
            public ValueTask CommitAsync(CancellationToken token)
                => phase == "commit" ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed)) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() { host.Closed++; return inner.DisposeAsync(); }
        }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("commit")]
    [InlineData("cancel")]
    [InlineData("local")]
    [InlineData("shared")]
    public async Task PartialGenerationKeepsFailuresTransactionalAndRefundsAdmission(string phase)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input"), Fixture("mixed", true), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        RecordingMemoryBudget root = new(phase == "shared" ? 16384 : 16777216);
        using CancellationTokenSource cancellation = new();
        using (IMemoryScope caller = root.CreateScope())
        {
            FailureHost host = new(storage, caller, phase, cancellation);
            QpdfLimits limits = phase == "local" ? QpdfLimits.Default with { MaxMemoryBytes = 16384 } : QpdfLimits.Default;
            QpdfExecutionResult result = await Run(host, true, "whole", "all", false, limits, cancellation.Token);
            Assert.Equal(phase == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            if (phase is "local" or "shared") { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded); }
            else { Assert.Equal(1, host.Closed); }
            Assert.Equal(0L, storage.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.True(storage.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}

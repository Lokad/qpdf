using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentCopyRecoveryTests
{
    private static byte[] Plain() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null)], 1);

    private static byte[] Source(bool direct, bool shared)
    {
        string spec = "<< /F (bad) /EF << /F 6 0 R >> /Child 7 0 R /Bad 1 0 R >>";
        string names = (shared ? "(A-kept) 9 0 R " : "") + "(B-bad) " + (direct ? spec : "5 0 R")
            + " (C-alias) 5 0 R" + (shared ? " (D-good) 10 0 R" : "");
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null),
            (4, "<< /Names [" + names + "] >>", null), (5, spec, null),
            (6, "<< /Type /EmbeddedFile /Length 1 >>", "X"u8.ToArray()),
            (7, "<< /Broken true /Back 5 0 R >>", null), (8, "null", null),
            (9, "<< /F (keep) /EF << /F 6 0 R >> >>", null),
            (10, "<< /F (later) /EF << /F 6 0 R >> >>", null)], 1);
    }

    private static void AddInputs(InMemoryQpdfHost host, bool existing, bool direct, bool shared)
    {
        byte[] plain = Plain(), source = Source(direct, shared);
        host.AddInput(QpdfPath.From("input"), existing ? source : plain, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("source"), existing ? plain : source, QpdfInputOwnership.Transfer);
    }

    private static ValueTask<QpdfExecutionResult> Execute(IQpdfHost host, string route, string mode, CancellationToken token)
    {
        bool linear = mode.StartsWith("linear", StringComparison.Ordinal);
        string streams = mode.EndsWith("streamed", StringComparison.Ordinal) ? "generate" : "disable";
        if (route == "argv")
        {
            List<string> argv = ["input", "--copy-attachments-from", "source", "--", "--object-streams=" + streams];
            if (linear) { argv.Add("--linearize"); }
            argv.AddRange(["--", "output"]);
            return Qpdf.ExecuteAsync(host, new(argv), token);
        }
        Dictionary<string, object> job = new()
        {
            ["inputFile"] = "input", ["outputFile"] = "output", ["objectStreams"] = streams,
            ["copyAttachmentsFrom"] = new[] { new { file = "source" } }
        };
        if (linear) { job["linearize"] = true; }
        return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), token);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool existing in new[] { false, true })
        foreach (bool direct in new[] { false, true })
        foreach (bool shared in new[] { false, true })
        {
            yield return [existing, direct, shared, "typed", "plain"];
            foreach (string route in new[] { "argv", "job" })
            foreach (string mode in new[] { "plain", "streamed", "linear", "linear-streamed" })
            { yield return [existing, direct, shared, route, mode]; }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SkipsFailedRootsWithoutLosingSuccessfulCopies(bool existing, bool direct, bool shared, string route, string mode)
    {
        using InMemoryQpdfHost host = new();
        AddInputs(host, existing, direct, shared);
        IReadOnlyList<QpdfDiagnostic> warnings;
        if (route == "typed")
        {
            QpdfAttachmentEditResult result = await Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
                [new QpdfAttachmentCopy(QpdfPath.From("source"), null)], CancellationToken.None);
            Assert.Equal(shared ? 2 : 0, result.AttachmentCount);
            warnings = result.Warnings;
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, route, mode, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
            warnings = result.Diagnostics;
        }
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, x => Assert.Equal("Skipped unpreservable attachment.", x.Message));
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("reread"), bytes, QpdfInputOwnership.Transfer);
        IReadOnlyList<QpdfAttachmentInfo> listed = await Qpdf.ListAttachmentsAsync(host, QpdfPath.From("reread"), CancellationToken.None);
        Assert.Equal(shared ? ["A-kept", "D-good"] : Array.Empty<string>(), listed.Select(x => x.Key));
        if (shared)
        {
            Assert.Single(listed.Select(x => x.StreamObjectNumber).Distinct());
            foreach (QpdfAttachmentInfo item in listed)
            {
                _ = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("reread"), item.Key, QpdfPath.From("extracted"), CancellationToken.None);
                Assert.True(host.TryGetOutput(QpdfPath.From("extracted"), out byte[] payload));
                Assert.Equal("X"u8.ToArray(), payload);
            }
        }
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("reread"), CancellationToken.None);
        Assert.All(check.Checks, x => Assert.NotEqual(QpdfCheckStatus.Failed, x.Status));
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread"), CancellationToken.None))
        {
            List<int> ids = [];
            foreach ((int id, XrefEntry entry) in document.Engine.XRefEntries)
            {
                if (id == 0 || entry.Kind == XrefEntryKind.Free) { continue; }
                ids.Add(id);
                QpdfObject value = document.GetObject(id);
                PdfDictionary? dictionary = value as PdfDictionary ?? (value as PdfStream)?.Dictionary;
                Assert.False(dictionary is not null && dictionary.TryGet(PdfName.FromAscii("Broken"), out _));
            }
            if (mode == "plain") { Assert.Equal(Enumerable.Range(1, ids.Count), ids.Order()); }
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, "typed", "write")]
    [InlineData(true, "typed", "commit")]
    [InlineData(false, "argv", "write")]
    [InlineData(true, "job", "commit")]
    [InlineData(false, "typed", "cancel")]
    [InlineData(true, "argv", "cancel")]
    public async Task PublicationFailureAfterSkipsPreservesPriorOutput(bool existing, string route, string fault)
    {
        using InMemoryQpdfHost inner = new();
        AddInputs(inner, existing, true, true);
        await using (IQpdfOutputTransaction prior = await inner.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        using CancellationTokenSource cancellation = new();
        SelectiveAttachmentExtractionTests.FaultHost host = new(inner, fault, cancellation);
        if (route == "typed")
        {
            Exception? error = await Record.ExceptionAsync(() => Qpdf.CopyAttachmentsAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
                [new QpdfAttachmentCopy(QpdfPath.From("source"), null)], cancellation.Token).AsTask());
            if (fault == "cancel") { Assert.IsAssignableFrom<OperationCanceledException>(error); }
            else { Assert.IsAssignableFrom<QpdfException>(error); }
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, route, "linear", cancellation.Token);
            Assert.Equal(fault == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        }
        Assert.Equal(1, host.Created);
        Assert.True(inner.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0, inner.OpenLeaseCount);
    }

    private sealed class BudgetHost(InMemoryQpdfHost inner, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => inner.CreateOutputAsync(path, token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedRefusalRemainsFatalAcrossRecoveryAndPublication(bool existing)
    {
        int failures = 0;
        bool reachedRecovery = false, completed = false;
        for (int point = 1; point <= 16; point++)
        {
            RecordingMemoryBudget root = new(16L << 20) { RefuseOnGrowth = point };
            using (IMemoryScope caller = root.CreateScope())
            using (InMemoryQpdfHost inner = new())
            {
                AddInputs(inner, existing, true, true);
                QpdfExecutionResult result = await Execute(new BudgetHost(inner, caller), "argv", "linear", CancellationToken.None);
                if (result.Status == QpdfExecutionStatus.Failed)
                {
                    failures += 1;
                    Assert.Contains(result.Diagnostics, x => x.Code == QpdfDiagnosticCode.BudgetExceeded);
                    reachedRecovery |= result.Diagnostics.Any(x => x.Message == "Skipped unpreservable attachment.");
                    Assert.False(inner.TryGetOutput(QpdfPath.From("output"), out _));
                }
                else
                {
                    Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
                    Assert.True(inner.TryGetOutput(QpdfPath.From("output"), out _));
                    completed = true;
                }
                Assert.Equal(0, inner.OpenLeaseCount);
            }
            Assert.Equal(0L, root.ReservedBytes);
            if (completed) { break; }
        }
        Assert.True(completed);
        Assert.True(reachedRecovery);
        Assert.True(failures > 0);
    }
}

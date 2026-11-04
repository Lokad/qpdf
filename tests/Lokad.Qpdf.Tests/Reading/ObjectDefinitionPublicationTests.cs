using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class ObjectDefinitionPublicationTests
{
    private static byte[] Source(string shape, bool compressed)
    {
        string body = shape switch
        {
            "scalar" => "42", "array" => "[7 0 R]", "dictionary" => "<< /Value 7 0 R >>",
            "reference" or "cycle" => "7 0 R", "self" => "6 0 R",
            _ => throw new ArgumentException("Unknown definition fixture.", nameof(shape)),
        };
        string second = shape == "cycle" ? "6 0 R" : "<< /Marker (Keep) >>";
        const string extras = "/Resources << >> /Audit 6 0 R";
        return compressed ? ReaderAcceptanceTests.ObjectStreamPdf(body, second, true, true, null, extras)
            : CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " + extras + " >>", null),
                (4, "null", null), (5, "null", null), (6, body, null), (7, second, null)], 1);
    }

    private static Task<QpdfExecutionResult> Publish(IQpdfHost host, bool job, bool strict, bool json, QpdfLimits limits)
    {
        List<string> arguments = ["input"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
        if (strict) { arguments.Add("--suppress-recovery"); settings["suppressRecovery"] = true; }
        if (json) { arguments.Add("--json-output=2"); settings["jsonOutput"] = "2"; }
        arguments.AddRange(["--", "output"]);
        return (job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), new QpdfExecutionOptions(limits), CancellationToken.None)
            : Qpdf.ExecuteAsync(host, new(arguments), new QpdfExecutionOptions(limits), CancellationToken.None)).AsTask();
    }

    private static async Task Seed(InMemoryQpdfHost host)
    {
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
        await prior.CommitAsync(CancellationToken.None);
    }

    public static IEnumerable<object[]> Publications()
    {
        foreach (string shape in new[] { "scalar", "array", "dictionary", "reference", "self", "cycle" })
        foreach (bool compressed in new[] { false, true })
        foreach (bool strict in new[] { false, true })
        foreach (bool job in new[] { false, true })
        foreach (bool json in new[] { false, true }) { yield return [shape, compressed, strict, job, json]; }
    }

    [Theory]
    [MemberData(nameof(Publications))]
    public async Task DefinitionsHaveOnePolicyAcrossStorageAndPublication(string shape, bool compressed, bool strict, bool job, bool json)
    {
        bool invalid = shape is "reference" or "self" or "cycle";
        RecordingMemoryBudget budget = new(32L << 20);
        using IMemoryScope owner = budget.CreateScope();
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        {
            host.AddInput(QpdfPath.From("input"), Source(shape, compressed), QpdfInputOwnership.Transfer);
            await Seed(host);
            QpdfExecutionResult result = await Publish(host, job, strict, json, QpdfLimits.Default);
            Assert.Equal(invalid && strict ? QpdfExitCode.Error : invalid ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            if (invalid && strict)
            {
                Assert.Equal("prior"u8.ToArray(), output);
                Assert.Contains(result.Diagnostics, d => d.Message.Contains("indirect reference", StringComparison.Ordinal));
            }
            else
            {
                if (invalid) { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.RepairedObject && d.Message.Contains("reference", StringComparison.Ordinal)); }
                else { Assert.Empty(result.Diagnostics); }
                host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
                if (json)
                {
                    await Qpdf.CreatePdfFromJsonAsync(host, QpdfPath.From("read"), QpdfPath.From("imported"), CancellationToken.None);
                    Assert.True(host.TryGetOutput(QpdfPath.From("imported"), out output));
                    host.AddInput(QpdfPath.From("pdf"), output, QpdfInputOwnership.Transfer);
                }
                await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From(json ? "pdf" : "read"), CancellationToken.None);
                Assert.Equal(1, document.PageCount);
                PdfDictionary page = document.PageReferences[0].Page;
                QpdfObject value = page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject raw) ? raw : PdfNull.Instance;
                QpdfObject Resolve(QpdfObject item) => item is PdfReference reference ? document.GetObject(reference.ObjectNumber) : item;
                value = Resolve(value);
                if (invalid) { Assert.Same(PdfNull.Instance, value); }
                else if (shape == "scalar") { Assert.Equal(42L, Assert.IsType<PdfInteger>(value).Value); }
                else
                {
                    QpdfObject nested;
                    if (value is PdfArray array) { nested = Assert.Single(array.Items); }
                    else { Assert.True(Assert.IsType<PdfDictionary>(value).TryGet(PdfName.FromAscii("Value"), out nested)); }
                    Assert.True(Assert.IsType<PdfDictionary>(Resolve(nested)).TryGet(PdfName.FromAscii("Marker"), out QpdfObject marker));
                    Assert.Equal("Keep"u8.ToArray(), Assert.IsType<PdfByteString>(marker).Value);
                }
            }
        }
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LazyStrictFailureAndLegalValuesHaveBoundedLifetimes(bool compressed)
    {
        PdfByteString escaped;
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(QpdfPath.From("input"), Source("reference", compressed), QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
            {
                Assert.Equal(1, document.PageCount);
                Assert.Throws<QpdfParseException>(() => document.GetObject(6));
            }
            host.AddInput(QpdfPath.From("control"), Source("array", compressed), QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("control"), CancellationToken.None))
            {
                Assert.True(Assert.IsType<PdfDictionary>(document.GetObject(7)).TryGet(PdfName.FromAscii("Marker"), out QpdfObject marker));
                escaped = Assert.IsType<PdfByteString>(marker);
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        Assert.Equal("Keep"u8.ToArray(), escaped.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticOverflowPreservesThePreviousTarget(bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Source("cycle", true), QpdfInputOwnership.Transfer);
        await Seed(host);
        QpdfExecutionResult result = await Publish(host, job, false, true, QpdfLimits.Default with { MaxDiagnostics = 1L });
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        Assert.Equal("prior"u8.ToArray(), output); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RefusalAndOutputFaultsAfterRepairRetainTheTargetAndRefund(bool job, bool json)
    {
        async Task<int> Attempt(int refuseAt, string fault)
        {
            RecordingMemoryBudget budget = new(32L << 20) { RefuseOnGrowth = refuseAt };
            using IMemoryScope owner = budget.CreateScope();
            using (InMemoryQpdfHost host = new())
            {
                host.AddInput(QpdfPath.From("input"), Source("cycle", true), QpdfInputOwnership.Transfer);
                await Seed(host);
                QpdfExecutionResult result = await Publish(new View(host, owner, fault), job, false, json, QpdfLimits.Default);
                bool failed = refuseAt != 0 || fault != "none";
                Assert.Equal(failed ? QpdfExitCode.Error : QpdfExitCode.Warning, result.ExitCode);
                if (failed)
                {
                    Assert.Contains(result.Diagnostics, d => d.Code == (refuseAt != 0 ? QpdfDiagnosticCode.BudgetExceeded : QpdfDiagnosticCode.HostOperationFailed));
                    Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
                    Assert.Equal("prior"u8.ToArray(), output);
                }
                Assert.Equal(0L, host.OpenLeaseCount);
            }
            int growths = budget.GrowthCalls;
            owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
            return growths;
        }
        int growths = await Attempt(0, "none"); Assert.InRange(growths, 1, 64);
        for (int index = 1; index <= growths; index++) { await Attempt(index, "none"); }
        await Attempt(0, "write"); await Attempt(0, "commit");
    }

    private sealed class View(InMemoryQpdfHost host, IMemoryBudget budget, string fault) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => host.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Output(await host.CreateOutputAsync(path, token), fault);
    }

    private sealed class Output(IQpdfOutputTransaction inner, string fault) : IQpdfOutputTransaction
    {
        public QpdfPath Path => inner.Path;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token) => fault == "write"
            ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed)) : inner.WriteAsync(data, token);
        public ValueTask CommitAsync(CancellationToken token) => fault == "commit"
            ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed)) : inner.CommitAsync(token);
        public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Filters;
using Lokad.Qpdf.Tests.Inspection;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class PracticalMemoryTests(ITestOutputHelper output)
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly QpdfPath Target = QpdfPath.From("output.pdf");
    private static readonly QpdfLimits Limits = QpdfLimits.Default with { MaxMemoryBytes = 32L * 1024L * 1024L };

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static InMemoryQpdfHost Host(byte[] input)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(Input, input, QpdfInputOwnership.Copy);
        return host;
    }

    private void Report(string name, QpdfExecutionResult result, long allocated)
    {
        Assert.InRange(result.Counters.PeakMemoryBytes, 0L, Limits.MaxMemoryBytes);
        Assert.Equal(0L, result.Counters.OpenHandles);
        output.WriteLine("{0}: peak estimate={1}, closing charge={2}, cumulative allocation={3}", name,
            result.Counters.PeakMemoryBytes, result.Counters.LiveMemoryBytes, allocated);
    }

    private static async Task ValidatePdf(InMemoryQpdfHost host, int pages)
    {
        Assert.True(host.TryGetOutput(Target, out byte[] bytes));
        using (UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(bytes))
        { Assert.Equal(pages, independent.NumberOfPages); }
        host.AddInput(QpdfPath.From("reopened.pdf"), bytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reopened.pdf"), CancellationToken.None))
        { Assert.Equal(pages, document.PageCount); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static byte[] Contents(QpdfDocument document, int page)
    {
        Assert.True(document.PageReferences[page].Page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
        return Decoded(Assert.IsType<PdfStream>(Resolve(document, contents)));
    }

    private static byte[] Decoded(PdfStream stream)
    {
        if (!stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter)) { return stream.Data; }
        Assert.Equal("FlateDecode", Assert.IsType<PdfName>(filter).DisplayText);
        using MemoryStream source = new(stream.Data);
        using ZLibStream decoder = new(source, CompressionMode.Decompress);
        using MemoryStream decoded = new();
        decoder.CopyTo(decoded);
        return decoded.ToArray();
    }

    private static PdfReference ImageReference(QpdfDocument document, int page)
    {
        Assert.True(document.PageReferences[page].Page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resources));
        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(Resolve(document, resources));
        Assert.True(dictionary.TryGet(PdfName.FromAscii("XObject"), out QpdfObject xobjects));
        PdfDictionary images = Assert.IsType<PdfDictionary>(Resolve(document, xobjects));
        Assert.True(images.TryGet(PdfName.FromAscii("Im0"), out QpdfObject image));
        return Assert.IsType<PdfReference>(image);
    }

    [Fact]
    public async Task TwoPageSelectionProducesValidOutputWithinItsEstimate()
    {
        InMemoryQpdfHost host = Host(Fixture("minimal-2page.pdf"));
        long before = GC.GetTotalAllocatedBytes(true);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["input.pdf", "--pages", ".", "2,1", "--", "output.pdf"]),
            new QpdfExecutionOptions(Limits), CancellationToken.None);
        Report("two-page selection", result, GC.GetTotalAllocatedBytes(true) - before);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        await ValidatePdf(host, 2);
        await using QpdfDocument original = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        await using QpdfDocument rewritten = await QpdfDocument.OpenAsync(host, QpdfPath.From("reopened.pdf"), CancellationToken.None);
        Assert.Equal(Contents(original, 1), Contents(rewritten, 0));
        Assert.Equal(Contents(original, 0), Contents(rewritten, 1));
    }

    [Fact]
    public async Task SharedImageResourcesSurviveCompressedLinearizedOutput()
    {
        InMemoryQpdfHost host = Host(Fixture("images-1page.pdf"));
        long before = GC.GetTotalAllocatedBytes(true);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.pdf","outputFile":"output.pdf","pages":[{"file":".","range":"1,1"}],"linearize":"","objectStreams":"generate"}""",
            new QpdfExecutionOptions(Limits), CancellationToken.None);
        Report("shared image/linearization", result, GC.GetTotalAllocatedBytes(true) - before);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        await ValidatePdf(host, 2);
        Assert.True(host.TryGetOutput(Target, out byte[] bytes));
        Assert.Contains("/Linearized", System.Text.Encoding.ASCII.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 1024))), StringComparison.Ordinal);
        // This fixture has only page/root dictionaries and streams. The
        // managed writer keeps those dictionaries direct; native packs the
        // page-tree root. Object-stream presence is a valid layout difference.
        await using (QpdfDocument rewritten = await QpdfDocument.OpenAsync(host, QpdfPath.From("reopened.pdf"), CancellationToken.None))
        await using (QpdfDocument original = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None))
        {
            Assert.Equal(ImageReference(rewritten, 0), ImageReference(rewritten, 1));
            Assert.Equal(Contents(original, 0), Contents(rewritten, 0));
            Assert.Equal(Contents(original, 0), Contents(rewritten, 1));
            PdfStream image = Assert.IsType<PdfStream>(Resolve(rewritten, ImageReference(rewritten, 0)));
            Assert.True(image.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filter));
            Assert.Equal("FlateDecode", Assert.IsType<PdfName>(filter).DisplayText);
            Assert.Equal(Decoded(Assert.IsType<PdfStream>(Resolve(original, ImageReference(original, 0)))), Decoded(image));
        }
        QpdfCheckResult check = await Qpdf.CheckAsync(host, QpdfPath.From("reopened.pdf"), new QpdfCheckOptions(), Limits, CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, outcome => outcome.Name == "linearization").Status);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static async Task RejectDecodedAmplification(byte[] input)
    {
        InMemoryQpdfHost host = Host(input);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Target, CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["input.pdf", "--stream-data=uncompress", "--decode-level=all", "output.pdf"]),
            new QpdfExecutionOptions(Limits with { MaxDecodedBytes = 4096L }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(host.TryGetOutput(Target, out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Fact]
    public async Task DecompressionAndDeclaredImageAmplificationRejectBeforePublication()
    {
        using MemoryStream compressed = new();
        using (ZLibStream encoder = new(compressed, CompressionLevel.Fastest, true))
        { encoder.Write(new byte[1024 * 1024]); }
        byte[] payload = compressed.ToArray();
        await RejectDecodedAmplification(CheckTests.Assemble(
            CheckTests.SinglePage("<< /Length " + payload.Length + " /Filter /FlateDecode >>", payload), 1));
        byte[] jpeg = DctDecodeTests.PatchFrameDimensions(DctDecodeTests.EncodeGrayJpeg(8, 8), 4096, 4096);
        await RejectDecodedAmplification(DctDecodeTests.BuildDctPage(jpeg));
    }

    private sealed record Completed(QpdfExecutionResult Result, WeakReference Host, long Allocated, byte[]? Output);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Completed RunLarge(byte[] input, string outcome)
    {
        InMemoryQpdfHost host = Host(input);
        using CancellationTokenSource canceled = new();
        if (outcome == "cancel") { canceled.Cancel(); }
        QpdfLimits limits = outcome == "failure" ? Limits with { MaxOutputBytes = 128L } : Limits;
        long before = GC.GetTotalAllocatedBytes(true);
        QpdfExecutionResult result = Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["input.pdf", "--stream-data=preserve", "output.pdf"]),
            new QpdfExecutionOptions(limits), canceled.Token).AsTask().GetAwaiter().GetResult();
        Assert.Equal(outcome == "failure" ? QpdfExecutionStatus.Failed
            : outcome == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(0L, host.OpenLeaseCount);
        byte[]? alias = outcome == "success" && host.TryGetOutput(Target, out byte[] bytes) ? bytes : null;
        return new(result, new(host), GC.GetTotalAllocatedBytes(true) - before, alias);
    }

    [Fact]
    [Trait("Layer", "Stress")]
    public async Task RepeatedLargeJobsReleaseHostStorageWhileResultsSurvive()
    {
        byte[] data = new byte[1024 * 1024];
        Array.Fill(data, (byte)' ');
        "0 0 10 10 re S\n% surviving stream payload\n"u8.CopyTo(data);
        byte[] input = CheckTests.Assemble(CheckTests.SinglePage("<< /Length " + data.Length + " >>", data), 1);
        // Exclude abandoned allocations from earlier tests from this small
        // workload's baseline. Collection is observation setup, not a required
        // receipt-retirement schedule or a production operation.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long retainedBefore = GC.GetTotalMemory(false);
        List<Completed> completed = [];
        foreach (string outcome in new[] { "success", "success", "failure", "cancel", "success", "success" })
        {
            Completed job = RunLarge(input, outcome);
            completed.Add(job);
            Report("repeated " + outcome, job.Result, job.Allocated);
        }
        Stopwatch deadline = Stopwatch.StartNew();
        do
        {
            // Observe eligibility of material host payloads, not receipt debt or
            // a prescribed number of collection/maintenance rounds.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (completed.All(job => !job.Host.IsAlive)) { break; }
            Thread.Yield();
        } while (deadline.Elapsed < TimeSpan.FromSeconds(5));
        Assert.All(completed, job => Assert.False(job.Host.IsAlive));
        Assert.All(completed, job => Assert.Equal(0L, job.Result.Counters.OpenHandles));
        long deliberateOutputs = completed.Sum(job => (long)(job.Output?.Length ?? 0));
        long retainedChange = GC.GetTotalMemory(false) - retainedBefore - deliberateOutputs;
        output.WriteLine("Repeated jobs: retained heap change excluding caller outputs={0}", retainedChange);
        // A broad allowance for stable pools and one job's copy overlap. A
        // continuing full-payload leak across this sequence exceeds it.
        Assert.True(retainedChange < 4L * input.Length, $"Repeated jobs retained {retainedChange} unexplained bytes.");
        foreach (Completed job in completed)
        {
            if (job.Output is null) { continue; }
            using UglyToad.PdfPig.PdfDocument surviving = UglyToad.PdfPig.PdfDocument.Open(job.Output);
            Assert.Equal(1, surviving.NumberOfPages);
            InMemoryQpdfHost reopened = Host(job.Output);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(reopened, Input, CancellationToken.None))
            { Assert.Equal(data, Contents(document, 0)); }
            Assert.Equal(0L, reopened.OpenLeaseCount);
        }
        GC.KeepAlive(completed);
    }

    [Fact]
    public async Task ReturnedCounterSnapshotsAndVersionsRemainUsableAfterLaterJobs()
    {
        InMemoryQpdfHost host = Host(Fixture("minimal-2page.pdf"));
        QpdfCheckResult check = await Qpdf.CheckAsync(host, Input, new QpdfCheckOptions(), Limits, CancellationToken.None);
        QpdfExecutionCounters snapshot = check.Counters with { };
        string version = check.Version.ToString();
        QpdfExecutionResult later = await Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.pdf","outputFile":"output.pdf"}""", new QpdfExecutionOptions(Limits), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, later.Status);
        Assert.Equal(snapshot, check.Counters);
        Assert.Equal(version, check.Version.ToString());
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None))
        {
            QpdfExecutionCounters first = document.Counters;
            QpdfExecutionCounters saved = first with { };
            await document.DisposeAsync();
            _ = document.Counters;
            Assert.Equal(saved, first);
            Assert.Equal("1.4", document.HeaderVersion.ToString());
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

using Lokad.Qpdf.Image;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Content;
using Lokad.Qpdf.Tests.Filters;
using Lokad.Qpdf.Tests.Reading;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class PublicCpuCancellationTests(ITestOutputHelper output)
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly QpdfPath Target = QpdfPath.From("output.pdf");
    private static readonly byte[] Prior = "prior"u8.ToArray();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    // Resume the first read before observing admission for CPU processing.
    // The cancellation control requires every read callback to have returned.
    private sealed class Host : IQpdfHost, IQpdfJpegCodecHost, IQpdfMemoryHost, IDisposable
    {
        internal readonly InMemoryQpdfHost Inner = new(QpdfLimits.Default, LokadImageJpegCodec.Instance);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IQpdfJpegCodec? JpegCodec => Inner.JpegCodec;
        internal RecordingMemoryBudget Budget { get; } = new(1L << 30);
        public IMemoryBudget MemoryBudget => Budget;
        internal int PendingReads;
        public void Dispose() { Budget.Dispose(); Inner.Dispose(); }
        internal Task Entered => _entered.Task;
        internal void Resume() => _resume.TrySetResult();

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => new Lease(this, await Inner.OpenInputAsync(path, token));

        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => Inner.CreateOutputAsync(path, token);

        private sealed class Lease(Host host, IQpdfInput inner) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;

            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                Interlocked.Increment(ref host.PendingReads);
                try
                {
                    int bytes = await inner.ReadAsync(offset, destination, token);
                    host._entered.TrySetResult();
                    await host._resume.Task.WaitAsync(token);
                    return bytes;
                }
                finally { Interlocked.Decrement(ref host.PendingReads); }
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private async Task Verify(byte[] pdf, bool job, bool cancel, Func<long, long, bool> reached)
    {
        using Host host = new();
        host.Inner.AddInput(Input, pdf, QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(Target, CancellationToken.None))
        {
            await seed.WriteAsync(Prior, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }

        using CancellationTokenSource cancellation = new();
        QpdfExecutionOptions options = new(QpdfLimits.Default);
        Task<QpdfExecutionResult> running = job
            ? Qpdf.ExecuteJobAsync(host,
                """{"inputFile":"input.pdf","outputFile":"output.pdf","decodeLevel":"all","streamData":"uncompress"}""",
                options, cancellation.Token).AsTask()
            : Qpdf.ExecuteAsync(host,
                new QpdfCommandInvocation(["input.pdf", "--object-streams=generate", "output.pdf"]),
                options, cancellation.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(Deadline);
            long baseline = host.Budget.PeakBytes;
            Assert.True(baseline > 0L);
            host.Resume();
            if (cancel)
            {
                Assert.True(SpinWait.SpinUntil(() => reached(host.Budget.PeakBytes, baseline) && Volatile.Read(ref host.PendingReads) == 0 || running.IsCompleted, Deadline));
                long observed = host.Budget.PeakBytes;
                Assert.False(running.IsCompleted, "The positive workload finished before CPU cancellation was observed.");
                Assert.True(reached(observed, baseline));
                cancellation.Cancel();
                Assert.Equal(0, Volatile.Read(ref host.PendingReads));
                output.WriteLine("Canceled during processing: admitted peak={0}", observed);
            }

            QpdfExecutionResult result = await running.WaitAsync(Deadline);
            Assert.Equal(cancel ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Completed, result.Status);
            Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.True(host.Inner.TryGetOutput(Target, out byte[] bytes));
            if (cancel)
            {
                Assert.Equal(Prior, bytes);
                Assert.Equal(0L, result.Counters.TotalOutputBytes);
            }
            else
            {
                Assert.NotEqual(Prior, bytes);
                using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(bytes);
                Assert.Equal(1, independent.NumberOfPages);
                host.Inner.AddInput(QpdfPath.From("reopened.pdf"), bytes, QpdfInputOwnership.Copy);
                await using QpdfDocument reopened = await QpdfDocument.OpenAsync(host.Inner,
                    QpdfPath.From("reopened.pdf"), CancellationToken.None);
                Assert.Equal(1, reopened.PageCount);
                QpdfObject Resolve(QpdfObject value) => value is PdfReference reference
                    ? reopened.GetObject(reference.ObjectNumber) : value;
                PdfDictionary page = reopened.PageReferences[0].Page;
                if (job)
                {
                    Assert.True(page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resources));
                    PdfDictionary dictionary = Assert.IsType<PdfDictionary>(Resolve(resources));
                    Assert.True(dictionary.TryGet(PdfName.FromAscii("XObject"), out QpdfObject images));
                    PdfDictionary xobjects = Assert.IsType<PdfDictionary>(Resolve(images));
                    Assert.True(xobjects.TryGet(PdfName.FromAscii("Im0"), out QpdfObject image));
                    PdfStream stream = Assert.IsType<PdfStream>(Resolve(image));
                    Assert.False(stream.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _));
                    Assert.Equal(3072 * 3072, stream.Data.Length);
                }
                else
                {
                    Assert.True(page.TryGet(PdfName.FromAscii("Audit"), out QpdfObject audit));
                    PdfInteger integer = Assert.IsType<PdfInteger>(Resolve(audit));
                    Assert.Equal(7L, integer.Value);
                    Assert.Equal(16 * 1024 * 1024 + 1, integer.Text.Length);
                    Assert.True(integer.Text.AsSpan()[..^1].IndexOfAnyExcept('0') < 0);
                    Assert.Equal('7', integer.Text[^1]);
                }
            }
        }
        finally
        {
            cancellation.Cancel();
            host.Resume();
            await running.WaitAsync(Deadline);
        }
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        host.Dispose();
        Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NumericHeavyArgvRewriteCancelsAfterParsingWork(bool cancel)
    {
        byte[] pdf = TolerantObjectLoadingTests.Pdf([new string('0', 16 * 1024 * 1024) + "7"], false);
        return Verify(pdf, false, cancel, static (current, baseline) => current >= baseline + 16L * 1024L * 1024L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task JpegJobCancelsAfterCodecAdmission(bool cancel)
    {
        const int dimension = 3072;
        const long pixels = (long)dimension * dimension;
        byte[] jpeg = DctDecodeTests.EncodeGrayJpeg(dimension, dimension);
        byte[] pdf = ImageOptimizeTests.BuildImagePage(dimension, dimension, "DeviceGray", 8,
            " /Filter /DCTDecode", jpeg);
        return Verify(pdf, true, cancel, static (current, baseline) =>
            current >= baseline + pixels + 4L * 1024L * 1024L);
    }
}

using Lokad.Qpdf.ImageSharp;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
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

    // Hold the first read only to locate the existing job governor. Resume
    // before observing CPU work; no cancellation is injected at a host boundary.
    private sealed class Host : IQpdfHost, IQpdfJpegCodecHost
    {
        internal readonly InMemoryQpdfHost Inner = new(QpdfLimits.Default, ImageSharpJpegCodec.Instance);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IQpdfJpegCodec? JpegCodec => Inner.JpegCodec;
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

            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
            {
                ReadOnlyMemory<byte> bytes = await inner.ReadAsync(offset, count, token);
                host._entered.TrySetResult();
                await host._resume.Task.WaitAsync(token);
                return bytes;
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private async Task Verify(byte[] pdf, bool job, bool cancel, Func<QpdfExecutionCounters, QpdfExecutionCounters, bool> reached)
    {
        Host host = new();
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
            ResourceGovernor governor = CompletedCancellationObservation.FindGovernor(running);
            QpdfExecutionCounters baseline = governor.Snapshot();
            host.Resume();
            if (cancel)
            {
                Assert.True(SpinWait.SpinUntil(() => reached(governor.Snapshot(), baseline) || running.IsCompleted, Deadline));
                QpdfExecutionCounters observed = governor.Snapshot();
                Assert.False(running.IsCompleted, "The positive workload finished before CPU cancellation was observed.");
                Assert.True(reached(observed, baseline));
                cancellation.Cancel();
                output.WriteLine("Canceled after CPU/codec work: work={0}, peak estimate={1}",
                    observed.TotalWork - baseline.TotalWork, observed.PeakMemoryBytes);
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
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NumericHeavyArgvRewriteCancelsAfterParsingWork(bool cancel)
    {
        byte[] pdf = TolerantObjectLoadingTests.Pdf([new string('0', 16 * 1024 * 1024) + "7"], false);
        return Verify(pdf, false, cancel, static (current, baseline) => current.TotalWork >= baseline.TotalWork + 64L);
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
            current.TotalWork >= baseline.TotalWork + pixels
            && current.PeakMemoryBytes >= baseline.PeakMemoryBytes + pixels + 4L * 1024L * 1024L);
    }
}

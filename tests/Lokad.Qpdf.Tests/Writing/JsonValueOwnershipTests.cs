using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Reading;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonValueOwnershipTests
{
    private static InMemoryQpdfHost Host(byte[] input)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        return host;
    }

    [Theory]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task BulkJsonDiscardsFitConsumerMemoryAllowance(string route, bool brokenXref)
    {
        string body = "<< /A (" + new string('A', 4096) + ") /A null >>";
        byte[] input = TolerantObjectLoadingTests.Pdf(Enumerable.Repeat(body, 50).ToArray(), brokenXref);
        InMemoryQpdfHost host = Host(input);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = input.Length + 60000L, MaxParserErrors = 0L };
        QpdfPath output = QpdfPath.From("outputs/a.json");
        IReadOnlyList<QpdfDiagnostic> diagnostics;
        QpdfExecutionCounters counters;
        if (route == "typed")
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("inputs/a.pdf"), output,
                new QpdfJsonOptions(2, ["qpdf"]), limits, CancellationToken.None);
            diagnostics = result.Warnings;
            counters = result.Counters;
        }
        else
        {
            QpdfExecutionOptions options = new(limits);
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host,
                    """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.json","json":"2","jsonKey":["qpdf"],"global":{"parserMaxErrors":0}}""",
                    options, CancellationToken.None)
                : await Qpdf.ExecuteAsync(host,
                    new QpdfCommandInvocation(["inputs/a.pdf", "--json=2", "--json-key=qpdf", "--parser-max-errors=0", "--", "outputs/a.json"]),
                    options, CancellationToken.None);
            Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
            diagnostics = result.Diagnostics;
            counters = result.Counters;
        }
        Assert.Equal(50, diagnostics.Count(d => d.Code == QpdfDiagnosticCode.DuplicateKey));
        Assert.InRange(counters.PeakMemoryBytes, input.Length, limits.MaxMemoryBytes);
        Assert.True(counters.PeakMemoryBytes - counters.LiveMemoryBytes >= input.Length);
        Assert.Equal(0L, counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        using JsonDocument report = JsonDocument.Parse(bytes);
        JsonElement objects = report.RootElement.GetProperty("qpdf")[1];
        for (int number = 5; number < 55; number++)
        {
            Assert.Empty(objects.GetProperty($"obj:{number} 0 R").GetProperty("value").EnumerateObject());
        }
    }

    [Theory]
    [InlineData("typed", false, false)]
    [InlineData("argv", false, false)]
    [InlineData("job", false, false)]
    [InlineData("typed", true, false)]
    [InlineData("argv", true, false)]
    [InlineData("job", true, false)]
    [InlineData("typed", false, true)]
    [InlineData("argv", false, true)]
    [InlineData("job", false, true)]
    [InlineData("typed", true, true)]
    [InlineData("argv", true, true)]
    [InlineData("job", true, true)]
    public async Task SidecarsPreserveRawAndDecodedBytesAfterReaderCloses(string route, bool raw, bool oneHandle)
    {
        byte[] samples = [0, 255, 10, 13, 128, 32, 65];
        using MemoryStream buffer = new();
        using (ZLibStream encoder = new(buffer, CompressionLevel.Optimal, true)) { encoder.Write(samples); }
        byte[] encoded = buffer.ToArray();
        string stream = "<< /Length 6 0 R /Filter /FlateDecode >>\nstream\n" + Encoding.Latin1.GetString(encoded) + "\nendstream";
        byte[] input = TolerantObjectLoadingTests.Pdf([stream, encoded.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)], false);
        InMemoryQpdfHost host = Host(input);
        QpdfPath output = QpdfPath.From("outputs/a.json");
        QpdfLimits limits = oneHandle ? QpdfLimits.Default with { MaxOpenHandles = 1L } : QpdfLimits.Default;
        if (route == "typed")
        {
            await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("inputs/a.pdf"), output,
                new QpdfJsonOptions(2, ["qpdf"], "file", "side", ["5"], raw), limits, CancellationToken.None);
        }
        else
        {
            string job = raw
                ? """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.json","jsonOutput":"2","jsonKey":["qpdf"],"jsonStreamData":"file","jsonStreamPrefix":"side","jsonObject":["5"]}"""
                : """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.json","json":"2","jsonKey":["qpdf"],"jsonStreamData":"file","jsonStreamPrefix":"side","jsonObject":["5"]}""";
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, job, new QpdfExecutionOptions(limits), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host,
                    new QpdfCommandInvocation(["inputs/a.pdf", raw ? "--json-output=2" : "--json=2", "--json-key=qpdf",
                        "--json-stream-data=file", "--json-stream-prefix=side", "--json-object=5", "--", "outputs/a.json"]),
                    new QpdfExecutionOptions(limits), CancellationToken.None);
            Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Array.Fill(input, (byte)'X');
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(host.TryGetOutput(output, out byte[] reportBytes));
        using JsonDocument report = JsonDocument.Parse(reportBytes);
        JsonElement emitted = report.RootElement.GetProperty("qpdf")[1].GetProperty("obj:5 0 R").GetProperty("stream");
        Assert.Equal("side-5", emitted.GetProperty("datafile").GetString());
        Assert.Equal(raw, emitted.GetProperty("dict").TryGetProperty("/Filter", out _));
        Assert.True(host.TryGetOutput(QpdfPath.From("side-5"), out byte[] sidecar));
        Assert.Equal(raw ? encoded : samples, sidecar);
        Array.Fill(sidecar, (byte)'Y');
        Assert.True(host.TryGetOutput(QpdfPath.From("side-5"), out byte[] secondCopy));
        Assert.Equal(raw ? encoded : samples, secondCopy);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task InputCloseFaultPreservesMainAndSidecarTargets(string route)
    {
        InMemoryQpdfHost source = Host(TolerantObjectLoadingTests.Pdf(["<< /Length 3 >>\nstream\nabc\nendstream"], false));
        QpdfPath main = QpdfPath.From("outputs/a.json");
        QpdfPath sidecar = QpdfPath.From("side-5");
        foreach (QpdfPath path in new[] { main, sidecar })
        {
            await using IQpdfOutputTransaction prior = await source.CreateOutputAsync(path, CancellationToken.None);
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        CloseFaultHost host = new(source);
        if (route == "typed")
        {
            await Assert.ThrowsAsync<QpdfHostException>(() => Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("inputs/a.pdf"), main,
                new QpdfJsonOptions(2, ["qpdf"], "file", "side", ["5"], false), CancellationToken.None).AsTask());
        }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host,
                    """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.json","json":"2","jsonKey":["qpdf"],"jsonStreamData":"file","jsonStreamPrefix":"side","jsonObject":["5"]}""",
                    CancellationToken.None)
                : await Qpdf.ExecuteAsync(host,
                    new QpdfCommandInvocation(["inputs/a.pdf", "--json=2", "--json-key=qpdf", "--json-stream-data=file",
                        "--json-stream-prefix=side", "--json-object=5", "--", "outputs/a.json"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.HostOperationFailed);
            Assert.True(result.Counters.PeakMemoryBytes > 0L);
            Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(1, host.InputCloses);
        Assert.Equal(0, host.OutputCreates);
        Assert.Equal(0L, source.OpenLeaseCount);
        foreach (QpdfPath path in new[] { main, sidecar })
        {
            Assert.True(source.TryGetOutput(path, out byte[] bytes));
            Assert.Equal("prior"u8.ToArray(), bytes);
        }
    }

    [Theory]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("typed", true)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task FailedOrCanceledJsonClosesReaderAndPreservesPriorOutput(string route, bool cancel)
    {
        InMemoryQpdfHost source = Host(TolerantObjectLoadingTests.Pdf(["(" + new string('A', 4096) + ")"], false));
        QpdfPath output = QpdfPath.From("outputs/a.json");
        await using (IQpdfOutputTransaction prior = await source.CreateOutputAsync(output, CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        using CancellationTokenSource cancellation = new();
        CancelOnPublicationHost observed = new(source, cancellation);
        IQpdfHost host = cancel ? observed : source;
        QpdfLimits limits = cancel ? QpdfLimits.Default : QpdfLimits.Default with { MaxOutputBytes = 128L };
        if (route == "typed")
        {
            Task<QpdfJsonResult> execution = Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("inputs/a.pdf"), output,
                new QpdfJsonOptions(2, ["qpdf"]), limits, cancellation.Token).AsTask();
            if (cancel) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution); }
            else
            {
                QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() => execution);
                Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), failure.LimitName);
            }
        }
        else
        {
            QpdfExecutionOptions options = new(limits);
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host,
                    """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.json","json":"2","jsonKey":["qpdf"]}""",
                    options, cancellation.Token)
                : await Qpdf.ExecuteAsync(host,
                    new QpdfCommandInvocation(["inputs/a.pdf", "--json=2", "--json-key=qpdf", "--", "outputs/a.json"]),
                    options, cancellation.Token);
            Assert.Equal(cancel ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            Assert.True(result.Counters.PeakMemoryBytes > 0L);
            Assert.Equal(0L, result.Counters.OpenHandles);
        }
        if (cancel) { Assert.Equal(1, observed.PublicationAttempts); Assert.True(cancellation.IsCancellationRequested); }
        Assert.Equal(0L, source.OpenLeaseCount);
        Assert.True(source.TryGetOutput(output, out byte[] preserved));
        Assert.Equal("prior"u8.ToArray(), preserved);
    }

    private sealed class CancelOnPublicationHost(InMemoryQpdfHost source, CancellationTokenSource cancellation) : IQpdfHost
    {
        internal int PublicationAttempts { get; private set; }

        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
            => source.OpenInputAsync(path, cancellationToken);

        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            PublicationAttempts++;
            cancellation.Cancel();
            return source.CreateOutputAsync(path, cancellationToken);
        }
    }

    private sealed class CloseFaultHost(InMemoryQpdfHost source) : IQpdfHost
    {
        internal int InputCloses { get; private set; }
        internal int OutputCreates { get; private set; }

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken)
            => new ClosingInput(await source.OpenInputAsync(path, cancellationToken), this);

        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken)
        {
            OutputCreates++;
            return source.CreateOutputAsync(path, cancellationToken);
        }

        private sealed class ClosingInput(IQpdfInput inner, CloseFaultHost host) : IQpdfInput
        {
            public QpdfPath Path => inner.Path;
            public string Identity => inner.Identity;
            public long Length => inner.Length;
            public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken)
                => inner.ReadAsync(offset, count, cancellationToken);

            public async ValueTask DisposeAsync()
            {
                host.InputCloses++;
                await inner.DisposeAsync();
                throw new QpdfHostException(Path, QpdfHostFailureReason.ReadFailed);
            }
        }
    }
}

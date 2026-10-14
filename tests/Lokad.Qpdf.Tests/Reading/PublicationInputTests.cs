using System.Runtime.InteropServices;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class PublicationInputTests
{
    private static readonly QpdfPath InputPath = QpdfPath.From("in.pdf"), OutputPath = QpdfPath.From("out.pdf");
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("q Q\n", 24576)));
    private static readonly byte[] Input = Build();

    private static byte[] Build()
    {
        List<(int Number, string Body, byte[]? Stream)> objects = CheckTests.SinglePage($"<< /Length {Payload.Length} >>", Payload);
        byte[] unused = new byte[2 * 1048576];
        objects.Add((6, $"<< /Length {unused.Length} >>", unused));
        return CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData("typed", "success", false)]
    [InlineData("typed", "success", true)]
    [InlineData("argv", "success", true)]
    [InlineData("linearize", "success", true)]
    [InlineData("qdf", "success", true)]
    [InlineData("argv", "write", true)]
    [InlineData("job", "cancel", true)]
    public async Task PrivateInputDoesNotEscapeThroughRetainedPublicationChunks(string route, string outcome, bool retain)
    {
        using CancellationTokenSource cancellation = new();
        Host host = new(retain, outcome, cancellation);
        await using (IQpdfOutputTransaction previous = await host.Outputs.CreateOutputAsync(OutputPath, CancellationToken.None))
        {
            await previous.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await previous.CommitAsync(CancellationToken.None);
        }
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 8 * 1048576L, MaxOutputBytes = 262144L };
        if (route == "typed")
        {
            PdfPageSelectionResult selected = await Qpdf.SelectPageAsync(host, InputPath, OutputPath,
                PdfPageSelectionOptions.FromPages([1]), limits, cancellation.Token);
            Assert.Equal(1, selected.PageCount);
        }
        else
        {
            QpdfExecutionOptions options = new(limits);
            List<string> arguments = ["in.pdf", "--stream-data=preserve", "--decode-level=none", "--compress-streams=n",
                "--object-streams=disable", "--static-id", "--remove-unreferenced-resources=no"];
            if (route == "linearize") { arguments.Add("--linearize"); }
            if (route == "qdf") { arguments.Add("--qdf"); }
            arguments.Add("out.pdf");
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host,
                    """{"inputFile":"in.pdf","outputFile":"out.pdf","compressStreams":"n","decodeLevel":"none","objectStreams":"disable"}""",
                    options, cancellation.Token)
                : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(arguments), options, cancellation.Token);
            Assert.Equal(outcome switch { "cancel" => QpdfExecutionStatus.Canceled, "write" => QpdfExecutionStatus.Failed,
                _ => QpdfExecutionStatus.Completed }, result.Status);
            Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0, host.Leases);
        Assert.Equal(Input.Length, host.ReadBytes);
        if (retain)
        {
            Assert.NotEmpty(host.Chunks);
            Assert.Contains(host.Chunks, chunk => chunk.Length >= 8192);
            foreach (ReadOnlyMemory<byte> chunk in host.Chunks)
            {
                Assert.True(MemoryMarshal.TryGetArray(chunk, out ArraySegment<byte> backing));
                Assert.NotNull(backing.Array);
                // This is actual backing retention, not an accounting receipt:
                // emitted payload chunks must not pin the 2 MiB omitted stream.
                Assert.True(backing.Array.Length < Input.Length / 2);
            }
        }
        Assert.True(host.Outputs.TryGetOutput(OutputPath, out byte[] output));
        if (outcome != "success") { Assert.Equal("prior"u8.ToArray(), output); return; }
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(1, independent.NumberOfPages);
        InMemoryQpdfHost reader = new(); reader.AddInput(OutputPath, output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, OutputPath, CancellationToken.None);
        Assert.Contains(document.Engine.XRefEntries.Keys.Select(document.GetObject), value => value is PdfStream stream
            && stream.Data.AsSpan().SequenceEqual(Payload));
    }

    private sealed class Host(bool retain, string outcome, CancellationTokenSource cancellation) : IQpdfHost
    {
        internal InMemoryQpdfHost Outputs { get; } = new();
        internal List<ReadOnlyMemory<byte>> Chunks { get; } = [];
        internal int Leases;
        internal long ReadBytes;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Leases++;
            return new(new Lease(this, new FakeInput(path, Input)));
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            IQpdfOutputTransaction transaction = await Outputs.CreateOutputAsync(path, token);
            return retain ? new Transaction(this, transaction, outcome, cancellation) : transaction;
        }
        private sealed class Lease(Host host, IQpdfInput input) : IQpdfInput
        {
            public QpdfPath Path => input.Path;
            public string Identity => input.Identity;
            public long Length => input.Length;
            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
            {
                ReadOnlyMemory<byte> bytes = await input.ReadAsync(offset, count, token);
                host.ReadBytes += bytes.Length; return bytes;
            }
            public async ValueTask DisposeAsync() { await input.DisposeAsync(); host.Leases--; }
        }
        private sealed class Transaction(Host host, IQpdfOutputTransaction inner, string outcome,
            CancellationTokenSource cancellation) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
            {
                host.Chunks.Add(data);
                if (data.Length >= 8192)
                {
                    if (outcome == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                    if (outcome == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                }
                await Task.Yield();
                await inner.WriteAsync(data, token);
            }
            public ValueTask CommitAsync(CancellationToken token) => inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}

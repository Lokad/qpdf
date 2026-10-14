using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ClassicContentIdTests
{
    private static readonly QpdfPath Input = QpdfPath.From("in.pdf"), Output = QpdfPath.From("out.pdf");

    private sealed class Host(byte[] input) : IQpdfHost
    {
        internal InMemoryQpdfHost Inner { get; } = new();
        internal string Failure = "";
        internal CancellationTokenSource? Cancellation;
        internal int Created, Committed;
        internal List<ReadOnlyMemory<byte>> Packets { get; } = [];

        internal void Initialize() => Inner.AddInput(Input, input, QpdfInputOwnership.Copy);
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => Inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Created++;
            return new Transaction(this, await Inner.CreateOutputAsync(path, token));
        }

        private sealed class Transaction(Host host, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
            {
                if (data.Span.IndexOf("trailer\n"u8) >= 0)
                {
                    if (host.Failure == "fail") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                    if (host.Failure == "cancel") { host.Cancellation?.Cancel(); token.ThrowIfCancellationRequested(); }
                }
                host.Packets.Add(data);
                return inner.WriteAsync(data, token);
            }
            public ValueTask CommitAsync(CancellationToken token)
            { host.Committed++; return inner.CommitAsync(token); }
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static byte[] Build(int length, int originalLength)
    {
        byte[] payload = new byte[length]; payload.AsSpan().Fill((byte)' ');
        "q Q"u8.CopyTo(payload);
        string original = Convert.ToHexString(Enumerable.Range(0, originalLength).Select(index => (byte)index).ToArray());
        string trailer = originalLength == 0 ? "" : $" /ID [<{original}><AA>]";
        return WriterControlsTests.BuildOnePage("1.4", payload, "", trailer);
    }

    private static QpdfCommandInvocation Invocation(bool newline)
        => new(newline ? ["in.pdf", "--stream-data=preserve", "--newline-before-endstream", "out.pdf"]
            : ["in.pdf", "--stream-data=preserve", "out.pdf"]);

    [Theory]
    [InlineData(3, 0, false)]
    [InlineData(98321, 3, false)]
    [InlineData(32769, 256, true)]
    [InlineData(65537, 0, true)]
    public async Task PublicIdsMatchWholePlaceholderFileAndRetainingHost(int length, int originalLength, bool newline)
    {
        Host host = new(Build(length, originalLength)); host.Initialize();
        QpdfCommandInvocation invocation = Invocation(newline);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, invocation, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] bytes));
        (string first, string second) = WriterControlsTests.IdsOf(bytes);
        byte[] placeholder = (byte[])bytes.Clone();
        int start = Encoding.ASCII.GetString(bytes).LastIndexOf("/ID [<", StringComparison.Ordinal) + 6;
        Assert.True(start >= 6);
        if (originalLength == 0) { placeholder.AsSpan(start, first.Length).Fill((byte)'0'); }
        placeholder.AsSpan(start + first.Length + 2, second.Length).Fill((byte)'0');
        string expected = Convert.ToHexString(MD5.HashData(placeholder));
        Assert.Equal(expected, second);
        Assert.Equal(originalLength == 0 ? expected
            : Convert.ToHexString(Enumerable.Range(0, originalLength).Select(index => (byte)index).ToArray()), first);
        using (UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(bytes))
        { Assert.Equal(1, independent.NumberOfPages); }
        byte[][] retained = host.Packets.Select(packet => packet.ToArray()).ToArray();
        Assert.Equal(bytes, retained.SelectMany(packet => packet).ToArray());

        QpdfExecutionResult repeated = await Qpdf.ExecuteAsync(host, invocation, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, repeated.Status);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] again)); Assert.Equal(bytes, again);
        for (int index = 0; index < retained.Length; index++) { Assert.Equal(retained[index], host.Packets[index].ToArray()); }

        int created = host.Created;
        QpdfExecutionResult limited = await Qpdf.ExecuteAsync(host, invocation,
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxOutputBytes = bytes.Length - 1L }), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, limited.Status); Assert.Equal(created, host.Created);
        Assert.True(host.Inner.TryGetOutput(Output, out again)); Assert.Equal(bytes, again);
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("cancel")]
    public async Task FinalTrailerFailurePreservesPriorOutputAndClosesResources(string failure)
    {
        Host host = new(Build(65537, 0)); host.Initialize();
        await Qpdf.ExecuteAsync(host, Invocation(false), CancellationToken.None);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior));
        using CancellationTokenSource cancellation = new();
        host.Failure = failure; host.Cancellation = cancellation;
        int committed = host.Committed;
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, Invocation(false), cancellation.Token);
        Assert.Equal(failure == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(committed, host.Committed);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] survived)); Assert.Equal(prior, survived);
        Assert.Equal(0L, host.Inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Fact]
    public async Task ContentHashFitsTheStaticJobHostOperationCeiling()
    {
        Host fixedHost = new(Build(3, 0)); fixedHost.Initialize();
        QpdfExecutionResult baseline = await Qpdf.ExecuteAsync(fixedHost,
            new QpdfCommandInvocation(["in.pdf", "--stream-data=preserve", "--static-id", "out.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, baseline.Status);
        Host contentHost = new(Build(3, 0)); contentHost.Initialize();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(contentHost, Invocation(false),
            new QpdfExecutionOptions(QpdfLimits.Default with { MaxHostOperations = baseline.Counters.HostOperations }),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(0L, contentHost.Inner.OpenLeaseCount);
    }
}

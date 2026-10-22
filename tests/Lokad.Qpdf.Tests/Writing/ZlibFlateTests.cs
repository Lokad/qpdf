using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ZlibFlateTests
{
    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static byte[] TextPayload()
    {
        StringBuilder builder = new();
        for (int i = 0; i < 400; i++)
        {
            builder.Append("The quick brown fox jumps over the lazy dog. ");
        }

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static byte[] BinaryPayload()
    {
        byte[] payload = new byte[4096];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 256);
        }

        return payload;
    }

    private static async Task<(QpdfExecutionResult Result, byte[]? Output)> RunAsync(byte[] input, QpdfZlibFlateOptions options)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.bin", input);
        QpdfExecutionResult result = await Qpdf.RunZlibFlateAsync(host, QpdfPath.From("inputs/a.bin"), QpdfPath.From("outputs/b.bin"), options, CancellationToken.None);
        bool published = host.TryGetOutput(QpdfPath.From("outputs/b.bin"), out byte[] output);
        return (result, published ? output : null);
    }

    private static async Task<byte[]> CompressAsync(byte[] input, int? level)
    {
        (QpdfExecutionResult result, byte[]? output) = await RunAsync(input, new QpdfZlibFlateOptions(QpdfZlibFlateMode.Compress, level));
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.NotNull(output);
        return output;
    }

    private static async Task<byte[]> UncompressAsync(byte[] input)
    {
        (QpdfExecutionResult result, byte[]? output) = await RunAsync(input, new QpdfZlibFlateOptions(QpdfZlibFlateMode.Uncompress, null));
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.NotNull(output);
        return output;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public async Task RoundTripPreservesTextPayload(int? level)
    {
        byte[] input = TextPayload();
        byte[] compressed = await CompressAsync(input, level);
        Assert.NotEqual(input, compressed);
        byte[] restored = await UncompressAsync(compressed);
        Assert.Equal(input, restored);
    }

    [Fact]
    public async Task RoundTripPreservesBinaryPayload()
    {
        byte[] input = BinaryPayload();
        byte[] restored = await UncompressAsync(await CompressAsync(input, 9));
        Assert.Equal(input, restored);
    }

    [Fact]
    public async Task UncompressChargesTheResultOnce()
    {
        byte[] raw = BinaryPayload();
        InMemoryQpdfHost host = HostWith("inputs/a.bin", await CompressAsync(raw, null));
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = raw.Length };
        QpdfExecutionResult result = await Qpdf.RunZlibFlateAsync(host, QpdfPath.From("inputs/a.bin"),
            QpdfPath.From("outputs/b.bin"), new QpdfZlibFlateOptions(QpdfZlibFlateMode.Uncompress, null),
            limits, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/b.bin"), out byte[] output));
        Assert.Equal(raw, output);
        Assert.Equal(raw.Length, result.Counters.TotalDecodedBytes);
    }

    [Fact]
    public async Task HigherLevelCompressesSmaller()
    {
        byte[] input = TextPayload();
        byte[] fast = await CompressAsync(input, 1);
        byte[] small = await CompressAsync(input, 9);
        Assert.True(small.Length <= fast.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(-1)]
    public async Task OutOfRangeLevelFails(int level)
    {
        (QpdfExecutionResult result, byte[]? output) = await RunAsync(TextPayload(), new QpdfZlibFlateOptions(QpdfZlibFlateMode.Compress, level));
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Null(output);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.InvalidArguments);
    }

    [Fact]
    public async Task LevelWithUncompressFails()
    {
        byte[] compressed = await CompressAsync(TextPayload(), null);
        (QpdfExecutionResult result, byte[]? output) = await RunAsync(compressed, new QpdfZlibFlateOptions(QpdfZlibFlateMode.Uncompress, 6));
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Null(output);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.InvalidArguments);
    }

    [Fact]
    public async Task UnknownModeFails()
    {
        (QpdfExecutionResult result, byte[]? output) = await RunAsync(TextPayload(), new QpdfZlibFlateOptions((QpdfZlibFlateMode)42, null));
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Null(output);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.InvalidArguments);
    }

    [Fact]
    public async Task PlainBytesDoNotInflate()
    {
        (QpdfExecutionResult result, byte[]? output) = await RunAsync(TextPayload(), new QpdfZlibFlateOptions(QpdfZlibFlateMode.Uncompress, null));
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Null(output);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.InvalidArguments);
    }

    [Fact]
    public async Task TruncatedStreamFailsWithoutPartialOutput()
    {
        byte[] compressed = await CompressAsync(TextPayload(), 9);
        byte[] cut = new byte[compressed.Length / 2];
        Buffer.BlockCopy(compressed, 0, cut, 0, cut.Length);
        (QpdfExecutionResult result, byte[]? output) = await RunAsync(cut, new QpdfZlibFlateOptions(QpdfZlibFlateMode.Uncompress, null));
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Null(output);
    }

    [Fact]
    public async Task EmptyInflateFails()
    {
        (QpdfExecutionResult result, byte[]? output) = await RunAsync([], new QpdfZlibFlateOptions(QpdfZlibFlateMode.Uncompress, null));
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Null(output);
    }

    [Theory]
    [InlineData("789C000300FCFF71205101E700E3")]
    [InlineData("789C0300000001")]
    public async Task IncompleteFrameWithMatchingChecksumPreservesPriorOutput(string hex)
    {
        using InMemoryQpdfHost host = HostWith("inputs/a.bin", Convert.FromHexString(hex));
        QpdfPath target = QpdfPath.From("outputs/b.bin");
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(target, CancellationToken.None))
        { await seed.WriteAsync(new byte[] { 7, 8, 9 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Qpdf.RunZlibFlateAsync(host, QpdfPath.From("inputs/a.bin"), target,
            new(QpdfZlibFlateMode.Uncompress, null), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.Equal(0L, result.Counters.TotalOutputBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(target, out byte[] prior)); Assert.Equal(new byte[] { 7, 8, 9 }, prior);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InflationAdmissionFailurePreservesOutputAndRetiresStorage(bool shared)
    {
        byte[] compressed = await CompressAsync(new byte[1024 * 1024], null);
        RecordingMemoryBudget root = new(shared ? 192 * 1024L : 4 * 1024 * 1024L);
        using (IMemoryScope owner = root.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        {
            QpdfPath input = QpdfPath.From("input"), target = QpdfPath.From("output");
            host.AddInput(input, compressed, QpdfInputOwnership.Transfer);
            await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(target, CancellationToken.None))
            { await seed.WriteAsync(new byte[] { 7, 8, 9 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
            QpdfLimits limits = shared ? QpdfLimits.Default : QpdfLimits.Default with { MaxMemoryBytes = 192 * 1024L };
            QpdfExecutionResult result = await Qpdf.RunZlibFlateAsync(host, input, target,
                new(QpdfZlibFlateMode.Uncompress, null), limits, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.True(host.TryGetOutput(target, out byte[] prior)); Assert.Equal(new byte[] { 7, 8, 9 }, prior);
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Fact]
    public async Task EmptyCompressYieldsValidZlibOutput()
    {
        (QpdfExecutionResult result, byte[]? output) = await RunAsync([], new QpdfZlibFlateOptions(QpdfZlibFlateMode.Compress, null));
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.NotNull(output);
        Assert.True(output.Length >= 8);
        byte[] restored = await UncompressAsync(output);
        Assert.Empty(restored);
    }

    [Fact]
    public async Task MissingInputFailsAsHostError()
    {
        InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Qpdf.RunZlibFlateAsync(host, QpdfPath.From("inputs/missing.bin"), QpdfPath.From("outputs/b.bin"), new QpdfZlibFlateOptions(QpdfZlibFlateMode.Compress, null), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.HostOperationFailed);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/b.bin"), out byte[] _));
    }

    [Fact]
    public async Task TinyOutputBudgetFails()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.bin", TextPayload());
        QpdfLimits limits = new QpdfLimits { MaxOutputBytes = 16L };
        QpdfExecutionResult result = await Qpdf.RunZlibFlateAsync(host, QpdfPath.From("inputs/a.bin"), QpdfPath.From("outputs/b.bin"), new QpdfZlibFlateOptions(QpdfZlibFlateMode.Compress, null), limits, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompressionBudgetFailurePreservesPublishedTarget(bool outputLimit)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.bin", BinaryPayload());
        QpdfPath target = QpdfPath.From("outputs/b.bin");
        await using (IQpdfOutputTransaction transaction = await host.CreateOutputAsync(target, CancellationToken.None))
        {
            await transaction.WriteAsync(new byte[] { 7, 8, 9 }, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        QpdfLimits limits = outputLimit
            ? QpdfLimits.Default with { MaxOutputBytes = 16L }
            : QpdfLimits.Default with { MaxMemoryBytes = 65536L };
        QpdfExecutionResult result = await Qpdf.RunZlibFlateAsync(host, QpdfPath.From("inputs/a.bin"), target,
            new QpdfZlibFlateOptions(QpdfZlibFlateMode.Compress, null), limits, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(host.TryGetOutput(target, out byte[] output));
        Assert.Equal(new byte[] { 7, 8, 9 }, output);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CancelledTokenReportsCanceled()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.bin", TextPayload());
        using CancellationTokenSource source = new();
        await source.CancelAsync();
        QpdfExecutionResult result = await Qpdf.RunZlibFlateAsync(host, QpdfPath.From("inputs/a.bin"), QpdfPath.From("outputs/b.bin"), new QpdfZlibFlateOptions(QpdfZlibFlateMode.Compress, null), source.Token);
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
    }

    [Fact]
    public async Task NullGuardsThrow()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.bin", TextPayload());
        QpdfZlibFlateOptions options = new(QpdfZlibFlateMode.Compress, null);
#pragma warning disable CS8625 // Intentional null literals for negative tests.
        await Assert.ThrowsAsync<ArgumentNullException>(() => Qpdf.RunZlibFlateAsync(null, QpdfPath.From("inputs/a.bin"), QpdfPath.From("outputs/b.bin"), options, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => Qpdf.RunZlibFlateAsync(host, QpdfPath.From("inputs/a.bin"), QpdfPath.From("outputs/b.bin"), null, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => Qpdf.RunZlibFlateAsync(host, QpdfPath.From("inputs/a.bin"), QpdfPath.From("outputs/b.bin"), options, null, CancellationToken.None).AsTask());
#pragma warning restore CS8625
    }
}

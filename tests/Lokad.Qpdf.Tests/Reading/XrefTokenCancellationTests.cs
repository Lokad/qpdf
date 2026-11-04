using System.Text;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class XrefTokenCancellationTests
{
    [Theory]
    [InlineData(false, "comment")]
    [InlineData(false, "whitespace")]
    [InlineData(false, "name")]
    [InlineData(false, "string")]
    [InlineData(true, "comment")]
    [InlineData(true, "whitespace")]
    [InlineData(true, "name")]
    [InlineData(true, "string")]
    public void CancellationDuringSectionTokensPrecedesMalformedInputAndRefundsOwners(bool subsection, string shape)
    {
        string longToken = shape switch
        {
            "comment" => "%" + new string('x', 32768) + "\nbad",
            "whitespace" => new string(' ', 32768) + "bad",
            "name" => "/" + new string('x', 32768),
            _ => "(" + new string('x', 32768),
        };
        byte[] input = Encoding.ASCII.GetBytes((subsection ? "xref\n" : "") + longToken);
        using CancellationTokenSource cancellation = new();
        // Token scans must observe cancellation independently of the periodic
        // operation interval, including before malformed syntax is diagnosed.
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = long.MaxValue };
        ResourceGovernor governor = new(limits, cancellation.Token);
        bool armed = false; long firstReadBytes = -1L;
        using ObservedInput storage = new(input, () =>
        {
            if (!armed || cancellation.IsCancellationRequested) { return; }
            long live = governor.Snapshot().LiveMemoryBytes;
            if (firstReadBytes < 0L) { firstReadBytes = live; }
            // Subsection storage is admitted after the xref keyword. Observe
            // that real boundary rather than counting tokenizer implementation calls.
            if (!subsection || live > firstReadBytes) { cancellation.Cancel(); }
        });
        ReadOnlyMemory<byte> memory = storage.Memory;
        armed = true;
        Assert.ThrowsAny<OperationCanceledException>(() => XRefTable.ReadClassicWindow(memory,
            9L, input.Length + 9, limits, governor, new DiagnosticBag(governor)));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task CancellationDuringShortInputReadsPreservesTargetsAndClosesLeases(string route)
    {
        byte[] content = new byte[32768]; content.AsSpan().Fill((byte)' ');
        byte[] input = CheckTests.Assemble(CheckTests.SinglePage("<< /Length 32768 >>", content), 1);
        using CancellationTokenSource cancellation = new();
        SelectiveInputTests.Host host = new(input, 37) { Cancel = cancellation };
        using var outputs = host.Outputs;
        QpdfPath target = QpdfPath.From("output");
        await using (IQpdfOutputTransaction prior = await outputs.CreateOutputAsync(target, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        if (route == "typed")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Qpdf.SelectPageAsync(host,
                QpdfPath.From("input"), target, PdfPageSelectionOptions.FromPages([1]), cancellation.Token));
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1", "--", "output"]), cancellation.Token)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1"}]}""", cancellation.Token);
            Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
        }
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(3, host.Reads.Count); Assert.Equal(0, host.Leases); Assert.Equal(0, host.OutputAttempts);
        Assert.True(outputs.TryGetOutput(target, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
    }
}

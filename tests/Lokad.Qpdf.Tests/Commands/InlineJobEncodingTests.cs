using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class InlineJobEncodingTests
{
    public static IEnumerable<object[]> InvalidText()
    {
        foreach (int padding in new[] { 0, 20000 })
        foreach (bool key in new[] { false, true })
        foreach (char surrogate in new[] { '\ud800', '\udc00' })
        { yield return [padding, key, surrogate]; }
    }

    [Theory]
    [MemberData(nameof(InvalidText))]
    public async Task InvalidLiteralUtf16ReturnsFailureAndPreservesOtherOwners(int padding, bool key, char surrogate)
    {
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope sibling = root.CreateScope();
        Assert.True(sibling.TryReserve(4096L, out IMemoryReservation? held));
        using (held)
        using (IMemoryScope caller = root.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, caller))
        {
            QpdfPath output = QpdfPath.From("output");
            await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(output, CancellationToken.None))
            { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
            long before = root.ReservedBytes;
            string json = (key ? "{\"empty\":\"\",\"outputFile\":\"output\",\"" + surrogate + "\":\"\"}"
                : "{\"empty\":\"\",\"outputFile\":\"output\",\"minVersion\":\"" + surrogate + "\"}") + new string(' ', padding);
            QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, json, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            QpdfDiagnostic error = Assert.Single(result.Diagnostics);
            Assert.Equal(QpdfDiagnosticCode.InvalidArguments, error.Code);
            Assert.Contains("Invalid string encoding", error.Message, StringComparison.Ordinal);
            Assert.Equal(0L, result.Counters.HostOperations); Assert.Equal(0L, host.OpenLeaseCount);
            Assert.InRange(root.ReservedBytes - before, 1L, 2048L);
            Assert.NotNull(held); Assert.Equal(4096L, held.Bytes);
            using IMemoryScope export = root.CreateScope();
            Assert.True(host.TryGetOutput(output, export, out byte[] bytes));
            Assert.Equal("prior"u8.ToArray(), bytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20000)]
    public async Task ValidUnicodeStillPublishesTheExactLogicalName(int padding)
    {
        using InMemoryQpdfHost host = new();
        const string name = "output-\u00e9-\ud83d\ude80";
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            "{\"empty\":\"\",\"outputFile\":\"" + name + "\"}" + new string(' ', padding), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.True(host.TryGetOutput(QpdfPath.From(name), out byte[] bytes));
        Assert.True(bytes.AsSpan().StartsWith(Encoding.ASCII.GetBytes("%PDF-")));
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

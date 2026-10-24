using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class FilterBoundaryTests
{
    internal static InMemoryQpdfHost Host(string filter, byte[] data)
    {
        InMemoryQpdfHost host = new();
        var objects = Inspection.CheckTests.SinglePage("<< /Length " + data.Length + " /Filter /" + filter + " >>", data);
        host.AddInput(QpdfPath.From("input"), Inspection.CheckTests.Assemble(objects, 1), QpdfInputOwnership.Transfer);
        return host;
    }

    public static IEnumerable<object[]> RunLengthCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (int tail in Enumerable.Range(0, 5)) { yield return new object[] { route, tail }; }
    }

    [Theory]
    [MemberData(nameof(RunLengthCases))]
    public async Task RunLengthIgnoresEverythingAfterEndOfData(string route, int tail)
    {
        byte[] suffix = tail switch { 1 => [0, 66], 2 => [129, 66], 3 => [255], 4 => [128, 0, 67], _ => [] };
        using InMemoryQpdfHost host = Host("RunLengthDecode", [3, 113, 32, 81, 10, 128, ..suffix]);
        if (route == "typed")
        {
            using InMemoryQpdfHost control = Host("RunLengthDecode", [3, 113, 32, 81, 10, 128]);
            QpdfCheckResult expected = await Qpdf.CheckAsync(control, QpdfPath.From("input"), CancellationToken.None);
            QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), new(),
                QpdfLimits.Default with { MaxDecodedBytes = expected.Counters.TotalDecodedBytes }, CancellationToken.None);
            Assert.True(result.Passed); Assert.Equal(expected.Counters.TotalDecodedBytes, result.Counters.TotalDecodedBytes);
        }
        else
        {
            QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxDecodedBytes = 4 });
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--show-object=4", "--filtered-stream-data", "--decode-level=all"]), options, CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","showObject":"4","filteredStreamData":"","decodeLevel":"all"}""", options, CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Equal(4, result.Counters.TotalDecodedBytes);
            ReadOnlyMemory<byte> bytes = Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes);
            host.Dispose(); Assert.Equal("q Q\n"u8.ToArray(), bytes.ToArray());
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> AsciiCases()
    {
        foreach (string filter in new[] { "ASCIIHexDecode", "ASCII85Decode" })
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (int value in new[] { 0, 9, 10, 12, 13, 32, 7, 8, 11, 14, 27 })
        { yield return new object[] { filter, route, value }; }
    }

    [Theory]
    [MemberData(nameof(AsciiCases))]
    public async Task AsciiFiltersUsePdfWhitespace(string filter, string route, int value)
    {
        byte[] encoded = DecoderOwnershipFixtures.Encode(filter, "q Q\n"u8.ToArray());
        using InMemoryQpdfHost host = Host(filter, [encoded[0], (byte)value, ..encoded.AsSpan(1)]);
        bool valid = value is 0 or 9 or 10 or 12 or 13 or 32;
        if (route == "typed")
        {
            QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
            Assert.Equal(valid, result.Passed);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--show-object=4", "--filtered-stream-data"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","showObject":"4","filteredStreamData":""}""", CancellationToken.None);
            Assert.Equal(valid ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
            if (valid)
            {
                ReadOnlyMemory<byte> bytes = Assert.IsType<ReadOnlyMemory<byte>>(result.OutputBytes);
                host.Dispose(); Assert.Equal("q Q\n"u8.ToArray(), bytes.ToArray());
            }
            else { Assert.Null(result.OutputBytes); Assert.NotEmpty(result.Diagnostics); }
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("ASCIIHexDecode", "argv")]
    [InlineData("ASCII85Decode", "argv")]
    [InlineData("ASCIIHexDecode", "job")]
    [InlineData("ASCII85Decode", "job")]
    public async Task InvalidAsciiWhitespacePreservesPriorOutput(string filter, string route)
    {
        byte[] encoded = DecoderOwnershipFixtures.Encode(filter, "q Q\n"u8.ToArray());
        using InMemoryQpdfHost host = Host(filter, [encoded[0], 11, ..encoded.AsSpan(1)]);
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await seed.WriteAsync(new byte[] { 17, 23 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = route == "argv"
            ? await Qpdf.ExecuteAsync(host, new(["input", "--show-object=4", "--filtered-stream-data", "--", "output"]), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","showObject":"4","filteredStreamData":""}""", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior));
        Assert.Equal(new byte[] { 17, 23 }, prior); Assert.Equal(0L, host.OpenLeaseCount);
    }
}

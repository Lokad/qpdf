using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class LogicalContentMemoryTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task RepeatedRawPartsUseOneAdmittedJoinAndPreserveRefusedTargets(bool job, bool shared, bool refuse)
    {
        long maximum = (refuse ? 16L : 384L) * 1024L;
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = shared ? QpdfLimits.Default.MaxMemoryBytes : maximum };
        RecordingMemoryBudget budget = new(shared ? maximum : 8L * 1024L * 1024L);
        using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(limits, null, owner);
        QpdfPath input = QpdfPath.From("input"), output = QpdfPath.From("output");
        host.AddInput(input, Fixture(), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(output, CancellationToken.None))
        { await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        QpdfExecutionOptions options = new(limits);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
            { inputFile = input.Value, outputFile = output.Value, normalizeContent = "y", streamData = "uncompress", removeUnreferencedResources = "no" }), options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new([input.Value, "--normalize-content=y", "--stream-data=uncompress", "--remove-unreferenced-resources=no", output.Value]), options, CancellationToken.None);
        Assert.True(host.TryGetOutput(output, out byte[] bytes));
        if (refuse)
        {
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded);
            Assert.Equal("prior"u8.ToArray(), bytes);
        }
        else
        {
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            using InMemoryQpdfHost verification = new();
            verification.AddInput(input, bytes, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(verification, input, CancellationToken.None);
            Assert.Equal(8, document.PageCount);
        }
        Assert.Equal(0, host.OpenLeaseCount);
        host.Dispose(); owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    private static byte[] Fixture()
    {
        List<(int, string, byte[]?)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 8 /Kids [3 0 R 4 0 R 5 0 R 6 0 R 7 0 R 8 0 R 9 0 R 10 0 R] >>", null),
        ];
        for (int number = 3; number <= 10; number++)
        { objects.Add((number, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 12 0 R >>", null)); }
        byte[] program = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("q Q\r\n", 1638)));
        objects.Add((11, "<< /Length " + program.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " >>", program));
        objects.Add((12, "[" + string.Join(' ', Enumerable.Repeat("11 0 R", 32)) + "]", null));
        return CheckTests.Assemble(objects, 1);
    }
}

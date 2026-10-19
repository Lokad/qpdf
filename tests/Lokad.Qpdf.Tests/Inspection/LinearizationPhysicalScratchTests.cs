using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Inspection;

/// <summary>Physical-checker scratch; complete hint-body ownership remains a separate obligation.</summary>
public sealed class LinearizationPhysicalScratchTests
{
    private static byte[] Input(string id)
    {
        using Stream stream = typeof(LinearizationPhysicalScratchTests).Assembly.GetManifestResourceStream(
            "Lokad.Qpdf.Tests.Inspection.LinearizationCheckCases.json") ?? throw new InvalidOperationException();
        JsonObject corpus = Assert.IsType<JsonObject>(JsonNode.Parse(stream));
        JsonObject row = Assert.IsType<JsonArray>(corpus["Cases"]).OfType<JsonObject>()
            .Single(row => row["Id"]?.GetValue<string>() == id);
        string digest = row["SHA256"]?.GetValue<string>() ?? throw new InvalidOperationException();
        return Convert.FromBase64String(corpus["Files"]?[digest]?.GetValue<string>() ?? throw new InvalidOperationException());
    }

    [Theory]
    [InlineData("classic-valid-two-member-group", true)]
    [InlineData("part8-streamed-control", true)]
    [InlineData("classic-wrong-page-shared-id", false)]
    [InlineData("outline-wrong-length", false)]
    public async Task RepeatedChecksRetireScratchUnder32KiB(string id, bool valid)
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("inputs/a.pdf");
        host.AddInput(path, Input(id), QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, path, CancellationToken.None))
        {
            QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32L * 1024L };
            ResourceGovernor governor = new(limits, CancellationToken.None);
            for (int cycle = 0; cycle < 32; cycle++)
            {
                LinearizationInspection check = LinearizationCheck.Inspect(document.Engine, document.PageCount,
                    limits, governor, CancellationToken.None);
                Assert.Equal(valid ? LinearizationStatus.Valid : LinearizationStatus.Invalid, check.Status);
                Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            }
            Assert.InRange(governor.Snapshot().PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
            // Source objects belong to the still-open document, not the borrowed scratch governor.
            Assert.True(document.PageCount > 0);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("classic-valid-two-member-group")]
    [InlineData("part8-streamed-control")]
    public async Task FailedScratchAdmissionRetiresPriorReservations(string id)
    {
        InMemoryQpdfHost host = new();
        QpdfPath path = QpdfPath.From("inputs/a.pdf");
        host.AddInput(path, Input(id), QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, path, CancellationToken.None);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32L * 1024L };
        ResourceGovernor warm = new(limits, CancellationToken.None);
        Assert.Equal(LinearizationStatus.Valid, LinearizationCheck.Inspect(document.Engine, document.PageCount,
            limits, warm, CancellationToken.None).Status);
        Assert.Equal(0L, warm.Snapshot().LiveMemoryBytes);
        QpdfLimits tight = limits with { MaxMemoryBytes = warm.Snapshot().PeakMemoryBytes - 1L };
        ResourceGovernor failed = new(tight, CancellationToken.None);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => LinearizationCheck.Inspect(
            document.Engine, document.PageCount, tight, failed, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(0L, failed.Snapshot().LiveMemoryBytes);
        failed.ReserveMemory(1L);
        failed.ReleaseMemory(1L);
        Assert.Equal(0L, failed.Snapshot().LiveMemoryBytes);
        Assert.Equal(LinearizationStatus.Valid, LinearizationCheck.Inspect(document.Engine, document.PageCount,
            limits, warm, CancellationToken.None).Status);
        Assert.Equal(0L, warm.Snapshot().LiveMemoryBytes);
    }
}

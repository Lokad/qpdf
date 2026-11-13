using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Jobs;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class JobConfigurationOwnershipTests
{
    private sealed class MemoryHost(InMemoryQpdfHost storage, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => storage.CreateOutputAsync(path, token);
    }

    [Fact]
    public void CompletedCopyPoolRefundsUnusedCapacityWhileKeepingAcceptedStrings()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse("\"" + new string('A', 131072) + "\"");
        using ConfigurationText text = ConfigurationText.Create(governor);
        text.AdmitJsonCopies(1048576L);
        string? value = text.ReadJsonString(document.RootElement);
        text.CompleteJsonCopies();
        Assert.InRange(governor.Snapshot().LiveMemoryBytes, GovernedText.StringBytes(131072), 524288L);
        Assert.Equal(131072, value?.Length);
        text.CompleteJsonCopies();
        Assert.InRange(governor.Snapshot().LiveMemoryBytes, GovernedText.StringBytes(131072), 524288L);
        value = null;
        text.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("mixed")]
    [InlineData("nested")]
    public async Task LoadedPlansKeepTheirCopiedTextBeyondParsingAndRefundWithoutCollection(string route)
    {
        string json = "{\"empty\":\"\",\"outputFile\":\"output\",\"addAttachment\":[{\"file\":\"attachment\",\"description\":\"" + new string('A', 131072) + "\"}]}";
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("config"), Encoding.UTF8.GetBytes(json), QpdfInputOwnership.Transfer);
        RecordingMemoryBudget root = new(16777216L);
        using IMemoryScope caller = root.CreateScope();
        MemoryHost host = new(storage, caller);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            using JobJsonLoader.LoadedJob loaded = route switch
            {
                "mixed" => await JobJsonLoader.ParseArgvAsync(host, ["--job-json-file=config"], QpdfLimits.Default, governor, CancellationToken.None),
                "nested" => await JobJsonLoader.ParseAsync(host, """{"jobJsonFile":"config"}""", QpdfLimits.Default, governor, CancellationToken.None),
                _ => await JobJsonLoader.ParseAsync(host, json, QpdfLimits.Default, governor, CancellationToken.None),
            };
            Assert.Equal(131072, Assert.Single(loaded.Plan.AttachmentsToAdd).Description?.Length);
            // Accepted decoded text remains admitted through plan consumption.
            Assert.True(governor.Snapshot().LiveMemoryBytes >= GovernedText.StringBytes(131072));
            Assert.True(root.ReservedBytes >= GovernedText.StringBytes(131072));
            Assert.Equal(0L, storage.OpenLeaseCount);
            loaded.Dispose();
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(0L, root.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("mixed")]
    [InlineData("nested")]
    public async Task LoadedConfigurationRefundsAdmissionAndClosesItsPublicLifetime(string route)
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("child.json"), "{\"empty\":\"\",\"showNpages\":\"\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        RecordingMemoryBudget root = new(16777216L);
        using IMemoryScope caller = root.CreateScope();
        MemoryHost host = new(storage, caller);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        JobJsonLoader.LoadedJob loaded = route switch
        {
            "argv" => await JobJsonLoader.ParseArgvAsync(host, ["--empty", "--show-npages"], QpdfLimits.Default, governor, CancellationToken.None),
            "mixed" => await JobJsonLoader.ParseArgvAsync(host, ["--job-json-file=child.json"], QpdfLimits.Default, governor, CancellationToken.None),
            _ => await JobJsonLoader.ParseAsync(host, """{"jobJsonFile":"child.json"}""", QpdfLimits.Default, governor, CancellationToken.None),
        };
        Assert.True(governor.Snapshot().LiveMemoryBytes > 0);
        Assert.True(root.ReservedBytes > 0);
        Assert.Equal(0L, storage.OpenLeaseCount);
        loaded.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loaded.Plan);
        loaded.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"invented\":\"\"}")]
    public async Task FailedLoadingReleasesConfigurationAdmission(string child)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("child.json"), Encoding.UTF8.GetBytes(child), QpdfInputOwnership.Copy);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        await Assert.ThrowsAsync<QpdfParseException>(async () => await JobJsonLoader.ParseAsync(host,
            """{"jobJsonFile":"child.json"}""", QpdfLimits.Default, governor, CancellationToken.None));
        Assert.True(governor.Snapshot().PeakMemoryBytes > 0);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

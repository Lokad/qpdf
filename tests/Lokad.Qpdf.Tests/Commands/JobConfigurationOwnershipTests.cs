using System.Reflection;
using System.Runtime.CompilerServices;
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
        Assert.Equal(GovernedText.StringBytes(131072) + 128L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(131072, value?.Length);
        text.CompleteJsonCopies();
        Assert.Equal(GovernedText.StringBytes(131072) + 128L, governor.Snapshot().LiveMemoryBytes);
        value = null;
        text.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("file")]
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
                "file" => await JobJsonLoader.ParseFileAsync(host, QpdfPath.From("config"), QpdfLimits.Default, governor, CancellationToken.None),
                "mixed" => await JobJsonLoader.ParseArgvAsync(host, ["--job-json-file=config"], QpdfLimits.Default, governor, CancellationToken.None),
                "nested" => await JobJsonLoader.ParseAsync(host, """{"jobJsonFile":"config"}""", QpdfLimits.Default, governor, CancellationToken.None),
                _ => await JobJsonLoader.ParseAsync(host, json, QpdfLimits.Default, governor, CancellationToken.None),
            };
            Assert.Equal(131072, Assert.Single(loaded.Plan.AttachmentsToAdd).Description?.Length);
            object session = loaded.GetType().GetField("_owner", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(loaded)
                ?? throw new InvalidOperationException("Loaded configuration owner missing.");
            // Separate the retained DOM/controller estimate from the decoded
            // text owner, without fixing the DOM's conservative byte formula.
            long documents = Assert.IsType<long>(session.GetType().GetField("_reserved", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session));
            long textBytes = governor.Snapshot().LiveMemoryBytes - documents;
            Assert.InRange(textBytes, GovernedText.StringBytes(131072), GovernedText.StringBytes(131072) + 8192L);
            Assert.Equal(0L, storage.OpenLeaseCount);
            loaded.Dispose();
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            Assert.Equal(0L, root.ReservedBytes);
        }
    }

    [Fact]
    public async Task MixedArgvModelsDropReferencesOnDisposal()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("child.json"), "{\"empty\":\"\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        JobJsonLoader.LoadedJob loaded = await JobJsonLoader.ParseArgvAsync(host,
            ["--job-json-file=child.json", "--show-npages"], QpdfLimits.Default, governor, CancellationToken.None);
        Assert.True(governor.Snapshot().LiveMemoryBytes > 0);
        WeakReference[] documents = SnapshotAndDispose(loaded);
        Assert.Equal(0L, host.OpenLeaseCount);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        governor.ReserveMemory(0L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.All(documents, document => Assert.False(document.IsAlive));
        GC.KeepAlive(loaded);
    }

    [Fact]
    public async Task LoadedConfigurationStaysAdmittedUntilOwnerDisposal()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("child.json"), "{\"empty\":\"\",\"showNpages\":\"\"}"u8.ToArray(), QpdfInputOwnership.Copy);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        JobJsonLoader.LoadedJob loaded = await JobJsonLoader.ParseAsync(host, """{"jobJsonFile":"child.json"}""", QpdfLimits.Default, governor, CancellationToken.None);
        Assert.True(governor.Snapshot().LiveMemoryBytes > 0);
        Assert.Equal(0L, host.OpenLeaseCount);
        WeakReference[] documents = SnapshotAndDispose(loaded);
        Assert.Throws<ObjectDisposedException>(() => loaded.Plan);
        long disposedMemory = governor.Snapshot().LiveMemoryBytes;
        loaded.Dispose();
        Assert.Equal(disposedMemory, governor.Snapshot().LiveMemoryBytes);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        governor.ReserveMemory(0L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.All(documents, document => Assert.False(document.IsAlive));
        GC.KeepAlive(loaded);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] SnapshotAndDispose(IDisposable loaded)
    {
        object owner = loaded.GetType().GetField("_owner", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(loaded)
            ?? throw new InvalidOperationException("Loaded configuration owner missing.");
        List<JsonDocument> documents = Assert.IsType<List<JsonDocument>>(owner.GetType().GetField("_documents", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner));
        WeakReference[] references = documents.Select(document => new WeakReference(document)).ToArray();
        Assert.Equal(2, references.Length);
        loaded.Dispose();
        return references;
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

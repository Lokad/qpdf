using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Jobs;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class JobConfigurationOwnershipTests
{
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
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => loaded.Plan);
        loaded.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
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
        Assert.True(governor.Snapshot().TotalWork > 0 && governor.Snapshot().PeakMemoryBytes > 0);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

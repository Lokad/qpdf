using System.Reflection;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests;

public sealed class ProbeRequestedInputTests
{
    [Fact]
    public async Task RequestedFilesLoadOnceAndUnrelatedPathsStayUntouched()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("qpdfprobe-requested-");
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        try
        {
            string file = Path.Combine(directory.FullName, "selected");
            File.WriteAllBytes(file, "data"u8.ToArray());
            Dictionary<string, string> available = new(StringComparer.Ordinal)
            {
                ["inputs/selected"] = file,
                ["inputs/unrelated"] = Path.Combine(directory.FullName, "absent"),
            };
            using InMemoryQpdfHost storage = new();
            Assembly probe = Assembly.LoadFrom(ProbeCliTests.FindProbeDll());
            Type type = probe.GetType("Lokad.Qpdf.Probe.ProbeInputFiles+Requested", true)
                ?? throw new InvalidOperationException("Missing requested-input host.");
            IQpdfHost host = Assert.IsAssignableFrom<IQpdfHost>(Activator.CreateInstance(type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [storage, available], null));
            string identity;
            await using (IQpdfInput first = await host.OpenInputAsync(QpdfPath.From("././inputs/selected"), CancellationToken.None))
            {
                byte[] bytes = new byte[4];
                await QpdfInputReader.ReadExactlyAsync(first, 0L, bytes, CancellationToken.None);
                Assert.Equal("data"u8.ToArray(), bytes); identity = first.Identity;
            }
            File.WriteAllBytes(file, "edit"u8.ToArray());
            await using (IQpdfInput second = await host.OpenInputAsync(QpdfPath.From("inputs/selected"), CancellationToken.None))
            {
                byte[] bytes = new byte[4];
                await QpdfInputReader.ReadExactlyAsync(second, 0L, bytes, CancellationToken.None);
                Assert.Equal(identity, second.Identity); Assert.Equal("data"u8.ToArray(), bytes);
            }
            Assert.Equal("inputs/unrelated", Assert.Single(available).Key);
            using CancellationTokenSource canceled = new(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await host.OpenInputAsync(QpdfPath.From("inputs/unrelated"), canceled.Token));
            Assert.Equal("inputs/unrelated", Assert.Single(available).Key);
            Assert.Equal(0L, storage.OpenLeaseCount);
        }
        finally
        {
            Assert.StartsWith(tempRoot, Path.GetFullPath(directory.FullName), StringComparison.OrdinalIgnoreCase);
            directory.Delete(true);
        }
    }
}

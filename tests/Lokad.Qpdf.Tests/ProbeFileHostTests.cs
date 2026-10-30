using System.Reflection;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests;

public sealed class ProbeFileHostTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("cancel")]
    [InlineData("success")]
    public async Task PhysicalInputFailuresUseHostOutcomesAndKeepCancellation(string outcome)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("qpdfprobe-file-host-");
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        try
        {
            string physical = Path.Combine(directory.FullName, "input");
            if (outcome == "success") { File.WriteAllBytes(physical, "data"u8.ToArray()); }
            if (outcome == "directory") { physical = directory.FullName; }
            using InMemoryQpdfHost output = new();
            Assembly probe = Assembly.LoadFrom(ProbeCliTests.FindProbeDll());
            Type type = probe.GetType("Lokad.Qpdf.Probe.FileSelectionCommand+FileHost", true)
                ?? throw new InvalidOperationException("Missing live file host.");
            IQpdfHost host = Assert.IsAssignableFrom<IQpdfHost>(Activator.CreateInstance(type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [output, new Dictionary<string, string>(StringComparer.Ordinal) { ["input"] = physical }], null));
            using CancellationTokenSource cancellation = new();
            if (outcome == "cancel") { cancellation.Cancel(); }
            Exception? failure = await Record.ExceptionAsync(async () =>
            {
                await using IQpdfInput input = await host.OpenInputAsync(QpdfPath.From("input"), cancellation.Token);
                byte[] bytes = new byte[4];
                await QpdfInputReader.ReadExactlyAsync(input, 0L, bytes, cancellation.Token);
                Assert.Equal("data"u8.ToArray(), bytes);
            });
            if (outcome == "success") { Assert.Null(failure); }
            else if (outcome == "cancel") { Assert.IsAssignableFrom<OperationCanceledException>(failure); }
            else
            {
                QpdfHostException error = Assert.IsType<QpdfHostException>(failure);
                Assert.Equal(QpdfPath.From("input"), error.Path);
                Assert.Equal(outcome == "missing" ? QpdfHostFailureReason.NotFound : QpdfHostFailureReason.ReadFailed, error.Reason);
                Assert.NotNull(error.InnerException);
            }
        }
        finally
        {
            Assert.StartsWith(tempRoot, Path.GetFullPath(directory.FullName), StringComparison.OrdinalIgnoreCase);
            directory.Delete(true);
        }
    }
}

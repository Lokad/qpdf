using System.Reflection;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Foundation;

// Bound the number of independently rooted jobs during a shared collection.
// Every row still executes its original producer and assertion body.
internal static class OwnershipMatrix
{
    private const int BatchSize = 64;

    internal static IEnumerable<object[]> Batches(
        IEnumerable<(string Method, IEnumerable<object[]> Rows)> matrix)
    {
        int count = matrix.Sum(entry => entry.Rows.Count());
        for (int batch = 0; batch < (count + BatchSize - 1) / BatchSize; batch++)
        { yield return [batch]; }
    }

    internal static Task RunAsync<T>(ITestOutputHelper output,
        IEnumerable<(string Method, IEnumerable<object[]> Rows)> matrix,
        int batch, Func<OwnershipBatch.Observation, T> create)
    {
        var rows = matrix.SelectMany(entry => entry.Rows.Select(row => (entry.Method, Row: row)))
            .Skip(checked(batch * BatchSize)).Take(BatchSize).ToArray();
        Assert.NotEmpty(rows);
        return OwnershipBatch.RunAsync(output, rows.Select(entry =>
            (OwnershipBatch.Id(typeof(T).Name, entry.Method, entry.Row),
            (Func<OwnershipBatch.Observation, Task>)(async observation =>
            {
                T fixture = create(observation);
                MethodInfo method = typeof(T).GetMethod(entry.Method,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Missing original ownership scenario.");
                Task running = (Task)(method.Invoke(fixture, entry.Row)
                    ?? throw new InvalidOperationException("Missing original observation task."));
                await running;
            }))));
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json;
using Lokad.Qpdf.Runtime;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Foundation;

// Each case runs to its next original collection boundary before another case
// advances. Paused operations use independent sinks and governors; only the
// process-wide collection is shared.
internal static class OwnershipBatch
{
    // A producer's immediate private accounting read precedes the lifetime
    // observation. Keep incidental process-wide collection out of that phase;
    // the fixture's explicit collection/maintenance boundaries still follow it.
    internal static IDisposable CaptureWithoutCollection()
    {
        Assert.True(GC.TryStartNoGCRegion(16L * 1024L * 1024L));
        return new CaptureScope();
    }

    private sealed class CaptureScope : IDisposable
    {
        public void Dispose() => GC.EndNoGCRegion();
    }

    internal sealed class Observation
    {
        private TaskCompletionSource _arrived = NewSignal();
        private TaskCompletionSource? _release;
        internal ResourceGovernor? Governor;
        internal int Maintenance;
        internal int Rounds;
        internal int RemainingRounds;
        internal bool Compacting;
        internal bool Interleaved;
        internal int Phases;
        internal int Boundaries;
        internal int RequestedCollections;
        internal bool Done;
        internal string? Error;

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task CollectAsync(ResourceGovernor? governor, int maintenance)
            => CollectAsync(governor, maintenance, 4, false, false);

        internal Task CollectAsync(ResourceGovernor? governor, int maintenance,
            int rounds, bool compacting, bool interleaved)
        {
            Assert.Null(_release);
            Assert.InRange(rounds, 1, 4);
            if (interleaved) { Assert.Equal(rounds, maintenance); }
            Governor = governor;
            Maintenance = maintenance;
            Rounds = rounds;
            RemainingRounds = rounds;
            Compacting = compacting;
            Interleaved = interleaved;
            Phases++;
            Boundaries += maintenance;
            RequestedCollections += checked(rounds * (compacting ? 1 : 2));
            _release = NewSignal();
            _arrived.SetResult();
            return _release.Task;
        }

        internal async Task ExecuteAsync(Func<Observation, Task> run)
        {
            try { await run(this); }
            catch (Exception exception) { Error = exception.ToString(); }
            finally { Done = true; _arrived.TrySetResult(); }
        }

        internal Task ArriveAsync() => _arrived.Task.WaitAsync(TimeSpan.FromSeconds(60));

        internal async Task AdvanceAsync()
        {
            TaskCompletionSource release = _release ?? throw new InvalidOperationException("Missing collection boundary.");
            _release = null;
            Governor = null;
            _arrived = NewSignal();
            release.SetResult();
            await ArriveAsync();
        }

        internal async Task RejectBoundaryAsync(Exception exception)
        {
            TaskCompletionSource release = _release ?? throw new InvalidOperationException("Missing collection boundary.");
            _release = null;
            Governor = null;
            _arrived = NewSignal();
            release.SetException(exception);
            await ArriveAsync();
        }
    }

    internal static string Id(string type, string method, object[] arguments)
        => type + "." + method + "|" + JsonSerializer.Serialize(arguments);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckIndependentGovernors(IEnumerable<Observation> observations)
    {
        HashSet<ResourceGovernor> seen = new(ReferenceEqualityComparer.Instance);
        foreach (Observation observation in observations)
        { if (observation.Governor is not null) { Assert.True(seen.Add(observation.Governor)); } }
    }

    internal static async Task RunAsync(ITestOutputHelper output,
        IEnumerable<(string Id, Func<Observation, Task> Run)> cases)
    {
        List<(string Id, Observation State, Task Completion)> running = [];
        foreach ((string id, Func<Observation, Task> run) in cases)
        {
            Observation state = new();
            Task completion = state.ExecuteAsync(run);
            await state.ArriveAsync();
            running.Add((id, state, completion));
        }
        CheckIndependentGovernors(running.Where(item => !item.State.Done).Select(item => item.State));
        int phases = 0, maintenance = 0, collections = 0;
        while (running.Any(item => !item.State.Done))
        {
            CheckIndependentGovernors(running.Where(item => !item.State.Done).Select(item => item.State));
            // Preserve each fixture's original collection geometry and the
            // ordering of maintenance within or after its collection rounds.
            var cohorts = running.Where(item => !item.State.Done)
                .GroupBy(item => item.State.Compacting)
                .Select(group => group.ToArray()).ToArray();
            foreach (var cohort in cohorts)
            {
                bool compacting = cohort[0].State.Compacting;
                if (compacting) { GC.Collect(2, GCCollectionMode.Forced, true, true); }
                else { GC.Collect(); }
                GC.WaitForPendingFinalizers(); collections++;
                if (!compacting) { GC.Collect(); collections++; }
                foreach (var item in cohort)
                { item.State.RemainingRounds--; }
                foreach (var item in cohort.Where(item => !item.State.Done && item.State.Interleaved))
                {
                    try
                    {
                        (item.State.Governor ?? throw new InvalidOperationException("Missing maintenance governor.")).ReserveMemory(0L);
                        maintenance++;
                    }
                    catch (Exception exception) { await item.State.RejectBoundaryAsync(exception); }
                }
                // Resume a row immediately after its own original number of
                // rounds. Three-round observations need not await a separate
                // four-round cohort, and newly arrived phases start next round.
                var ready = cohort.Where(item => !item.State.Done && item.State.RemainingRounds == 0).ToArray();
                if (ready.Length != 0) { phases++; }
                foreach (var item in ready.Where(item => !item.State.Interleaved))
                {
                    try
                    {
                        for (int boundary = 0; boundary < item.State.Maintenance; boundary++)
                        {
                            (item.State.Governor ?? throw new InvalidOperationException("Missing maintenance governor.")).ReserveMemory(0L);
                            maintenance++;
                        }
                    }
                    catch (Exception exception) { await item.State.RejectBoundaryAsync(exception); }
                }
                foreach (var item in ready.Where(item => !item.State.Done))
                { await item.State.AdvanceAsync(); }
            }
        }
        List<string> failures = [];
        foreach (var item in running)
        {
            await item.Completion;
            output.WriteLine("OWNERSHIP {0} {1}", item.State.Error is null ? "PASS" : "FAIL", item.Id);
            output.WriteLine("OWNERSHIP OBSERVATION {0} phases={1} maintenance={2}",
                item.Id, item.State.Phases, item.State.Boundaries);
            output.WriteLine("OWNERSHIP COLLECTION CONTROL {0} collections={1}",
                item.Id, item.State.RequestedCollections);
            if (item.State.Error is not null) { failures.Add(item.Id + ": " + item.State.Error); }
        }
        output.WriteLine("OWNERSHIP BATCH cases={0} phases={1} collections={2} maintenance={3}",
            running.Count, phases, collections, maintenance);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}

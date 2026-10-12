using System.Text.Json;
using System.Text.Json.Nodes;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class LinearizedPublicHintOwnershipTests
{
    public static IEnumerable<object[]> Jobs()
    {
        // Cover each publication outcome through both routes, with raw/decoded
        // inputs and classic/streamed output. Chunk boundaries and private
        // receipt geometry are implementation details.
        yield return ["raw", "argv", "disable", false, "success"];
        yield return ["decoded", "job", "generate", true, "success"];
        yield return ["decoded", "argv", "generate", false, "write-failure"];
        yield return ["raw", "job", "disable", true, "write-failure"];
        yield return ["raw", "argv", "generate", true, "commit-failure"];
        yield return ["decoded", "job", "disable", false, "commit-failure"];
        yield return ["decoded", "argv", "disable", true, "cancellation"];
        yield return ["raw", "job", "generate", false, "cancellation"];
    }

    private static byte[] Input(string shape)
    {
        if (shape == "raw") { return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf")); }
        using Stream stream = typeof(LinearizedPublicHintOwnershipTests).Assembly.GetManifestResourceStream(
            "Lokad.Qpdf.Tests.Inspection.LinearizationCheckCases.json") ?? throw new InvalidOperationException();
        JsonObject corpus = JsonNode.Parse(stream) as JsonObject ?? throw new InvalidOperationException();
        JsonObject row = (corpus["Cases"] as JsonArray ?? throw new InvalidOperationException()).OfType<JsonObject>()
            .Single(row => row["Id"]?.GetValue<string>() == "forms-2page.pdf-generate-True-argv-corrected");
        string digest = row["SHA256"]?.GetValue<string>() ?? throw new InvalidOperationException();
        return Convert.FromBase64String(corpus["Files"]?[digest]?.GetValue<string>() ?? throw new InvalidOperationException());
    }

    [Theory]
    [MemberData(nameof(Jobs))]
    public async Task CompleteJobsPreserveLinearizedPublicationAndRetainedChunks(
        string inputShape, string route, string objectStreams, bool compress, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        PausedHost host = new(outcome);
        host.Inner.AddInput(QpdfPath.From("input.pdf"), Input(inputShape), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(QpdfPath.From("output.pdf"), CancellationToken.None))
        {
            await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] priorAlias));
        string streamData = compress ? "compress" : "uncompress";
        Task<QpdfExecutionResult> executing = route == "argv"
            ? Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(new[] { "input.pdf", "--linearize", "--object-streams=" + objectStreams,
                "--stream-data=" + streamData, "--", "output.pdf" }), new QpdfExecutionOptions(QpdfLimits.Default), cancellation.Token).AsTask()
            : Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input.pdf", outputFile = "output.pdf", linearize = "",
                objectStreams, streamData }), new QpdfExecutionOptions(QpdfLimits.Default), cancellation.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(executing.IsCompleted);
            Assert.True(host.HintChunk.Length > 0);
            if (outcome == "cancellation") { cancellation.Cancel(); }
            else { host.Resume(); }
            QpdfExecutionResult result = await executing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed :
                outcome == "cancellation" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(outcome == "success" ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
            Assert.Equal(host.ExpectedChunk, host.HintChunk.ToArray());
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.Equal(new byte[] { 17, 23, 41 }, priorAlias);
            Assert.True(host.Inner.TryGetOutput(QpdfPath.From("output.pdf"), out byte[] output));
            if (outcome == "success")
            {
                InMemoryQpdfHost checking = new();
                checking.AddInput(QpdfPath.From("result.pdf"), output, QpdfInputOwnership.Copy);
                QpdfCheckResult check = await Qpdf.CheckAsync(checking, QpdfPath.From("result.pdf"), CancellationToken.None);
                Assert.Equal(2, check.PageCount);
                Assert.Equal(QpdfCheckStatus.Passed, Assert.Single(check.Checks, value => value.Name == "linearization").Status);
                QpdfExecutionResult show = await Qpdf.ExecuteAsync(checking,
                    new QpdfCommandInvocation(new[] { "result.pdf", "--show-linearization" }), CancellationToken.None);
                Assert.Equal(QpdfExitCode.Success, show.ExitCode);
                Assert.Contains(show.Diagnostics, value => value.Message.Contains("Page Offsets Hint Table", StringComparison.Ordinal));
                Assert.Equal(0L, checking.OpenLeaseCount);
                if (inputShape == "decoded") { Assert.True(result.Counters.TotalDecodedBytes > 0L); }
                GC.KeepAlive(output);
            }
            else { Assert.Equal(priorAlias, output); }
            GC.KeepAlive(priorAlias);
        }
        finally
        {
            host.Resume();
            await executing;
        }
    }

    private sealed class PausedHost(string outcome) : IQpdfHost
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _paused;
        private string Outcome { get; } = outcome;
        internal InMemoryQpdfHost Inner { get; } = new();
        internal Task Entered => _entered.Task;
        internal ReadOnlyMemory<byte> HintChunk { get; private set; }
        internal byte[] ExpectedChunk { get; private set; } = Array.Empty<byte>();
        internal void Resume() => _resume.TrySetResult();
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => Inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => new Transaction(this, await Inner.CreateOutputAsync(path, token));

        private void Observe(ReadOnlyMemory<byte> data)
        {
            HintChunk = data;
            ExpectedChunk = data.ToArray();
        }

        private sealed class Transaction(PausedHost host, IQpdfOutputTransaction inner) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
            {
                if (!host._paused && IsHint(data))
                {
                    host._paused = true;
                    host.Observe(data);
                    host._entered.TrySetResult();
                    await host._resume.Task.WaitAsync(token);
                    if (host.Outcome == "write-failure") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                }
                await inner.WriteAsync(data, token);
            }
            private static bool IsHint(ReadOnlyMemory<byte> data)
                => data.Span.IndexOf(" /S "u8) >= 0 && data.Span.IndexOf("\nstream\n"u8) >= 0;
            public ValueTask CommitAsync(CancellationToken token)
                => host.Outcome == "commit-failure" ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed)) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}

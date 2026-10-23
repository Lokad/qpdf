using System.Collections;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class InvocationSnapshotTests
{
    [Fact]
    public void StringSettingsExposeReadOnlyDefensiveSnapshots()
    {
        string[] arguments = ["input.pdf", "--show-npages"], keys = ["qpdf"], objects = ["1,0"];
        QpdfCommandInvocation invocation = new(arguments);
        QpdfJsonOptions options = new(2, keys, "none", null, objects, false);
        arguments[1] = "--show-pages"; keys[0] = "pages"; objects[0] = "trailer";
        Assert.Equal(new[] { "input.pdf", "--show-npages" }, invocation.Arguments);
        Assert.Equal(new[] { "qpdf" }, options.Keys);
        Assert.Equal(new[] { "1,0" }, options.Objects);
        foreach (IReadOnlyList<string> snapshot in new[] { invocation.Arguments, options.Keys, options.Objects })
        {
            IList<string> list = Assert.IsAssignableFrom<IList<string>>(snapshot);
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list[0] = "changed");
            Assert.Throws<NotSupportedException>(() => list.Add("changed"));
            Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList>(snapshot).Clear());
        }
        Assert.Same(options.Keys, (options with { }).Keys);
        Assert.Same(options.Objects, (options with { }).Objects);
    }

    [Fact]
    public async Task InvocationReuseKeepsTheOriginalInspection()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input.pdf"), await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf")), QpdfInputOwnership.Transfer);
        string[] source = ["input.pdf", "--show-npages"];
        QpdfCommandInvocation invocation = new(source);
        QpdfExecutionResult first = await Qpdf.ExecuteAsync(host, invocation, CancellationToken.None);
        source[1] = "--show-pages";
        Assert.Throws<NotSupportedException>(() => ((IList<string>)invocation.Arguments)[1] = "--show-pages");
        QpdfExecutionResult second = await Qpdf.ExecuteAsync(host, invocation, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, first.ExitCode); Assert.Equal(QpdfExitCode.Success, second.ExitCode);
        Assert.Equal(QpdfDiagnosticCode.PageCount, Assert.Single(first.Diagnostics).Code);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.Equal(first.Counters, second.Counters);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task InFlightJsonAndReuseKeepOriginalKeyAndObjectSelections()
    {
        using InMemoryQpdfHost storage = new();
        storage.AddInput(QpdfPath.From("input.pdf"), await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf")), QpdfInputOwnership.Transfer);
        PausedHost host = new(storage);
        string[] keys = ["qpdf"], objects = ["1,0"];
        QpdfJsonOptions options = new(2, keys, "none", null, objects, false);
        Task<QpdfJsonResult> running = Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input.pdf"),
            QpdfPath.From("output.json"), options, CancellationToken.None).AsTask();
        try
        {
            await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            keys[0] = "pages"; objects[0] = "trailer";
            Assert.Throws<NotSupportedException>(() => ((IList<string>)options.Keys)[0] = "pages");
            Assert.Throws<NotSupportedException>(() => ((IList<string>)options.Objects)[0] = "trailer");
        }
        finally { host.Resume.TrySetResult(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        QpdfJsonResult first = await running;
        QpdfJsonResult second = await Qpdf.GetDocumentJsonAsync(storage, QpdfPath.From("input.pdf"),
            QpdfPath.From("output.json"), options, CancellationToken.None);
        Assert.Equal(first.Json, second.Json);
        using JsonDocument json = JsonDocument.Parse(first.Json);
        Assert.False(json.RootElement.TryGetProperty("pages", out _));
        Assert.Equal("obj:1 0 R", Assert.Single(json.RootElement.GetProperty("qpdf")[1].EnumerateObject()).Name);
        Assert.Equal(0L, first.Counters.OpenHandles); Assert.Equal(0L, second.Counters.OpenHandles);
        Assert.Equal(0L, storage.OpenLeaseCount);
    }

    private sealed class PausedHost(InMemoryQpdfHost storage) : IQpdfHost
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            Entered.TrySetResult(); await Resume.Task.WaitAsync(token);
            return await storage.OpenInputAsync(path, token);
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => storage.CreateOutputAsync(path, token);
    }
}

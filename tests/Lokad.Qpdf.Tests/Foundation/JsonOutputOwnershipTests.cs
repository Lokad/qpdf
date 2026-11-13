using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Tests.Reading;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class JsonOutputOwnershipTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.json"), Side = QpdfPath.From("side-4");

    private static void Closed(RecordingJobHost host, object? result)
    {
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        QpdfExecutionCounters? counters = result is null ? null : ExecutionJobFixtures.Counters(result);
        QpdfExecutionCounters? snapshot = counters is null ? null : counters with { };
        if (counters is not null)
        { Assert.Equal(0L, counters.OpenHandles); Assert.Equal(host.HostCalls, counters.HostOperations); }
        host.Dispose();
        Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
        Assert.Equal(snapshot, counters);
    }

    public static IEnumerable<object[]> InlineJobs()
    {
        yield return ["argv", false]; yield return ["argv", true];
        foreach (string route in new[] { "job", "mixed", "response" }) { yield return [route, false]; }
    }

    [Theory]
    [MemberData(nameof(InlineJobs))]
    public async Task IndependentInlineTextAliasSurvivesHostCleanup(string route, bool compressed)
    {
        using RecordingJobHost host = await ExecutionJobFixtures.PrepareAsync("argv-json", compressed, "input-close", "success");
        host.PausePath = Input;
        string flags = "--json=2\n--json-stream-data=inline";
        List<string> argv = ["input.pdf", "--json=2", "--json-stream-data=inline"];
        if (route == "mixed") { argv.Insert(1, "--job-json-file=settings.json"); }
        if (route == "response")
        { host.Inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes("input.pdf\n" + flags + "\n"), QpdfInputOwnership.Copy); argv = ["@args.txt"]; }
        Task<QpdfExecutionResult> running = route == "job"
            ? Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input.pdf\",\"json\":\"2\",\"jsonStreamData\":\"inline\"}", CancellationToken.None).AsTask()
            : Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), CancellationToken.None).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L); host.Resume();
            QpdfExecutionResult result = await running;
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            string text = Assert.IsType<string>(result.OutputText);
            Assert.Equal(1378, text.Length);
            Assert.True(host.Budget.ReservedBytes >= GovernedText.StringBytes(text.Length));
            Assert.True(host.Budget.ReservedBytes < host.Budget.PeakBytes);
            Closed(host, result);
            using JsonDocument parsed = JsonDocument.Parse(text);
            Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32());
        }
        finally { host.Resume(); await running; }
    }

    public static IEnumerable<object[]> PublicationJobs()
    {
        foreach (string route in new[] { "typed", "argv" })
        {
            yield return [route, false, 1, "none"];
            foreach (string mode in new[] { "none", "inline", "file" }) { yield return [route, false, 2, mode]; }
        }
        yield return ["argv", true, 2, "inline"];
        yield return ["job", true, 2, "file"];
        yield return ["mixed", false, 2, "inline"];
        yield return ["response", true, 2, "none"];
    }

    [Theory]
    [MemberData(nameof(PublicationJobs))]
    public async Task SuspendedJsonWritesKeepAdmissionAndHostCopiesSurviveCleanup(string route, bool compressed, int version, string mode)
    {
        using RecordingJobHost owner = await ExecutionJobFixtures.PrepareAsync("argv-json", compressed, "write", "success");
        owner.PausePath = Output;
        CaptureHost host = new(owner);
        QpdfLimits limits = QpdfLimits.Default with { MaxOpenHandles = 1L };
        Task<object> running = RunPublished(host, route, version, mode, limits, CancellationToken.None);
        try
        {
            await owner.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.IsCompleted); Assert.False(owner.PendingWrite.IsEmpty);
            Assert.True(owner.Budget.ReservedBytes >= owner.PendingWrite.Length);
            Assert.Equal(owner.PendingWrite.ToArray(), Assert.Single(host.Writes).Bytes);
            owner.Resume();
            object result = await running;
            string? text = result is QpdfJsonResult typed ? typed.Json : null;
            if (result is QpdfExecutionResult command)
            { Assert.Equal(QpdfExecutionStatus.Completed, command.Status); Assert.Null(command.OutputText); }
            Assert.True(owner.Inner.TryGetOutput(Output, out byte[] main));
            using (JsonDocument parsed = JsonDocument.Parse(main)) { Assert.Equal(version, parsed.RootElement.GetProperty("version").GetInt32()); }
            Assert.Equal(main, host.Writes.Single(x => x.Path == Output).Bytes);
            if (text is not null)
            {
                Assert.Equal(main, Encoding.UTF8.GetBytes(text));
                Assert.True(owner.Budget.ReservedBytes >= GovernedText.StringBytes(text.Length));
            }
            if (mode == "file")
            {
                Assert.Contains("side-4", host.Names);
                Assert.True(owner.Inner.TryGetOutput(Side, out byte[] side)); Assert.Equal("q Q\n"u8.ToArray(), side);
                Assert.Equal(side, host.Writes.Single(x => x.Path == Side).Bytes);
            }
            Assert.True(owner.Budget.ReservedBytes < owner.Budget.PeakBytes);
            Closed(owner, result);
            Assert.Equal(main, host.Writes.Single(x => x.Path == Output).Bytes);
            Assert.Contains("output.json", host.Names);
            if (text is not null) { using JsonDocument parsed = JsonDocument.Parse(text); Assert.Equal(version, parsed.RootElement.GetProperty("version").GetInt32()); }
        }
        finally { owner.Resume(); await running; }
    }

    private static async Task<object> RunPublished(CaptureHost host, string route, int version, string mode,
        QpdfLimits limits, CancellationToken token)
    {
        if (route == "typed")
        {
            return await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input.pdf"), QpdfPath.From("output.json"),
                new QpdfJsonOptions(version, [], mode, mode == "file" ? "side" : null, [], false), limits, token);
        }
        if (route == "job")
        {
            string job = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["inputFile"] = "input.pdf",
                ["outputFile"] = "output.json",
                ["json"] = version.ToString(),
                ["jsonStreamData"] = mode,
                ["jsonStreamPrefix"] = "side",
            });
            return await Qpdf.ExecuteJobAsync(host, job, new QpdfExecutionOptions(limits), token);
        }
        List<string> argv = ["input.pdf", "--json=" + version, "--json-stream-data=" + mode, "--json-stream-prefix=side", "--", "output.json"];
        if (route == "mixed") { argv.Insert(1, "--job-json-file=settings.json"); }
        if (route == "response")
        {
            host.Inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy);
            argv = ["@args.txt"];
        }
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), new QpdfExecutionOptions(limits), token);
    }

    public static IEnumerable<object[]> TextEncodings()
    {
        foreach (int version in new[] { 1, 2 })
        foreach (string encoding in new[] { "utf8", "utf8-invalid", "utf16-big", "utf16-little" })
        foreach (int padding in new[] { 0, 253, 254, 255, 256 }) { yield return [version, encoding, padding]; }
    }

    [Theory]
    [InlineData("typed", false)]
    [InlineData("typed", true)]
    [InlineData("argv", false)]
    [InlineData("argv", true)]
    [InlineData("job", false)]
    [InlineData("job", true)]
    [Trait("Layer", "Contract")]
    public async Task PublicJsonRepairMemoizationPreservesTypedRejectionAndCommandWarningCounts(string route, bool recovered)
    {
        byte[] input = TolerantObjectLoadingTests.Pdf(Enumerable.Repeat("[ (" + new string('A', 4096) + ") @ ]", 50).ToArray(), recovered);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input.pdf"), input, QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output.json"), CancellationToken.None))
        { await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = input.Length + 60000L, MaxParserErrors = 1L, MaxOpenHandles = 1L };
        IReadOnlyList<QpdfDiagnostic> diagnostics;
        QpdfExecutionCounters counters;
        if (route == "typed")
        {
            // The typed JSON route is strict; it must retain its existing
            // rejection rather than acquire the commands' repair policy.
            await Assert.ThrowsAsync<QpdfParseException>(async () => await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input.pdf"), QpdfPath.From("output.json"),
                new QpdfJsonOptions(1, ["objects", "objectinfo"], "none", null, [], false), limits, CancellationToken.None));
            Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output.json"), out byte[] prior));
            Assert.Equal("prior"u8.ToArray(), prior);
            return;
        }
        else
        {
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input.pdf","outputFile":"output.json","json":"1","jsonKey":["objects","objectinfo"],"global":{"parserMaxErrors":1}}""", new QpdfExecutionOptions(limits), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["input.pdf", "--json=1", "--json-key=objects", "--json-key=objectinfo", "--parser-max-errors=1", "--", "output.json"]), new QpdfExecutionOptions(limits), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
            diagnostics = result.Diagnostics; counters = result.Counters;
        }
        Assert.Equal(100, diagnostics.Count(x => x.Code == QpdfDiagnosticCode.RepairedObject));
        Assert.Equal(recovered ? 101L : 100L, counters.DiagnosticCount);
        Assert.InRange(counters.PeakMemoryBytes, input.Length, limits.MaxMemoryBytes);
        Assert.Equal(0L, counters.OpenHandles); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output.json"), out byte[] bytes));
        using JsonDocument json = JsonDocument.Parse(bytes);
        for (int number = 5; number < 55; number++)
        {
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("objects").GetProperty($"{number} 0 R").ValueKind);
            Assert.False(json.RootElement.GetProperty("objectinfo").GetProperty($"{number} 0 R").GetProperty("stream").GetProperty("is").GetBoolean());
        }
    }

    [Theory]
    [MemberData(nameof(TextEncodings))]
    [Trait("Layer", "Contract")]
    public async Task PublicUnicodeNamesStringsAndNumericBytesSurviveChunkBoundaries(int version, string encoding, int padding)
    {
        string expected = new string('A', padding) + "\"\\\n\t" + string.Concat(Enumerable.Repeat("é😀", 1024));
        byte[] payload = encoding switch
        {
            "utf16-big" => new byte[] { 254, 255 }.Concat(Encoding.BigEndianUnicode.GetBytes(expected)).ToArray(),
            "utf16-little" => new byte[] { 255, 254 }.Concat(Encoding.Unicode.GetBytes(expected)).ToArray(),
            "utf8-invalid" => new byte[] { 239, 187, 191 }.Concat(Encoding.UTF8.GetBytes(expected)).Concat(new byte[] { 237, 160, 128, 240, 159, 255 }).ToArray(),
            _ => new byte[] { 239, 187, 191 }.Concat(Encoding.UTF8.GetBytes(expected)).ToArray(),
        };
        if (encoding == "utf8-invalid") { expected = Encoding.UTF8.GetString(payload, 3, payload.Length - 3); }
        byte[] input = CheckTests.Assemble(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null),
            (4, "<< /Text <" + Convert.ToHexString(payload)
                + "> /#22#5c /#c3#a9 /Numbers [001 -.5 000. .25] >>", null),
        ], 1);
        InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("unicode.pdf"), input, QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = input.Length + 60000L, MaxOpenHandles = 1L };
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("unicode.pdf"), QpdfPath.From("unicode.json"),
            new QpdfJsonOptions(version, [version == 1 ? "objects" : "qpdf"], "none", null, ["4"], false), limits, CancellationToken.None);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(QpdfPath.From("unicode.json"), out byte[] bytes));
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(result.Json));
        using JsonDocument parsed = JsonDocument.Parse(bytes);
        JsonElement value = version == 1 ? parsed.RootElement.GetProperty("objects").GetProperty("4 0 R")
            : parsed.RootElement.GetProperty("qpdf")[1].GetProperty("obj:4 0 R").GetProperty("value");
        string representation = version == 1 ? expected : encoding == "utf16-big"
            ? "u:" + expected : "b:" + Convert.ToHexString(payload).ToLowerInvariant();
        Assert.Equal(representation, value.GetProperty("/Text").GetString());
        Assert.Equal(version == 1 ? "/#c3#a9" : "/é", value.GetProperty("/\"\\").GetString());
        Assert.Equal("[1,-0.5,0.0,0.25]", value.GetProperty("/Numbers").GetRawText());
    }

    public static IEnumerable<object[]> FailureJobs()
    {
        foreach (string route in new[] { "typed", "argv" })
        foreach (string mode in new[] { "inline", "file" })
        foreach (string outcome in new[] { "input-close", "create", "write", "commit", "output-close", "cancel-input", "cancel-write", "memory", "output-bytes" })
        { yield return [route, route == "argv" && outcome != "memory", mode, outcome, false]; }
        foreach (string route in new[] { "typed", "argv" })
        foreach (string outcome in new[] { "write", "commit", "output-close", "cancel-write" })
        { yield return [route, false, "file", outcome, true]; }
        foreach (string route in new[] { "job", "mixed", "response" }) { yield return [route, false, "file", "write", false]; }
    }

    [Theory]
    [MemberData(nameof(FailureJobs))]
    public async Task FailedAndCanceledPublicJobsPreserveTargetsAndEscapedHostBytes(string route, bool compressed, string mode, string outcome, bool secondary)
    {
        using CancellationTokenSource cancellation = new();
        bool budget = outcome is "memory" or "output-bytes";
        string boundary = outcome switch { "memory" or "output-bytes" => "read", "cancel-input" => "input-close", "cancel-write" => "write", _ => outcome };
        using RecordingJobHost owner = await ExecutionJobFixtures.PrepareAsync("argv-json", compressed, boundary,
            budget || outcome.StartsWith("cancel", StringComparison.Ordinal) ? "success" : "fault");
        owner.PausePath = boundary is "read" or "input-close" ? Input : secondary ? Side : Output;
        if (outcome == "memory")
        {
            byte[] content = Enumerable.Repeat((byte)' ', 65536).ToArray();
            owner.Inner.AddInput(Input, CheckTests.Assemble(CheckTests.SinglePage("<< /Length " + content.Length + " >>", content), 1), QpdfInputOwnership.Copy);
        }
        QpdfPath[] targets = [Output, Side];
        byte[][] aliases = new byte[targets.Length][];
        for (int i = 0; i < targets.Length; i++)
        {
            await using IQpdfOutputTransaction seed = await owner.Inner.CreateOutputAsync(targets[i], CancellationToken.None);
            await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
            Assert.True(owner.Inner.TryGetOutput(targets[i], out aliases[i]));
        }
        CaptureHost host = new(owner);
        QpdfLimits limits = QpdfLimits.Default with { MaxOpenHandles = 1L, MaxOutputBytes = outcome == "output-bytes" ? 128L : QpdfLimits.Default.MaxOutputBytes };
        Task<object> running = RunPublished(host, route, 2, mode, limits, cancellation.Token);
        try
        {
            Assert.Same(owner.Entered, await Task.WhenAny(owner.Entered, running).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(owner.ReadBytes > 0L); Assert.True(owner.Budget.ReservedBytes > 0L);
            if (boundary == "write") { Assert.False(owner.PendingWrite.IsEmpty); Assert.True(owner.Budget.ReservedBytes >= owner.PendingWrite.Length); }
            if (outcome == "memory") { owner.Budget.RefuseGrowth = true; }
            if (outcome.StartsWith("cancel", StringComparison.Ordinal)) { cancellation.Cancel(); } else { owner.Resume(); }
            object? result = null;
            if (route == "typed")
            {
                Exception error = await Assert.ThrowsAnyAsync<Exception>(async () => await running.WaitAsync(TimeSpan.FromSeconds(10)));
                if (outcome.StartsWith("cancel", StringComparison.Ordinal)) { Assert.IsAssignableFrom<OperationCanceledException>(error); }
                else if (outcome == "memory") { Assert.IsType<QpdfMemoryAdmissionException>(error); }
                else if (outcome == "output-bytes") { Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), Assert.IsType<QpdfLimitException>(error).LimitName); }
                else { Assert.IsType<QpdfHostException>(error); }
            }
            else
            {
                QpdfExecutionResult command = Assert.IsType<QpdfExecutionResult>(await running.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(outcome.StartsWith("cancel", StringComparison.Ordinal) ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, command.Status);
                if (budget) { Assert.Contains(command.Diagnostics, x => x.Code == QpdfDiagnosticCode.BudgetExceeded); }
                Assert.Null(command.OutputText); Assert.Null(command.OutputBytes); result = command;
            }
            int committed = secondary ? outcome == "output-close" ? 2 : 1 : outcome == "output-close" ? 1 : 0;
            Assert.Equal(committed, owner.Committed.Count);
            for (int i = 0; i < targets.Length; i++)
            {
                Assert.Equal(new byte[] { 17, 23, 41 }, aliases[i]);
                Assert.True(owner.Inner.TryGetOutput(targets[i], out byte[] current));
                if (i >= committed) { Assert.Equal(aliases[i], current); }
                else if (targets[i] == Output) { using JsonDocument parsed = JsonDocument.Parse(current); Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32()); }
                else { Assert.Equal("q Q\n"u8.ToArray(), current); }
            }
            if (budget || outcome is "input-close" or "cancel-input" or "create") { Assert.Empty(host.Writes); }
            CapturedWrite[] copies = host.Writes.ToArray();
            Closed(owner, result);
            foreach (CapturedWrite copy in copies)
            {
                if (copy.Path == Output) { using JsonDocument parsed = JsonDocument.Parse(copy.Bytes); Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32()); }
                else { Assert.Equal("q Q\n"u8.ToArray(), copy.Bytes); }
            }
        }
        finally { owner.Resume(); try { await running; } catch (QpdfException) { } catch (OperationCanceledException) { } }
    }

    private sealed record CapturedWrite(QpdfPath Path, byte[] Bytes);

    private sealed class CaptureHost(RecordingJobHost host) : IQpdfHost, IQpdfMemoryHost
    {
        internal InMemoryQpdfHost Inner => host.Inner;
        public IMemoryBudget MemoryBudget => host.Budget;
        internal List<CapturedWrite> Writes { get; } = [];
        internal List<string> Names { get; } = [];
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => host.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Names.Add(new string(path.Value.AsSpan()));
            return new Output(this, await host.CreateOutputAsync(path, token));
        }
        private sealed class Output(CaptureHost owner, IQpdfOutputTransaction transaction) : IQpdfOutputTransaction
        {
            public QpdfPath Path => transaction.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            { owner.Writes.Add(new(Path, bytes.ToArray())); return transaction.WriteAsync(bytes, token); }
            public ValueTask CommitAsync(CancellationToken token) => transaction.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => transaction.DiscardAsync(token);
            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }
}

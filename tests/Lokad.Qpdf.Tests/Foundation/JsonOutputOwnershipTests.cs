using System.Reflection;
using Xunit.Abstractions;
using System.Runtime.InteropServices;
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
    private readonly ITestOutputHelper _output;
    private OwnershipBatch.Observation? _collection;

    public JsonOutputOwnershipTests(ITestOutputHelper output) => _output = output;

    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static IEnumerable<object[]> InlineJobs()
    {
        foreach (string route in new[] { "argv", "job", "mixed", "response" })
        foreach (bool compressed in new[] { false, true }) { yield return [route, compressed]; }
    }

    private static async Task<(ResourceGovernor Governor, string Text)> CompleteInline(string route, bool compressed)
    {
        Type fixture = typeof(ExecutionCounterTests);
        Task preparing = (Task)(fixture.GetMethod("Prepare", Members)?.Invoke(null, ["argv-json", compressed, "input-close", "success"])
            ?? throw new InvalidOperationException("Missing existing complete-job fixture."));
        await preparing;
        object host = preparing.GetType().GetProperty("Result")?.GetValue(preparing)
            ?? throw new InvalidOperationException("Missing prepared host.");
        Type hostType = host.GetType();
        hostType.GetField("PausePath", Members)?.SetValue(host, QpdfPath.From("input.pdf"));
        InMemoryQpdfHost inner = (InMemoryQpdfHost)(hostType.GetProperty("Inner", Members)?.GetValue(host)
            ?? throw new InvalidOperationException("Missing input host."));
        List<string> argv = ["input.pdf", "--json=2", "--json-stream-data=inline"];
        Task<QpdfExecutionResult> running;
        if (route == "job")
        {
            running = Qpdf.ExecuteJobAsync((IQpdfHost)host,
                "{\"inputFile\":\"input.pdf\",\"json\":\"2\",\"jsonStreamData\":\"inline\"}",
                new QpdfExecutionOptions(QpdfLimits.Default), CancellationToken.None).AsTask();
        }
        else
        {
            if (route == "mixed") { argv.Insert(1, "--job-json-file=settings.json"); }
            if (route == "response")
            {
                inner.AddInput(QpdfPath.From("args.txt"), Encoding.UTF8.GetBytes(string.Join('\n', argv) + "\n"), QpdfInputOwnership.Copy);
                argv = ["@args.txt"];
            }
            running = Qpdf.ExecuteAsync((IQpdfHost)host, new QpdfCommandInvocation(argv),
                new QpdfExecutionOptions(QpdfLimits.Default), CancellationToken.None).AsTask();
        }
        Task entered = (Task)(hostType.GetProperty("Entered", Members)?.GetValue(host)
            ?? throw new InvalidOperationException("Missing observed close boundary."));
        await entered.WaitAsync(TimeSpan.FromSeconds(10));
        ResourceGovernor governor = (ResourceGovernor)(fixture.GetMethod("FindGovernor", Members)?.Invoke(null,
            [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
            ?? throw new InvalidOperationException("Missing actual job governor."));
        hostType.GetMethod("Resume", Members)?.Invoke(host, []);
        QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, inner.OpenLeaseCount);
        string text = Assert.IsType<string>(result.OutputText);
        using JsonDocument parsed = JsonDocument.Parse(text);
        Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32());
        // Keep the original fixture and its exact serialized output length.
        Assert.Equal(1378, text.Length);
        return (governor, text);
    }

    [Theory]
    [MemberData(nameof(InlineJobs))]
    [Trait("Layer", "Contract")]
    public async Task IndependentInlineTextAliasKeepsItsActualStorageFunded(string route, bool compressed)
    {
        var captured = await CompleteInline(route, compressed);
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        await CollectAsync(captured.Governor);
        long minimum = (24L + 2L * (captured.Text.Length + 1) + 7L) & ~7L;
        Assert.Equal(2784L, minimum);
        Assert.True(captured.Governor.Snapshot().LiveMemoryBytes >= minimum,
            "The actual governor must fund the independently surviving public JSON string.");
        GC.KeepAlive(captured.Text);
    }

    public static IEnumerable<object[]> PublicationJobs()
    {
        foreach (string route in new[] { "typed", "argv", "job", "mixed", "response" })
        foreach (bool compressed in new[] { false, true })
        foreach (int version in new[] { 1, 2 })
        foreach (string mode in new[] { "none", "inline", "file" })
        {
            if (version == 1 && mode != "none") { continue; }
            yield return [route, compressed, version, mode];
        }
    }

    private sealed record Observation(ResourceGovernor Governor, object? Alias, long Minimum,
        WeakReference Result, WeakReference Host, long PeakMemoryBytes);

    private static async Task<Observation> CompletePublished(string route, bool compressed, int version, string mode, string alias)
    {
        Type fixture = typeof(ExecutionCounterTests);
        Task preparing = (Task)(fixture.GetMethod("Prepare", Members)?.Invoke(null,
            ["argv-json", compressed, "input-close", "success"])
            ?? throw new InvalidOperationException("Missing existing fixture."));
        await preparing;
        object original = preparing.GetType().GetProperty("Result")?.GetValue(preparing)
            ?? throw new InvalidOperationException("Missing existing host.");
        original.GetType().GetField("PausePath", Members)?.SetValue(original, QpdfPath.From("input.pdf"));
        InMemoryQpdfHost inner = (InMemoryQpdfHost)(original.GetType().GetProperty("Inner", Members)?.GetValue(original)
            ?? throw new InvalidOperationException("Missing input host."));
        CaptureHost host = new((IQpdfHost)original, inner);
        // Existing Flate inflation admits its codec workspace even for four
        // decoded bytes. Keep the raw lane at 32 KiB; decoder containment is
        // a separate audit obligation, with its existing workspace unchanged.
        QpdfLimits limits = QpdfLimits.Default with
        { MaxMemoryBytes = compressed ? 262144L : route == "mixed" ? 65536L : 32768L, MaxOpenHandles = 1L };
        Task<object> running = RunPublished(host, route, version, mode, limits, CancellationToken.None);
        Task entered = (Task)(original.GetType().GetProperty("Entered", Members)?.GetValue(original)
            ?? throw new InvalidOperationException("Missing observed close boundary."));
        Task reached = await Task.WhenAny(entered, running);
        if (ReferenceEquals(reached, running))
        {
            object early = await running;
            throw new InvalidOperationException(early is QpdfExecutionResult execution
                ? string.Join(" | ", execution.Diagnostics.Select(diagnostic => diagnostic.Message)) : "Job completed before observed close.");
        }
        await entered.WaitAsync(TimeSpan.FromSeconds(10));
        ResourceGovernor governor = (ResourceGovernor)(fixture.GetMethod("FindGovernor", Members)?.Invoke(null,
            [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
            ?? throw new InvalidOperationException("Missing actual governor."));
        original.GetType().GetMethod("Resume", Members)?.Invoke(original, []);
        object result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        QpdfExecutionCounters counters;
        string? text;
        if (result is QpdfJsonResult typed)
        { counters = typed.Counters; text = typed.Json; }
        else
        {
            QpdfExecutionResult execution = Assert.IsType<QpdfExecutionResult>(result);
            Assert.True(execution.Status == QpdfExecutionStatus.Completed,
                string.Join(" | ", execution.Diagnostics.Select(diagnostic => diagnostic.Message)));
            Assert.Null(execution.OutputText);
            counters = execution.Counters; text = null;
        }
        Assert.Equal(0L, counters.OpenHandles); Assert.Equal(0L, inner.OpenLeaseCount);
        Assert.Equal(text is null ? 0L : GovernedText.StringBytes(text.Length), governor.CallerStorageBytes);
        Assert.True(counters.PeakMemoryBytes <= limits.MaxMemoryBytes);
        Assert.True(inner.TryGetOutput(QpdfPath.From("output.json"), out byte[] main));
        using JsonDocument parsed = JsonDocument.Parse(main);
        Assert.Equal(version, parsed.RootElement.GetProperty("version").GetInt32());
        if (text is not null) { Assert.Equal(main, Encoding.UTF8.GetBytes(text)); }
        object? value;
        long minimum;
        if (alias == "text")
        { value = text; minimum = text is null ? 0L : (24L + 2L * (text.Length + 1) + 7L) & ~7L; }
        else if (alias == "bytes")
        {
            value = host.Writes.ToArray();
            minimum = BufferMinimum(host.Writes);
            Assert.NotEmpty(host.Writes);
            Assert.Equal(main, host.Writes[0].ToArray());
        }
        else if (alias == "name")
        {
            value = host.Names.ToArray();
            minimum = mode == "file" ? (24L + 2L * ("side-4".Length + 1) + 7L) & ~7L : 0L;
            if (mode == "file") { Assert.Contains("side-4", host.Names); }
        }
        else { value = null; minimum = 0L; }
        return new(governor, value, minimum, new(result), new(host), counters.PeakMemoryBytes);
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
                ["inputFile"] = "input.pdf", ["outputFile"] = "output.json", ["json"] = version.ToString(),
                ["jsonStreamData"] = mode, ["jsonStreamPrefix"] = "side",
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

    private async Task CollectAsync(ResourceGovernor governor)
    {
        if (_collection is not null) { await _collection.CollectAsync(governor, 4); }
        else
        {
            for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            for (int boundary = 0; boundary < 4; boundary++) { governor.ReserveMemory(0L); }
        }
    }

    private static long BufferMinimum(IEnumerable<ReadOnlyMemory<byte>> writes)
    {
        HashSet<byte[]> arrays = new(ReferenceEqualityComparer.Instance);
        foreach (ReadOnlyMemory<byte> memory in writes)
        {
            Assert.True(MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment));
            arrays.Add(Assert.IsType<byte[]>(segment.Array));
        }
        return arrays.Sum(array => (24L + array.Length + 7L) & ~7L);
    }

    [Theory]
    [MemberData(nameof(PublicationJobs))]
    [Trait("Layer", "Contract")]
    public async Task HostCopiesSurviveReaderAndResultRetirementWithoutPrivatePublicationCharges(string route, bool compressed, int version, string mode)
    {
        Observation observation = await CompletePublished(route, compressed, version, mode, "bytes");
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        await CollectAsync(observation.Governor);
        Assert.False(observation.Result.IsAlive); Assert.False(observation.Host.IsAlive);
        Assert.Equal(observation.Governor.CallerMetadataBytes + observation.Governor.CallerStorageBytes, observation.Governor.Snapshot().LiveMemoryBytes);
        Assert.NotEmpty(Assert.IsType<ReadOnlyMemory<byte>[]>(observation.Alias));
        GC.KeepAlive(observation.Alias);
    }

    [Theory]
    [MemberData(nameof(PublicationJobs))]
    [Trait("Layer", "Contract")]
    public async Task CompletedPublishedJobsRetireUnescapedSerializationStorage(string route, bool compressed, int version, string mode)
    {
        Observation observation = await CompletePublished(route, compressed, version, mode, "none");
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        await CollectAsync(observation.Governor);
        Assert.False(observation.Result.IsAlive); Assert.False(observation.Host.IsAlive);
        Assert.Equal(observation.Governor.CallerMetadataBytes + observation.Governor.CallerStorageBytes, observation.Governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "inline")]
    [InlineData(true, "inline")]
    [InlineData(false, "file")]
    [InlineData(true, "file")]
    [Trait("Layer", "Contract")]
    public async Task IndependentTypedTextDoesNotRootItsPublishedBuffers(bool compressed, string mode)
    {
        Observation observation = await CompletePublished("typed", compressed, 2, mode, "text");
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        await CollectAsync(observation.Governor);
        Assert.False(observation.Result.IsAlive); Assert.False(observation.Host.IsAlive);
        Assert.True(observation.Governor.Snapshot().LiveMemoryBytes >= observation.Minimum);
        Assert.True(observation.Governor.Snapshot().LiveMemoryBytes < observation.PeakMemoryBytes);
        using JsonDocument parsed = JsonDocument.Parse(Assert.IsType<string>(observation.Alias));
        Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32());
        GC.KeepAlive(observation.Alias);
    }

    public static IEnumerable<object[]> SidecarJobs()
    {
        foreach (string route in new[] { "typed", "argv", "job", "mixed", "response" })
        foreach (bool compressed in new[] { false, true }) { yield return [route, compressed]; }
    }

    [Theory]
    [MemberData(nameof(SidecarJobs))]
    [Trait("Layer", "Contract")]
    public async Task HostFilenameCopiesSurviveWithoutPrivatePublicationCharges(string route, bool compressed)
    {
        Observation observation = await CompletePublished(route, compressed, 2, "file", "name");
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        await CollectAsync(observation.Governor);
        Assert.False(observation.Result.IsAlive); Assert.False(observation.Host.IsAlive);
        Assert.Equal(observation.Governor.CallerMetadataBytes + observation.Governor.CallerStorageBytes, observation.Governor.Snapshot().LiveMemoryBytes);
        Assert.Contains("side-4", Assert.IsType<string[]>(observation.Alias));
        GC.KeepAlive(observation.Alias);
    }

    private static async Task<(object Alias, WeakReference[] Metadata)> CompleteWithoutGovernorRoot(string route, bool compressed)
    {
        Observation observation = await CompletePublished(route, compressed, 2, "file", "bytes");
        Assert.Equal(observation.Governor.CallerMetadataBytes + observation.Governor.CallerStorageBytes, observation.Governor.Snapshot().LiveMemoryBytes);
        WeakReference[] metadata = (WeakReference[])(typeof(CompletedJobOwnershipTests).GetMethod("Metadata", Members)?.Invoke(null,
            [observation.Governor]) ?? throw new InvalidOperationException("Missing existing metadata observation."));
        return (observation.Alias ?? throw new InvalidOperationException("Missing public buffer alias."), metadata);
    }

    [Theory]
    [MemberData(nameof(SidecarJobs))]
    [Trait("Layer", "Contract")]
    public async Task EscapedHostBuffersDoNotRootTheCompletedGovernorOrIndex(string route, bool compressed)
    {
        var captured = await CompleteWithoutGovernorRoot(route, compressed);
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        await (_collection ?? throw new InvalidOperationException()).CollectAsync(null, 0);
        Assert.All(captured.Metadata, weak => Assert.False(weak.IsAlive));
        Assert.NotEmpty(Assert.IsType<ReadOnlyMemory<byte>[]>(captured.Alias));
        GC.KeepAlive(captured.Alias);
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
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("objects").GetProperty($"{number} 0").ValueKind);
            Assert.False(json.RootElement.GetProperty("objectinfo").GetProperty($"{number} 0").GetProperty("stream").GetProperty("is").GetBoolean());
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
        JsonElement value = version == 1 ? parsed.RootElement.GetProperty("objects").GetProperty("4 0")
            : parsed.RootElement.GetProperty("qpdf")[1].GetProperty("obj:4 0 R").GetProperty("value");
        Assert.Equal((version == 1 ? "" : "u:") + expected, value.GetProperty("/Text").GetString());
        Assert.Equal(version == 1 ? "/#c3#a9" : "/é", value.GetProperty("/\"\\").GetString());
        Assert.Equal("[1,-0.5,0.0,0.25]", value.GetProperty("/Numbers").GetRawText());
    }

    public static IEnumerable<object[]> FailureJobs()
    {
        foreach (string route in new[] { "typed", "argv", "job", "mixed", "response" })
        foreach (bool compressed in new[] { false, true })
        foreach (string mode in new[] { "inline", "file" })
        foreach (string outcome in new[] { "input-close", "create", "write", "commit", "output-close", "cancel-input", "cancel-write", "memory", "work", "output-bytes" })
        { yield return [route, compressed, mode, outcome]; }
    }

    private static async Task<Observation> FailPublished(string route, bool compressed, string mode, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        bool budget = outcome is "memory" or "work" or "output-bytes";
        Type fixture = typeof(ExecutionCounterTests);
        Task preparing = (Task)(fixture.GetMethod("Prepare", Members)?.Invoke(null,
            ["argv-json", compressed, budget ? "read" : "input-close", outcome == "input-close" ? "fault" : "success"])
            ?? throw new InvalidOperationException("Missing existing fixture."));
        await preparing;
        object original = preparing.GetType().GetProperty("Result")?.GetValue(preparing)
            ?? throw new InvalidOperationException("Missing existing host.");
        original.GetType().GetField("PausePath", Members)?.SetValue(original, QpdfPath.From("input.pdf"));
        InMemoryQpdfHost inner = (InMemoryQpdfHost)(original.GetType().GetProperty("Inner", Members)?.GetValue(original)
            ?? throw new InvalidOperationException("Missing input host."));
        byte[][] priorAliases = new byte[2][];
        if (outcome == "memory" && !compressed)
        {
            // A separate, explicit value fixture creates real overlap between
            // the reader value, JSON chunks and the immutable output copy.
            byte[] input = CheckTests.Assemble(
            [
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R /Resources << >> >>", null),
                (4, "<< /Length 4 >>", "q Q\n"u8.ToArray()),
                (5, "(" + new string('A', route == "mixed" ? 8192 : 4096) + ")", null),
            ], 1);
            inner.AddInput(QpdfPath.From("input.pdf"), input, QpdfInputOwnership.Copy);
        }
        string[] targets = ["output.json", "side-4"];
        for (int index = 0; index < targets.Length; index++)
        {
            await using IQpdfOutputTransaction seed = await inner.CreateOutputAsync(QpdfPath.From(targets[index]), CancellationToken.None);
            await seed.WriteAsync(new byte[] { 17, 23, 41 }, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
            Assert.True(inner.TryGetOutput(QpdfPath.From(targets[index]), out priorAliases[index]));
        }
        CaptureHost host = new((IQpdfHost)original, inner) { Outcome = outcome, Cancellation = cancellation };
        QpdfLimits limits = QpdfLimits.Default with { MaxOpenHandles = 1L };
        limits = outcome switch
        {
            "memory" => limits with { MaxMemoryBytes = compressed ? 140000L : route == "mixed" ? 60000L : 16384L },
            "work" => limits with { MaxWork = 5000L },
            "output-bytes" => limits with { MaxOutputBytes = 128L },
            _ => limits,
        };
        Task<object> running = RunPublished(host, route, 2, mode, limits, cancellation.Token);
        Task entered = (Task)(original.GetType().GetProperty("Entered", Members)?.GetValue(original)
            ?? throw new InvalidOperationException("Missing observed I/O boundary."));
        Task reached = await Task.WhenAny(entered, running).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(entered, reached);
        ResourceGovernor governor = (ResourceGovernor)(fixture.GetMethod("FindGovernor", Members)?.Invoke(null,
            [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
            ?? throw new InvalidOperationException("Missing actual governor."));
        Assert.True(governor.Snapshot().HostOperations > 0L);
        Assert.True((long)(original.GetType().GetField("ReadBytes", Members)?.GetValue(original)
            ?? throw new InvalidOperationException("Missing observed read count.")) > 0L);
        if (outcome == "cancel-input") { cancellation.Cancel(); }
        original.GetType().GetMethod("Resume", Members)?.Invoke(original, []);
        object result;
        if (route == "typed")
        {
            Exception error = await Assert.ThrowsAnyAsync<Exception>(async () => { await running.WaitAsync(TimeSpan.FromSeconds(10)); });
            if (outcome.StartsWith("cancel", StringComparison.Ordinal)) { Assert.IsAssignableFrom<OperationCanceledException>(error); }
            else if (budget)
            {
                QpdfLimitException limit = Assert.IsType<QpdfLimitException>(error);
                Assert.Equal(outcome == "memory" ? nameof(QpdfLimits.MaxMemoryBytes)
                    : outcome == "work" ? nameof(QpdfLimits.MaxWork) : nameof(QpdfLimits.MaxOutputBytes), limit.LimitName);
            }
            else { Assert.IsType<QpdfHostException>(error); }
            result = error;
        }
        else
        {
            QpdfExecutionResult execution = Assert.IsType<QpdfExecutionResult>(await running.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(outcome.StartsWith("cancel", StringComparison.Ordinal) ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, execution.Status);
            if (budget) { Assert.Contains(execution.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.BudgetExceeded); }
            result = execution;
        }
        Assert.Equal(0L, governor.Snapshot().OpenHandles); Assert.Equal(0L, inner.OpenLeaseCount);
        foreach (byte[] priorAlias in priorAliases) { Assert.Equal(new byte[] { 17, 23, 41 }, priorAlias); }
        for (int index = 0; index < targets.Length; index++)
        {
            Assert.True(inner.TryGetOutput(QpdfPath.From(targets[index]), out byte[] current));
            if (outcome == "output-close" && index == 0)
            { using JsonDocument parsed = JsonDocument.Parse(current); Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32()); }
            else { Assert.Equal(priorAliases[index], current); }
        }
        if (budget || outcome is "input-close" or "cancel-input" or "create") { Assert.Empty(host.Writes); }
        ReadOnlyMemory<byte>[] alias = host.Writes.ToArray();
        long minimum = BufferMinimum(alias);
        return new(governor, alias, minimum, new(result), new(host), governor.Snapshot().PeakMemoryBytes);
    }

    [Theory]
    [MemberData(nameof(FailureJobs))]
    [Trait("Layer", "Contract")]
    public async Task FailedAndCanceledPublicJobsPreserveTargetsAndEscapedHostBytes(string route, bool compressed, string mode, string outcome)
    {
        Observation observation = await FailPublished(route, compressed, mode, outcome);
        if (_collection is null) { return; } // Producer contract; lifetime phases run in the batch.
        await CollectAsync(observation.Governor);
        Assert.False(observation.Result.IsAlive); Assert.False(observation.Host.IsAlive);
        Assert.Equal(observation.Governor.CallerMetadataBytes + observation.Governor.CallerStorageBytes, observation.Governor.Snapshot().LiveMemoryBytes);
        Assert.All(Assert.IsType<ReadOnlyMemory<byte>[]>(observation.Alias), bytes =>
        { using JsonDocument parsed = JsonDocument.Parse(bytes); Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32()); });
        GC.KeepAlive(observation.Alias);
    }

    private sealed class CaptureHost(IQpdfHost host, InMemoryQpdfHost inner) : IQpdfHost
    {
        internal InMemoryQpdfHost Inner => inner;
        internal List<ReadOnlyMemory<byte>> Writes { get; } = [];
        internal List<string> Names { get; } = [];
        internal string Outcome = "success";
        internal CancellationTokenSource? Cancellation;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => host.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Names.Add(new string(path.Value.AsSpan()));
            if (Outcome == "create") { throw new QpdfHostException(path, QpdfHostFailureReason.WriteFailed); }
            return new Output(this, await host.CreateOutputAsync(path, token));
        }
        private sealed class Output(CaptureHost owner, IQpdfOutputTransaction transaction) : IQpdfOutputTransaction
        {
            public QpdfPath Path => transaction.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                owner.Writes.Add(bytes.ToArray());
                if (owner.Outcome == "cancel-write") { owner.Cancellation?.Cancel(); token.ThrowIfCancellationRequested(); }
                if (owner.Outcome == "write") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
                return transaction.WriteAsync(bytes, token);
            }
            public ValueTask CommitAsync(CancellationToken token)
            {
                if (owner.Outcome == "commit") { throw new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed); }
                return transaction.CommitAsync(token);
            }
            public ValueTask DiscardAsync(CancellationToken token) => transaction.DiscardAsync(token);
            public async ValueTask DisposeAsync()
            {
                await transaction.DisposeAsync();
                if (owner.Outcome == "output-close") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); }
            }
        }
    }

    private static IEnumerable<(string Method, IEnumerable<object[]> Rows)> OwnershipCases()
        => new (string, IEnumerable<object[]>)[]
        {
            (nameof(IndependentInlineTextAliasKeepsItsActualStorageFunded), InlineJobs()),
            (nameof(HostCopiesSurviveReaderAndResultRetirementWithoutPrivatePublicationCharges), PublicationJobs()),
            (nameof(CompletedPublishedJobsRetireUnescapedSerializationStorage), PublicationJobs()),
            (nameof(IndependentTypedTextDoesNotRootItsPublishedBuffers), new object[][] { [false, "none"], [true, "none"], [false, "inline"], [true, "inline"], [false, "file"], [true, "file"] }),
            (nameof(HostFilenameCopiesSurviveWithoutPrivatePublicationCharges), SidecarJobs()),
            (nameof(EscapedHostBuffersDoNotRootTheCompletedGovernorOrIndex), SidecarJobs()),
            (nameof(FailedAndCanceledPublicJobsPreserveTargetsAndEscapedHostBytes), FailureJobs()),
        };

    public static IEnumerable<object[]> OwnershipBatches() => OwnershipMatrix.Batches(OwnershipCases());

    [Theory]
    [MemberData(nameof(OwnershipBatches))]
    [Trait("Layer", "Ownership")]
    public Task BatchedLifetimeObservationsPreserveEveryOriginalPhase(int batch)
        => OwnershipMatrix.RunAsync(_output, OwnershipCases(), batch,
            observation => new JsonOutputOwnershipTests(_output) { _collection = observation });
}

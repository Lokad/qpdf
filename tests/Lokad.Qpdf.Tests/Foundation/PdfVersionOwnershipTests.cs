using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class PdfVersionOwnershipTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");

    private sealed class DocumentAliases(ResourceGovernor governor, PdfVersion[] versions, WeakReference document,
        WeakReference host, WeakReference header, WeakReference effective, bool same)
    {
        internal ResourceGovernor? Governor = governor;
        internal PdfVersion[] Versions = versions;
        internal readonly WeakReference Document = document;
        internal readonly WeakReference Host = host;
        internal readonly WeakReference Header = header;
        internal readonly WeakReference Effective = effective;
        internal readonly bool Same = same;
    }

    private static object Field(object value, string name) => value.GetType().GetField(name, Members)?.GetValue(value)
        ?? throw new InvalidOperationException("Missing complete public-job fixture field: " + name);

    internal static long MeasurePhysicalSize(Type type)
    {
        ResourceGovernor observer = new(QpdfLimits.Default, CancellationToken.None);
        Func<object> create = type == typeof(WeakReference<ResourceGovernor>)
            ? () => new WeakReference<ResourceGovernor>(observer) : () => RuntimeHelpers.GetUninitializedObject(type);
        object[] roots = new object[1024];
        for (int index = 0; index < roots.Length; index++) { roots[index] = create(); }
        Array.Clear(roots);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < roots.Length; index++) { roots[index] = create(); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(0L, bytes % roots.Length);
        GC.KeepAlive(roots); GC.KeepAlive(observer);
        return bytes / roots.Length;
    }

    private static async Task<object> CompleteCheck(bool compressed)
    {
        Task task = (Task)(typeof(TypedCounterOwnershipTests).GetMethod("Complete", Members)?.Invoke(null,
            ["typed-check", compressed, "read", "success", "result", QpdfLimits.Default])
            ?? throw new InvalidOperationException("Missing original public check fixture."));
        await task;
        return task.GetType().GetProperty("Result", Members)?.GetValue(task) ?? throw new InvalidOperationException();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PdfVersion KeepOnlyVersion(object captured)
    {
        QpdfCheckResult result = (QpdfCheckResult)Field(captured, "Value");
        PdfVersion version = result.Version;
        captured.GetType().GetField("Value", Members)?.SetValue(captured, version);
        return version;
    }

    private static void Collect(ResourceGovernor governor)
    {
        for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        for (int boundary = 0; boundary < 4; boundary++) { governor.ReserveMemory(0L); }
    }

    private sealed class ChannelAliases(ResourceGovernor governor, PdfVersion version, QpdfExecutionCounters counters,
        WeakReference host, WeakReference result)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal PdfVersion? Version = version;
        internal QpdfExecutionCounters? Counters = counters;
        internal readonly WeakReference Host = host;
        internal readonly WeakReference Result = result;
    }

    private static async Task<ChannelAliases> CompleteChannels(bool compressed)
    {
        object captured = await CompleteCheck(compressed);
        QpdfCheckResult result = (QpdfCheckResult)Field(captured, "Value");
        return new((ResourceGovernor)Field(captured, "Governor"), result.Version, result.Counters,
            (WeakReference)Field(captured, "Host"), new(result));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropChannel(ChannelAliases aliases, string channel)
    {
        if (channel == "version") { aliases.Version = null; }
        else { aliases.Counters = null; }
    }

    [Theory]
    [InlineData(false, "version")]
    [InlineData(true, "version")]
    [InlineData(false, "counter")]
    [InlineData(true, "counter")]
    public async Task CompleteCheckAliasesKeepTheSharedLinkUntilBothChannelsRetire(bool compressed, string first)
    {
        ChannelAliases aliases = await CompleteChannels(compressed);
        await Task.Yield();
        Collect(aliases.Governor);
        Assert.False(aliases.Host.IsAlive); Assert.False(aliases.Result.IsAlive);
        long bodies = MeasurePhysicalSize(typeof(PdfVersion)) + MeasurePhysicalSize(typeof(QpdfExecutionCounters));
        Assert.True(aliases.Governor.Snapshot().LiveMemoryBytes >= bodies);
        Assert.True(aliases.Governor.Snapshot().LiveMemoryBytes >= bodies + 64L + 24L);
        Assert.Same(Field(aliases.Governor, "_versionLink"), Field(aliases.Governor, "_executionResultLink"));
        Assert.Equal(1, Field(aliases.Governor, "_versionCarriers"));
        Assert.Equal(1, Field(aliases.Governor, "_executionResultCarriers"));
        DropChannel(aliases, first);
        Collect(aliases.Governor);
        long surviving = first == "version" ? MeasurePhysicalSize(typeof(QpdfExecutionCounters))
            : MeasurePhysicalSize(typeof(PdfVersion));
        Assert.True(aliases.Governor.Snapshot().LiveMemoryBytes >= surviving + 32L + 24L);
        Assert.Equal(1L, Field(aliases.Governor, "_reservedResultWork"));
        DropChannel(aliases, first == "version" ? "counter" : "version");
        Collect(aliases.Governor);
        Assert.Equal(0, Field(aliases.Governor, "_versionCarriers"));
        Assert.Equal(0, Field(aliases.Governor, "_executionResultCarriers"));
        Assert.Equal(0L, Field(aliases.Governor, "_reservedResultWork"));
        Assert.Null(typeof(ResourceGovernor).GetField("_versionLink", Members)?.GetValue(aliases.Governor));
        Assert.Null(typeof(ResourceGovernor).GetField("_executionResultLink", Members)?.GetValue(aliases.Governor));
        Assert.Equal(0L, aliases.Governor.Snapshot().OpenHandles);
        GC.KeepAlive(aliases);
    }

    public static IEnumerable<object[]> DocumentCases()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (string catalog in new[] { "none", "direct", "indirect" })
        foreach (string alias in new[] { "header", "effective", "both", "copy-header", "copy-effective", "none" })
        { yield return [compressed, catalog, alias]; }
    }

    public static IEnumerable<object[]> MetadataCases()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (string catalog in new[] { "none", "direct", "indirect" })
        { yield return [compressed, catalog]; }
    }

    private static byte[] DocumentBytes(bool compressed, string catalog)
    {
        byte[] content = "q Q\n"u8.ToArray();
        if (compressed)
        {
            using MemoryStream output = new();
            using (ZLibStream encoder = new(output, CompressionLevel.Optimal, true)) { encoder.Write(content); }
            content = output.ToArray();
        }
        List<(int, string, byte[]?)> objects = CheckTests.SinglePage(
            "<< /Length " + content.Length + (compressed ? " /Filter /FlateDecode" : "") + " >>", content);
        string extra = catalog == "direct" ? " /Version /1.7" : catalog == "indirect" ? " /Version 6 0 R" : "";
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R" + extra + " >>", null);
        if (catalog == "indirect") { objects.Add((6, "/1.7", null)); }
        return CheckTests.Assemble(objects, 1);
    }

    private static async Task<DocumentAliases> CompleteDocument(bool compressed, string catalog, string alias)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(Input, DocumentBytes(compressed, catalog), QpdfInputOwnership.Copy);
        QpdfDocument? document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        ResourceGovernor governor = (ResourceGovernor)Field(document, "_governor");
        await document.DisposeAsync();
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, governor.Snapshot().OpenHandles);
        PdfVersion header = document.HeaderVersion;
        PdfVersion effective = document.Version;
        Assert.Equal("1.4", header.ToString()); Assert.Equal(catalog == "none" ? "1.4" : "1.7", effective.ToString());
        bool same = ReferenceEquals(header, effective);
        Assert.Equal(catalog == "none", same);
        PdfVersion[] versions = alias switch
        {
            "header" => [header],
            "effective" => [effective],
            "both" => same ? [header] : [header, effective],
            "copy-header" => [header with { }],
            "copy-effective" => [new PdfVersion(effective.Major, effective.Minor)],
            _ => [],
        };
        if (alias.StartsWith("copy-", StringComparison.Ordinal))
        {
            PdfVersion original = alias == "copy-header" ? header : effective;
            Assert.Equal(original, versions[0]); Assert.Equal(original.GetHashCode(), versions[0].GetHashCode());
            Assert.False(typeof(PdfVersion).GetProperty(nameof(PdfVersion.Major))?.CanWrite);
            Assert.False(typeof(PdfVersion).GetProperty(nameof(PdfVersion.Minor))?.CanWrite);
            Assert.Null(typeof(PdfVersion).GetField("_storage", Members)?.GetValue(versions[0]));
        }
        DocumentAliases result = new(governor, versions, new(document), new(host), new(header), new(effective), same);
        document = null;
        return result;
    }

    private static object? Storage(PdfVersion version) => typeof(PdfVersion).GetField("_storage", Members)?.GetValue(version);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void KeepFirst(DocumentAliases aliases) => aliases.Versions = [aliases.Versions[0]];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropVersions(DocumentAliases aliases) => aliases.Versions = [];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long KnownMinimum(PdfVersion[] versions)
    {
        long bytes = 24L;
        foreach (PdfVersion version in versions)
        {
            object? storage = Storage(version); Assert.NotNull(storage);
            long owned = MeasurePhysicalSize(typeof(PdfVersion)) + MeasurePhysicalSize(storage.GetType());
            Assert.Equal(owned, Field(storage, "_bytes")); bytes += owned;
        }
        return bytes;
    }

    [Theory]
    [MemberData(nameof(DocumentCases))]
    public async Task HeaderCatalogAndCallerAliasesRetireIndependentlyAfterDisposal(bool compressed, string catalog, string kind)
    {
        DocumentAliases aliases = await CompleteDocument(compressed, catalog, kind);
        await Task.Yield();
        ResourceGovernor governor = aliases.Governor ?? throw new InvalidOperationException();
        Collect(governor);
        Assert.False(aliases.Document.IsAlive); Assert.False(aliases.Host.IsAlive);
        bool owned = kind is "header" or "effective" or "both";
        if (owned)
        {
            long physical = aliases.Versions.Length * MeasurePhysicalSize(typeof(PdfVersion));
            Assert.True(governor.Snapshot().LiveMemoryBytes >= physical,
                $"Retained document version storage {governor.Snapshot().LiveMemoryBytes} is below measured minimum {physical}.");
            Assert.True(governor.Snapshot().LiveMemoryBytes >= KnownMinimum(aliases.Versions));
            Assert.Equal(aliases.Versions.Length, Field(governor, "_versionCarriers"));
            Assert.Equal((long)aliases.Versions.Length, Field(governor, "_reservedResultWork"));
            Assert.Equal(kind is "header" or "both" || aliases.Same, aliases.Header.IsAlive);
            Assert.Equal(kind is "effective" or "both" || aliases.Same, aliases.Effective.IsAlive);
            KeepFirst(aliases);
            Collect(governor);
            Assert.Equal(1, Field(governor, "_versionCarriers"));
            Assert.True(governor.Snapshot().LiveMemoryBytes >= KnownMinimum(aliases.Versions));
        }
        else
        {
            Assert.False(aliases.Header.IsAlive); Assert.False(aliases.Effective.IsAlive);
            Assert.Equal(0, Field(governor, "_versionCarriers")); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
            if (aliases.Versions.Length != 0) { Assert.Null(Storage(aliases.Versions[0])); }
        }
        DropVersions(aliases);
        Collect(governor);
        Assert.Equal(0, Field(governor, "_versionCarriers")); Assert.Equal(0L, Field(governor, "_reservedResultWork"));
        Assert.Null(typeof(ResourceGovernor).GetField("_versionLink", Members)?.GetValue(governor));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(aliases);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropGovernor(DocumentAliases aliases)
    {
        WeakReference observer = new(aliases.Governor); aliases.Governor = null; return observer;
    }

    [Theory]
    [MemberData(nameof(MetadataCases))]
    public async Task SurvivingVersionsDoNotRootCompletedDocumentsHostsOrGovernors(bool compressed, string catalog)
    {
        DocumentAliases aliases = await CompleteDocument(compressed, catalog, "both");
        await Task.Yield();
        WeakReference governor = DropGovernor(aliases);
        for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(governor.IsAlive); Assert.False(aliases.Document.IsAlive); Assert.False(aliases.Host.IsAlive);
        Assert.Equal("1.4", aliases.Versions[0].ToString());
        foreach (PdfVersion version in aliases.Versions) { Assert.NotNull(Storage(version)); }
        GC.KeepAlive(aliases);
    }

    public static IEnumerable<object[]> TypedJobs() => TypedCounterOwnershipTests.PublicJobs();
    public static IEnumerable<object[]> BudgetJobs() => TypedCounterOwnershipTests.BudgetJobs();

    private static async Task<object> CompleteTyped(string route, bool compressed, string boundary, string outcome, QpdfLimits limits)
    {
        Task task = (Task)(typeof(TypedCounterOwnershipTests).GetMethod("Complete", Members)?.Invoke(null,
            [route, compressed, boundary, outcome, "counters", limits]) ?? throw new InvalidOperationException());
        await task;
        return task.GetType().GetProperty("Result", Members)?.GetValue(task) ?? throw new InvalidOperationException();
    }

    private static void CheckRetirement(object captured)
    {
        ResourceGovernor governor = (ResourceGovernor)Field(captured, "Governor");
        Collect(governor);
        Assert.False(((WeakReference)Field(captured, "Host")).IsAlive);
        Assert.False(((WeakReference)Field(captured, "Result")).IsAlive);
        Assert.Equal(0, Field(governor, "_versionCarriers"));
        Assert.Equal(0, Field(governor, "_pendingVersionRetirements"));
        Assert.Null(typeof(ResourceGovernor).GetField("_versionLink", Members)?.GetValue(governor));
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        GC.KeepAlive(captured);
    }

    [Theory]
    [MemberData(nameof(TypedJobs))]
    public async Task CompleteTypedSuccessFaultAndCancellationRetireUnreturnedVersions(string route, bool compressed, string boundary, string outcome)
    {
        object captured = await CompleteTyped(route, compressed, boundary, outcome, QpdfLimits.Default);
        await Task.Yield();
        CheckRetirement(captured);
    }

    [Theory]
    [MemberData(nameof(BudgetJobs))]
    public async Task OriginalMemoryAndWorkFailuresRetireVersionsAfterRealIO(string route, bool compressed, string budget)
    {
        QpdfLimits limits = budget == "memory" ? QpdfLimits.Default with { MaxMemoryBytes = 2048L }
            : QpdfLimits.Default with { MaxWork = 128L };
        object captured = await CompleteTyped(route, compressed, "read", "budget", limits);
        await Task.Yield();
        CheckRetirement(captured);
        ResourceGovernor governor = (ResourceGovernor)Field(captured, "Governor");
        Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork <= limits.MaxWork);
    }

    public static IEnumerable<object[]> WriterCases()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (bool compressed in new[] { false, true })
        foreach (string profile in new[] { "borrowed", "minimum", "force", "object-streams", "merge", "json-create", "json-update", "empty" })
        foreach ((string boundary, string outcome) in new[] { ("write", "success"), ("write", "fault"), ("write", "cancel"),
            ("commit", "fault"), ("output-close", "success"), ("output-close", "fault"), ("output-close", "cancel") })
        { yield return [route, compressed, profile, boundary, outcome]; }
    }

    private static async Task<object> PrepareWriter(string profile, bool compressed, string boundary, string outcome)
    {
        string fixture = profile == "json-create" ? "typed-import" : profile == "json-update" ? "typed-update" : "typed-page";
        Task task = (Task)(typeof(TypedCounterOwnershipTests).GetMethod("Prepare", Members)?.Invoke(null,
            [fixture, compressed, boundary, outcome]) ?? throw new InvalidOperationException());
        await task;
        return task.GetType().GetProperty("Result", Members)?.GetValue(task) ?? throw new InvalidOperationException();
    }

    private static object Property(object value, string name) => value.GetType().GetProperty(name, Members)?.GetValue(value)
        ?? throw new InvalidOperationException(name);
    private static void Resume(object host) => host.GetType().GetMethod("Resume", Members)?.Invoke(host, []);

    private static async Task<QpdfExecutionResult> StartWriter(IQpdfHost host, string route, string profile, CancellationToken token)
    {
        string input = profile == "json-create" ? "input.json" : "input.pdf";
        List<string> argv = profile == "empty" ? ["--empty", "--"] : [input];
        Dictionary<string, object> job = [];
        if (profile == "empty") { job.Add("empty", ""); } else { job.Add("inputFile", input); }
        if (profile is "minimum" or "merge" or "json-create" or "json-update")
        { argv.Add("--min-version=1.7"); job.Add("minVersion", "1.7"); }
        if (profile == "force") { argv.Add("--force-version=1.6"); job.Add("forceVersion", "1.6"); }
        if (profile is not ("json-create" or "json-update" or "empty"))
        {
            string objects = profile == "object-streams" ? "generate" : "disable";
            argv.Add("--object-streams=" + objects); job.Add("objectStreams", objects);
        }
        if (profile == "merge")
        {
            argv.AddRange(["--pages", ".", "1", "second.pdf", "1", "--"]);
            job.Add("pages", new[] { new { file = ".", range = "1" }, new { file = "second.pdf", range = "1" } });
        }
        if (profile == "json-create") { argv.AddRange(["--json-input", "--"]); job.Add("jsonInput", ""); }
        if (profile == "json-update") { argv.AddRange(["--update-from-json=input.json", "--"]); job.Add("updateFromJson", "input.json"); }
        argv.Add("output.pdf"); job.Add("outputFile", "output.pdf");
        QpdfExecutionOptions options = new(QpdfLimits.Default);
        return route == "job" ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), options, token)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), options, token);
    }

    private sealed record WriterObservation(ResourceGovernor Governor, WeakReference Host, WeakReference Result,
        int VersionsBeforePublication, long WorkBeforePublication);

    private static async Task<WriterObservation> CompleteWriter(string route, bool compressed, string profile, string boundary, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        object host = await PrepareWriter(profile, compressed, boundary, outcome);
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Property(host, "Inner");
        Assert.True(inner.TryGetOutput(Output, out byte[] prior));
        Assert.True(GC.TryStartNoGCRegion(16L * 1024L * 1024L));
        Task<QpdfExecutionResult> running = StartWriter((IQpdfHost)host, route, profile, cancellation.Token);
        try
        {
            Task entered = (Task)Property(host, "Entered");
            Task first = await Task.WhenAny(entered, running).WaitAsync(TimeSpan.FromSeconds(10));
            if (ReferenceEquals(first, running))
            {
                QpdfExecutionResult early = await running;
                Assert.Fail("Writer finished before its requested checkpoint: " + early.Status + "; "
                    + string.Join("; ", early.Diagnostics.Select(diagnostic => diagnostic.Message)));
            }
            ResourceGovernor governor = (ResourceGovernor)(typeof(ExecutionCounterTests).GetMethod("FindGovernor", Members)?.Invoke(null,
                [running, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)]) ?? throw new InvalidOperationException());
            int versions = (int)(typeof(ResourceGovernor).GetField("_versionCarriers", Members)?.GetValue(governor) ?? 0);
            long work = governor.Snapshot().TotalWork;
            if (outcome == "cancel") { cancellation.Cancel(); } else { Resume(host); }
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed : outcome == "cancel" ? QpdfExecutionStatus.Canceled
                : QpdfExecutionStatus.Failed, result.Status);
            Assert.Equal(governor.Snapshot(), result.Counters);
            Assert.Equal(0L, inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.True(result.Counters.PeakMemoryBytes <= QpdfLimits.Default.MaxMemoryBytes);
            Assert.True(result.Counters.TotalWork <= QpdfLimits.Default.MaxWork);
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            Assert.True(inner.TryGetOutput(Output, out byte[] output));
            if ((int)Field(host, "Commits") == 0) { Assert.Equal(prior, output); }
            if (outcome == "success")
            {
                string expected = profile is "minimum" or "merge" or "json-create" or "json-update" ? "1.7"
                    : profile == "force" ? "1.6" : profile == "object-streams" ? "1.5" : profile == "empty" ? "1.3" : "1.4";
                Assert.StartsWith("%PDF-" + expected, Encoding.ASCII.GetString(output.AsSpan(0, 8)));
            }
            if (profile != "empty") { Assert.True((long)Field(host, "ReadBytes") > 0L); }
            return new(governor, new(host), new(result), versions, work);
        }
        finally
        {
            Resume(host);
            try { await running; } finally { GC.EndNoGCRegion(); }
        }
    }

    [Theory]
    [MemberData(nameof(WriterCases))]
    public async Task PublicWriterNegotiationsAdmitVersionsBeforePublicationAndRetireEveryOutcome(
        string route, bool compressed, string profile, string boundary, string outcome)
    {
        WriterObservation observation = await CompleteWriter(route, compressed, profile, boundary, outcome);
        await Task.Yield();
        Collect(observation.Governor);
        Assert.True(observation.VersionsBeforePublication > 0);
        Assert.True(observation.WorkBeforePublication > 0L);
        Assert.False(observation.Host.IsAlive); Assert.False(observation.Result.IsAlive);
        Assert.Equal(0, Field(observation.Governor, "_versionCarriers"));
        Assert.Equal(0L, Field(observation.Governor, "_reservedResultWork"));
        Assert.Null(typeof(ResourceGovernor).GetField("_versionLink", Members)?.GetValue(observation.Governor));
        GC.KeepAlive(observation);
    }

    private static MethodInfo VersionFactory()
    {
        MethodInfo? method = typeof(ResourceGovernor).GetMethod("OwnVersion", Members);
        Assert.NotNull(method);
        return method;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AdmitAndDiscard(ResourceGovernor governor)
    {
        PdfVersion version = (PdfVersion)(VersionFactory().Invoke(governor, [1, 7]) ?? throw new InvalidOperationException());
        Assert.Equal("1.7", version.ToString());
        Assert.Equal(KnownMinimum([version]), governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(1L, governor.Snapshot().TotalWork);
        Assert.Equal(1L, Field(governor, "_reservedResultWork"));
        GC.KeepAlive(version);
    }

    [Theory]
    [InlineData("memory-short")]
    [InlineData("work-short")]
    [InlineData("admitted")]
    [InlineData("canceled")]
    public void ScalarAdmissionRejectsBeforeConstructionAndRetirementUsesHeldWork(string boundary)
    {
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with
        { MaxMemoryBytes = boundary == "memory-short" ? 87L : 88L, MaxWork = boundary == "work-short" ? 1L : 2L, CancellationCheckInterval = 1L };
        ResourceGovernor governor = new(limits, cancellation.Token);
        MethodInfo method = VersionFactory();
        if (boundary == "admitted")
        {
            AdmitAndDiscard(governor);
            cancellation.Cancel();
            Collect(governor);
            Assert.Equal(2L, governor.Snapshot().TotalWork);
        }
        else
        {
            if (boundary == "canceled") { cancellation.Cancel(); }
            TargetInvocationException error = Assert.Throws<TargetInvocationException>(() => method.Invoke(governor, [1, 7]));
            if (boundary == "canceled") { Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException); }
            else { Assert.IsType<QpdfLimitException>(error.InnerException); }
            Assert.Equal(0L, governor.Snapshot().TotalWork); Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0, Field(governor, "_versionCarriers")); Assert.Equal(0L, Field(governor, "_reservedResultWork"));
        Assert.Null(typeof(ResourceGovernor).GetField("_versionLink", Members)?.GetValue(governor));
    }

    [Fact]
    public void VersionAndReceiptBudgetsEqualIndependentPhysicalGeometry()
    {
        Assert.Equal(32L, MeasurePhysicalSize(typeof(PdfVersion)));
        Type? memory = typeof(PdfVersion).Assembly.GetType("Lokad.Qpdf.Runtime.VersionMemory");
        Assert.NotNull(memory);
        Assert.Equal(32L, MeasurePhysicalSize(memory));
        Assert.Equal(24L, MeasurePhysicalSize(typeof(WeakReference<ResourceGovernor>)));
        Assert.Equal(248L, MeasurePhysicalSize(typeof(ResourceGovernor)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalCompleteCheckVersionAliasMeetsTheIndependentPhysicalMinimum(bool compressed)
    {
        object captured = await CompleteCheck(compressed);
        await Task.Yield();
        PdfVersion version = KeepOnlyVersion(captured);
        ResourceGovernor governor = (ResourceGovernor)Field(captured, "Governor");
        long minimum = MeasurePhysicalSize(version.GetType());
        Collect(governor);
        Assert.Equal(1, version.Major); Assert.Equal(4, version.Minor);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
        Assert.False(((WeakReference)Field(captured, "Host")).IsAlive);
        Assert.False(((WeakReference)Field(captured, "Result")).IsAlive);
        Assert.False(((WeakReference)Field(captured, "Counters")).IsAlive);
        // Preserve the independent original failure before looking for any new
        // implementation field: raw input retains 0 bytes against 24 measured.
        Assert.True(governor.Snapshot().LiveMemoryBytes >= minimum,
            $"Retained version storage {governor.Snapshot().LiveMemoryBytes} is below measured minimum {minimum}.");
        GC.KeepAlive(version); GC.KeepAlive(captured);
    }
}

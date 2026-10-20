using System.IO.Compression;
using System.Reflection;
using Xunit.Abstractions;
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
    private static readonly Dictionary<Type, long> PhysicalSizes = [];

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
        // A CLR type has one layout for this process. Establish that physical
        // fact once, then reuse it in independent public ownership jobs.
        lock (PhysicalSizes)
        {
            if (PhysicalSizes.TryGetValue(type, out long size)) { return size; }
            long measured = MeasureUncachedPhysicalSize(type);
            PhysicalSizes.Add(type, measured);
            return measured;
        }
    }

    private static long MeasureUncachedPhysicalSize(Type type)
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

    public static IEnumerable<object[]> DocumentCases()
    {
        foreach (bool compressed in new[] { false, true })
        foreach (string catalog in new[] { "none", "direct", "indirect" })
        foreach (string alias in new[] { "header", "effective", "both", "copy-header", "copy-effective", "none" })
        { yield return [compressed, catalog, alias]; }
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
        }
        DocumentAliases result = new(governor, versions, new(document), new(host), new(header), new(effective), same);
        document = null;
        return result;
    }

    [Theory]
    [MemberData(nameof(DocumentCases))]
    [Trait("Layer", "Contract")]
    public async Task HeaderCatalogAndCallerValuesRemainUsableAfterDisposal(bool compressed, string catalog, string kind)
    {
        DocumentAliases aliases = await CompleteDocument(compressed, catalog, kind);
        await Task.Yield();
        ResourceGovernor governor = aliases.Governor ?? throw new InvalidOperationException();
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

    private sealed record WriterObservation(ResourceGovernor Governor, WeakReference Host, WeakReference Result);

    private static async Task<WriterObservation> CompleteWriter(string route, bool compressed, string profile, string boundary, string outcome)
    {
        using CancellationTokenSource cancellation = new();
        object host = await PrepareWriter(profile, compressed, boundary, outcome);
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Property(host, "Inner");
        Assert.True(inner.TryGetOutput(Output, out byte[] prior));
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
            if (outcome == "cancel") { cancellation.Cancel(); } else { Resume(host); }
            QpdfExecutionResult result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed : outcome == "cancel" ? QpdfExecutionStatus.Canceled
                : QpdfExecutionStatus.Failed, result.Status);
            QpdfExecutionCounters final = governor.Snapshot();
            Assert.Equal(final.PeakMemoryBytes, result.Counters.PeakMemoryBytes);
            Assert.Equal(0L, inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
            Assert.True(result.Counters.PeakMemoryBytes <= QpdfLimits.Default.MaxMemoryBytes);
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
            return new(governor, new(host), new(result));
        }
        finally
        {
            Resume(host);
            await running;
        }
    }

    [Theory]
    [MemberData(nameof(WriterCases))]
    [Trait("Layer", "Contract")]
    public async Task WriterNegotiationsPreserveHeadersAndPublicationOutcomes(
        string route, bool compressed, string profile, string boundary, string outcome)
    {
        WriterObservation observation = await CompleteWriter(route, compressed, profile, boundary, outcome);
        await Task.Yield();
    }

}

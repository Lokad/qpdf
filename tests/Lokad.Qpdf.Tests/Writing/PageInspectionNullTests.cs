using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class PageInspectionNullTests
{
    private static readonly string[] Routes = ["typed", "argv", "job", "show-argv", "show-job"];

    private static byte[] Fixture(string resourceShape, bool indirectParentResources, string contentsShape)
    {
        string resources = resourceShape switch
        {
            "absent" => "", "null" => "/Resources null", "reference-null" => "/Resources 6 0 R", "stale" => "/Resources 6 1 R",
            "free" => "/Resources 6 0 R", "empty" => "/Resources << >>", "local" => "/Resources << /XObject << /Im 5 0 R >> >>",
            "reference-local" => "/Resources 7 0 R", "scalar" => "/Resources 42", "reference-scalar" => "/Resources 8 0 R", _ => "/Resources 99 0 R",
        };
        string contents = contentsShape switch
        {
            "absent" => "", "null" => "/Contents null", "reference-null" => "/Contents 6 0 R", "stale" => "/Contents 6 1 R",
            "free" => "/Contents 6 0 R", "empty" => "/Contents []", "reference-empty" => "/Contents 9 0 R",
            "stream" => "/Contents 4 0 R", "array" => "/Contents [4 0 R 4 0 R]", "reference-array" => "/Contents 9 0 R",
            "scalar" => "/Contents 42", "reference-scalar" => "/Contents 8 0 R", "unknown" => "/Contents 99 0 R", _ => "/Contents [null 6 0 R 4 0 R]",
        };
        const string parentResources = "<< /XObject << /Im 5 0 R >> >>";
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /Resources " + (indirectParentResources ? "7 0 R" : parentResources) + " >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " + resources + " " + contents + " >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Length 3 >>", new byte[] { 255, 0, 0 }),
            (6, "null", null), (7, parentResources, null), (8, "42", null),
            (9, contentsShape == "reference-array" ? "[4 0 R 4 0 R]" : "[]", null)], 1);
        if (resourceShape == "free" || contentsShape == "free")
        {
            int xref = source.AsSpan().LastIndexOf("xref\n0 10\n"u8); Assert.True(xref >= 0);
            int row = xref + "xref\n0 10\n"u8.Length + 6 * 20; Assert.Equal((byte)'n', source[row + 17]);
            "0000000000 65535 f \n"u8.CopyTo(source.AsSpan(row, 20));
        }
        return source;
    }

    private static async Task<(JsonElement? Page, string[] Messages)> Inspect(IQpdfHost host, string route, int version, CancellationToken token)
    {
        if (route == "typed")
        {
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(version, ["pages"]), token);
            using JsonDocument json = JsonDocument.Parse(result.Json); return (json.RootElement.GetProperty("pages")[0].Clone(), []);
        }
        QpdfExecutionResult execution;
        if (route.StartsWith("show-", StringComparison.Ordinal))
        {
            execution = route == "show-argv" ? await Qpdf.ExecuteAsync(host, new(["input", "--show-pages", "--with-images"]), token)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","showPages":true,"withImages":true}""", token);
        }
        else
        {
            execution = route == "argv" ? await Qpdf.ExecuteAsync(host, new(["input", "--json=" + version, "--json-key=pages"]), token)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", json = version.ToString(System.Globalization.CultureInfo.InvariantCulture), jsonKey = new[] { "pages" } }), token);
        }
        Assert.Equal(QpdfExitCode.Success, execution.ExitCode);
        if (execution.OutputText is string text)
        { using JsonDocument json = JsonDocument.Parse(text); return (json.RootElement.GetProperty("pages")[0].Clone(), []); }
        return (null, execution.Diagnostics.Select(d => d.Message).ToArray());
    }

    public static IEnumerable<object[]> ResourceCases()
    {
        foreach (string shape in new[] { "absent", "null", "reference-null", "stale", "free", "empty", "local", "reference-local", "scalar", "reference-scalar", "unknown" })
        foreach (string route in Routes)
        {
            yield return [shape, route, false];
            if (shape is "null" or "reference-null" or "stale" or "free") { yield return [shape, route, true]; }
        }
    }

    [Theory]
    [MemberData(nameof(ResourceCases))]
    public async Task ImagesUseSharedResolvedPageInheritance(string shape, string route, bool indirectParentResources)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(shape, indirectParentResources, "stream"), QpdfInputOwnership.Transfer);
        (JsonElement? page, string[] messages) = await Inspect(host, route, 2, CancellationToken.None);
        bool inherited = shape is "absent" or "null" or "reference-null" or "stale" or "free" or "local" or "reference-local" or "unknown";
        if (page is JsonElement json) { Assert.Equal(inherited ? 1 : 0, json.GetProperty("images").GetArrayLength()); }
        else { Assert.Equal(inherited, messages.Any(s => s.Contains("/Im: 5 0 R", StringComparison.Ordinal))); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> ContentsCases()
    {
        foreach (string shape in new[] { "absent", "null", "reference-null", "stale", "free", "empty", "reference-empty", "stream", "array", "reference-array", "scalar", "reference-scalar", "unknown", "damaged-array" })
        foreach (string route in Routes)
        {
            yield return [shape, route, 2];
            if (!route.StartsWith("show-", StringComparison.Ordinal) && shape is "reference-null" or "stream" or "array" or "reference-array" or "unknown")
            { yield return [shape, route, 1]; }
        }
    }

    [Theory]
    [MemberData(nameof(ContentsCases))]
    public async Task OuterNullContentsAreEmptyWithoutResolvingStreamEntries(string shape, string route, int version)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("local", false, shape), QpdfInputOwnership.Transfer);
        (JsonElement? page, string[] messages) = await Inspect(host, route, version, CancellationToken.None);
        string?[] expected = shape switch
        {
            "stream" => ["4 0 R"], "array" or "reference-array" => ["4 0 R", "4 0 R"], "scalar" => ["42"],
            "reference-scalar" => ["8 0 R"], "unknown" => ["99 0 R"], "damaged-array" => [null, "6 0 R", "4 0 R"], _ => [],
        };
        if (page is JsonElement json)
        {
            JsonElement[] entries = json.GetProperty("contents").EnumerateArray().ToArray(); Assert.Equal(expected.Length, entries.Length);
            for (int i = 0; i < entries.Length; i++) { Assert.Equal(expected[i], entries[i].ValueKind == JsonValueKind.Null ? null : entries[i].ValueKind == JsonValueKind.String ? entries[i].GetString() : entries[i].GetRawText()); }
        }
        else
        {
            string[] references = expected.Where(s => s is not null && s.EndsWith(" R", StringComparison.Ordinal)).Select(s => "    " + s).ToArray();
            Assert.Equal(references, messages.Where(s => s.StartsWith("    ", StringComparison.Ordinal) && !s.Contains("/Im:", StringComparison.Ordinal)));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private sealed class BudgetHost(InMemoryQpdfHost inner, IMemoryBudget caller) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => caller;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => inner.CreateOutputAsync(path, token);
    }

    public static IEnumerable<object[]> AncestorCases()
    {
        foreach (string shape in new[] { "cycle", "reference-null", "missing", "scalar", "depth" })
        foreach (string route in new[] { "typed", "argv", "show-job" }) { yield return [shape, route]; }
    }

    [Theory]
    [MemberData(nameof(AncestorCases))]
    public async Task RequiredParentErrorsAndDepthStillReject(string shape, string route)
    {
        string parent = shape switch { "cycle" => "3 0 R", "reference-null" => "6 0 R", "missing" => "99 0 R", "scalar" => "42", _ => "2 0 R" };
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 " + (shape == "depth" ? "/Parent 7 0 R" : "") + " >>", null),
            (3, "<< /Type /Page /Parent " + parent + " /MediaBox [0 0 100 100] >>", null),
            (4, "null", null), (5, "null", null), (6, "null", null), (7, "<< /Type /Pages /Resources << >> >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfLimits limits = shape == "depth" ? QpdfLimits.Default with { MaxNestingDepth = 1 } : QpdfLimits.Default;
        if (route == "typed")
        {
            await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(2, ["pages"]), limits, CancellationToken.None).AsTask());
        }
        else
        {
            QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=pages", "--", "output"]), new(limits), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","showPages":true,"withImages":true}""", new(limits), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(0L, host.OpenLeaseCount);
        if (shape == "depth")
        {
            await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(2, ["pages"]), limits with { MaxNestingDepth = 2 }, CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShowPagesKeepsLargeStreamPayloadsUnread(bool job)
    {
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), JsonArrayReaderTests.LargeStream(), QpdfInputOwnership.Transfer);
        RecordingMemoryBudget root = new(8 * 1048576L); using IMemoryScope caller = root.CreateScope(); BudgetHost host = new(storage, caller);
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxMemoryBytes = 3 * 1048576L });
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","showPages":true}""", options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--show-pages"]), options, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Contains(result.Diagnostics, d => d.Message == "    4 0 R");
        Assert.InRange(result.Counters.PeakMemoryBytes, 2 * 1048576L, options.Limits.MaxMemoryBytes);
        Assert.Equal(0L, storage.OpenLeaseCount); storage.Dispose(); Assert.InRange(root.ReservedBytes, 1L, 4096L);
        caller.Dispose(); Assert.Equal(0L, root.ReservedBytes);
    }
}

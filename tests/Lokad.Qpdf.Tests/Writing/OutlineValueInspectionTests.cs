using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class OutlineValueInspectionTests
{
    private static byte[] Fixture(string title, string count, string destination, int generation)
    {
        byte[] bytes = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 " + generation + " R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << >> >>", null),
            (4, "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>", null),
            (5, "<< " + (title.Length == 0 ? "" : "/Title " + title) + (count.Length == 0 ? "" : " /Count " + count)
                + " " + destination + " /Parent 4 0 R /First 6 0 R /Last 6 0 R >>", null),
            (6, "<< /Title (Child) /Parent 5 0 R /Dest [3 " + generation + " R /Fit] >>", null),
            (7, "(Parent)", null), (8, "-1", null),
            (9, "<< /S /GoTo /D [3 " + generation + " R /Fit] >>", null), (10, "[3 " + generation + " R /Fit]", null),
            (11, "/GoTo", null), (12, "42", null), (13, "null", null), (14, "null", null)], 1, new Dictionary<int, int> { [3] = generation });
        // The shared builder emits contiguous objects; mark one reviewed xref
        // row free without changing any object or startxref offsets.
        int xref = bytes.AsSpan().LastIndexOf("xref\n0 15\n"u8); Assert.True(xref >= 0);
        "0000000000 65535 f \n"u8.CopyTo(bytes.AsSpan(xref + "xref\n0 15\n"u8.Length + 14 * 20, 20));
        return bytes;
    }

    private static async Task<JsonDocument> Inspect(InMemoryQpdfHost storage, IQpdfHost host, string route, int version)
    {
        if (route == "typed")
        { await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"), QpdfPath.From("report"), new(version, ["outlines", "pages"]), CancellationToken.None); }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--json=" + version, "--json-key=outlines", "--json-key=pages", "--", "report"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "report", json = version.ToString(), jsonKey = new[] { "outlines", "pages" } }), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, storage.OpenLeaseCount); Assert.True(storage.TryGetOutput(QpdfPath.From("report"), out byte[] bytes));
        return JsonDocument.Parse(bytes);
    }

    public static IEnumerable<object[]> ScalarCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (int version in new[] { 1, 2 })
        foreach ((string title, string count, string expected, bool open) in new[]
        {
            ("(Parent)", "-1", "Parent", false), ("7 0 R", "-1", "Parent", false),
            ("(Parent)", "8 0 R", "Parent", false), ("7 0 R", "8 0 R", "Parent", false),
            ("", "", "", true), ("null", "null", "", true), ("99 0 R", "99 0 R", "", true),
            ("8 0 R", "7 0 R", "", true), ("7 1 R", "8 1 R", "", true),
        }) { yield return [route, version, title, count, expected, open]; }
    }

    [Theory]
    [MemberData(nameof(ScalarCases))]
    public async Task OptionalTitleAndCountKeepDirectSemantics(string route, int version, string title, string count, string expected, bool open)
    {
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), Fixture(title, count, "/Dest [3 0 R /Fit]", 0), QpdfInputOwnership.Transfer);
        using JsonDocument report = await Inspect(storage, storage, route, version);
        JsonElement parent = report.RootElement.GetProperty("outlines")[0];
        Assert.Equal(expected, parent.GetProperty("title").GetString()); Assert.Equal(open, parent.GetProperty("open").GetBoolean());
        Assert.Equal(1, parent.GetProperty("destpageposfrom1").GetInt32()); Assert.Single(parent.GetProperty("kids").EnumerateArray());
        Assert.Equal(new[] { expected, "Child" }, report.RootElement.GetProperty("pages")[0].GetProperty("outlines").EnumerateArray().Select(entry => entry.GetProperty("title").GetString()));
    }

    public static IEnumerable<object[]> ActionCases()
    {
        const string local = "/A << /S /GoTo /D [3 0 R /Fit] >>";
        const string fit = "[\"3 0 R\",\"/Fit\"]";
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (int version in new[] { 1, 2 })
        foreach ((string specification, string expected, int page) in new[]
        {
            (local, fit, 1), ("/A 9 0 R", fit, 1), ("/A << /S 11 0 R /D 10 0 R >>", fit, 1),
            ("/A << /S /GoTo /D 10 0 R >>", fit, 1), ("/A << /S /GoTo /D 99 0 R >>", "null", 0),
            ("/A 99 0 R", "null", 0), ("/A null", "null", 0), ("/A 42", "null", 0),
            ("/A << /S /URI /D [3 0 R /Fit] /URI (https://example.invalid/) >>", "null", 0),
            ("/A << /S /GoToR /F (other.pdf) /D [3 0 R /Fit] >>", "null", 0),
            ("/A << /S 99 0 R /D [3 0 R /Fit] >>", "null", 0),
            ("/Dest null " + local, fit, 1), ("/Dest 99 0 R " + local, fit, 1),
            ("/Dest 12 1 R " + local, fit, 1), ("/Dest 42 " + local, "42", 0), ("/Dest [] " + local, "[]", 0),
            ("/Dest [3 0 R /XYZ 0 0 1] " + local, "[\"3 0 R\",\"/XYZ\",0,0,1]", 1),
            ("/Dest /Chapter " + local, "\"/Chapter\"", 0), ("/A << /S /GoTo /D /Chapter >>", "\"/Chapter\"", 0),
            ("/A << /S /GoTo /D 42 >>", "42", 0), ("/A << /S /GoTo /D null >>", "null", 0),
            ("/A << /S /GoTo >>", "null", 0),
        }) { yield return [route, version, specification, expected, page]; }
    }

    [Theory]
    [MemberData(nameof(ActionCases))]
    public async Task LocalActionsKeepExplicitDestinationAndPrecedence(string route, int version, string specification, string expected, int page)
    {
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), Fixture("(Parent)", "-1", specification, 0), QpdfInputOwnership.Transfer);
        using JsonDocument report = await Inspect(storage, storage, route, version);
        JsonElement parent = report.RootElement.GetProperty("outlines")[0]; using JsonDocument destination = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(destination.RootElement, parent.GetProperty("dest")));
        Assert.False(parent.GetProperty("open").GetBoolean()); Assert.Equal("Parent", parent.GetProperty("title").GetString());
        if (page == 0) { Assert.Equal(JsonValueKind.Null, parent.GetProperty("destpageposfrom1").ValueKind); }
        else { Assert.Equal(page, parent.GetProperty("destpageposfrom1").GetInt32()); }
        Assert.Equal(page == 0 ? new[] { "Child" } : ["Parent", "Child"],
            report.RootElement.GetProperty("pages")[0].GetProperty("outlines").EnumerateArray().Select(entry => entry.GetProperty("title").GetString()));
    }

    public static IEnumerable<object[]> GenerationCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (int version in new[] { 1, 2 })
        foreach (bool action in new[] { false, true })
        foreach ((int active, int number, int generation, bool live, bool page) in new[]
        {
            (0, 3, 0, true, true), (4, 3, 4, true, true),
            (0, 3, 1, false, false), (4, 3, 0, false, false), (4, 3, 5, false, false),
            (0, 99, 0, false, false), (4, 99, 4, false, false),
            (0, 12, 0, true, false), (0, 14, 0, false, false), (4, 14, 1, false, false),
        }) { yield return [route, version, action, active, number, generation, live, page]; }
    }

    [Theory]
    [MemberData(nameof(GenerationCases))]
    public async Task DestinationsRespectEffectiveObjectGenerations(
        string route, int version, bool action, int active, int number, int generation, bool live, bool page)
    {
        string array = "[" + number + " " + generation + " R /Fit]";
        string specification = action ? "/A << /S /GoTo /D " + array + " >>" : "/Dest " + array;
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), Fixture("(Parent)", "-1", specification, active), QpdfInputOwnership.Transfer);
        using JsonDocument report = await Inspect(storage, storage, route, version);
        JsonElement parent = report.RootElement.GetProperty("outlines")[0]; JsonElement destination = parent.GetProperty("dest");
        if (live) { Assert.Equal(number + " " + generation + " R", destination[0].GetString()); }
        else { Assert.Equal(JsonValueKind.Null, destination[0].ValueKind); }
        Assert.Equal("/Fit", destination[1].GetString());
        if (page) { Assert.Equal(1, parent.GetProperty("destpageposfrom1").GetInt32()); }
        else { Assert.Equal(JsonValueKind.Null, parent.GetProperty("destpageposfrom1").ValueKind); }
        Assert.Equal(page ? new[] { "Parent", "Child" } : ["Child"],
            report.RootElement.GetProperty("pages")[0].GetProperty("outlines").EnumerateArray().Select(entry => entry.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task NormalizationBorrowsSourceValuesWithoutMutatingTheirArray()
    {
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), Fixture("(Parent)", "-1", "/Dest [3 1 R /Fit]", 0), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(storage, QpdfPath.From("input"), CancellationToken.None);
        PdfDictionary parent = Assert.IsType<PdfDictionary>(document.GetObject(5)); Assert.True(parent.TryGet(PdfName.FromAscii("Dest"), out QpdfObject raw));
        PdfArray source = Assert.IsType<PdfArray>(raw); QpdfObject mode = source[1];
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (PreparedJsonOutput output = PreparedJsonOutput.Create(governor))
        using (DisplayBuffer text = DocumentJson.WriteDocument(document, new(2, ["outlines"], "none", "none", null, null, false), output,
            QpdfLimits.Default, governor, new(governor), CancellationToken.None))
        using (GovernedText rendered = text.Finish())
        using (JsonDocument report = JsonDocument.Parse(rendered.Value))
        { Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("outlines")[0].GetProperty("dest")[0].ValueKind); }
        Assert.True(parent.TryGet(PdfName.FromAscii("Dest"), out QpdfObject after)); Assert.Same(source, after);
        Assert.Equal(1, Assert.IsType<PdfReference>(source[0]).Generation); Assert.Same(mode, source[1]);
    }

    [Fact]
    public async Task OversizedMalformedDestinationRefusesBeforeItsImmutableCopy()
    {
        string array = "[3 1 R /Fit " + string.Join(' ', Enumerable.Repeat("null", 65536)) + "]";
        using InMemoryQpdfHost storage = new(); storage.AddInput(QpdfPath.From("input"), Fixture("(Parent)", "-1", "/Dest " + array, 0), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(storage, QpdfPath.From("input"), CancellationToken.None);
        document.GetObject(4); document.GetObject(5);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096 }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(71L); long live = governor.Snapshot().LiveMemoryBytes;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() =>
        {
            using PreparedJsonOutput output = PreparedJsonOutput.Create(governor);
            using DisplayBuffer text = DocumentJson.WriteDocument(document, new(2, ["outlines"], "none", "none", null, null, false), output,
                limits, governor, new(governor), CancellationToken.None);
        });
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget budget, string? fault) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => storage.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            IQpdfOutputTransaction inner = await storage.CreateOutputAsync(path, token);
            return fault is null ? inner : new Transaction(inner, fault);
        }
        private sealed class Transaction(IQpdfOutputTransaction inner, string fault) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
                => fault == "write" ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed)) : inner.WriteAsync(bytes, token);
            public ValueTask CommitAsync(CancellationToken token)
                => fault == "commit" ? ValueTask.FromException(new QpdfHostException(Path, QpdfHostFailureReason.CommitFailed)) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static async Task Seed(InMemoryQpdfHost storage)
    {
        storage.AddInput(QpdfPath.From("input"), Fixture("7 0 R", "8 0 R", "/A << /S 11 0 R /D [3 1 R /Fit] >>", 0), QpdfInputOwnership.Transfer);
        await using IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("report"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SharedRefusalPreservesPublicationAndEscapedJsonKeepsCallerOwnership()
    {
        RecordingMemoryBudget root = new(64L << 20, false); int growths; string escaped;
        using (IMemoryScope caller = root.CreateScope())
        {
            using InMemoryQpdfHost storage = new(); await Seed(storage);
            QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(new Host(storage, caller, null), QpdfPath.From("input"), QpdfPath.From("report"), new(2, ["outlines", "pages"]), CancellationToken.None);
            growths = root.GrowthCalls; escaped = result.Json; Assert.Equal(0L, storage.OpenLeaseCount); storage.Dispose();
            Assert.True(root.ReservedBytes > 0L);
            using JsonDocument report = JsonDocument.Parse(escaped); JsonElement parent = report.RootElement.GetProperty("outlines")[0];
            Assert.Equal("Parent", parent.GetProperty("title").GetString()); Assert.False(parent.GetProperty("open").GetBoolean());
            Assert.Equal(JsonValueKind.Null, parent.GetProperty("dest")[0].ValueKind);
        }
        Assert.Equal(0L, root.ReservedBytes); Assert.Contains("Parent", escaped, StringComparison.Ordinal); Assert.True(growths > 0);
        for (int point = 1; point <= growths; point++)
        {
            RecordingMemoryBudget refused = new(64L << 20, false) { RefuseOnGrowth = point };
            using (IMemoryScope caller = refused.CreateScope())
            {
                using InMemoryQpdfHost storage = new(); await Seed(storage);
                await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(async () => await Qpdf.GetDocumentJsonAsync(new Host(storage, caller, null),
                    QpdfPath.From("input"), QpdfPath.From("report"), new(2, ["outlines", "pages"]), CancellationToken.None));
                Assert.True(storage.TryGetOutput(QpdfPath.From("report"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior); Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(0L, refused.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("commit")]
    public async Task OutputFailureKeepsPriorJsonAndRetiresAdmission(string fault)
    {
        RecordingMemoryBudget root = new(64L << 20, false);
        using (IMemoryScope caller = root.CreateScope())
        {
            using InMemoryQpdfHost storage = new(); await Seed(storage);
            await Assert.ThrowsAsync<QpdfHostException>(async () => await Qpdf.GetDocumentJsonAsync(new Host(storage, caller, fault),
                QpdfPath.From("input"), QpdfPath.From("report"), new(2, ["outlines", "pages"]), CancellationToken.None));
            Assert.True(storage.TryGetOutput(QpdfPath.From("report"), out byte[] prior)); Assert.Equal("prior"u8.ToArray(), prior); Assert.Equal(0L, storage.OpenLeaseCount);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }
}

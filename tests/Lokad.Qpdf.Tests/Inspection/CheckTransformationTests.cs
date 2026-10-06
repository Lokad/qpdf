using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class CheckTransformationTests
{
    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-2page.pdf"));

    private static byte[] NestedFixture() => NestedFixture(false);

    private static byte[] NestedFixture(bool contentGenerations) => CheckTests.Assemble(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [8 0 R] /Count 2 /MediaBox [0 0 600 800] /Resources << /ProcSet [/PDF] >> /Rotate 90 >>", null),
            (3, "<< /Type /Page /Parent 8 0 R /Contents " + (contentGenerations ? "[5 3 R 6 4 R]" : "5 0 R") + " /Rotate null >>", null),
            (4, "<< /Type /Page /Parent 8 0 R /Contents " + (contentGenerations ? "6 4 R" : "6 0 R") + " >>", null),
            (5, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (6, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (7, "(orphan)", null),
            (8, "<< /Type /Pages /Parent 2 0 R /Kids [3 2 R 4 0 R] /Count 2 >>", null),
        ], 1, contentGenerations ? new Dictionary<int, int> { [3] = 2, [5] = 3, [6] = 4 } : new Dictionary<int, int> { [3] = 2 });

    public static IEnumerable<object?[]> NestedObjects()
    {
        foreach (string route in new[] { "job", "argv", "mixed" })
        foreach (bool check in new[] { false, true })
        {
            yield return [route, check, "1", null, "3,2", "<< /Contents 5 0 R /MediaBox 9 0 R /Parent 2 0 R /Resources << /ProcSet [ /PDF ] >> /Rotate 90 /Type /Page >>"];
            yield return [route, check, "1", null, "3,0", "null"];
            yield return [route, check, "1", null, "4", "null"];
            yield return [route, check, "1", null, "9", "[ 0 0 600 800 ]"];
            yield return [route, check, "1", null, "10", "<< /ProcSet [ /PDF ] >>"];
            yield return [route, check, "1,1", "+90:2", "11", "<< /Contents 5 0 R /MediaBox 9 0 R /Parent 2 0 R /Resources << /ProcSet [ /PDF ] >> /Rotate 180 /Type /Page >>"];
            yield return [route, check, "1,1", "+90:2", "3,2", "<< /Contents 5 0 R /MediaBox 9 0 R /Parent 2 0 R /Resources << /ProcSet [ /PDF ] >> /Rotate 90 /Type /Page >>"];
            yield return [route, check, null, "+90:1", "3,2", "<< /Contents 5 0 R /Parent 8 0 R /Rotate 180 /Type /Page >>"];
            yield return [route, check, null, "-90:2", "4", "<< /Contents 6 0 R /Parent 8 0 R /Rotate 0 /Type /Page >>"];
        }
    }

    [Theory]
    [MemberData(nameof(NestedObjects))]
    public async Task NestedSelectionPreservesGenerationsAndInheritedAllocationOrder(string route, bool check,
        string? range, string? rotation, string selector, string expected)
    {
        InMemoryQpdfHost host = Host(NestedFixture());
        string properties = "\"showObject\":\"" + selector + "\",\"showPages\":\"\",\"showXref\":\"\"";
        List<string> flags = ["--show-object=" + selector, "--show-pages", "--show-xref"];
        if (check) { properties += ",\"check\":\"\""; flags.Add("--check"); }
        if (range is not null) { properties += ",\"pages\":[{\"file\":\".\",\"range\":\"" + range + "\"}]"; flags.AddRange(["--pages", ".", range, "--"]); }
        if (rotation is not null) { properties += ",\"rotate\":[\"" + rotation + "\"]"; flags.Add("--rotate=" + rotation); }
        QpdfExecutionResult result = await Execute(host, route, properties, flags.ToArray(), QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(expected, Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "page 1: 3 2 R");
        if (range == "1,1") { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "page 2: 11 0 R"); }
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.XrefListing && d.Message.StartsWith("3/2: ", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.XrefListing && d.Message.StartsWith("9/", StringComparison.Ordinal));
        Assert.Null(result.OutputBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    private static InMemoryQpdfHost Host(byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("a.pdf"), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static ValueTask<QpdfExecutionResult> Execute(InMemoryQpdfHost host, string route,
        string properties, string[] flags, QpdfLimits limits)
        => Execute(host, host, route, properties, flags, limits, CancellationToken.None);

    private static ValueTask<QpdfExecutionResult> Execute(InMemoryQpdfHost storage, IQpdfHost host, string route,
        string properties, string[] flags, QpdfLimits limits, CancellationToken cancellationToken)
    {
        QpdfExecutionOptions options = new(limits);
        if (route == "job") { return Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"a.pdf\"," + properties + "}", options, cancellationToken); }
        if (route == "mixed")
        {
            storage.AddInput(QpdfPath.From("settings.json"), Encoding.UTF8.GetBytes("{" + properties + "}"), QpdfInputOwnership.Copy);
            return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["a.pdf", "--job-json-file=settings.json"]), options, cancellationToken);
        }
        return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["a.pdf", .. flags]), options, cancellationToken);
    }

    public static IEnumerable<object[]> SourceContexts()
    {
        foreach (string route in new[] { "job", "argv", "mixed" })
        foreach (bool check in new[] { false, true })
        foreach (string mode in new[] { "same", "alias", "foreign-duplicate", "interleaved", "collation" }) { yield return [route, check, mode]; }
    }

    [Theory]
    [MemberData(nameof(SourceContexts))]
    public async Task SourceKeysCollationAndForeignSharingMatchNativeIdentities(string route, bool check, string mode)
    {
        InMemoryQpdfHost storage = Host(Fixture());
        storage.AddInput(QpdfPath.From("b.pdf"), Fixture(), QpdfInputOwnership.Copy);
        TestHost host = new(storage, null);
        string sources = mode switch
        {
            "same" => "{\"file\":\"a.pdf\",\"range\":\"2\"}",
            "alias" => "{\"file\":\"./a.pdf\",\"range\":\"2\"}",
            "foreign-duplicate" => "{\"file\":\"b.pdf\",\"range\":\"1,1\"}",
            "interleaved" => "{\"file\":\"b.pdf\",\"range\":\"1\"},{\"file\":\".\",\"range\":\"2\"},{\"file\":\"b.pdf\",\"range\":\"2\"}",
            _ => "{\"file\":\".\",\"range\":\"1,2\"},{\"file\":\"b.pdf\",\"range\":\"1,2\"}",
        };
        string properties = "\"pages\":[" + sources + "],\"showNpages\":\"\",\"showPages\":\"\",\"showObject\":\"2\",\"showXref\":\"\"";
        List<string> flags = ["--show-npages", "--show-pages", "--show-object=2", "--show-xref", "--pages"];
        flags.AddRange(mode switch
        {
            "same" => ["a.pdf", "2"], "alias" => ["./a.pdf", "2"], "foreign-duplicate" => ["b.pdf", "1,1"],
            "interleaved" => ["b.pdf", "1", ".", "2", "b.pdf", "2"], _ => [".", "1,2", "b.pdf", "1,2"],
        });
        flags.Add("--");
        if (check) { properties += ",\"check\":\"\""; flags.Add("--check"); }
        if (mode == "collation") { properties += ",\"collate\":\"1\""; flags.Add("--collate=1"); }
        int[] expected = mode switch { "same" => [4], "alias" => [8], "foreign-duplicate" => [8, 11], "interleaved" => [8, 4, 11], _ => [3, 8, 4, 11] };
        QpdfExecutionResult result = await Execute(storage, host, route, properties, flags.ToArray(), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        string[] pageLines = result.Diagnostics.Where(d => d.Code == QpdfDiagnosticCode.PageListing && d.Message.StartsWith("page ", StringComparison.Ordinal)).Select(d => d.Message).ToArray();
        Assert.Equal(expected.Select((number, index) => $"page {index + 1}: {number} 0 R").ToArray(), pageLines);
        Assert.Equal("<< /Count " + expected.Length + " /Kids [ " + string.Join(" ", expected.Select(number => number + " 0 R")) + " ] /Type /Pages >>",
            Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message);
        if (mode == "foreign-duplicate") { Assert.Equal(2, result.Diagnostics.Count(d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "    9 0 R")); }
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.XrefListing && d.Message.StartsWith("8/", StringComparison.Ordinal));
        Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData("job", false)]
    [InlineData("argv", false)]
    [InlineData("mixed", false)]
    [InlineData("job", true)]
    [InlineData("argv", true)]
    [InlineData("mixed", true)]
    public async Task ContentsArraysKeepEachStreamGeneration(string route, bool selection)
    {
        string properties = "\"showPages\":\"\",\"showObject\":\"3,2\"";
        List<string> flags = ["--show-pages", "--show-object=3,2"];
        if (selection) { properties += ",\"pages\":[{\"file\":\".\",\"range\":\"1,1\"}]"; flags.AddRange(["--pages", ".", "1,1", "--"]); }
        QpdfExecutionResult result = await Execute(Host(NestedFixture(true)), route, properties, flags.ToArray(), QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "page 1: 3 2 R");
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "    5 3 R");
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "    6 4 R");
        Assert.Contains("/Contents [ 5 3 R 6 4 R ]", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task CancellationDuringForeignReadDisposesThePreparedPrimaryView(string route)
    {
        InMemoryQpdfHost storage = Host(Fixture());
        storage.AddInput(QpdfPath.From("b.pdf"), Fixture(), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction seed = await storage.CreateOutputAsync(QpdfPath.From("prior.pdf"), CancellationToken.None))
        {
            await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        using CancellationTokenSource cancellation = new();
        TestHost host = new(storage, cancellation);
        QpdfExecutionResult result = await Execute(storage, host, route,
            "\"check\":\"\",\"pages\":[{\"file\":\"b.pdf\",\"range\":\"1\"}],\"showNpages\":\"\"",
            ["--check", "--show-npages", "--pages", "b.pdf", "1", "--"], QpdfLimits.Default, cancellation.Token);
        Assert.True(host.ForeignReadCompleted);
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
        Assert.True(result.Counters.TotalWork > 0L);
        Assert.True(result.Counters.PeakMemoryBytes > 0L);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.Null(result.OutputBytes);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount);
        Assert.True(storage.TryGetOutput(QpdfPath.From("prior.pdf"), out byte[] prior));
        Assert.Equal("prior"u8.ToArray(), prior);
    }

    private sealed class TestHost(InMemoryQpdfHost storage, CancellationTokenSource? cancellation) : IQpdfHost
    {
        internal bool ForeignReadCompleted { get; private set; }

        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            string name = path.Value.StartsWith("./", StringComparison.Ordinal) ? path.Value[2..] : path.Value;
            IQpdfInput input = await storage.OpenInputAsync(QpdfPath.From(name), token);
            return name == "b.pdf" && cancellation is not null ? new ObservedInput(input, this, cancellation) : input;
        }

        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => storage.CreateOutputAsync(path, token);

        private sealed class ObservedInput(IQpdfInput input, TestHost host, CancellationTokenSource cancellation) : IQpdfInput
        {
            public QpdfPath Path => input.Path;
            public string Identity => input.Identity;
            public long Length => input.Length;
            public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken token)
            {
                ReadOnlyMemory<byte> bytes = await input.ReadAsync(offset, count, token);
                host.ForeignReadCompleted = !bytes.IsEmpty;
                cancellation.Cancel();
                return bytes;
            }
            public ValueTask DisposeAsync() => input.DisposeAsync();
        }
    }

    public static IEnumerable<object[]> Effects()
    {
        foreach (string route in new[] { "job", "argv", "mixed" })
        foreach (string mode in new[] { "first", "second", "reorder", "duplicate", "select-rotate", "rotate" })
        foreach (bool check in new[] { false, true }) { yield return [route, mode, check]; }
    }

    [Theory]
    [MemberData(nameof(Effects))]
    public async Task SelectionAndRotationChangeActualCountsAndOriginalIdentities(string route, string mode, bool check)
    {
        InMemoryQpdfHost host = Host(Fixture());
        string? range = mode switch { "first" => "1", "second" or "select-rotate" => "2", "reorder" => "2,1", "duplicate" => "1,1", _ => null };
        string? rotate = mode switch { "duplicate" => "+90:2", "select-rotate" => "+90:1", "rotate" => "-90:2", _ => null };
        string selector = mode == "duplicate" ? "8" : mode == "select-rotate" || mode == "rotate" ? "4" : "2";
        string properties = "\"showNpages\":\"\",\"showPages\":\"\",\"showXref\":\"\",\"showObject\":\"" + selector + "\"";
        List<string> flags = ["--show-npages", "--show-pages", "--show-xref", "--show-object=" + selector];
        if (check) { properties += ",\"check\":\"\""; flags.Add("--check"); }
        if (range is not null) { properties += ",\"pages\":[{\"file\":\".\",\"range\":\"" + range + "\"}]"; flags.AddRange(["--pages", ".", range, "--"]); }
        if (rotate is not null) { properties += ",\"rotate\":[\"" + rotate + "\"]"; flags.Add("--rotate=" + rotate); }
        QpdfExecutionResult result = await Execute(host, route, properties, [.. flags], QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        int count = mode is "first" or "second" or "select-rotate" ? 1 : 2;
        Assert.EndsWith(": " + count, Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        string[] pageLines = result.Diagnostics.Where(d => d.Code == QpdfDiagnosticCode.PageListing && d.Message.StartsWith("page ", StringComparison.Ordinal)).Select(d => d.Message).ToArray();
        string[] expected = mode switch
        {
            "first" => ["page 1: 3 0 R"],
            "second" or "select-rotate" => ["page 1: 4 0 R"],
            "reorder" => ["page 1: 4 0 R", "page 2: 3 0 R"],
            "duplicate" => ["page 1: 3 0 R", "page 2: 8 0 R"],
            _ => ["page 1: 3 0 R", "page 2: 4 0 R"],
        };
        Assert.Equal(expected, pageLines);
        string definition = Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message;
        if (rotate is not null) { Assert.Contains(mode == "rotate" ? "/Rotate 270" : "/Rotate 90", definition, StringComparison.Ordinal); }
        else { Assert.Contains("/Count " + count, definition, StringComparison.Ordinal); }
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.XrefListing && d.Message == "3/0: uncompressed; offset = 121");
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.XrefListing && d.Message.StartsWith("8/", StringComparison.Ordinal));
        Assert.Null(result.OutputBytes);
        Assert.Equal(0L, result.Counters.TotalOutputBytes);
        Assert.True(result.Counters.TotalWork > 0L);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task RemovedPageObjectsResolveToNullAfterSelection(string route)
    {
        QpdfExecutionResult result = await Execute(Host(Fixture()), route,
            "\"check\":\"\",\"pages\":[{\"file\":\".\",\"range\":\"1\"}],\"showObject\":\"4\"",
            ["--check", "--show-object=4", "--pages", ".", "1", "--"], QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal("null", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task SelectedFilteredStreamReturnsTheOriginalPayload(string route)
    {
        QpdfExecutionResult result = await Execute(Host(Fixture()), route,
            "\"pages\":[{\"file\":\".\",\"range\":\"1\"}],\"showObject\":\"5\",\"filteredStreamData\":\"\"",
            ["--show-object=5", "--filtered-stream-data", "--pages", ".", "1", "--"], QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(result.OutputBytes.HasValue);
        Assert.Equal("BT /F1 24 Tf 100 700 Td (First) Tj ET"u8.ToArray(), result.OutputBytes.GetValueOrDefault().ToArray());
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task RangeBoundsReturnExplicitFailureAfterInputWork(string route)
    {
        InMemoryQpdfHost host = Host(Fixture());
        QpdfExecutionResult result = await Execute(host, route,
            "\"check\":\"\",\"pages\":[{\"file\":\".\",\"range\":\"9\"}],\"showNpages\":\"\"",
            ["--check", "--show-npages", "--pages", ".", "9", "--"], QpdfLimits.Default);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Severity == QpdfDiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount);
        Assert.True(result.Counters.HostOperations > 0L);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("job", "memory")]
    [InlineData("argv", "memory")]
    [InlineData("mixed", "memory")]
    [InlineData("job", "work")]
    [InlineData("argv", "work")]
    [InlineData("mixed", "work")]
    public async Task TransformationCeilingsPreserveExistingTargets(string route, string ceiling)
    {
        string range = string.Join(",", Enumerable.Repeat("1", 150));
        string properties = "\"check\":\"\",\"pages\":[{\"file\":\".\",\"range\":\"" + range + "\"}],\"rotate\":[\"+90\"],\"showPages\":\"\"";
        string[] flags = ["--check", "--show-pages", "--rotate=+90", "--pages", ".", range, "--"];
        InMemoryQpdfHost host = Host(Fixture());
        QpdfLimits generous = QpdfLimits.Default with { MaxDiagnostics = 1000 };
        QpdfExecutionResult control = await Execute(host, route, properties, flags, generous);
        Assert.Equal(QpdfExitCode.Success, control.ExitCode);
        Assert.Equal(150, control.Diagnostics.Count(d => d.Code == QpdfDiagnosticCode.PageListing && d.Message.StartsWith("page ", StringComparison.Ordinal)));
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("prior.pdf"), CancellationToken.None))
        {
            await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }
        QpdfLimits limits = ceiling == "memory" ? generous with { MaxMemoryBytes = control.Counters.PeakMemoryBytes / 2L }
            // Weak identity-index collisions vary between equivalent jobs.
            : generous with { MaxWork = control.Counters.TotalWork / 2L };
        QpdfExecutionResult result = await Execute(host, route, properties, flags, limits);
        Assert.True(result.Status == QpdfExecutionStatus.Failed,
            $"Expected budget failure; got {result.Status}/{result.ExitCode}, control peak {control.Counters.PeakMemoryBytes}, "
            + $"limited peak {result.Counters.PeakMemoryBytes}, ceiling {limits.MaxMemoryBytes}; "
            + string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(result.Counters.HostOperations > 0L);
        Assert.InRange(result.Counters.PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
        Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("prior.pdf"), out byte[] prior));
        Assert.Equal("prior"u8.ToArray(), prior);
        Assert.Null(result.OutputBytes);
    }

    [Fact]
    public async Task TypedSelectionStillPreservesRotationAndWritesReadablePages()
    {
        InMemoryQpdfHost host = Host(Fixture());
        await Qpdf.SelectPageAsync(host, QpdfPath.From("a.pdf"), QpdfPath.From("out.pdf"),
            PdfPageSelectionOptions.FromPages([2]), [new PdfPageRotation(PdfRotationMode.Add, 90, null)], CancellationToken.None);
        Assert.True(host.TryGetOutput(QpdfPath.From("out.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("written.pdf"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument written = await QpdfDocument.OpenAsync(host, QpdfPath.From("written.pdf"), CancellationToken.None);
        Assert.Equal(1, written.PageCount);
        PdfDictionary root = Assert.IsType<PdfDictionary>(written.GetObject(2));
        Assert.True(root.TryGet(PdfName.FromAscii("Kids"), out QpdfObject kids));
        PdfReference page = Assert.IsType<PdfReference>(Assert.IsType<PdfArray>(kids)[0]);
        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(written.GetObject(page.ObjectNumber));
        Assert.True(dictionary.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject angle));
        Assert.Equal(90L, Assert.IsType<PdfInteger>(angle).Value);
    }
}

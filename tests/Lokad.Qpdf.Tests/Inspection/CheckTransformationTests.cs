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
            public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken token)
            {
                int bytes = await input.ReadAsync(offset, destination, token);
                host.ForeignReadCompleted = bytes != 0;
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
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task OverlayChangesInspectedPageBeforeChecking(string route)
    {
        InMemoryQpdfHost host = Host(Fixture());
        host.AddInput(QpdfPath.From("b.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Execute(host, route,
            "\"overlay\":{\"file\":\"b.pdf\"},\"check\":\"\",\"showNpages\":\"\",\"showPages\":\"\",\"showObject\":\"3\"",
            ["--overlay", "b.pdf", "--", "--check", "--show-npages", "--show-pages", "--show-object=3"], QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal("<< /Contents 11 0 R /MediaBox [ 0 0 612 792 ] /Parent 2 0 R /Resources << /XObject << /Fx0 8 0 R /Fx1 9 0 R >> >> /Type /Page >>",
            Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "page 1: 3 0 R");
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageListing && d.Message == "page 2: 4 0 R");
        Assert.EndsWith(": 2", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message, StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task MissingOverlayFailsBeforeCheckingOrListing()
    {
        InMemoryQpdfHost host = Host(Fixture());
        QpdfExecutionResult result = await Execute(host, "job",
            "\"overlay\":{\"file\":\"missing.pdf\"},\"check\":\"\",\"showNpages\":\"\"",
            [], QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("missing.pdf", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount
            || d.Code == QpdfDiagnosticCode.ObjectListing || d.Code == QpdfDiagnosticCode.PageListing);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task CancellationDuringOverlayReadClosesBothInputs()
    {
        InMemoryQpdfHost storage = Host(Fixture());
        storage.AddInput(QpdfPath.From("b.pdf"), Fixture(), QpdfInputOwnership.Copy);
        using CancellationTokenSource cancellation = new();
        TestHost host = new(storage, cancellation);
        QpdfExecutionResult result = await Execute(storage, host, "job",
            "\"overlay\":{\"file\":\"b.pdf\"},\"check\":\"\",\"showNpages\":\"\"", [], QpdfLimits.Default, cancellation.Token);
        Assert.True(host.ForeignReadCompleted);
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount);
        Assert.Equal(0L, storage.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
    }

    [Theory]
    [InlineData("minimal-2page.pdf", false)]
    [InlineData("forms-1page.pdf", true)]
    public async Task OverlayRegistersImportedFieldsBeforeChecking(string primary, bool existingForm)
    {
        InMemoryQpdfHost host = Host(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", primary)));
        host.AddInput(QpdfPath.From("b.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "overlay-widget-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Execute(host, "job",
            "\"overlay\":{\"file\":\"b.pdf\"},\"check\":\"\",\"showObject\":\"1\"", [], QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        string catalog = Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message;
        Assert.Contains("/AcroForm ", catalog, StringComparison.Ordinal);
        if (existingForm)
        {
            Assert.Contains("/Fields [ 6 0 R ", catalog, StringComparison.Ordinal);
            Assert.Contains("/DR ", catalog, StringComparison.Ordinal);
            Assert.Contains("/NeedAppearances true", catalog, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.FormStructureWarning);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature
            && d.Message.Contains("source form does not reference", StringComparison.Ordinal));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task OverlayPreservesIndirectAnnotationsAndFormGenerations()
    {
        byte[] primary = CheckTests.Assemble(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 9 3 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /Helv 7 0 R >> >> /Contents 4 0 R /Annots 8 2 R >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Length 3 >>", "q Q"u8.ToArray()),
            (6, "<< /Type /Annot /Subtype /Widget /Rect [100 600 300 620] /FT /Tx /T (Name) /P 3 0 R /AP << /N 5 0 R >> >>", null),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (8, "[6 4 R]", null),
            (9, "<< /Fields [6 4 R] /DR << /Font << /Helv 7 0 R >> >> /NeedAppearances true >>", null),
        ], 1, new Dictionary<int, int> { [6] = 4, [8] = 2, [9] = 3 });
        InMemoryQpdfHost host = Host(primary);
        host.AddInput(QpdfPath.From("b.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "overlay-widget-1page.pdf")), QpdfInputOwnership.Copy);
        string properties = "\"overlay\":{\"file\":\"b.pdf\"},\"check\":\"\",\"showObject\":\"";
        QpdfExecutionResult form = await Execute(host, "job", properties + "9,3\"", [], QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, form.ExitCode);
        string listing = Assert.Single(form.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message;
        Assert.Contains("/Fields [ 6 4 R ", listing, StringComparison.Ordinal);
        Assert.Contains("/DR ", listing, StringComparison.Ordinal);
        QpdfExecutionResult annotations = await Execute(host, "job", properties + "8,2\"", [], QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, annotations.ExitCode);
        Assert.StartsWith("[ 6 4 R ", Assert.Single(annotations.Diagnostics,
            d => d.Code == QpdfDiagnosticCode.ObjectListing).Message, StringComparison.Ordinal);
        Assert.Contains("/Fields [ 6 4 R ", listing, StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task OverlayUpdatesAFormInADirectTrailerCatalog()
    {
        string input = Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "forms-1page.pdf")));
        byte[] primary = Encoding.ASCII.GetBytes(input.Replace("/Root 1 0 R",
            "/Root << /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /NeedAppearances true >> >>",
            StringComparison.Ordinal));
        InMemoryQpdfHost host = Host(primary);
        host.AddInput(QpdfPath.From("b.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "overlay-widget-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Execute(host, "job",
            "\"overlay\":{\"file\":\"b.pdf\"},\"check\":\"\",\"showObject\":\"trailer\"", [], QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        string trailer = Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message;
        Assert.Contains("/Root << /AcroForm << /DR ", trailer, StringComparison.Ordinal);
        Assert.Contains("/Fields [ 6 0 R ", trailer, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.FormStructureWarning);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("overlay", "Fx0", "Fx1")]
    [InlineData("underlay", "Fx1", "Fx0")]
    public async Task LayerInspectionWithoutCheckReturnsTheTransformedStream(string role, string first, string second)
    {
        InMemoryQpdfHost host = Host(Fixture());
        host.AddInput(QpdfPath.From("b.pdf"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Execute(host, "job", "\"" + role
            + "\":{\"file\":\"b.pdf\"},\"showObject\":\"11\",\"filteredStreamData\":\"\"", [], QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(result.OutputBytes.HasValue);
        string content = Encoding.ASCII.GetString(result.OutputBytes.GetValueOrDefault().Span);
        int firstPlacement = content.IndexOf("/" + first + " Do", StringComparison.Ordinal);
        int secondPlacement = content.IndexOf("/" + second + " Do", StringComparison.Ordinal);
        Assert.True(firstPlacement >= 0 && secondPlacement > firstPlacement, content);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task OverlayHandleLimitStopsInspectionAndClosesPrimary()
    {
        InMemoryQpdfHost host = Host(Fixture());
        host.AddInput(QpdfPath.From("b.pdf"), Fixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Execute(host, "job",
            "\"overlay\":{\"file\":\"b.pdf\"},\"check\":\"\",\"showNpages\":\"\"", [],
            QpdfLimits.Default with { MaxOpenHandles = 1 });
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, result.Counters.OpenHandles);
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
    [InlineData("job")]
    [InlineData("argv")]
    [InlineData("mixed")]
    public async Task TransformationCeilingsPreserveExistingTargets(string route)
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
        QpdfLimits limits = generous with { MaxMemoryBytes = control.Counters.PeakMemoryBytes / 2L };
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

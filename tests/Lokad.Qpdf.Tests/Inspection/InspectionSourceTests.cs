using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Operations;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class InspectionSourceTests
{
    [Theory]
    [InlineData("missing", false, 2)]
    [InlineData("null", false, 2)]
    [InlineData("7 0 R", false, 2)]
    [InlineData("7 1 R", false, 2)]
    [InlineData("7 0 R", true, 2)]
    [InlineData("7 0 R", false, 1)]
    public async Task InheritedInspectionValuesRetainNullFallbackSharingAndOwnGenerations(string empty, bool job, int version)
    {
        string Entry(string name) => empty == "missing" ? "" : " /" + name + " " + empty;
        byte[] input = PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 3 /MediaBox [0 0 100 200] /CropBox [0 0 100 200] /Resources << /ProcSet [/PDF] >> /Rotate 90 >>", null),
            (3, "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R 5 0 R 6 0 R] /Count 3 /CropBox [1 2 99 198] /Resources null /Rotate null >>", null),
            (4, "<< /Type /Page /Parent 3 0 R /Rotate 180" + Entry("MediaBox") + " >>", null),
            (5, "<< /Type /Page /Parent 3 0 R" + Entry("CropBox") + Entry("Resources") + " >>", null),
            (6, "<< /Type /Page /Parent 3 0 R /MediaBox 8 7 R /CropBox 9 9 R /Resources 10 5 R /Rotate 11 2 R >>", null),
            (7, "null", null), (8, "[0 0 200 400]", null), (9, "[2 3 198 397]", null),
            (10, "<< /ProcSet [/PDF /Text] >>", null), (11, "270", null)], 1,
            new Dictionary<int, int> { [8] = 7, [9] = 9, [10] = 5, [11] = 2 });
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", json = version.ToString(), jsonKey = new[] { "pages", "qpdf" }, pages = new[] { new { file = ".", range = "1-z" } } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1-z", "--", "--json=" + version, "--json-key=pages", "--json-key=" + (version == 2 ? "qpdf" : "objects")]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(0L, host.OpenLeaseCount);
        using JsonDocument report = JsonDocument.Parse(result.OutputText ?? throw new InvalidOperationException());
        JsonElement table = version == 2 ? report.RootElement.GetProperty("qpdf")[1] : report.RootElement.GetProperty("objects");
        JsonElement Resolve(JsonElement value) => value.ValueKind != JsonValueKind.String ? value : version == 2
            ? table.GetProperty("obj:" + value.GetString()).GetProperty("value") : table.GetProperty(value.GetString() ?? throw new InvalidOperationException());
        JsonElement[] pages = report.RootElement.GetProperty("pages").EnumerateArray().Select(p => Resolve(p.GetProperty("object"))).ToArray();
        Assert.Equal(3, pages.Length);
        for (int index = 0; index < 2; index++)
        {
            Assert.Equal(new[] { 0, 0, 100, 200 }, Resolve(pages[index].GetProperty("/MediaBox")).EnumerateArray().Select(v => v.GetInt32()));
            Assert.Equal(new[] { 1, 2, 99, 198 }, Resolve(pages[index].GetProperty("/CropBox")).EnumerateArray().Select(v => v.GetInt32()));
            Assert.Equal("/PDF", Assert.Single(Resolve(pages[index].GetProperty("/Resources")).GetProperty("/ProcSet").EnumerateArray()).GetString());
            Assert.Equal(index == 0 ? 180 : 90, Resolve(pages[index].GetProperty("/Rotate")).GetInt32());
        }
        Assert.Equal(pages[0].GetProperty("/MediaBox").GetString(), pages[1].GetProperty("/MediaBox").GetString());
        Assert.Equal(pages[0].GetProperty("/CropBox").GetString(), pages[1].GetProperty("/CropBox").GetString());
        Assert.Equal("8 7 R", pages[2].GetProperty("/MediaBox").GetString());
        Assert.Equal("9 9 R", pages[2].GetProperty("/CropBox").GetString());
        Assert.Equal("10 5 R", pages[2].GetProperty("/Resources").GetString());
        Assert.Equal("11 2 R", pages[2].GetProperty("/Rotate").GetString());
        Assert.Equal(new[] { 0, 0, 200, 400 }, Resolve(pages[2].GetProperty("/MediaBox")).EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal(new[] { 2, 3, 198, 397 }, Resolve(pages[2].GetProperty("/CropBox")).EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal(new[] { "/PDF", "/Text" }, Resolve(pages[2].GetProperty("/Resources")).GetProperty("/ProcSet").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(270, Resolve(pages[2].GetProperty("/Rotate")).GetInt32());
    }

    private sealed class ClosureHost(InMemoryQpdfHost inner, IMemoryBudget budget, RecordingMemoryBudget root,
        string outcome, CancellationTokenSource cancellation) : IQpdfHost, IQpdfMemoryHost
    {
        internal bool OtherClosed { get; private set; }
        internal bool OtherWasClosedAtThird { get; private set; }
        internal long ReservedAtThird { get; private set; }
        public IMemoryBudget MemoryBudget => budget;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            if (path.Value == "other") { OtherClosed = false; }
            if (path.Value == "third")
            {
                OtherWasClosedAtThird = OtherClosed; ReservedAtThird = root.ReservedBytes;
                if (outcome == "failure") { throw new QpdfHostException(path, QpdfHostFailureReason.NotFound); }
                if (outcome == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            }
            IQpdfInput input = await inner.OpenInputAsync(path, token);
            return path.Value == "other" ? new ObservedInput(input, this) : input;
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => inner.CreateOutputAsync(path, token);
        private sealed class ObservedInput(IQpdfInput input, ClosureHost host) : IQpdfInput
        {
            public QpdfPath Path => input.Path;
            public string Identity => input.Identity;
            public long Length => input.Length;
            public ValueTask<int> ReadAsync(long offset, Memory<byte> target, CancellationToken token)
                => input.ReadAsync(offset, target, token);
            public async ValueTask DisposeAsync()
            { await input.DisposeAsync(); host.OtherClosed = true; }
        }
    }

    private static byte[] Fixture(string shape, string marker)
        => Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(SplitPageMembershipTests.Fixture(shape, false))
            .Replace("(A", "(" + marker, StringComparison.Ordinal));

    private static ValueTask<QpdfExecutionResult> Execute(InMemoryQpdfHost host, string route,
        IReadOnlyList<(string Path, string Range)> groups, string? collation,
        QpdfLimits limits, bool details, string? selector)
    {
        Dictionary<string, object> job = new()
        {
            ["inputFile"] = "input", ["pages"] = groups.Select(g => new { file = g.Path, range = g.Range }).ToArray(),
        };
        List<string> args = ["input", "--pages"];
        foreach ((string path, string range) in groups) { args.Add(path); args.Add(range); }
        args.Add("--");
        if (details) { args.Add("--show-pages"); job["showPages"] = true; }
        else if (selector is null) { args.Add("--show-npages"); job["showNpages"] = true; }
        if (selector is not null) { args.Add("--show-object=" + selector); job["showObject"] = selector; }
        if (collation is not null) { args.Add("--collate=" + collation); job["collate"] = collation; }
        QpdfExecutionOptions options = new(limits);
        if (route == "mixed" && selector is not null) { job["showObject"] = "trailer"; }
        string text = JsonSerializer.Serialize(job);
        if (route == "job") { return Qpdf.ExecuteJobAsync(host, text, options, CancellationToken.None); }
        if (route == "mixed")
        {
            host.AddInput(QpdfPath.From("settings"), Encoding.UTF8.GetBytes(text), QpdfInputOwnership.Transfer);
            args = selector is null ? ["--job-json-file=settings"] : ["--job-json-file=settings", "--show-object=" + selector];
        }
        return Qpdf.ExecuteAsync(host, new(args), options, CancellationToken.None);
    }

    private static QpdfObject Get(QpdfObject value, string key)
    {
        Assert.True(Assert.IsType<PdfDictionary>(value).TryGet(PdfName.FromAscii(key), out QpdfObject result));
        return result;
    }

    public static IEnumerable<object[]> SelectionCases()
    {
        foreach (string shape in new[] { "direct", "indirect", "shared", "action" })
        foreach (string selection in new[] { "combined", "groups", "reverse", "duplicate", "interleaved", "collated", "omitted", "zero", "overlap" })
        { yield return [shape, selection]; }
    }

    [Theory]
    [MemberData(nameof(SelectionCases))]
    public async Task SelectedTargetsAndAliasesUseTheCompleteLogicalSource(string shape, string selection)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(shape, "P"), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("other"), Fixture(shape, "A"), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("third"), Fixture(shape, "B"), QpdfInputOwnership.Transfer);
        (string Path, string Range)[] groups = selection switch
        {
            "combined" => [("other", "1-3")], "groups" => [("other", "1"), ("other", "2,3")],
            "reverse" => [("other", "2,3"), ("other", "1")], "duplicate" => [("other", "1,1,3")],
            "interleaved" => [("other", "1"), ("third", "1"), ("other", "2,3")],
            "collated" => [("other", "1,3"), (".", "2"), ("other", "2")],
            "omitted" => [("other", "1,3")], "zero" => [("other", "1,3"), ("other", "2")],
            _ => [("other", "1,3"), ("other", "2"), ("other", "2")],
        };
        string? collation = selection switch { "collated" => "1", "zero" => "1,0", "overlap" => "1,0,1", _ => null };
        string[] expected = selection switch
        {
            "reverse" => ["A2", "A3", "A1"], "duplicate" => ["A1", "A1", "A3"],
            "interleaved" => ["A1", "B1", "A2", "A3"], "collated" => ["A1", "P2", "A2", "A3"],
            "omitted" or "zero" => ["A1", "A3"], "overlap" => ["A1", "A2", "A3"], _ => ["A1", "A2", "A3"],
        };
        QpdfLimits limits = QpdfLimits.Default with { MaxOpenHandles = 2 };
        QpdfPath input = QpdfPath.From("input");
        CheckPlan plan = new(input, QpdfRecoveryMode.BoundedRepair)
        {
            Sources = groups.Select(group => new PdfPageSource(QpdfPath.From(group.Path), PdfPageSelectionOptions.FromRange(group.Range))).ToArray(),
            Collation = collation is null ? null : new PdfPageCollation(collation.Split(',').Select(int.Parse).ToArray()),
            ShowPages = true,
        };
        ResourceGovernor governor = new(limits, CancellationToken.None, host);
        DiagnosticBag diagnostics = new(governor);
        try
        {
            await using QpdfDocument document = await QpdfDocument.OpenInspectionCore(host, input, null, limits, governor,
                diagnostics, plan.Recovery, CancellationToken.None);
            using InspectionGraphPreparation.InspectionGraph graph = Assert.IsType<InspectionGraphPreparation.InspectionGraph>(
                await InspectionGraphPreparation.PrepareInspectionGraphAsync(host, document, plan, limits,
                    governor, diagnostics, CancellationToken.None));
            Assert.Empty(diagnostics.Entries);
            IIndirectResolver resolver = document.Engine;
            QpdfObject Resolve(QpdfObject value) => PageInheritance.Resolve(resolver, value, limits, governor);
            string Marker(QpdfObject value) => Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(Resolve(value), "Marker")).ValueSpan);

            PdfReference[] references = document.PageReferences.Select(page => Assert.IsType<PdfReference>(page.Source)).ToArray();
            Assert.Equal(expected, references.Select(Marker));
            Assert.Equal(references.Length, references.Distinct().Count());
            List<QpdfObject> sharedDestinations = [];
            for (int index = 0; index < references.Length; index++)
            {
                QpdfObject page = Resolve(references[index]);
                Assert.Same(document.PageReferences[index].Page, page);
                QpdfObject annotation = Resolve(Assert.IsType<PdfArray>(Resolve(Get(page, "Annots")))[0]);
                QpdfObject destinationValue = shape == "action" ? Get(Resolve(Get(annotation, "A")), "D") : Get(annotation, "Dest");
                PdfArray destination = Assert.IsType<PdfArray>(Resolve(destinationValue));
                string marker = expected[index];
                int targetPage = shape == "shared" ? 3 : (marker[1] - '0') % 3 + 1;
                string target = marker[..1] + targetPage.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (expected.Contains(target))
                {
                    Assert.Equal(target, Marker(destination[0]));
                    Assert.Contains(Assert.IsType<PdfReference>(destination[0]), references);
                }
                else { Assert.IsType<PdfNull>(Resolve(destination[0])); }
                if (shape == "shared" && marker[0] == 'A') { sharedDestinations.Add(destinationValue); }
            }
            if (shape == "shared") { Assert.Single(sharedDestinations.Distinct()); }
        }
        finally { governor.CompleteStorage(); }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.Equal(0L, governor.Snapshot().OpenHandles);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    public async Task PageListingPairsResolveThroughThePublicSelector(string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("shared", "P"), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("other"), Fixture("shared", "A"), QpdfInputOwnership.Transfer);
        (string, string)[] groups = [("other", "1,1,3")];
        QpdfLimits limits = QpdfLimits.Default with { MaxOpenHandles = 2 };
        QpdfExecutionResult listed = await Execute(host, route, groups, null, limits, true, null);
        Assert.Equal(QpdfExitCode.Success, listed.ExitCode);
        string[] pages = listed.Diagnostics.Where(d => d.Code == QpdfDiagnosticCode.PageListing && d.Message.StartsWith("page ", StringComparison.Ordinal))
            .Select(d => d.Message[(d.Message.IndexOf(':') + 2)..]).ToArray();
        Assert.Equal(3, pages.Length);
        Assert.Equal(3, pages.Distinct().Count());
        string selector = pages[1][..^2].Replace(' ', ',');
        Assert.True(int.Parse(selector.AsSpan(0, selector.IndexOf(',')), System.Globalization.CultureInfo.InvariantCulture) > 19);
        QpdfExecutionResult shown = await Execute(host, route, groups, null, limits, false, selector);
        Assert.Equal(QpdfExitCode.Success, shown.ExitCode);
        Assert.Contains("/Marker (A1)", Assert.Single(shown.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message,
            StringComparison.Ordinal);
        Assert.Equal(0L, shown.Counters.OpenHandles);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("mixed", false)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    [InlineData("mixed", true)]
    public async Task SecondaryVersionsDoNotOutlivePrivateInspection(string route, bool catalogOverride)
    {
        byte[] bytes = PdfTestDocument.Assemble(PdfTestDocument.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray()), 1);
        if (catalogOverride)
        {
            bytes = PdfTestDocument.Assemble([(1, "<< /Type /Catalog /Pages 2 0 R /Version /2.0 >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1);
        }
        foreach (string range in new[] { "1", "999" })
        {
            QpdfExecutionResult[] results = new QpdfExecutionResult[2];
            for (int provider = 0; provider < results.Length; provider++)
            {
                using InMemoryQpdfHost host = new();
                host.AddInput(QpdfPath.From("input"), bytes, QpdfInputOwnership.Copy);
                host.AddInput(QpdfPath.From("other"), bytes, QpdfInputOwnership.Copy);
                results[provider] = await Execute(host, route, [(provider == 0 ? "." : "other", range)], null, QpdfLimits.Default, false, null);
                Assert.Equal(0L, host.OpenLeaseCount);
            }
            Assert.Equal(range == "1" ? QpdfExitCode.Success : QpdfExitCode.Error, results[1].ExitCode);
            Assert.Equal(results[0].ExitCode, results[1].ExitCode);
            Assert.Equal(results[0].Diagnostics.Select(d => (d.Code, d.Severity, d.Message)),
                results[1].Diagnostics.Select(d => (d.Code, d.Severity, d.Message)));
            Assert.Equal(results[0].OutputText, results[1].OutputText);
            Assert.Equal(results[0].Counters.LiveMemoryBytes, results[1].Counters.LiveMemoryBytes);
        }
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    public async Task ZeroCollationValidatesWithoutKeepingSourceRoots(string route)
    {
        using InMemoryQpdfHost host = new();
        foreach (string path in new[] { "input", "other", "third" })
        { host.AddInput(QpdfPath.From(path), Fixture("direct", "A"), QpdfInputOwnership.Transfer); }
        QpdfLimits limits = QpdfLimits.Default with { MaxOpenHandles = 2 };
        QpdfExecutionResult empty = await Execute(host, route, [("other", "1-3")], "0", limits, false, null);
        Assert.Equal(QpdfExitCode.Success, empty.ExitCode);
        Assert.Equal("input: 0", Assert.Single(empty.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message);
        QpdfExecutionResult later = await Execute(host, route, [("other", "1-3"), ("third", "1")], "0,1", limits, false, null);
        Assert.Equal(QpdfExitCode.Success, later.ExitCode);
        Assert.Equal("input: 1", Assert.Single(later.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message);
        QpdfExecutionResult invalid = await Execute(host, route, [("other", "99")], "0", limits, false, null);
        Assert.Equal(QpdfExitCode.Error, invalid.ExitCode);
        Assert.DoesNotContain(invalid.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    public async Task EverySharedAdmissionRefusalRefundsCompletedSourceGraphs(string route)
    {
        byte[] content = new byte[131072]; content.AsSpan().Fill((byte)' '); "q Q"u8.CopyTo(content);
        byte[] source = PdfTestDocument.Assemble(PdfTestDocument.SinglePage("<< /Length " + content.Length + " >>", content), 1);
        async Task<(QpdfExecutionResult Result, int FirstGrowth, int LastGrowth)> Run(RecordingMemoryBudget budget)
        {
            using (IMemoryScope owner = budget.CreateScope())
            {
                using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
                host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Copy);
                host.AddInput(QpdfPath.From("other"), source, QpdfInputOwnership.Copy);
                host.AddInput(QpdfPath.From("third"), source, QpdfInputOwnership.Copy);
                await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("prior"), CancellationToken.None))
                { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
                const string job = "{\"inputFile\":\"input\",\"pages\":[{\"file\":\"other\",\"range\":\"1\"},{\"file\":\"third\",\"range\":\"1\"}],\"showNpages\":true}";
                if (route == "mixed")
                { host.AddInput(QpdfPath.From("settings"), Encoding.UTF8.GetBytes(job), QpdfInputOwnership.Transfer); }
                int firstGrowth = budget.GrowthCalls;
                QpdfExecutionResult result = route == "job"
                    ? await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None)
                    : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(route == "mixed"
                        ? ["--job-json-file=settings"]
                        : ["input", "--pages", "other", "1", "third", "1", "--", "--show-npages"]), CancellationToken.None);
                int lastGrowth = budget.GrowthCalls;
                Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
                Assert.True(host.TryGetOutput(QpdfPath.From("prior"), out byte[] retained));
                Assert.Equal("prior"u8.ToArray(), retained);
                if (result.ExitCode == QpdfExitCode.Success)
                { Assert.Equal("input: 2", Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount).Message); }
                else
                {
                    Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
                    Assert.Null(result.OutputText);
                }
                return (result, firstGrowth, lastGrowth);
            }
        }
        RecordingMemoryBudget control = new(16777216);
        (QpdfExecutionResult success, int setupGrowths, int executionGrowths) = await Run(control);
        Assert.Equal(QpdfExitCode.Success, success.ExitCode); Assert.Equal(0L, control.ReservedBytes);
        Assert.True(executionGrowths > setupGrowths);
        for (int refusal = setupGrowths + 1; refusal <= executionGrowths; refusal++)
        {
            RecordingMemoryBudget budget = new(16777216) { RefuseOnGrowth = refusal };
            (QpdfExecutionResult failure, int setup, _) = await Run(budget);
            Assert.Equal(setupGrowths, setup); Assert.Equal(QpdfExitCode.Error, failure.ExitCode);
            Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
        }
    }

    [Theory]
    [InlineData("argv", "success")]
    [InlineData("job", "success")]
    [InlineData("argv", "failure")]
    [InlineData("job", "failure")]
    [InlineData("argv", "malformed")]
    [InlineData("job", "malformed")]
    [InlineData("argv", "cancel")]
    [InlineData("job", "cancel")]
    public async Task CompletedSourceClosesWithBorrowedPayloadStillFunded(string route, string outcome)
    {
        byte[] content = new byte[1048576]; content.AsSpan().Fill((byte)' '); "q Q"u8.CopyTo(content);
        byte[] source = PdfTestDocument.Assemble(PdfTestDocument.SinglePage("<< /Length " + content.Length + " >>", content), 1);
        RecordingMemoryBudget root = new(33554432);
        using (IMemoryScope owner = root.CreateScope())
        {
            using InMemoryQpdfHost inner = new(QpdfLimits.Default, null, owner);
            foreach (string path in new[] { "input", "other", "third" })
            { inner.AddInput(QpdfPath.From(path), path == "third" && outcome == "malformed"
                ? "%PDF-1.4\ntruncated"u8.ToArray() : source, QpdfInputOwnership.Copy); }
            long baseline = root.ReservedBytes;
            using CancellationTokenSource cancellation = new();
            ClosureHost host = new(inner, owner, root, outcome, cancellation);
            QpdfExecutionResult result = route == "job"
                ? await Qpdf.ExecuteJobAsync(host,
                    "{\"inputFile\":\"input\",\"pages\":[{\"file\":\"other\",\"range\":\"1\"},{\"file\":\"third\",\"range\":\"1\"}],\"showNpages\":true}", cancellation.Token)
                : await Qpdf.ExecuteAsync(host, new(["input", "--pages", "other", "1", "third", "1", "--", "--show-npages"]), cancellation.Token);
            Assert.True(host.OtherWasClosedAtThird);
            Assert.True(host.ReservedAtThird >= baseline + content.LongLength,
                $"Completed source lost funding: baseline {baseline}, observed {host.ReservedAtThird}, payload {content.LongLength}.");
            Assert.Equal(outcome switch { "success" => QpdfExecutionStatus.Completed,
                "failure" or "malformed" => QpdfExecutionStatus.Failed, _ => QpdfExecutionStatus.Canceled }, result.Status);
            if (outcome != "success") { Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount); }
            Assert.Equal(0L, inner.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0, root.LiveReservations);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task ImportedBinaryResultSurvivesReadersAndHost(string route)
    {
        byte[] content = new byte[1048576];
        for (int index = 0; index < content.Length; index++) { content[index] = (byte)(index % 251); }
        byte[] source = PdfTestDocument.Assemble(PdfTestDocument.SinglePage("<< /Length " + content.Length + " >>", content), 1);
        RecordingMemoryBudget root = new(33554432);
        using (IMemoryScope owner = root.CreateScope())
        {
            QpdfExecutionResult result;
            using (InMemoryQpdfHost inner = new(QpdfLimits.Default, null, owner))
            {
                foreach (string path in new[] { "input", "other", "third" })
                { inner.AddInput(QpdfPath.From(path), source, QpdfInputOwnership.Copy); }
                using CancellationTokenSource cancellation = new();
                ClosureHost host = new(inner, owner, root, "success", cancellation);
                QpdfExecutionResult listing = route == "job"
                    ? await Qpdf.ExecuteJobAsync(host,
                        "{\"inputFile\":\"input\",\"pages\":[{\"file\":\"other\",\"range\":\"1\"},{\"file\":\"third\",\"range\":\"1\"}],\"showPages\":true}", CancellationToken.None)
                    : await Qpdf.ExecuteAsync(host, new(["input", "--pages", "other", "1", "third", "1", "--", "--show-pages"]), CancellationToken.None);
                Assert.Equal(QpdfExitCode.Success, listing.ExitCode);
                string reference = listing.Diagnostics.First(d => d.Code == QpdfDiagnosticCode.PageListing
                    && d.Message.StartsWith("    ", StringComparison.Ordinal)).Message.Trim();
                string selector = reference[..^2].Replace(' ', ',');
                result = route == "job"
                    ? await Qpdf.ExecuteJobAsync(host,
                        JsonSerializer.Serialize(new { inputFile = "input", pages = new[] { new { file = "other", range = "1" },
                            new { file = "third", range = "1" } }, showObject = selector, rawStreamData = true }), CancellationToken.None)
                    : await Qpdf.ExecuteAsync(host, new(["input", "--pages", "other", "1", "third", "1", "--",
                        "--show-object=" + selector, "--raw-stream-data"]), CancellationToken.None);
                Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
                Assert.True(host.OtherWasClosedAtThird); Assert.Equal(0L, inner.OpenLeaseCount);
            }
            Assert.True(result.OutputBytes is ReadOnlyMemory<byte> output && output.Span.SequenceEqual(content), "Imported payload changed after source/host closure.");
            Assert.True(root.ReservedBytes >= content.LongLength, "Escaped payload lost its admission.");
        }
        Assert.Equal(0L, root.ReservedBytes); Assert.Equal(0, root.LiveReservations);
    }
}

using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Writing;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class InspectionSourceTests
{
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
        string text = JsonSerializer.Serialize(job);
        if (route == "job") { return Qpdf.ExecuteJobAsync(host, text, options, CancellationToken.None); }
        if (route == "mixed")
        {
            host.AddInput(QpdfPath.From("settings"), Encoding.UTF8.GetBytes(text), QpdfInputOwnership.Transfer);
            args = ["--job-json-file=settings"];
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
        foreach (string route in new[] { "argv", "job", "mixed" }) { yield return [shape, selection, route]; }
    }

    [Theory]
    [MemberData(nameof(SelectionCases))]
    public async Task SelectedTargetsAndAliasesUseTheCompleteLogicalSource(string shape, string selection, string route)
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
        QpdfExecutionResult result = await Execute(host, route, groups, collation, limits, true, null);
        Assert.True(result.ExitCode == QpdfExitCode.Success,
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        string[] pageReferences = result.Diagnostics.Where(d => d.Code == QpdfDiagnosticCode.PageListing
                && d.Message.StartsWith("page ", StringComparison.Ordinal))
            .Select(d => d.Message[(d.Message.IndexOf(':') + 2)..]).ToArray();
        ResourceGovernor parsing = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope parsed = ParserMemoryScope.Create(parsing);
        Dictionary<string, QpdfObject> objects = new(StringComparer.Ordinal);
        async Task<QpdfObject> Read(string reference)
        {
            if (objects.TryGetValue(reference, out QpdfObject? known)) { return known; }
            string selector = reference[..^2].Replace(' ', ',');
            QpdfExecutionResult shown = await Execute(host, route, groups, collation, limits, false, selector);
            Assert.True(shown.ExitCode == QpdfExitCode.Success, string.Join("; ", shown.Diagnostics.Select(d => d.Message)));
            string body = Assert.Single(shown.Diagnostics, d => d.Code == QpdfDiagnosticCode.ObjectListing).Message;
            PdfParser parser = new(QpdfLimits.Default, parsing, new DiagnosticBag(parsing), null);
            QpdfObject value = parser.ParseValue(new PdfTokenizer(Encoding.Latin1.GetBytes(body), CancellationToken.None), 0, parsed);
            objects.Add(reference, value); return value;
        }
        async Task<QpdfObject> Resolve(QpdfObject value)
        {
            while (value is PdfReference reference)
            { value = await Read(reference.ObjectNumber + " " + reference.Generation + " R"); }
            return value;
        }
        async Task<string> Marker(QpdfObject value)
            => Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(await Resolve(value), "Marker")).ValueSpan);
        List<string> markers = [];
        foreach (string reference in pageReferences) { markers.Add(await Marker(await Read(reference))); }
        Assert.Equal(expected, markers);
        Assert.Equal(pageReferences.Length, pageReferences.Distinct().Count());
        for (int index = 0; index < pageReferences.Length; index++)
        {
            QpdfObject page = await Read(pageReferences[index]);
            QpdfObject annotation = await Resolve(Assert.IsType<PdfArray>(await Resolve(Get(page, "Annots")))[0]);
            PdfArray destination = Assert.IsType<PdfArray>(await Resolve(shape == "action"
                ? Get(await Resolve(Get(annotation, "A")), "D") : Get(annotation, "Dest")));
            string marker = expected[index];
            string target = marker[..1] + (shape == "shared" ? "3" : (int.Parse(marker[1..], System.Globalization.CultureInfo.InvariantCulture) % 3 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (expected.Contains(target))
            {
                Assert.Equal(target, await Marker(destination[0]));
                PdfReference targetReference = Assert.IsType<PdfReference>(destination[0]);
                Assert.Contains(targetReference.ObjectNumber + " " + targetReference.Generation + " R", pageReferences);
            }
            else { Assert.IsType<PdfNull>(await Resolve(destination[0])); }
        }
        if (shape == "shared")
        {
            List<QpdfObject> sameSource = [];
            for (int index = 0; index < pageReferences.Length; index++)
            {
                if (expected[index][0] != 'A') { continue; }
                QpdfObject page = await Read(pageReferences[index]);
                QpdfObject annotation = await Resolve(Assert.IsType<PdfArray>(await Resolve(Get(page, "Annots")))[0]);
                sameSource.Add(Get(annotation, "Dest"));
            }
            Assert.Single(sameSource.Distinct());
        }
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
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
        byte[] bytes = CheckTests.Assemble(CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray()), 1);
        if (catalogOverride)
        {
            bytes = CheckTests.Assemble([(1, "<< /Type /Catalog /Pages 2 0 R /Version /2.0 >>", null),
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
        byte[] source = CheckTests.Assemble(CheckTests.SinglePage("<< /Length " + content.Length + " >>", content), 1);
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
        byte[] source = CheckTests.Assemble(CheckTests.SinglePage("<< /Length " + content.Length + " >>", content), 1);
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
        byte[] source = CheckTests.Assemble(CheckTests.SinglePage("<< /Length " + content.Length + " >>", content), 1);
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

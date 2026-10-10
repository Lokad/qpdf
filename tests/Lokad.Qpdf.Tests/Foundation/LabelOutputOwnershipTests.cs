using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Writing;
using Lokad.Qpdf.Writing;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class LabelOutputOwnershipTests(ITestOutputHelper log)
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");
    private static readonly byte[] Prior = [4, 8, 15, 16, 23, 42];
    private static readonly Dictionary<string, long> Sizes = [];
    private static readonly Dictionary<char, byte[]> SourceFixtures = [];

    private sealed class Host(string outcome) : IQpdfHost
    {
        internal readonly InMemoryQpdfHost Inner = new();
        internal readonly List<QpdfPath> Created = [];
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _paused;
        internal Task Entered => _entered.Task;
        internal void Resume() => _resume.TrySetResult();
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => Inner.OpenInputAsync(path, token);
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            Created.Add(path);
            if (!_paused)
            {
                _paused = true; _entered.TrySetResult(); await _resume.Task.WaitAsync(token);
                if (outcome == "create-fault") { throw new QpdfHostException(path, QpdfHostFailureReason.WriteFailed); }
                if (outcome == "host-cancel") { throw new OperationCanceledException(new CancellationToken(true)); }
            }
            return new Transaction(await Inner.CreateOutputAsync(path, token), outcome);
        }
        private sealed class Transaction(IQpdfOutputTransaction inner, string outcome) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
                => outcome == "write-fault" ? throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed) : inner.WriteAsync(bytes, token);
            public ValueTask CommitAsync(CancellationToken token)
                => outcome == "commit-fault" ? throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed) : inner.CommitAsync(token);
            public ValueTask DiscardAsync(CancellationToken token) => inner.DiscardAsync(token);
            public async ValueTask DisposeAsync()
            {
                try { if (outcome == "close-fault") { throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed); } }
                finally { await inner.DisposeAsync(); }
            }
        }
    }

    private sealed class Observation(string id, ResourceGovernor governor, object? result,
        WeakReference running, List<object> aliases, List<string> missing, List<byte[]> published)
    {
        internal readonly string Id = id;
        internal readonly ResourceGovernor Governor = governor;
        internal readonly WeakReference Running = running;
        internal readonly List<object> Aliases = aliases;
        internal readonly List<string> Missing = missing;
        internal readonly List<byte[]> Published = published;
        internal object? Result = result;
    }

    private static T? Find<T>(object value, HashSet<object> seen, int depth) where T : class
    {
        if (depth > 96 || !seen.Add(value)) { return null; }
        if (value is T found) { return found; }
        if (value is Task task)
        {
            object? state = task.GetType().GetField("StateMachine", Fields)?.GetValue(task);
            return state is null ? null : Find<T>(state, seen, depth + 1);
        }
        foreach (FieldInfo field in value.GetType().GetFields(Fields))
        { if (field.GetValue(value) is T direct) { return direct; } }
        foreach (FieldInfo field in value.GetType().GetFields(Fields))
        {
            if (field.GetValue(value) is object child && (child is Task || child.GetType().IsValueType))
            { T? nested = Find<T>(child, seen, depth + 1); if (nested is not null) { return nested; } }
        }
        return null;
    }

    private static PdfDictionary? FindTree(object value, HashSet<object> seen, int depth)
    {
        if (depth > 96 || !seen.Add(value) || value is IQpdfHost or ResourceGovernor) { return null; }
        if (value is PdfDictionary catalog && catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject tree) && tree is PdfDictionary labels)
        { return labels; }
        if (value is PdfDictionary numberTree && numberTree.TryGet(PdfName.FromAscii("Nums"), out QpdfObject rawNumbers)
            && rawNumbers is PdfArray numbers && numbers.Count > 0)
        { return numberTree; }
        if (value is Task task)
        {
            object? state = task.GetType().GetField("StateMachine", Fields)?.GetValue(task);
            return state is null ? null : FindTree(state, seen, depth + 1);
        }
        if (value is IEnumerable values && value is not string and not byte[])
        {
            foreach (object? item in values)
            { if (item is not null && FindTree(item, seen, depth + 1) is PdfDictionary found) { return found; } }
        }
        Type type = value.GetType();
        if (!type.IsValueType && !(type.Namespace?.StartsWith("Lokad.Qpdf", StringComparison.Ordinal) ?? false)) { return null; }
        FieldInfo[] fields = type.GetFields(Fields);
        // Observe the executing writer before outer preliminary-reader values.
        // Whole-file commands also keep an earlier borrowed source tree.
        foreach (FieldInfo field in fields)
        {
            if (field.GetValue(value) is object child && (child is Task || child.GetType().IsValueType && !child.GetType().IsPrimitive && !child.GetType().IsEnum))
            { PdfDictionary? found = FindTree(child, seen, depth + 1); if (found is not null) { return found; } }
        }
        foreach (FieldInfo field in fields)
        {
            if (field.GetValue(value) is object child && child is not string && !child.GetType().IsPrimitive)
            { PdfDictionary? found = FindTree(child, seen, depth + 1); if (found is not null) { return found; } }
        }
        return null;
    }

    private static long Physical(object value)
    {
        string key = value.GetType().FullName + (value is Array a ? ":" + a.Length : value is string s ? ":" + s.Length : "");
        if (Sizes.TryGetValue(key, out long bytes)) { return bytes; }
        if (value is not Array and not string) { bytes = PdfVersionOwnershipTests.MeasurePhysicalSize(value.GetType()); }
        else
        {
            using IDisposable capture = OwnershipBatch.CaptureWithoutCollection();
            object[] roots = new object[32];
            object Allocate() => value is Array array ? array.Clone() : new string('x', ((string)value).Length);
            for (int i = 0; i < roots.Length; i++) { roots[i] = Allocate(); }
            Array.Clear(roots); long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < roots.Length; i++) { roots[i] = Allocate(); }
            long total = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, total % roots.Length); bytes = total / roots.Length; GC.KeepAlive(roots);
        }
        Sizes.Add(key, bytes); return bytes;
    }

    private static long BackingPhysical(object value)
    {
        long bytes = Physical(value);
        foreach (FieldInfo field in value.GetType().GetFields(Fields))
        {
            if (field.GetValue(value) is Array array && array.Length > 0) { bytes += Physical(array); }
            else if (field.Name == "_dictionaryKeys" && field.GetValue(value) is object keys) { bytes += Physical(keys); }
        }
        return bytes;
    }

    private static bool Funded(ResourceGovernor governor, object value, long bytes)
    {
        object? owner = typeof(ResourceGovernor).GetField("_valueOwners", Fields)?.GetValue(governor);
        object? segment = owner?.GetType().GetField("_segments", Fields)?.GetValue(owner);
        while (segment is not null)
        {
            if (segment.GetType().GetField("Values", Fields)?.GetValue(segment) is ConditionalWeakTable<object, RetainedValueOwners.Receipt> values
                && values.TryGetValue(value, out RetainedValueOwners.Receipt? receipt)) { return receipt.Bytes >= bytes; }
            segment = segment.GetType().GetField("Next", Fields)?.GetValue(segment);
        }
        return false;
    }

    private static void Graph(PdfDictionary tree, ResourceGovernor governor, List<object> aliases, List<string> missing)
    {
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        void Add(object value, long minimum)
        {
            if (!seen.Add(value)) { return; }
            aliases.Add(value); if (!Funded(governor, value, minimum)) { missing.Add(value.GetType().Name + ":" + minimum); }
        }
        void Visit(QpdfObject value)
        {
            if (seen.Contains(value)) { return; } Add(value, Physical(value));
            switch (value)
            {
                case PdfDictionary dictionary:
                    Add(dictionary.AllocationCarrier, BackingPhysical(dictionary.AllocationCarrier));
                    using (PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator())
                    { while (entries.MoveNext()) { Visit(entries.Current.Key); Visit(entries.Current.Value); } }
                    break;
                case PdfArray array:
                    Add(array.AllocationCarrier, Physical(array.AllocationCarrier));
                    for (int i = 0; i < array.Count; i++) { Visit(array[i]); } break;
                case PdfName name: Add(name.AllocationCarrier, Physical(name.AllocationCarrier)); break;
                case PdfByteString text: Add(text.AllocationCarrier, Physical(text.AllocationCarrier)); break;
                case PdfInteger integer: Add(integer.Text, Physical(integer.Text)); break;
                default: throw new InvalidOperationException("Unexpected label graph value.");
            }
        }
        Visit(tree);
    }

    private static byte[] Fixture() => (byte[])(typeof(LabelTests).GetMethod("BuildDoc", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, [3])
        ?? throw new InvalidOperationException("Missing existing three-page label fixture."));

    private static async Task<byte[]> SourceFixture(char character)
    {
        if (SourceFixtures.TryGetValue(character, out byte[]? known)) { return known; }
        InMemoryQpdfHost host = new(); host.AddInput(Input, Fixture(), QpdfInputOwnership.Copy);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation([Input.Value,
            "--set-page-labels", "1:D/17/" + new string(character, 512), "2:r/1/tail", "--", Output.Value]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        SourceFixtures.Add(character, bytes); return bytes;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<Observation> Complete(string route, string operation, string outcome, char character, bool inherited)
    {
        Host host = new(outcome); host.Inner.AddInput(Input, inherited ? await SourceFixture(character) : Fixture(), QpdfInputOwnership.Copy);
        await using (IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(Output, CancellationToken.None))
        { await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        using CancellationTokenSource cancel = new();
        string prefix = new(character, 512); string[] labels = ["1:D/17/" + prefix, "2:r/1/tail"];
        Task running;
        if (route == "typed")
        {
            PdfPageSource[] sources = operation == "merge"
                ? [new(Input, PdfPageSelectionOptions.FromPages([1])), new(Input, PdfPageSelectionOptions.FromPages([2, 3]))]
                : [new(Input, PdfPageSelectionOptions.FromPages(operation == "whole" ? [1, 2, 3] : [3, 1, 2]))];
            running = Qpdf.SelectPagesAsync(host, sources, Output, new PdfPageMergeOptions
            { Labels = inherited ? [] : [new(1, PdfPageLabelStyle.Decimal, 17, prefix), new(2, PdfPageLabelStyle.LowerRoman, 1, "tail")],
                SplitPages = operation == "split" ? 1 : null }, QpdfLimits.Default, cancel.Token).AsTask();
        }
        else if (route == "job")
        {
            Dictionary<string, object> job = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value };
            if (!inherited) { job["setPageLabels"] = labels; }
            if (operation != "whole")
            { job["pages"] = operation == "merge" ? new[] { new { file = ".", range = "1" }, new { file = Input.Value, range = "2,3" } }
                : new[] { new { file = ".", range = "3,1,2" } }; }
            if (operation == "split") { job["splitPages"] = "1"; }
            running = Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), cancel.Token).AsTask();
        }
        else
        {
            List<string> argv = [Input.Value];
            if (operation != "whole")
            { argv.AddRange(operation == "merge" ? ["--pages", ".", "1", Input.Value, "2,3", "--"] : ["--pages", ".", "3,1,2", "--"]); }
            if (operation == "split") { argv.Add("--split-pages=1"); }
            if (!inherited) { argv.AddRange(["--set-page-labels", .. labels, "--"]); }
            argv.Add(Output.Value);
            running = Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), cancel.Token).AsTask();
        }
        await host.Entered.WaitAsync(TimeSpan.FromSeconds(15));
        ResourceGovernor governor = Find<ResourceGovernor>(running, new(ReferenceEqualityComparer.Instance), 0)
            ?? throw new InvalidOperationException("Missing public-job governor.");
        PdfDictionary? tree = FindTree(running, new(ReferenceEqualityComparer.Instance), 0);
        List<object> aliases = []; List<string> missing = [];
        if (inherited && operation == "whole" && route != "typed")
        {
            // Whole-file pass-through borrows the preliminary reader's values
            // and scopes copied containers. It creates no fresh label values.
            // Bind the scoped observation to the actual writer model; the
            // independent alias test below covers the new label constructors.
            List<NumberedObject> model = Find<List<NumberedObject>>(running, new(ReferenceEqualityComparer.Instance), 0)
                ?? throw new InvalidOperationException("Missing whole-file writer model.");
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(model.Single(item => item.Number == 1).Value);
            Assert.True(catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject labelsTree));
            tree = Assert.IsType<PdfDictionary>(labelsTree);
            List<object> scoped = []; List<string> receiptObservations = [];
            Graph(tree, governor, scoped, receiptObservations);
            long minimum = scoped.Sum(value => value is PdfDictionary dictionary ? Physical(dictionary)
                : value is not Array and not string && value.GetType().Name.StartsWith("PdfNameTable", StringComparison.Ordinal) ? BackingPhysical(value) : Physical(value));
            Assert.True(governor.Snapshot().LiveMemoryBytes >= minimum);
            log.WriteLine("LABEL SCOPED {0}|{1}|{2}|{3} minimum={4} independent-receipts-missing={5}", route, operation, outcome, (int)character, minimum, receiptObservations.Count);
        }
        else if (operation != "split")
        { Assert.NotNull(tree); Graph(tree, governor, aliases, missing); }
        else if (inherited)
        {
            // Split remaps each number tree for its part. Keep the original
            // merged label values used by later parts independently of those
            // temporary trees and the construction list.
            List<(int Index, PdfDictionary Label)> items = Find<List<(int Index, PdfDictionary Label)>>(running,
                new(ReferenceEqualityComparer.Instance), 0) ?? throw new InvalidOperationException("Missing split label values.");
            Assert.NotEmpty(items);
            foreach ((int _, PdfDictionary label) in items) { Graph(label, governor, aliases, missing); }
        }
        if (outcome == "cancel") { cancel.Cancel(); } host.Resume();
        object? result = null;
        try
        {
            await running;
            result = running.GetType().GetProperty("Result", Fields)?.GetValue(running);
            if (route == "typed") { Assert.Equal("success", outcome); Assert.IsType<PdfMultiPageSelectionResult>(result); }
            else
            {
                QpdfExecutionResult command = Assert.IsType<QpdfExecutionResult>(result);
                Assert.Equal(outcome == "success" ? QpdfExecutionStatus.Completed : outcome is "cancel" or "host-cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, command.Status);
            }
        }
        catch (OperationCanceledException) { Assert.Equal("typed", route); Assert.Contains(outcome, new[] { "cancel", "host-cancel" }); }
        catch (QpdfHostException) { Assert.Equal("typed", route); Assert.EndsWith("fault", outcome); }
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        if (operation != "split" && outcome != "success" && outcome != "close-fault")
        { Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior); }
        if (outcome == "success" && operation != "split")
        {
            Assert.True(host.Inner.TryGetOutput(Output, out byte[] bytes));
            InMemoryQpdfHost validation = new(); validation.AddInput(Input, bytes, QpdfInputOwnership.Copy);
            Assert.True((await Qpdf.CheckAsync(validation, Input, CancellationToken.None)).Passed);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(validation, Input, CancellationToken.None);
            Assert.Equal(3, document.PageCount);
        }
        List<byte[]> published = [];
        if (outcome == "success")
        {
            Assert.Equal(operation == "split" ? 3 : 1, host.Created.Count);
            int part = 0;
            foreach (QpdfPath path in host.Created)
            {
                Assert.True(host.Inner.TryGetOutput(path, out byte[] bytes)); published.Add(bytes);
                await ValidateLabels(bytes, prefix, operation, inherited, ++part);
            }
        }
        string id = route + "|" + operation + "|" + outcome + "|" + (int)character + "|" + (inherited ? "source" : "explicit");
        log.WriteLine("LABEL JOB {0} aliases={1} missing={2}", id, aliases.Count, missing.Count);
        return new(id, governor, result, new(running), aliases, missing, published);
    }

    private static async Task ValidateLabels(byte[] bytes, string prefix, string operation, bool inherited, int part)
    {
        InMemoryQpdfHost host = new(); host.AddInput(Input, bytes, QpdfInputOwnership.Copy);
        Assert.True((await Qpdf.CheckAsync(host, Input, CancellationToken.None)).Passed);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None);
        Assert.Equal(operation == "split" ? 1 : 3, document.PageCount);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        if (operation == "split" && !inherited)
        { Assert.False(catalog.TryGet(PdfName.FromAscii("PageLabels"), out _)); return; }
        Assert.True(catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject tree));
        Assert.True(Assert.IsType<PdfDictionary>(tree).TryGet(PdfName.FromAscii("Nums"), out QpdfObject raw));
        PdfArray numbers = Assert.IsType<PdfArray>(raw);
        Assert.Equal(operation == "split" ? 2 : inherited && operation == "selection" ? 6 : 4, numbers.Count);
        string expected = inherited && (operation == "selection" || operation == "split" && part != 2) ? "tail" : prefix;
        PdfDictionary label = Assert.IsType<PdfDictionary>(numbers[1]);
        Assert.Equal(0L, Assert.IsType<PdfInteger>(numbers[0]).Value);
        Assert.True(label.TryGet(PdfName.FromAscii("P"), out QpdfObject text));
        byte[] encoded = expected.Contains('\u03a9') ? Encoding.BigEndianUnicode.GetBytes("\ufeff" + expected) : Encoding.Latin1.GetBytes(expected);
        Assert.Equal(encoded, Assert.IsType<PdfByteString>(text).ValueSpan.ToArray());
        Assert.True(label.TryGet(PdfName.FromAscii("S"), out QpdfObject style));
        Assert.Equal(expected == "tail" ? "r" : "D", Assert.IsType<PdfName>(style).DisplayText);
        if (expected != "tail" || operation == "selection" || operation == "split")
        {
            Assert.True(label.TryGet(PdfName.FromAscii("St"), out QpdfObject start));
            Assert.Equal(expected == "tail" ? operation == "split" && part == 3 ? 1L : 2L : 17L, Assert.IsType<PdfInteger>(start).Value);
        }
    }

    private static void Collect(List<Observation> jobs)
    {
        for (int round = 0; round < 4; round++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); foreach (Observation job in jobs) { job.Governor.ReserveMemory(0L); } }
    }

    private static string Remaining(ResourceGovernor governor)
    {
        Dictionary<string, long> counts = [];
        object? owner = typeof(ResourceGovernor).GetField("_valueOwners", Fields)?.GetValue(governor);
        object? segment = owner?.GetType().GetField("_segments", Fields)?.GetValue(owner);
        while (segment is not null)
        {
            if (segment.GetType().GetField("Values", Fields)?.GetValue(segment) is ConditionalWeakTable<object, RetainedValueOwners.Receipt> values)
            { foreach (KeyValuePair<object, RetainedValueOwners.Receipt> value in values) { counts[value.Key.GetType().Name] = counts.GetValueOrDefault(value.Key.GetType().Name) + value.Value.Bytes; } }
            segment = segment.GetType().GetField("Next", Fields)?.GetValue(segment);
        }
        return JsonSerializer.Serialize(counts);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<List<Observation>> CompleteJobs()
    {
        List<Observation> jobs = [];
        foreach (bool inherited in new[] { false, true })
        foreach (string route in new[] { "argv", "job", "typed" })
        foreach (string operation in new[] { "whole", "selection", "merge", "split" })
        foreach (char encoding in new[] { 'p', '\u00e9', '\u03a9' })
        foreach (string outcome in new[] { "success", "create-fault", "write-fault", "commit-fault", "close-fault", "cancel", "host-cancel" })
        { jobs.Add(await Complete(route, operation, outcome, encoding, inherited)); }
        return jobs;
    }

    [Fact]
    public async Task CompletePublicLabelJobsFundGraphsAndRetireAfterAliases()
    {
        List<Observation> jobs = await CompleteJobs(); Collect(jobs);
        List<string> defects = DropAliases(jobs);
        Collect(jobs);
        foreach (Observation job in jobs)
        {
            long live = job.Governor.Snapshot().LiveMemoryBytes;
            log.WriteLine("LABEL FINAL {0} {1}", job.Id, live);
            if (job == jobs[0]) { log.WriteLine("LABEL REMAIN {0}", Remaining(job.Governor)); }
            if (live != 0L) { defects.Add(job.Id + "|final:" + live); }
        }
        Assert.True(defects.Count == 0, string.Join('\n', defects));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<string> DropAliases(List<Observation> jobs)
    {
        List<string> defects = [];
        foreach (Observation job in jobs)
        {
            if (job.Running.IsAlive) { defects.Add(job.Id + "|running-alive"); }
            foreach (string missing in job.Missing) { defects.Add(job.Id + "|" + missing); }
            foreach (object alias in job.Aliases)
            { if (!Funded(job.Governor, alias, Physical(alias))) { defects.Add(job.Id + "|completed:" + alias.GetType().Name); } }
            // The host getter returns caller-owned copies. Their usability
            // survives job cleanup without charging those copies to the job.
            foreach (byte[] published in job.Published)
            { Assert.True(published.AsSpan().StartsWith("%PDF-"u8)); }
            job.Aliases.Clear(); job.Result = null;
        }
        return defects;
    }

    [Fact]
    public async Task LargeTypedPrefixesRejectBeforeAllocatingUnadmittedEncoding()
    {
        string prefix = new('p', 4 * 1024 * 1024);
        List<string> defects = [];
        foreach (bool work in new[] { false, true })
        {
            InMemoryQpdfHost host = new(); host.AddInput(Input, Fixture(), QpdfInputOwnership.Copy);
            await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(Output, CancellationToken.None))
            { await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
            QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 2L * 1024 * 1024, MaxWork = work ? 25000L : QpdfLimits.Default.MaxWork };
            PdfPageMergeOptions options = new() { Labels = [new(1, PdfPageLabelStyle.Decimal, 1, prefix)] };
            PdfPageSource[] sources = [new(Input, PdfPageSelectionOptions.FromPages([1]))];
            long allocated = GC.GetTotalAllocatedBytes(true);
            QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(() => Qpdf.SelectPagesAsync(host, sources, Output, options, limits, CancellationToken.None).AsTask());
            long delta = GC.GetTotalAllocatedBytes(true) - allocated;
            log.WriteLine("LABEL BUDGET {0} allocation={1}", work ? "work" : "memory", delta);
            Assert.Equal(work ? nameof(QpdfLimits.MaxWork) : nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
            if (delta >= prefix.Length) { defects.Add((work ? "work" : "memory") + ":" + delta); }
            Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior);
        }
        Assert.True(defects.Count == 0, "Unadmitted prefix encoding allocated before public rejection: " + string.Join(',', defects));
    }

    [Fact]
    public async Task PrefixEncodingPreservesSurrogateBoundariesAndUndefinedPdfDocCharacters()
    {
        string[] prefixes = [new string('p', 255) + "\ud83d\ude00tail", new string('p', 255) + "\ud83dtail",
            new string('p', 256) + "\ude00tail", "\u007f\u00e9"];
        foreach (string prefix in prefixes)
        {
            InMemoryQpdfHost host = new(); host.AddInput(Input, Fixture(), QpdfInputOwnership.Copy);
            PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host, [new PdfPageSource(Input, PdfPageSelectionOptions.FromPages([1]))], Output,
                new PdfPageMergeOptions { Labels = [new(1, PdfPageLabelStyle.Decimal, 17, prefix)] }, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(1, result.PageCount); Assert.True(host.TryGetOutput(Output, out byte[] bytes));
            InMemoryQpdfHost reopened = new(); reopened.AddInput(Input, bytes, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(reopened, Input, CancellationToken.None);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            Assert.True(catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject tree));
            Assert.True(Assert.IsType<PdfDictionary>(tree).TryGet(PdfName.FromAscii("Nums"), out QpdfObject numbers));
            PdfDictionary label = Assert.IsType<PdfDictionary>(Assert.IsType<PdfArray>(numbers)[1]);
            Assert.True(label.TryGet(PdfName.FromAscii("P"), out QpdfObject text));
            Assert.Equal(Encoding.BigEndianUnicode.GetBytes("\ufeff" + prefix), Assert.IsType<PdfByteString>(text).ValueSpan.ToArray());
            Assert.Equal(0L, host.OpenLeaseCount);
        }
    }
}

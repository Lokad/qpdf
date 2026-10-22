using System.Reflection;
using System.Collections;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Writing;
using Lokad.Qpdf.Runtime;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Commands;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class TypedConfigurationOwnershipTests(ITestOutputHelper log)
{
    private static readonly QpdfPath Input = QpdfPath.From("inputs/typed.pdf");
    private static readonly QpdfPath Output = QpdfPath.From("outputs/typed.pdf");
    private static readonly byte[] Prior = "prior typed output"u8.ToArray();
    private static readonly byte[] Pdf = Assert.IsType<byte[]>(typeof(LabelTests)
        .GetMethod("BuildDoc", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, [3]));

    private sealed class Host : IQpdfHost
    {
        internal readonly InMemoryQpdfHost Inner = new();
        internal int Opens;
        internal bool Pause;
        internal string Outcome = "success";
        internal readonly TaskCompletionSource InputReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource InputResume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource OutputReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource OutputResume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            Opens++;
            if (Pause && Opens == 1) { InputReached.SetResult(); await InputResume.Task; }
            if (Outcome == "open-fault") { throw new QpdfHostException(path, QpdfHostFailureReason.ReadFailed); }
            return await Inner.OpenInputAsync(path, token);
        }
        public async ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        {
            if (Pause && !OutputReached.Task.IsCompleted) { OutputReached.SetResult(); await OutputResume.Task; }
            if (Outcome == "create-fault") { throw new QpdfHostException(path, QpdfHostFailureReason.WriteFailed); }
            if (Outcome == "host-cancel") { throw new OperationCanceledException(new CancellationToken(true)); }
            return new Transaction(await Inner.CreateOutputAsync(path, token), Outcome);
        }
        private sealed class Transaction(IQpdfOutputTransaction inner, string outcome) : IQpdfOutputTransaction
        {
            public QpdfPath Path => inner.Path;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
                => outcome == "write-fault" ? throw new QpdfHostException(Path, QpdfHostFailureReason.WriteFailed) : inner.WriteAsync(data, token);
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

    private static async Task<Host> CreateHost()
    {
        Host host = new(); host.Inner.AddInput(Input, Pdf, QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(Output, CancellationToken.None);
        await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        return host;
    }

    private static async Task<object> Run(Host host, string route, IReadOnlyList<PdfPageSource> sources,
        IReadOnlyList<PdfPageRotation> rotations, IReadOnlyList<PdfPageLabel> labels,
        QpdfLimits limits, CancellationToken token)
    {
        return route switch
        {
            "single" => await Qpdf.SelectPageAsync(host, Input, Output,
                sources[0].Selection, rotations, limits, token),
            "multi" => await Qpdf.SelectPagesAsync(host, sources, Output, limits, token),
            "rotations" => await Qpdf.SelectPagesAsync(host, sources, Output, rotations, limits, token),
            "merge" => await Qpdf.SelectPagesAsync(host, sources, Output,
                new PdfPageMergeOptions { Rotations = rotations, Labels = labels }, limits, token),
            "split" => await Qpdf.SelectPagesAsync(host, sources, Output,
                new PdfPageMergeOptions { Rotations = rotations, Labels = labels, SplitPages = 1 }, limits, token),
            _ => throw new InvalidOperationException(route),
        };
    }

    [Fact]
    public void IntegerSettingsKeepReadOnlyDefensiveSnapshots()
    {
        int[] source = [2, 1, 2];
        PdfPageSelectionOptions selection = PdfPageSelectionOptions.FromPages(source);
        PdfPageRotation rotation = new(PdfRotationMode.Add, 90, source);
        PdfPageCollation collation = new(source);
        source[0] = -5;
        foreach (IReadOnlyList<int>? snapshot in new[] { selection.Pages, rotation.Pages, collation.GroupSizes })
        {
            Assert.NotNull(snapshot);
            Assert.Equal(new[] { 2, 1, 2 }, snapshot);
            IList<int> list = Assert.IsAssignableFrom<IList<int>>(snapshot);
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list[0] = -5);
            Assert.Throws<NotSupportedException>(() => list.Add(7));
            Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList>(snapshot).Clear());
        }
        Assert.Same(selection.Pages, (selection with { }).Pages);
        Assert.Same(rotation.Pages, (rotation with { }).Pages);
        Assert.Same(collation.GroupSizes, (collation with { }).GroupSizes);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)new PdfPageSelectionOptions(1).Pages)[0] = -5);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)PdfPageSelectionOptions.Normalized([2, 1, 2]).Pages)[0] = -5);
    }

    [Fact]
    public async Task InFlightSelectionAndRotationKeepTheirValidatedSnapshots()
    {
        Host host = await CreateHost(); host.Pause = true;
        int[] pages = [1], positions = [1];
        PdfPageSelectionOptions selection = PdfPageSelectionOptions.FromPages(pages);
        PdfPageRotation rotation = new(PdfRotationMode.Add, 90, positions);
        Task<PdfPageSelectionResult> running = Qpdf.SelectPageAsync(host, Input, Output,
            selection, [rotation], QpdfLimits.Default, CancellationToken.None).AsTask();
        try
        {
            await host.InputReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pages[0] = 2; positions[0] = 2;
            Assert.Throws<NotSupportedException>(() => ((IList<int>)selection.Pages)[0] = 2);
            Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<int>>(rotation.Pages)[0] = 2);
        }
        finally { host.InputResume.TrySetResult(); host.OutputResume.TrySetResult(); }
        PdfPageSelectionResult result = await running;
        Assert.Equal(new[] { 1 }, result.SourcePages);
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] output));
        InMemoryQpdfHost reader = new(); reader.AddInput(Input, output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, Input, CancellationToken.None);
        Assert.True(document.PageReferences[0].Page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject angle));
        Assert.Equal(90L, Assert.IsType<PdfInteger>(angle).Value);
    }

    [Fact]
    public async Task TypedRoutesPreserveRotationOrderAndLabels()
    {
        foreach (string route in new[] { "single", "multi", "rotations", "merge" })
        {
            Host host = await CreateHost();
            PdfPageSource[] sources = [new(Input, PdfPageSelectionOptions.FromPages([1, 2, 3]))];
            PdfPageRotation[] rotations = [new(PdfRotationMode.Add, 90, [1, 1, 3]), new(PdfRotationMode.Subtract, 90, [3])];
            object result = await Run(host, route, sources, rotations,
                [new(1, PdfPageLabelStyle.Decimal, 17, "\u03a9"), new(2, PdfPageLabelStyle.LowerRoman, 1, "tail")], QpdfLimits.Default, CancellationToken.None);
            Assert.True(host.Inner.TryGetOutput(Output, out byte[] output));
            InMemoryQpdfHost reader = new(); reader.AddInput(Input, output, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, Input, CancellationToken.None);
            Assert.Equal(3, document.PageCount);
            if (route != "multi")
            {
                PdfDictionary first = document.PageReferences[0].Page;
                Assert.True(first.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject angle));
                Assert.Equal(180L, Assert.IsType<PdfInteger>(angle).Value);
                PdfDictionary third = document.PageReferences[2].Page;
                Assert.True(third.TryGet(PdfName.FromAscii("Rotate"), out angle));
                Assert.Equal(0L, Assert.IsType<PdfInteger>(angle).Value);
            }
            if (route == "merge")
            {
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
                Assert.True(catalog.TryGet(PdfName.FromAscii("PageLabels"), out QpdfObject rawTree));
                PdfDictionary tree = Assert.IsType<PdfDictionary>(rawTree);
                Assert.True(tree.TryGet(PdfName.FromAscii("Nums"), out QpdfObject rawNumbers));
                PdfArray numbers = Assert.IsType<PdfArray>(rawNumbers); Assert.Equal(4, numbers.Count);
                PdfDictionary first = Assert.IsType<PdfDictionary>(numbers[1]);
                Assert.True(first.TryGet(PdfName.FromAscii("P"), out QpdfObject prefix));
                Assert.Equal(new byte[] { 0xfe, 0xff, 0x03, 0xa9 }, Assert.IsType<PdfByteString>(prefix).Value.ToArray());
            }
            Assert.Equal(0L, host.Inner.OpenLeaseCount); GC.KeepAlive(result);
        }
    }

    [Fact]
    public async Task TypedPositionsKeepEmptyDefaultsAndDuplicatesBeyondTheTextCeiling()
    {
        foreach (string route in new[] { "single", "rotations", "merge" })
        foreach (string boundary in new[] { "null", "empty", "2048-repeats", "ordered", "2049-repeats" })
        {
            Host host = await CreateHost();
            PdfPageSource[] sources = [new(Input, PdfPageSelectionOptions.FromPages(Enumerable.Repeat(1, 11).ToArray()))];
            int[]? pages = boundary switch
            {
                    "null" => null,
                    "empty" => [],
                    "2048-repeats" => Enumerable.Repeat(1, 2048).ToArray(),
                    "ordered" => [.. Enumerable.Repeat(1, 2047), 11],
                    "2049-repeats" => Enumerable.Repeat(1, 2049).ToArray(),
                _ => throw new InvalidOperationException(boundary),
            };
            PdfPageRotation[] rotations = [new(PdfRotationMode.Add, 90, pages)];
            await Run(host, route, sources, rotations, [], QpdfLimits.Default, CancellationToken.None);
            Assert.True(host.Inner.TryGetOutput(Output, out byte[] output));
            InMemoryQpdfHost reader = new(); reader.AddInput(Input, output, QpdfInputOwnership.Copy);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, Input, CancellationToken.None);
            Assert.Equal(11, document.PageCount);
            for (int i = 0; i < 11; i++)
            {
                long expected = boundary switch
                {
                    "null" or "empty" => 90L,
                    "2048-repeats" => 0L,
                    "2049-repeats" => i == 0 ? 90L : 0L,
                    _ => i == 0 ? 270L : i == 10 ? 90L : 0L,
                };
                PdfDictionary page = document.PageReferences[i].Page;
                long actual = page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject angle) ? Assert.IsType<PdfInteger>(angle).Value : 0L;
                Assert.Equal(expected, actual);
            }
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            log.WriteLine("TYPED CONFIG BOUNDARY {0}|{1} preserved", route, boundary);
        }
    }

    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [Theory]
    [InlineData("single", false)]
    [InlineData("rotations", false)]
    [InlineData("merge", false)]
    [InlineData("single", true)]
    [InlineData("rotations", true)]
    [InlineData("merge", true)]
    public async Task NumericRotationBoundsPreservePriorOutputAndCloseInputs(string route, bool cardinality)
    {
        Host host = await CreateHost();
        PdfPageSource[] sources = [new(Input, PdfPageSelectionOptions.FromPages([1, 2, 3]))];
        PdfPageRotation[] rotations = [new(PdfRotationMode.Add, 90,
            cardinality ? Enumerable.Repeat(1, 2049).ToArray() : [4])];
        QpdfLimits limits = QpdfLimits.Default with { MaxContainerSize = 32L };
        Func<Task> run = () => Run(host, route, sources, rotations, [], limits, CancellationToken.None);
        if (cardinality)
        {
            QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(run);
            Assert.Equal(nameof(QpdfLimits.MaxContainerSize), failure.LimitName);
        }
        else { await Assert.ThrowsAsync<ArgumentOutOfRangeException>(run); }
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior);
    }

    private static readonly Dictionary<string, long> Sizes = [];

    private static long Physical(object value)
    {
        string key = value.GetType().FullName + (value is Array a ? ":" + a.Length : value is string s ? ":" + s.Length : "");
        if (Sizes.TryGetValue(key, out long size)) { return size; }
        if (value is not Array and not string) { size = PdfVersionOwnershipTests.MeasurePhysicalSize(value.GetType()); }
        else
        {
            object[] roots = new object[32];
            object Allocate() => value is Array array ? array.Clone() : new string('x', ((string)value).Length);
            for (int i = 0; i < roots.Length; i++) { roots[i] = Allocate(); }
            Array.Clear(roots); long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < roots.Length; i++) { roots[i] = Allocate(); }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, bytes % roots.Length); size = bytes / roots.Length; GC.KeepAlive(roots);
        }
        Sizes.Add(key, size); return size;
    }

    private static List<object> Walk(object root)
    {
        List<object> found = []; HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        void Visit(object value, int depth)
        {
            if (depth > 96 || !seen.Add(value) || value is IQpdfHost or string or Array) { return; }
            found.Add(value);
            if (value is Task task)
            {
                if (task.GetType().GetField("StateMachine", Fields)?.GetValue(task) is object state) { Visit(state, depth + 1); }
                return;
            }
            Type type = value.GetType();
            if (!type.IsValueType && !(type.Namespace?.StartsWith("Lokad.Qpdf", StringComparison.Ordinal) ?? false)) { return; }
            foreach (FieldInfo field in type.GetFields(Fields))
            { if (field.GetValue(value) is object child && !child.GetType().IsPrimitive && !child.GetType().IsEnum) { Visit(child, depth + 1); } }
        }
        Visit(root, 0); return found;
    }

    private static (ResourceGovernor Governor, long Minimum, long Admission) Observe(Task running)
    {
        List<object> values = Walk(running);
        ResourceGovernor governor = values.OfType<ResourceGovernor>().First();
        object? preparation = values.FirstOrDefault(x => x.GetType().FullName == "Lokad.Qpdf.Commands.TypedSelectionPreparation");
        HashSet<object> measured = new(ReferenceEqualityComparer.Instance); long minimum = 0;
        void Add(object value) { if (measured.Add(value)) { minimum += Physical(value); } }
        if (preparation is not null) { Add(preparation); }
        foreach (IList list in values.OfType<IList>())
        {
            Type type = list.GetType();
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>)) { continue; }
            string? name = type.GetGenericArguments()[0].FullName;
            if (name is not "Lokad.Qpdf.Commands.OutputRotation" and not "Lokad.Qpdf.Commands.PageLabelSpec" and not "Lokad.Qpdf.PdfPageSource") { continue; }
            Add(list);
            if (type.GetField("_items", Fields)?.GetValue(list) is Array backing && backing.Length > 0) { Add(backing); }
            if (name == "Lokad.Qpdf.PdfPageSource") { continue; } // Borrow caller-created source records.
            foreach (object item in list)
            {
                Add(item);
                // Numeric rotations borrow caller-owned immutable positions.
                // Only typed label adaptation still creates numeric text.
                if (name == "Lokad.Qpdf.Commands.PageLabelSpec" && preparation is not null
                    && item.GetType().GetProperty("FirstPage", Fields)?.GetValue(item) is string text && text.Length > 0) { Add(text); }
            }
        }
        Assert.True(minimum > 0);
        long admission = preparation?.GetType().GetField("_memory", Fields)?.GetValue(preparation) is ParserMemoryScope memory
            ? memory.ReservedBytes : -1L;
        return (governor, minimum, admission);
    }

    private sealed class Completed(string id, ResourceGovernor governor, object? result, List<byte[]> output)
    {
        internal readonly string Id = id;
        internal readonly ResourceGovernor Governor = governor;
        internal object? Result = result;
        internal readonly List<byte[]> Output = output;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Completed> Complete(string route, string outcome, List<string> defects, ITestOutputHelper log)
    {
        Host host = await CreateHost(); host.Pause = true; host.Outcome = outcome;
        using CancellationTokenSource cancel = new();
        int sourceCount = route == "multi" ? 512 : route == "merge" ? 64 : 1;
        PdfPageSource[] sources = Enumerable.Repeat(new PdfPageSource(Input,
            PdfPageSelectionOptions.FromPages(route is "merge" or "multi" ? [1] : [1, 2, 3])), sourceCount).ToArray();
        PdfPageRotation[] rotations = route == "multi" ? [] :
            [.. Enumerable.Repeat(new PdfPageRotation(PdfRotationMode.Add, 90, [1, 1, 3]), 64)];
        PdfPageLabel[] labels = route == "merge" ? Enumerable.Range(1, 64)
            .Select(i => new PdfPageLabel(i, PdfPageLabelStyle.Decimal, 17, "\u03a9")).ToArray()
            : [new(1, PdfPageLabelStyle.Decimal, 17, "\u03a9"), new(2, PdfPageLabelStyle.LowerRoman, 1, "tail")];
        string id = route + "|" + outcome;
        Task<object> running = Run(host, route, sources, rotations, labels, QpdfLimits.Default, cancel.Token);
        await host.InputReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var input = Observe(running);
        log.WriteLine("TYPED CONFIG MIN {0}|input minimum={1} admission={2}", id, input.Minimum, input.Admission);
        if (input.Admission < input.Minimum) { defects.Add(id + "|input:" + input.Minimum + ":" + input.Admission); }
        host.InputResume.SetResult();
        if (outcome != "open-fault")
        {
            await host.OutputReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var output = Observe(running);
            Assert.Same(input.Governor, output.Governor);
            log.WriteLine("TYPED CONFIG MIN {0}|output minimum={1} admission={2}", id, output.Minimum, output.Admission);
            if (output.Admission < output.Minimum) { defects.Add(id + "|output:" + output.Minimum + ":" + output.Admission); }
            if (outcome == "cancel") { cancel.Cancel(); }
            host.OutputResume.SetResult();
        }
        object? result = null;
        try { result = await running; Assert.Equal("success", outcome); }
        catch (OperationCanceledException) { Assert.Contains(outcome, new[] { "cancel", "host-cancel" }); }
        catch (QpdfHostException) { Assert.EndsWith("-fault", outcome); }
        List<byte[]> published = [];
        if (result is not null)
        {
            IReadOnlyList<QpdfPath> paths = result is PdfMultiPageSelectionResult multi ? multi.OutputPaths : [Output];
            Assert.Equal(route == "split" ? 3 : 1, paths.Count);
            foreach (QpdfPath path in paths)
            {
                Assert.True(host.Inner.TryGetOutput(path, out byte[] bytes)); published.Add(bytes);
                InMemoryQpdfHost reader = new(); reader.AddInput(Input, bytes, QpdfInputOwnership.Copy);
                await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, Input, CancellationToken.None);
                Assert.Equal(route == "split" ? 1 : route == "multi" ? 512 : route == "merge" ? 64 : 3, document.PageCount);
            }
        }
        else if (outcome != "close-fault")
        { Assert.True(host.Inner.TryGetOutput(Output, out byte[] prior)); Assert.Equal(Prior, prior); }
        Assert.Equal(0L, host.Inner.OpenLeaseCount);
        return new(id, input.Governor, result, published);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropResults(List<Completed> jobs)
    {
        foreach (Completed job in jobs)
        {
            if (job.Result is not null) { Assert.True(job.Governor.Snapshot().LiveMemoryBytes >= Physical(job.Result)); }
            job.Result = null;
        }
    }

    [Fact]
    public async Task CompleteTypedJobsAdmitConfigurationOverlapAndRetireBeforeCounters()
    {
        List<string> defects = []; List<Completed> jobs = [];
        foreach (string route in new[] { "single", "multi", "rotations", "merge", "split" })
        foreach (string outcome in new[] { "success", "open-fault", "create-fault", "write-fault", "commit-fault", "close-fault", "cancel", "host-cancel" })
        { jobs.Add(await Complete(route, outcome, defects, log)); }
        DropResults(jobs);
        foreach (Completed job in jobs)
        {
            long remaining = job.Governor.Snapshot().LiveMemoryBytes;
            log.WriteLine("TYPED CONFIG FINAL {0} {1}", job.Id, remaining);
            if (remaining != job.Governor.CallerMetadataBytes + job.Governor.CallerStorageBytes) { defects.Add(job.Id + "|final:" + remaining); }
            foreach (byte[] bytes in job.Output) { Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8)); }
        }
        Assert.Equal(40, jobs.Count);
        Assert.True(defects.Count == 0, "Typed configuration overlap was not admitted: " + string.Join(',', defects));
    }
}

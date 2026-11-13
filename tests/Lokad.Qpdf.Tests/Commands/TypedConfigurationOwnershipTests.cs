using Lokad.Qpdf.Commands;
using Host = Lokad.Qpdf.Tests.Foundation.RecordingJobHost;
using System.Collections;
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
    private static readonly byte[] Pdf = LabelTests.BuildDoc(3);

    private static Task<Host> CreateHost() => CreateHost(new(null, null, "success", true));

    private static async Task<Host> CreateHost(Host host)
    {
        host.Inner.AddInput(Input, Pdf, QpdfInputOwnership.Copy);
        foreach (QpdfPath path in new[] { Output, SplitPath(1), SplitPath(2), SplitPath(3) })
        {
            await using IQpdfOutputTransaction seed = await host.Inner.CreateOutputAsync(path, CancellationToken.None);
            await seed.WriteAsync(Prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None);
        }
        return host;
    }

    private static QpdfPath SplitPath(int number) => QpdfPath.From("outputs/typed-" + number + ".pdf");

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
        using Host host = await CreateHost(new("open", Input, "success", true));
        int[] pages = [1], positions = [1];
        PdfPageSelectionOptions selection = PdfPageSelectionOptions.FromPages(pages);
        PdfPageRotation rotation = new(PdfRotationMode.Add, 90, positions);
        Task<PdfPageSelectionResult> running = Qpdf.SelectPageAsync(host, Input, Output,
            selection, [rotation], QpdfLimits.Default, CancellationToken.None).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            pages[0] = 2; positions[0] = 2;
            Assert.Throws<NotSupportedException>(() => ((IList<int>)selection.Pages)[0] = 2);
            Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<int>>(rotation.Pages)[0] = 2);
        }
        finally { host.Resume(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
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
            using Host host = await CreateHost();
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
            using Host host = await CreateHost();
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

    [Theory]
    [InlineData("single", false)]
    [InlineData("rotations", false)]
    [InlineData("merge", false)]
    [InlineData("single", true)]
    [InlineData("rotations", true)]
    [InlineData("merge", true)]
    public async Task NumericRotationBoundsPreservePriorOutputAndCloseInputs(string route, bool cardinality)
    {
        using Host host = await CreateHost();
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

    [Fact]
    public void PreparationFreezesCallerContainersAndRefundsControlledGrowthRefusal()
    {
        using Host host = new(null, null, "success", true);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        PdfPageSource first = new(Input, new PdfPageSelectionOptions(1));
        PdfPageSource[] sources = [first, first];
        IReadOnlyList<PdfPageSource> snapshot;
        using (TypedSelectionPreparation owner = TypedSelectionPreparation.Create(governor))
        {
            owner.FreezeSources(sources);
            owner.ConvertLabels([new(1, PdfPageLabelStyle.Decimal, 17, "\u03a9")]);
            snapshot = owner.Sources;
            sources[0] = new(QpdfPath.From("other.pdf"), new PdfPageSelectionOptions(2));
            Assert.Same(first, snapshot[0]);
            Assert.Single(owner.Labels);
            host.Budget.RefuseGrowth = true;
            PdfPageRotation[] many = Enumerable.Repeat(new PdfPageRotation(PdfRotationMode.Add, 90, [1, 1, 3]), 262144).ToArray();
            Assert.Throws<QpdfMemoryAdmissionException>(() => owner.ConvertRotations(many));
            Assert.Equal(2, snapshot.Count); Assert.Same(first, snapshot[0]);
        }
        governor.CompleteStorage();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, host.Budget.ReservedBytes);
        Assert.Equal(2, snapshot.Count); Assert.Same(first, snapshot[0]);
    }

    private sealed record Completed(object? Result, QpdfExecutionCounters? Snapshot, List<byte[]> Output);

    private static QpdfExecutionCounters Counters(object result) => result switch
    {
        PdfPageSelectionResult single => single.Counters,
        PdfMultiPageSelectionResult multi => multi.Counters,
        _ => throw new InvalidOperationException(),
    };

    private static async Task<Completed> Complete(string route, string outcome, ITestOutputHelper log)
    {
        QpdfPath firstOutput = route == "split" ? SplitPath(1) : Output;
        QpdfPath failureOutput = route == "split" && outcome == "commit-fault" ? SplitPath(2) : firstOutput;
        Host pending = outcome switch
        {
            "success" or "cancel" => new("create", firstOutput, "success", true),
            "close-fault" => new("output-close", firstOutput, "fault", true),
            "host-cancel" => new("create", firstOutput, "host-cancel", true),
            _ => Host.ForOutcome(outcome, Input, failureOutput),
        };
        using Host host = await CreateHost(pending);
        using CancellationTokenSource cancel = new();
        int sourceCount = route == "multi" ? 512 : route == "merge" ? 64 : 1;
        PdfPageSource[] sources = Enumerable.Repeat(new PdfPageSource(Input,
            PdfPageSelectionOptions.FromPages(route is "merge" or "multi" ? [1] : [1, 2, 3])), sourceCount).ToArray();
        PdfPageRotation[] rotations = route == "multi" ? [] :
            [.. Enumerable.Repeat(new PdfPageRotation(PdfRotationMode.Add, 90, [1, 1, 3]), 64)];
        PdfPageLabel[] labels = route == "merge" ? Enumerable.Range(1, 64)
            .Select(i => new PdfPageLabel(i, PdfPageLabelStyle.Decimal, 17, "\u03a9")).ToArray()
            : [new(1, PdfPageLabelStyle.Decimal, 17, "\u03a9"), new(2, PdfPageLabelStyle.LowerRoman, 1, "tail")];
        Task<object> running = Run(host, route, sources, rotations, labels, QpdfLimits.Default, cancel.Token);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L);
            if (outcome == "cancel") { cancel.Cancel(); }
            host.Resume();
            object? result = null;
            try { result = await running.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal("success", outcome); }
            catch (OperationCanceledException) { Assert.Contains(outcome, new[] { "cancel", "host-cancel" }); }
            catch (QpdfHostException) { Assert.EndsWith("-fault", outcome); }
            List<byte[]> published = [];
            IReadOnlyList<QpdfPath> paths = route == "split" ? [SplitPath(1), SplitPath(2), SplitPath(3)] : [Output];
            foreach (QpdfPath path in paths)
            {
                Assert.True(host.Inner.TryGetOutput(path, out byte[] bytes));
                if (host.Committed.Contains(path))
                {
                    published.Add(bytes);
                    using InMemoryQpdfHost reader = new(); reader.AddInput(Input, bytes, QpdfInputOwnership.Copy);
                    await using QpdfDocument document = await QpdfDocument.OpenAsync(reader, Input, CancellationToken.None);
                    Assert.Equal(route == "split" ? 1 : route == "multi" ? 512 : route == "merge" ? 64 : 3, document.PageCount);
                }
                else { Assert.Equal(Prior, bytes); }
            }
            if (route == "split" && outcome == "commit-fault") { Assert.Single(published); }
            if (outcome == "success") { Assert.Equal(paths.Count, published.Count); }
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.True(host.Budget.ReservedBytes < host.Budget.PeakBytes);
            log.WriteLine("TYPED CONFIG CONTROL {0}|{1}", route, outcome);
            return new(result, result is null ? null : Counters(result) with { }, published);
        }
        finally
        {
            host.Resume();
            try { await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (QpdfException) { }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task CompleteTypedJobsAdmitConfigurationOverlapAndRetireBeforeCounters()
    {
        List<Completed> jobs = [];
        foreach (string route in new[] { "single", "multi", "rotations", "merge", "split" })
        { jobs.Add(await Complete(route, "success", log)); }
        foreach (string outcome in new[] { "open-fault", "create-fault", "write-fault", "commit-fault", "close-fault", "cancel", "host-cancel" })
        { jobs.Add(await Complete("single", outcome, log)); }
        jobs.Add(await Complete("multi", "write-fault", log));
        jobs.Add(await Complete("split", "commit-fault", log));
        foreach (Completed job in jobs)
        {
            if (job.Result is object result) { Assert.Equal(job.Snapshot, Counters(result)); }
            foreach (byte[] bytes in job.Output) { Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8)); }
        }
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class SubtreeReuseTests
{
    // Observe notifications in our own type, with resets disabled for finite
    // fixtures. This is not a public counter or a cumulative CPU allowance.
    private static readonly FieldInfo Work = typeof(ResourceGovernor).GetField("_operationsSinceCheck", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing internal work observation.");

    [Theory]
    [InlineData("pages")]
    [InlineData("labels")]
    [InlineData("attachment-missing")]
    public void ReuseIndexesDoNotCompareUnneededPrivateValues(string kind)
    {
        int comparisons = 0;
        PdfArray empty = new([]);
        PdfArray nums = new([new PdfInteger("0", 0), new PdfDictionary(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("St")] = new PdfInteger("10", 10) })]);
        PdfName pagesType = PdfName.FromAscii("Pages"); PdfInteger zero = new("0", 0);
        PdfDictionary Leaf()
        {
            Dictionary<PdfName, QpdfObject> fields = new() { [PdfName.FromAscii("Audit")] = new EqualityGuard(() => comparisons++) };
            if (kind == "pages") { fields[PdfName.FromAscii("Type")] = pagesType; fields[PdfName.FromAscii("Count")] = zero; fields[PdfName.FromAscii("Kids")] = empty; }
            else { fields[PdfName.FromAscii(kind == "labels" ? "Nums" : "Names")] = kind == "labels" ? nums : empty; }
            return new(fields);
        }
        PdfDictionary first = Leaf(), second = Leaf();
        Dictionary<PdfName, QpdfObject> rootFields = new() { [PdfName.FromAscii("Kids")] = new PdfArray([first, second]) };
        if (kind == "pages") { rootFields[PdfName.FromAscii("Type")] = pagesType; rootFields[PdfName.FromAscii("Count")] = zero; }
        PdfDictionary tree = new(rootFields);
        PdfDictionary catalog = kind == "attachment-missing"
            ? new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Names")] = new PdfDictionary(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("EmbeddedFiles")] = tree }) })
            : new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii(kind == "pages" ? "Pages" : "PageLabels")] = tree });
        ResourceGovernor sourceGovernor = new(QpdfLimits.Default, CancellationToken.None);
        using DocumentEngine engine = TestDocumentEngine.Create(new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Root")] = catalog }), sourceGovernor);
        QpdfLimits limits = Limits(); ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            if (kind == "pages") { Assert.Empty(PageTree.Read(engine, tree, new(governor), governor, limits, memory, CancellationToken.None)); }
            else if (kind == "labels") { Assert.Single(DocumentJson.ReadLabelTree(engine, limits, governor, memory, CancellationToken.None)); }
            else { Assert.Contains("requested attachment was not found", Assert.Throws<QpdfParseException>(() => EmbeddedFiles.ExtractAttachment(engine, "missing", limits, governor, new(governor), memory, CancellationToken.None)).Message, StringComparison.Ordinal); }
        }
        Assert.Equal(0, comparisons); Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
        Assert.True(first.Equals(second)); Assert.True(comparisons > 0);
    }

    private sealed class EqualityGuard(Action observe) : QpdfObject
    {
        public override bool Equals(object? other) { observe(); return other is EqualityGuard; }
        public override int GetHashCode() => 0;
    }

    [Theory]
    [InlineData("pages")]
    [InlineData("labels")]
    [InlineData("attachment-missing")]
    [InlineData("attachment-list")]
    public async Task SharedSubtreesKeepWorkProportionalToTheirGraphAndResult(string kind)
    {
        foreach (int depth in new[] { 6, 12, 16 })
        {
            long chain = await Measure(kind, false, depth);
            long shared = await Measure(kind, true, depth);
            Assert.InRange(shared, 1L, chain * 8L);
        }
    }

    private static async Task<long> Measure(string kind, bool shared, int depth)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(kind, shared, depth, "ordinary", false), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        QpdfLimits limits = Limits(); ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor)) { VisitCompleted(kind, document, limits, governor, memory, CancellationToken.None); }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes); Assert.InRange(governor.CounterValues.PeakMemoryBytes, 1L, limits.MaxMemoryBytes);
        return (long)(Work.GetValue(governor) ?? throw new InvalidOperationException());
    }

    [Theory]
    [InlineData("pages")]
    [InlineData("labels")]
    [InlineData("attachment-missing")]
    public async Task ReusedSubtreesStillEnforceDepthOnDeeperPaths(string kind)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(kind, false, 5, "deeper", false), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        QpdfLimits limits = Limits() with { MaxNestingDepth = 4 }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => Visit(kind, document, limits, governor, memory, CancellationToken.None));
            Assert.Equal(nameof(QpdfLimits.MaxNestingDepth), error.LimitName);
        }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Theory]
    [InlineData("pages")]
    [InlineData("labels")]
    [InlineData("attachment-missing")]
    public async Task ActivePathCyclesRemainErrors(string kind)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(kind, false, 3, "cycle", false), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        QpdfLimits limits = Limits(); ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        { Assert.Contains("cycle", Assert.Throws<QpdfParseException>(() => Visit(kind, document, limits, governor, memory, CancellationToken.None)).Message, StringComparison.Ordinal); }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public async Task RepeatedRealPagesKeepEveryOccurrence()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("pages", true, 2, "real", true), QpdfInputOwnership.Transfer);
        var count = await Qpdf.CountPagesAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.Equal(4, count.PageCount); Assert.Empty(count.Warnings);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.Equal(4, document.PageCount); Assert.All(document.PageReferences, page => Assert.Equal(new PdfReference(12, 0), page.Source));
    }

    [Fact]
    public async Task CachedLabelOverridesKeepTheFinalOccurrenceInABAOrder()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("labels", false, 2, "aba", false), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        QpdfLimits limits = Limits(); ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            SortedDictionary<int, QpdfObject> labels = DocumentJson.ReadLabelTree(document.Engine, limits, governor, memory, CancellationToken.None);
            PdfDictionary value = Assert.IsType<PdfDictionary>(Assert.Single(labels).Value);
            Assert.True(value.TryGet(PdfName.FromAscii("St"), out QpdfObject start)); Assert.Equal(10L, Assert.IsType<PdfInteger>(start).Value);
        }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public async Task ReplayedLabelsDoNotRestoreAnUnrelatedBranchesOlderValue()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("labels", false, 3, "isolation", false), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        QpdfLimits limits = Limits(); ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            SortedDictionary<int, QpdfObject> labels = DocumentJson.ReadLabelTree(document.Engine, limits, governor, memory, CancellationToken.None);
            Assert.Equal(2, labels.Count);
            foreach ((int index, long expected) in new[] { (0, 10L), (1, 30L) })
            {
                Assert.True(Assert.IsType<PdfDictionary>(labels[index]).TryGet(PdfName.FromAscii("St"), out QpdfObject raw));
                Assert.Equal(expected, Assert.IsType<PdfInteger>(raw).Value);
            }
        }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public void UniqueLabelPathsDoNotAllocateACompleteResultForEveryAncestor()
    {
        List<QpdfObject> numbers = [];
        for (int index = 0; index < 256; index++)
        {
            numbers.Add(new PdfInteger(index.ToString(CultureInfo.InvariantCulture), index));
            numbers.Add(new PdfDictionary(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("St")] = new PdfInteger((index + 10).ToString(CultureInfo.InvariantCulture), index + 10) }));
        }
        PdfDictionary node = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Nums")] = new PdfArray(numbers) });
        for (int depth = 0; depth < 24; depth++)
        { node = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Kids")] = new PdfArray([node]) }); }
        PdfDictionary catalog = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("PageLabels")] = node });
        PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Root")] = catalog });
        ResourceGovernor sourceGovernor = new(QpdfLimits.Default, CancellationToken.None);
        using DocumentEngine engine = TestDocumentEngine.Create(trailer, sourceGovernor);
        QpdfLimits limits = Limits() with { MaxContainerSize = 1024 }; ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        { Assert.Equal(256, DocumentJson.ReadLabelTree(engine, limits, governor, memory, CancellationToken.None).Count); }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes); Assert.InRange(governor.CounterValues.PeakMemoryBytes, 16384L, 32768L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnumerationAndRemovalStillDetectRepeatedAttachmentKeys(bool remove)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("attachment-list", true, 1, "real", false), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        QpdfLimits limits = Limits(); ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            QpdfParseException error = Assert.Throws<QpdfParseException>(() => EmbeddedFiles.ReadEntries(document.Engine, remove ? ["other"] : [], limits, governor, memory, memory, CancellationToken.None));
            Assert.Contains("duplicate logical keys", error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0L, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public async Task ExtractionStillReturnsTheFirstMatchBeforeUnneededInvalidKids()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("attachment-missing", false, 0, "early", false), QpdfInputOwnership.Transfer);
        QpdfAttachmentExtraction result = await Qpdf.ExtractAttachmentAsync(host, QpdfPath.From("input"), "other", QpdfPath.From("output"), CancellationToken.None);
        Assert.Equal(7L, result.ByteCount); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); Assert.Equal("payload"u8.ToArray(), bytes);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("labels")]
    [InlineData("select")]
    [InlineData("attachment")]
    public async Task PublicSharedGraphReportsAndSelectionKeepTheirResultsAndPriorTargets(string operation)
    {
        string kind = operation == "count" ? "pages" : operation == "attachment" ? "attachment-missing" : "labels";
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(kind, true, 16, "ordinary", operation == "count"), QpdfInputOwnership.Transfer);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 65536, MaxNestingDepth = 32, MaxContainerSize = 32 };
        if (operation == "count")
        {
            var count = await Qpdf.CountPagesAsync(host, QpdfPath.From("input"), limits, CancellationToken.None);
            Assert.Equal(0, count.PageCount); Assert.Empty(count.Warnings);
        }
        else if (operation == "attachment")
        {
            await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
            { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--show-attachment=missing", "output"]), new(limits), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.Contains(result.Diagnostics, item => item.Message.Contains("requested attachment was not found", StringComparison.Ordinal));
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        }
        else
        {
            string json;
            if (operation == "labels")
            {
                QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"json\":\"2\",\"jsonKey\":[\"pagelabels\"]}", new(limits), CancellationToken.None);
                Assert.Equal(QpdfExitCode.Success, result.ExitCode); json = Assert.IsType<string>(result.OutputText);
            }
            else
            {
                _ = await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"), new(1), limits, CancellationToken.None);
                Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
                json = (await Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("read"), new QpdfJsonOptions(2, ["pagelabels"]), limits, CancellationToken.None)).Json;
            }
            using JsonDocument parsed = JsonDocument.Parse(json);
            JsonElement label = Assert.Single(parsed.RootElement.GetProperty("pagelabels").EnumerateArray());
            Assert.Equal(0, label.GetProperty("index").GetInt32()); Assert.Equal(10, label.GetProperty("label").GetProperty("/St").GetInt32());
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyPageReusePreservesStoredAndSuppressedWarnings(bool suppress)
    {
        int depth = suppress ? 16 : 3;
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture("pages", true, depth, "warning", false), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        QpdfLimits limits = Limits(); ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor) { SuppressWarningStorage = suppress };
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            Assert.Empty(PageTree.Read(document.Engine, new PdfReference(10, 0), diagnostics, governor, limits, memory, CancellationToken.None));
            Assert.True(diagnostics.HasAcceptedWarnings);
            Assert.Equal(suppress ? 0 : 15, diagnostics.Entries.Count);
            Assert.All(diagnostics.Entries, diagnostic => Assert.Equal(QpdfDiagnosticCode.PageCountMismatch, diagnostic.Code));
            if (suppress) { Assert.InRange((long)(Work.GetValue(governor) ?? throw new InvalidOperationException()), 1L, 10000L); }
        }
        governor.CompleteStorage();
    }

    [Theory]
    [InlineData("pages")]
    [InlineData("labels")]
    [InlineData("attachment-missing")]
    public async Task EveryTraversalAdmissionRefusalRefundsItsPrivateState(string kind)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(kind, true, 16, "ordinary", false), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        int growths;
        using (RecordingMemoryBudget baseline = new(1048576L)) { RunBudgeted(document, host, kind, baseline, CancellationToken.None, CancellationToken.None); growths = baseline.GrowthCalls; Assert.Equal(0L, baseline.ReservedBytes); }
        Assert.InRange(growths, 1, 100);
        for (int at = 1; at <= growths; at++)
        {
            using RecordingMemoryBudget root = new(1048576L) { RefuseOnGrowth = at };
            Assert.Throws<QpdfMemoryAdmissionException>(() => RunBudgeted(document, host, kind, root, CancellationToken.None, CancellationToken.None));
            Assert.Equal(0L, root.ReservedBytes); Assert.Equal(at, root.GrowthCalls);
        }
    }

    [Theory]
    [InlineData("pages", false)]
    [InlineData("pages", true)]
    [InlineData("labels", false)]
    [InlineData("labels", true)]
    [InlineData("attachment-missing", false)]
    [InlineData("attachment-missing", true)]
    public async Task CancellationDuringTraversalAdmissionRefundsBeforeReturning(string kind, bool governorToken)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), Fixture(kind, true, 16, "ordinary", false), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        using RecordingMemoryBudget root = new(1048576L); using CancellationTokenSource cancellation = new();
        CancelBudget budget = new(root, cancellation);
        Assert.ThrowsAny<OperationCanceledException>(() => RunBudgeted(document, host, kind, budget,
            governorToken ? cancellation.Token : CancellationToken.None, governorToken ? CancellationToken.None : cancellation.Token));
        Assert.True(budget.Triggered); Assert.Equal(0L, root.ReservedBytes);
    }

    private static void RunBudgeted(QpdfDocument document, IQpdfHost storage, string kind, IMemoryBudget budget, CancellationToken governorToken, CancellationToken token)
    {
        QpdfLimits limits = Limits();
        ResourceGovernor governor = new(limits, governorToken, new MemoryBudgetHost(storage, budget));
        try { using ParserMemoryScope memory = ParserMemoryScope.Create(governor); VisitCompleted(kind, document, limits, governor, memory, token); }
        finally { governor.CompleteStorage(); }
    }

    private sealed class CancelBudget(IMemoryBudget inner, CancellationTokenSource cancellation) : IMemoryBudget
    {
        internal bool Triggered;
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            bool accepted = inner.TryReserve(bytes, out reservation);
            if (accepted && bytes > 0) { Triggered = true; cancellation.Cancel(); }
            return accepted;
        }
    }

    private static QpdfLimits Limits() => QpdfLimits.Default with { MaxMemoryBytes = 32768, MaxNestingDepth = 32, MaxContainerSize = 8, CancellationCheckInterval = long.MaxValue };

    private static void Visit(string kind, QpdfDocument document, QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        if (kind == "pages") { Assert.Empty(PageTree.Read(document.Engine, new PdfReference(10, 0), new(governor), governor, limits, memory, token)); }
        else if (kind == "labels") { Assert.Single(DocumentJson.ReadLabelTree(document.Engine, limits, governor, memory, token)); }
        else if (kind == "attachment-list") { Assert.Empty(EmbeddedFiles.ReadEntries(document.Engine, [], limits, governor, memory, memory, token)); }
        else { _ = EmbeddedFiles.ExtractAttachment(document.Engine, "missing", limits, governor, new(governor), memory, token); }
    }

    private static void VisitCompleted(string kind, QpdfDocument document, QpdfLimits limits, ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        try { Visit(kind, document, limits, governor, memory, token); Assert.False(kind == "attachment-missing", "The missing key unexpectedly extracted a payload."); }
        catch (QpdfParseException error) when (kind == "attachment-missing")
        { Assert.Contains("requested attachment was not found", error.Message, StringComparison.Ordinal); }
    }

    private static byte[] Fixture(string kind, bool shared, int depth, string shape, bool actualPageRoot)
    {
        string catalog = "<< /Type /Catalog /Pages " + (actualPageRoot ? "10" : "2") + " 0 R"
            + (kind == "labels" ? " /PageLabels 10 0 R" : kind.StartsWith("attachment", StringComparison.Ordinal) ? " /Names << /EmbeddedFiles 10 0 R >>" : "") + " >>";
        List<(int, string, byte[]?)> objects = [
            (1, catalog, null), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> >>", null),
            (4, "<< /Type /Filespec /F (Other) /EF << /F 5 0 R >> >>", null),
            (5, "<< /Length 7 >>", "payload"u8.ToArray()) ];
        for (int number = 6; number < 10; number++) { objects.Add((number, "null", null)); }
        for (int index = 0; index <= depth; index++)
        {
            string kids = index < depth ? (11 + index) + " 0 R" + (shared ? " " + (11 + index) + " 0 R" : "") : "";
            if (shape == "cycle" && index == depth) { kids = "10 0 R"; }
            if (shape == "deeper") { kids = index switch { 0 => "11 0 R 14 0 R", 1 => "12 0 R", 2 => "13 0 R", 3 => "", 4 => "15 0 R", _ => "11 0 R" }; }
            if (shape == "aba") { kids = index == 0 ? "11 0 R 12 0 R 11 0 R" : ""; }
            if (shape == "isolation") { kids = index == 0 ? "11 0 R 12 0 R 11 0 R 13 0 R 11 0 R" : ""; }
            string body;
            if (kind == "pages")
            {
                int count = shape == "warning" ? 1 : shape == "real" ? 1 << (depth - index) : 0;
                body = shape == "real" && index == depth
                    ? "<< /Type /Page /Parent " + (9 + index) + " 0 R /MediaBox [0 0 10 10] /Resources << >> >>"
                    : "<< /Type /Pages /Kids [" + kids + "] /Count " + count + (index == 0 ? "" : " /Parent " + (9 + index) + " 0 R") + " >>";
            }
            else if (kind == "labels")
            {
                string nums = shape == "aba" ? index == 1 ? "0 << /S /D /St 10 >>" : index == 2 ? "0 << /S /D /St 20 >>" : "" : index == depth ? "0 << /S /D /St 10 >>" : "";
                if (shape == "isolation") { nums = index switch { 1 => "0 << /S /D /St 10 >>", 2 => "1 << /S /D /St 20 >>", 3 => "1 << /S /D /St 30 >>", _ => "" }; }
                body = "<< /Kids [" + kids + "] /Nums [" + nums + "] >>";
            }
            else
            {
                string names = index == depth && (kind == "attachment-missing" || shape == "real" || shape == "early") ? "(other) 4 0 R" : "";
                body = "<< /Kids " + (shape == "early" ? "42" : "[" + kids + "]") + " /Names [" + names + "] >>";
            }
            objects.Add((10 + index, body, null));
        }
        return PdfTestDocument.Assemble(objects, 1);
    }
}

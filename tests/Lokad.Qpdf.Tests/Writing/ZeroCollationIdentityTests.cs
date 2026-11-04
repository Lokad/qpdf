using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class ZeroCollationIdentityTests
{
    private static (string File, int[] Pages, int Size)[] Groups(string shape) => shape switch
    {
        "first" => [("input", [3], 0), ("input", [1, 2], 1)],
        "overlap" => [("input", [1], 1), ("input", [2, 3], 0), ("input", [3], 1)],
        "different" => [("input", [1, 2], 1), ("other", [3], 0)],
        "empty" or "empty-split" => [("input", [1, 2, 3], 0)],
        _ => [("input", [1, 2], 1), ("input", [3], 0)]
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "first", "last", "overlap", "different", "empty", "empty-split" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [shape, route]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OnlyEmittedGroupsDefinePageIdentity(string shape, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), SourceGroupIdentityTests.Fixture("A"), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("other"), SourceGroupIdentityTests.Fixture("B"), QpdfInputOwnership.Transfer);
        byte[] prior = [17, 23, 41];
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await seed.WriteAsync(prior, CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        var groups = Groups(shape);
        (int Group, int Page)[] expected = shape == "overlap" ? [(0, 1), (2, 3)] : shape == "first" ? [(1, 1), (1, 2)]
            : shape.StartsWith("empty", StringComparison.Ordinal) ? [] : [(0, 1), (0, 2)];
        if (route == "typed")
        {
            PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host,
                groups.Select(group => new PdfPageSource(QpdfPath.From(group.File), PdfPageSelectionOptions.FromPages(group.Pages))).ToArray(),
                QpdfPath.From("output"), new PdfPageMergeOptions { Collation = new(groups.Select(group => group.Size).ToArray()), SplitPages = shape == "empty-split" ? 1 : null }, CancellationToken.None);
            Assert.Equal(groups.Select(_ => 3), result.SourcePageCounts);
            Assert.Equal(expected, result.Mapping.Select(item => (item.SourceIndex, item.SourcePage)));
            if (shape == "empty-split") { Assert.Empty(result.OutputPaths); }
        }
        else
        {
            string sizes = string.Join(',', groups.Select(group => group.Size));
            List<string> args = ["input", "--collate=" + sizes, "--pages"];
            foreach (var group in groups) { args.AddRange([group.File, string.Join(',', group.Pages)]); }
            args.Add("--");
            Dictionary<string, object> job = new()
            {
                ["inputFile"] = "input", ["outputFile"] = "output", ["collate"] = sizes,
                ["pages"] = groups.Select(group => new { file = group.File, range = string.Join(',', group.Pages) }).ToArray()
            };
            if (shape == "empty-split") { args.Add("--split-pages=1"); job.Add("splitPages", "1"); }
            args.Add("output");
            QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        if (shape == "empty-split") { Assert.Equal(prior, bytes); return; }
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument doc = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.Equal(expected.Length, doc.PageCount);
        for (int index = 0; index < expected.Length; index++)
        {
            PdfDictionary page = doc.PageReferences[index].Page;
            Assert.Equal("A" + expected[index].Page, Text(Get(page, "Marker")));
            PdfDictionary link = Assert.IsType<PdfDictionary>(Resolve(doc, Assert.IsType<PdfArray>(Resolve(doc, Get(page, "Annots")))[0]));
            PdfArray dest = Assert.IsType<PdfArray>(Resolve(doc, Get(link, "Dest")));
            int target = expected[index].Page % 3 + 1;
            if (expected.Any(item => item.Page == target))
            { Assert.Equal("A" + target, Text(Get(Assert.IsType<PdfDictionary>(Resolve(doc, dest[0])), "Marker"))); }
            else { Assert.IsType<PdfNull>(dest[0]); }
        }
        PdfDictionary info = Assert.IsType<PdfDictionary>(Resolve(doc, Get(doc.Engine.Trailer, "Info")));
        PdfDictionary data = Assert.IsType<PdfDictionary>(Resolve(doc, Get(info, "Private")));
        if (shape == "overlap") { Assert.Equal("A3", Text(Get(Assert.IsType<PdfDictionary>(Resolve(doc, Get(data, "Target"))), "Marker"))); }
        else { Assert.IsType<PdfNull>(Get(data, "Target")); }
        int pageObjects = 0;
        foreach (var entry in doc.Engine.XRefEntries)
        {
            if (entry.Value.Kind == XrefEntryKind.Free) { continue; }
            if (doc.Engine.Load(entry.Key) is PdfDictionary value && Get(value, "Type") is PdfName name && name.RawSpan.SequenceEqual("Page"u8)) { pageObjects++; }
        }
        Assert.Equal(expected.Length, pageObjects);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task OmittedGroupsStillValidateRequestedBounds(string route)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), SourceGroupIdentityTests.Fixture("A"), QpdfInputOwnership.Transfer);
        if (route == "typed")
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Qpdf.SelectPagesAsync(host,
                [new(QpdfPath.From("input"), new(1)), new(QpdfPath.From("input"), new(99))], QpdfPath.From("output"),
                new PdfPageMergeOptions { Collation = new([1, 0]) }, CancellationToken.None));
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--collate=1,0", "--pages", ".", "1", ".", "99", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","collate":"1,0","pages":[{"file":".","range":"1"},{"file":".","range":"99"}]}""", CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        }
        Assert.Equal(0L, host.OpenLeaseCount); Assert.False(host.TryGetOutput(QpdfPath.From("output"), out _));
    }
    [Theory]
    [InlineData("typed", false)]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("typed", true)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task OmissionPreservesContainerAndCollationValidation(string route, bool invalidCount)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), SourceGroupIdentityTests.Fixture("A"), QpdfInputOwnership.Transfer);
        int[] ignored = invalidCount ? [3] : [2, 3, 2, 3, 2, 3, 2, 3];
        QpdfLimits limits = invalidCount ? QpdfLimits.Default : QpdfLimits.Default with { MaxContainerSize = 8 };
        if (route == "typed")
        {
            Func<Task> execute = async () => await Qpdf.SelectPagesAsync(host,
                [new(QpdfPath.From("input"), new(1)), new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages(ignored))],
                QpdfPath.From("output"), new PdfPageMergeOptions { Collation = new(invalidCount ? [1, 0, 1] : [1, 0]) }, limits, CancellationToken.None);
            if (invalidCount) { await Assert.ThrowsAsync<ArgumentException>(execute); }
            else { await Assert.ThrowsAsync<QpdfLimitException>(execute); }
        }
        else
        {
            string sizes = invalidCount ? "1,0,1" : "1,0", range = string.Join(',', ignored);
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--collate=" + sizes, "--pages", ".", "1", ".", range, "--", "output"]), new(limits), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", collate = sizes, pages = new[] { new { file = ".", range = "1" }, new { file = ".", range } } }), new(limits), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, item => item.Code == (invalidCount ? QpdfDiagnosticCode.InvalidArguments : QpdfDiagnosticCode.BudgetExceeded));
        }
        Assert.Equal(0L, host.OpenLeaseCount); Assert.False(host.TryGetOutput(QpdfPath.From("output"), out _));
    }

    private static QpdfObject Get(PdfDictionary dict, string key) => dict.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? value : PdfNull.Instance;
    private static QpdfObject Resolve(QpdfDocument doc, QpdfObject value) => value is PdfReference reference ? doc.GetObject(reference.ObjectNumber) : value;
    private static string Text(QpdfObject value) => Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(value).ValueSpan);
}

using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Pages;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Pages;

public sealed class RangeGroupScopeTests
{
    private static readonly JsonDocument Corpus = Load();
    private static JsonDocument Load()
    {
        using Stream resource = typeof(RangeGroupScopeTests).Assembly.GetManifestResourceStream("Lokad.Qpdf.Tests.Pages.RangeGroupNativeCases.json")
            ?? throw new InvalidOperationException("Missing range reference cases.");
        return JsonDocument.Parse(resource);
    }

    public static IEnumerable<object[]> Ranges()
    {
        foreach (JsonElement row in Corpus.RootElement.GetProperty("ranges").EnumerateArray())
        { yield return [row.GetProperty("range").GetString() ?? throw new InvalidOperationException(), row.GetProperty("pages").EnumerateArray().Select(v => v.GetInt32()).ToArray()]; }
    }

    [Theory]
    [MemberData(nameof(Ranges))]
    public void ExclusionsKeepEarlierPositiveGroups(string range, int[] expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        { Assert.Equal(expected, PageRanges.Expand(range, 5, 100L, governor, memory)); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void RetainedPrefixStillConsumesTheSelectedEntryBudget()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => PageRanges.Expand("1-3,2,x1,4", 5, 4L, governor, memory));
            Assert.Equal(nameof(QpdfLimits.MaxContainerSize), failure.LimitName);
            Assert.Equal(new[] { 1, 2, 3, 4 }, PageRanges.Expand("1-3,2,x2,4", 5, 4L, governor, memory));
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void SingletonExclusionKeepsTheLargePrefixInOneAdmittedList()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1048576L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            IReadOnlyList<int> pages = PageRanges.Expand("1-z,1,x1", 100000, 100001L, governor, memory);
            Assert.Equal(100000, pages.Count); Assert.Equal(1, pages[0]); Assert.Equal(100000, pages[^1]);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    public static IEnumerable<object[]> Consumers()
    {
        foreach (JsonElement row in Corpus.RootElement.GetProperty("cases").EnumerateArray())
        foreach (bool job in new[] { false, true })
        {
            string range = row.GetProperty("range").GetString() ?? throw new InvalidOperationException();
            string mode = row.GetProperty("mode").GetString() ?? throw new InvalidOperationException();
            string expected = row.GetProperty("expected").GetRawText();
            yield return [range, mode, expected, job, false];
            if (range == "1,2,x1" && mode is "to" or "from" or "repeat") { yield return [range, mode, expected, job, true]; }
        }
    }

    private static byte[] Fixture(bool annotations)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 5 /Kids [3 0 R 4 0 R 5 0 R 6 0 R 7 0 R] >>", null)];
        for (int index = 1; index <= 5; index++)
        {
            objects.Add((index + 2, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources <<>> /AuditIndex " + index
                + " /Contents " + (index + 7) + " 0 R " + (annotations ? "/Annots [" + (index + 12) + " 0 R]" : "") + " >>", null));
            byte[] content = Encoding.ASCII.GetBytes("% Page " + index + "\n0." + index + " 0.2 0.3 rg " + (index * 5) + " 10 10 20 re f");
            objects.Add((index + 7, "<< /Length " + content.Length + " >>", content));
            if (annotations) { objects.Add((index + 12, "<< /Type /Annot /Subtype /Link /Rect [10 10 20 20] /A << /S /URI /URI (https://example.test/page-" + index + ") >> >>", null)); }
        }
        return CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public async Task SelectionRotationAndPlacementMatchTheNativeGroupScope(string range, string mode, string expected, bool job, bool underlay)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(mode is "select" or "rotate"), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("source"), Fixture(true), QpdfInputOwnership.Transfer);
        List<string> argv = ["input"]; Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
        if (mode == "select") { argv.AddRange(["--pages", ".", range, "--"]); settings["pages"] = new[] { new { file = ".", range } }; }
        else if (mode == "rotate") { argv.Add("--rotate=+90:" + range); settings["rotate"] = new[] { "+90:" + range }; }
        else
        {
            string placement = underlay ? "underlay" : "overlay";
            argv.AddRange(["--" + placement, "source", "--" + mode + "=" + range]);
            Dictionary<string, string> spec = new() { ["file"] = "source", [mode] = range };
            if (mode == "to") { argv.AddRange(["--from=1-z", "--repeat=1-z"]); spec["from"] = "1-z"; spec["repeat"] = "1-z"; }
            else if (mode == "from") { argv.Add("--to=1-z"); spec["to"] = "1-z"; }
            else { argv.AddRange(["--from=", "--to=1-z"]); spec["from"] = ""; spec["to"] = "1-z"; }
            argv.Add("--"); settings[placement] = new[] { spec };
        }
        argv.Add("output");
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject raw) => raw is PdfReference link ? document.GetObject(link.ObjectNumber) : raw;
        var actual = document.PageReferences.Select(location =>
        {
            PdfDictionary page = location.Page; Assert.True(page.TryGet(PdfName.FromAscii("AuditIndex"), out QpdfObject index));
            long rotation = page.TryGet(PdfName.FromAscii("Rotate"), out QpdfObject rotate) ? Assert.IsType<PdfInteger>(Resolve(rotate)).Value % 360 : 0;
            List<string> uris = [];
            if (page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annotations))
            {
                foreach (QpdfObject annotation in Assert.IsType<PdfArray>(Resolve(annotations)).Items)
                {
                    PdfDictionary annot = Assert.IsType<PdfDictionary>(Resolve(annotation));
                    Assert.True(annot.TryGet(PdfName.FromAscii("A"), out QpdfObject action));
                    Assert.True(Assert.IsType<PdfDictionary>(Resolve(action)).TryGet(PdfName.FromAscii("URI"), out QpdfObject uri));
                    uris.Add("u:" + Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Resolve(uri)).ValueSpan));
                }
            }
            return new { index = Assert.IsType<PdfInteger>(Resolve(index)).Value, rotation, uris };
        }).ToArray();
        using JsonDocument reference = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(reference.RootElement, JsonSerializer.SerializeToElement(actual)), JsonSerializer.Serialize(actual));
    }
}

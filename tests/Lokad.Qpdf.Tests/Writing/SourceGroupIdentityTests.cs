using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class SourceGroupIdentityTests
{
    internal static byte[] Fixture(string prefix)
    {
        byte[] program = "q 40 0 0 40 10 10 cm /Im0 Do Q"u8.ToArray(), payload = Encoding.ASCII.GetBytes("attachment-" + prefix);
        List<(int Number, string Value, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /Private 12 0 R /Names << /EmbeddedFiles << /Names [(K) 13 0 R] >> >> >>", null),
            (2, "<< /Type /Pages /Count 3 /Kids [3 0 R 4 0 R 5 0 R] >>", null)];
        for (int page = 1; page <= 3; page++)
        { objects.Add((page + 2, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 9 0 R /Contents 11 0 R /Annots [" + (page + 5) + " 0 R] /Marker (" + prefix + page + ") >>", null)); }
        for (int page = 1; page <= 3; page++)
        { objects.Add((page + 5, "<< /Type /Annot /Subtype /Link /Rect [10 10 30 30] /Dest [" + (page % 3 + 3) + " 0 R /Fit] >>", null)); }
        objects.AddRange([
            (9, "<< /XObject << /Im0 10 0 R >> >>", null),
            (10, "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 2 >>", new byte[] { 0, 255 }),
            (11, "<< /Length " + program.Length + " >>", program),
            (12, "<< /Target 5 0 R >>", null),
            (13, "<< /Type /Filespec /F (file) /EF << /F 14 0 R >> /Private 12 0 R >>", null),
            (14, "<< /Length " + payload.Length + " >>", payload),
            (15, "<< /Title (" + prefix + ") /Private 12 0 R >>", null)]);
        byte[] data = CheckTests.Assemble(objects, 1);
        return Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(data).Replace("/Root 1 0 R >>", "/Root 1 0 R /Info 15 0 R >>", StringComparison.Ordinal));
    }

    internal static (string File, int[] Pages)[] Groups(string shape) => shape switch
    {
        "combined" => [("input", [1, 2, 3])],
        "split" => [("input", [1]), ("input", [2]), ("input", [3])],
        "reverse" => [("input", [3]), ("input", [1]), ("input", [2])],
        "duplicate" => [("input", [1, 1]), ("input", [2, 3])],
        "interleaved" => [("input", [1]), ("other", [1]), ("input", [2, 3]), ("other", [2, 3])],
        "interleaved-duplicates" => [("input", [1, 1]), ("other", [1]), ("input", [2, 3])],
        "collated" => [("input", [1, 2]), ("other", [1, 2, 3]), ("input", [3])],
        "secondary" => [("other", [1]), ("other", [2]), ("other", [3])],
        _ => [("input", [1]), ("alias", [2, 3])]
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "combined", "split", "reverse", "duplicate", "interleaved", "interleaved-duplicates", "collated", "alias" })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [shape, route]; }
        foreach (string route in new[] { "argv", "job" }) { yield return ["secondary", route]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task GroupsKeepDocumentIdentityAndOriginalMappings(string shape, string route)
    {
        using InMemoryQpdfHost host = new();
        byte[] primary = Fixture("A");
        host.AddInput(QpdfPath.From("input"), primary, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("alias"), primary, QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("other"), Fixture("B"), QpdfInputOwnership.Copy);
        (string File, int[] Pages)[] groups = Groups(shape);
        (int Group, int Page)[] expected = shape == "collated" ? [(0, 1), (1, 1), (2, 3), (0, 2), (1, 2), (1, 3)]
            : groups.SelectMany((group, index) => group.Pages.Select(page => (index, page))).ToArray();
        if (route == "typed")
        {
            PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host,
                groups.Select(group => new PdfPageSource(QpdfPath.From(group.File), PdfPageSelectionOptions.FromPages(group.Pages))).ToArray(),
                QpdfPath.From("output"), new PdfPageMergeOptions { Collation = shape == "collated" ? PdfPageCollation.Interleave(1) : null }, CancellationToken.None);
            Assert.Equal(expected, result.Mapping.Select(item => (item.SourceIndex, item.SourcePage)));
            Assert.Equal(groups.Select(_ => 3), result.SourcePageCounts);
        }
        else
        {
            Dictionary<string, object> job = new()
            {
                ["inputFile"] = "input", ["outputFile"] = "output",
                ["pages"] = groups.Select(group => new { file = group.File, range = string.Join(',', group.Pages) }).ToArray()
            };
            if (shape == "collated") { job["collate"] = "1"; }
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(Arguments(shape)), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("result"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument reader = await QpdfDocument.OpenAsync(host, QpdfPath.From("result"), CancellationToken.None);
        Assert.Equal(expected.Length, reader.PageCount);
        Assert.Equal(expected.Length, reader.PageReferences.Select(page => page.Source).Distinct().Count());
        Dictionary<string, PdfReference> images = new(StringComparer.Ordinal);
        for (int i = 0; i < expected.Length; i++)
        {
            var item = expected[i]; string file = groups[item.Group].File, prefix = file == "other" ? "B" : "A";
            PdfDictionary page = reader.PageReferences[i].Page;
            Assert.Equal(prefix + item.Page, Text(Get(page, "Marker")));
            PdfArray annotations = Assert.IsType<PdfArray>(Resolve(reader, Get(page, "Annots")));
            PdfDictionary link = Assert.IsType<PdfDictionary>(Resolve(reader, annotations[0]));
            PdfArray destination = Assert.IsType<PdfArray>(Resolve(reader, Get(link, "Dest")));
            int target = item.Page % 3 + 1;
            bool selected = groups.Any(group => group.File == file && group.Pages.Contains(target));
            if (selected) { Assert.Equal(prefix + target, Text(Get(Assert.IsType<PdfDictionary>(Resolve(reader, destination[0])), "Marker"))); }
            else { Assert.IsType<PdfNull>(Resolve(reader, destination[0])); }
            PdfDictionary resources = Assert.IsType<PdfDictionary>(Resolve(reader, Get(page, "Resources")));
            PdfDictionary xobjects = Assert.IsType<PdfDictionary>(Resolve(reader, Get(resources, "XObject")));
            PdfReference image = Assert.IsType<PdfReference>(Get(xobjects, "Im0"));
            if (images.TryGetValue(file, out PdfReference? previous)) { Assert.Equal(previous, image); }
            else { images.Add(file, image); }
        }
        Assert.Equal(images.Count, images.Values.Distinct().Count());
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(reader, Get(reader.Engine.Trailer, "Root")));
        QpdfObject catalogPrivate = Get(catalog, "Private");
        PdfDictionary info = Assert.IsType<PdfDictionary>(Resolve(reader, Get(reader.Engine.Trailer, "Info")));
        Assert.Equal("A", Text(Get(info, "Title")));
        QpdfObject retainedPrivate = Get(info, "Private");
        if (catalogPrivate is not PdfNull) { Assert.Equal(retainedPrivate, catalogPrivate); }
        PdfDictionary names = Assert.IsType<PdfDictionary>(Resolve(reader, Get(catalog, "Names")));
        PdfDictionary tree = Assert.IsType<PdfDictionary>(Resolve(reader, Get(names, "EmbeddedFiles")));
        PdfArray entries = Assert.IsType<PdfArray>(Resolve(reader, Get(tree, "Names")));
        PdfDictionary spec = Assert.IsType<PdfDictionary>(Resolve(reader, entries[1]));
        Assert.Equal(retainedPrivate, Get(spec, "Private"));
        QpdfObject metadataTarget = Resolve(reader, Get(Assert.IsType<PdfDictionary>(Resolve(reader, retainedPrivate)), "Target"));
        if (groups.Any(group => group.File == "input" && group.Pages.Contains(3)))
        { Assert.Equal("A3", Text(Get(Assert.IsType<PdfDictionary>(metadataTarget), "Marker"))); }
        else { Assert.IsType<PdfNull>(metadataTarget); }
    }

    internal static IReadOnlyList<string> Arguments(string shape)
    {
        List<string> arguments = ["input", "--pages"];
        foreach (var group in Groups(shape)) { arguments.Add(group.File); arguments.Add(string.Join(',', group.Pages)); }
        arguments.Add("--"); if (shape == "collated") { arguments.Add("--collate=1"); }
        arguments.Add("output"); return arguments;
    }

    public static IEnumerable<object[]> Writers()
    {
        foreach (string mode in new[] { "classic", "packed", "qdf", "linear", "transforms" })
        foreach (string route in new[] { "argv", "job" }) { yield return [mode, route]; }
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task PublicationRetainsSelectedTargetsAcrossWriterModes(string mode, string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("A"), QpdfInputOwnership.Transfer);
        List<string> args = ["input", "--pages", ".", "1", "input", "2,3", "--"];
        Dictionary<string, object> job = new()
        {
            ["inputFile"] = "input", ["outputFile"] = "output",
            ["pages"] = new[] { new { file = ".", range = "1" }, new { file = "input", range = "2,3" } }
        };
        switch (mode)
        {
            case "packed": args.Add("--object-streams=generate"); job["objectStreams"] = "generate"; break;
            case "qdf": args.Add("--qdf"); job["qdf"] = true; break;
            case "linear": args.AddRange(["--linearize", "--object-streams=generate"]); job["linearize"] = true; job["objectStreams"] = "generate"; break;
            case "transforms":
                args.AddRange(["--rotate=90:2", "--coalesce-contents", "--normalize-content=y"]);
                job["rotate"] = new[] { "90:2" }; job["coalesceContents"] = true; job["normalizeContent"] = "y"; break;
        }
        args.Add("output");
        QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None)
            : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        {
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
            host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Copy);
            await using QpdfDocument doc = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
            foreach (var page in doc.PageReferences)
            {
                int original = int.Parse(Text(Get(page.Page, "Marker"))[1..], System.Globalization.CultureInfo.InvariantCulture);
                PdfDictionary link = Assert.IsType<PdfDictionary>(Resolve(doc, Assert.IsType<PdfArray>(Resolve(doc, Get(page.Page, "Annots")))[0]));
                PdfArray destination = Assert.IsType<PdfArray>(Resolve(doc, Get(link, "Dest")));
                string target = "A" + (original % 3 + 1);
                Assert.Equal(target, Text(Get(Assert.IsType<PdfDictionary>(Resolve(doc, destination[0])), "Marker")));
                if (mode == "transforms" && original == 2) { Assert.Equal(90L, Assert.IsType<PdfInteger>(Resolve(doc, Get(page.Page, "Rotate"))).Value); }
            }
            PdfDictionary info = Assert.IsType<PdfDictionary>(Resolve(doc, Get(doc.Engine.Trailer, "Info")));
            PdfDictionary metadata = Assert.IsType<PdfDictionary>(Resolve(doc, Get(info, "Private")));
            Assert.Equal("A3", Text(Get(Assert.IsType<PdfDictionary>(Resolve(doc, Get(metadata, "Target"))), "Marker")));
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    internal static byte[] FormFixture()
    {
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R /PageLabels << /Nums [0 << /S /D /P (Label) /St 7 >>] >> >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [6 0 R] >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [7 0 R] >>", null),
            (5, "<< /Fields [8 0 R] /CO [8 0 R] /DA (/F1 12 Tf 0 g) /DR << /Font << /F1 9 0 R >> >> >>", null),
            (6, "<< /Type /Annot /Subtype /Widget /Rect [5 5 95 35] /P 3 0 R /Parent 8 0 R /F 4 >>", null),
            (7, "<< /Type /Annot /Subtype /Widget /Rect [5 5 95 35] /P 4 0 R /Parent 8 0 R /F 4 >>", null),
            (8, "<< /FT /Tx /T (Shared) /V (value) /Kids [6 0 R 7 0 R] >>", null),
            (9, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null)], 1);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task RepeatedGroupsKeepOneRegisteredFieldAndLabelRun(string route)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), FormFixture(), QpdfInputOwnership.Transfer);
        if (route == "typed")
        {
            PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host,
                [new(QpdfPath.From("input"), new(1)), new(QpdfPath.From("input"), new(2))], QpdfPath.From("output"), CancellationToken.None);
            Assert.Equal(new[] { 2, 2 }, result.SourcePageCounts);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1", ".", "2", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1"},{"file":".","range":"2"}]}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount); Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument doc = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(doc, Get(doc.Engine.Trailer, "Root")));
        PdfDictionary form = Assert.IsType<PdfDictionary>(Resolve(doc, Get(catalog, "AcroForm")));
        PdfReference field = Assert.IsType<PdfReference>(Assert.Single(Assert.IsType<PdfArray>(Resolve(doc, Get(form, "Fields"))).Items));
        Assert.Equal(field, Assert.IsType<PdfArray>(Resolve(doc, Get(form, "CO")))[0]);
        foreach (var page in doc.PageReferences)
        {
            PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(doc, Assert.IsType<PdfArray>(Resolve(doc, Get(page.Page, "Annots")))[0]));
            Assert.Equal(field, Get(widget, "Parent"));
        }
        PdfDictionary parent = Assert.IsType<PdfDictionary>(Resolve(doc, field)); Assert.Equal("Shared", Text(Get(parent, "T")));
        PdfArray children = Assert.IsType<PdfArray>(Resolve(doc, Get(parent, "Kids"))); Assert.Equal(2, children.Count);
        PdfDictionary labels = Assert.IsType<PdfDictionary>(Resolve(doc, Get(catalog, "PageLabels")));
        PdfArray nums = Assert.IsType<PdfArray>(Resolve(doc, Get(labels, "Nums"))); Assert.Equal(2, nums.Count);
        PdfDictionary label = Assert.IsType<PdfDictionary>(Resolve(doc, nums[1])); Assert.Equal(7L, Assert.IsType<PdfInteger>(Get(label, "St")).Value);
    }
    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task DuplicateFieldsFollowInterleavedPageOrder(string route)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), FormFixture(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("other"), FormFixture(), QpdfInputOwnership.Transfer);
        if (route == "typed")
        {
            await Qpdf.SelectPagesAsync(host,
                [new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages([1, 1])),
                 new(QpdfPath.From("other"), new(1)), new(QpdfPath.From("input"), new(2))],
                QpdfPath.From("output"), CancellationToken.None);
        }
        else
        {
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1,1", "other", "1", ".", "2", "--", "output"]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","pages":[{"file":".","range":"1,1"},{"file":"other","range":"1"},{"file":".","range":"2"}]}""", CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument doc = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(doc, Get(doc.Engine.Trailer, "Root")));
        PdfDictionary form = Assert.IsType<PdfDictionary>(Resolve(doc, Get(catalog, "AcroForm")));
        PdfArray fields = Assert.IsType<PdfArray>(Resolve(doc, Get(form, "Fields")));
        Assert.Equal(new[] { "Shared", "Shared+1", "Shared+2" }, fields.Items.Select(field => Text(Get(Assert.IsType<PdfDictionary>(Resolve(doc, field)), "T"))));
        Assert.Equal(fields.Items, Assert.IsType<PdfArray>(Resolve(doc, Get(form, "CO"))).Items);
        string[] names = ["Shared", "Shared+1", "Shared+2", "Shared"];
        for (int index = 0; index < doc.PageCount; index++)
        {
            PdfArray annotations = Assert.IsType<PdfArray>(Resolve(doc, Get(doc.PageReferences[index].Page, "Annots")));
            PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(doc, annotations[0]));
            PdfDictionary field = Assert.IsType<PdfDictionary>(Resolve(doc, Get(widget, "Parent")));
            Assert.Equal(names[index], Text(Get(field, "T")));
            Assert.Equal(doc.PageReferences[index].Source, Get(widget, "P"));
        }
    }

    private static QpdfObject Get(PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? value : PdfNull.Instance;
    private static QpdfObject Resolve(QpdfDocument reader, QpdfObject value)
        => value is PdfReference link ? reader.GetObject(link.ObjectNumber) : value;
    private static string Text(QpdfObject value) => Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(value).ValueSpan);
}

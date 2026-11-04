using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class SplitPageMembershipTests
{
    internal static byte[] Fixture(string shape, bool form)
    {
        byte[] content = "q 40 0 0 40 10 10 cm /Im0 Do Q"u8.ToArray(), xml = "<audit/>"u8.ToArray();
        List<(int Number, string Value, byte[]? StreamData)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /Metadata 19 0 R /Names << /EmbeddedFiles << /Names [(K) 13 0 R] >> >> " + (form ? "/AcroForm 20 0 R" : "") + " >>", null),
            (2, "<< /Type /Pages /Count 3 /Kids [3 0 R 4 0 R 5 0 R] >>", null)];
        for (int page = 1; page <= 3; page++)
        {
            objects.Add((page + 2, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Marker (A" + page + ") /Resources 9 0 R /Contents 11 0 R /Annots [" + (page + 5) + " 0 R " + (form ? (page + 20) + " 0 R" : "") + "] >>", null));
            int target = shape == "shared" ? 3 : page % 3 + 1;
            string destination = shape == "direct" ? "[" + (target + 2) + " 0 R /Fit]" : (shape == "shared" ? 16 : page + 15) + " 0 R";
            objects.Add((page + 5, "<< /Type /Annot /Subtype /Link /Rect [10 10 30 30] " + (shape == "action" ? "/A << /S /GoTo /D " + destination + " >>" : "/Dest " + destination) + " >>", null));
            objects.Add((page + 15, "[" + (target + 2) + " 0 R /Fit]", null));
        }
        objects.AddRange([
            (9, "<< /XObject << /Im0 10 0 R >> >>", null),
            (10, "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 2 >>", new byte[] { 0, 255 }),
            (11, "<< /Length " + content.Length + " >>", content),
            (12, "<< /First 3 0 R /Last 5 0 R /Vector [3 0 R 5 0 R] /Navigation 16 0 R >>", null),
            (13, "<< /Type /Filespec /F (file) /EF << /F 14 0 R >> /Private 12 0 R >>", null),
            (14, "<< /Type /EmbeddedFile /Length 10 >>", "attachment"u8.ToArray()),
            (15, "<< /Title (Membership) /Private 12 0 R >>", null),
            (19, "<< /Type /Metadata /Subtype /XML /Private 12 0 R /Length " + xml.Length + " >>", xml)]);
        if (form)
        {
            objects.Add((20, "<< /Fields [24 0 R] /CO [24 0 R] /DA (/F1 12 Tf) /DR << /Font << /F1 25 0 R >> >> >>", null));
            for (int page = 1; page <= 3; page++)
            { objects.Add((page + 20, "<< /Type /Annot /Subtype /Widget /Rect [5 5 95 35] /P " + (page + 2) + " 0 R /Parent 24 0 R >>", null)); }
            objects.AddRange([(24, "<< /FT /Tx /T (Shared) /V (value) /Kids [21 0 R 22 0 R 23 0 R] >>", null),
                (25, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null)]);
        }
        objects.Sort((left, right) => left.Number.CompareTo(right.Number));
        byte[] bytes = CheckTests.Assemble(objects, 1);
        return Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(bytes).Replace("/Root 1 0 R >>", "/Root 1 0 R /Info 15 0 R >>", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "direct", "indirect", "shared", "action" })
        foreach (bool form in new[] { false, true })
        foreach (bool repeated in new[] { false, true })
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return [shape, form, repeated, route, "classic"]; }
        foreach (string shape in new[] { "indirect", "shared" })
        foreach (string route in new[] { "argv", "job" })
        foreach (string mode in new[] { "packed", "qdf", "linear", "preserve" }) { yield return [shape, true, true, route, mode]; }
        foreach (string route in new[] { "typed", "argv", "job" }) { yield return ["shared", true, true, route, "collated-zero"]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SplitMembershipBoundsAllRetainedPageReferences(string shape, bool form, bool repeated, string route, string mode)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture(shape, form), QpdfInputOwnership.Transfer);
        bool collatedZero = mode == "collated-zero";
        string firstRange = collatedZero ? "1,2" : "1", lastRange = collatedZero ? "3" : "2,3";
        int splitSize = collatedZero ? 1 : 2;
        if (route == "typed")
        {
            PdfPageSource[] sources = repeated
                ? [new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages(collatedZero ? [1, 2] : [1])), new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages(collatedZero ? [3] : [2, 3]))]
                : [new(QpdfPath.From("input"), PdfPageSelectionOptions.FromPages([1, 2, 3]))];
            PdfMultiPageSelectionResult result = await Qpdf.SelectPagesAsync(host, sources, QpdfPath.From("output"), new PdfPageMergeOptions { SplitPages = splitSize, Collation = collatedZero ? new([1, 0]) : null }, CancellationToken.None);
            Assert.Equal(collatedZero ? ["output-1", "output-2"] : new[] { "output-1-2", "output-3" }, result.OutputPaths.Select(path => path.Value));
        }
        else
        {
            List<string> arguments = ["input", "--split-pages=" + splitSize, "--pages", ".", repeated ? firstRange : "1-3"];
            if (repeated) { arguments.AddRange([".", lastRange]); }
            arguments.Add("--");
            Dictionary<string, object> job = new()
            {
                ["inputFile"] = "input", ["outputFile"] = "output", ["splitPages"] = splitSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["pages"] = repeated ? new[] { new { file = ".", range = firstRange }, new { file = ".", range = lastRange } } : new[] { new { file = ".", range = "1-3" } }
            };
            if (mode == "packed") { arguments.Add("--object-streams=generate"); job.Add("objectStreams", "generate"); }
            if (mode == "qdf") { arguments.Add("--qdf"); job.Add("qdf", true); }
            if (mode == "linear") { arguments.Add("--linearize"); job.Add("linearize", true); }
            if (mode == "preserve") { arguments.Add("--preserve-unreferenced"); job.Add("preserveUnreferenced", true); }
            if (collatedZero) { arguments.Add("--collate=1,0"); job.Add("collate", "1,0"); }
            arguments.Add("output");
            QpdfExecutionResult result = route == "argv" ? await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        foreach ((string path, int[] selected) in collatedZero ? new[] { ("output-1", new[] { 1 }), ("output-2", new[] { 2 }) } : new[] { ("output-1-2", new[] { 1, 2 }), ("output-3", new[] { 3 }) })
        {
            Assert.True(host.TryGetOutput(QpdfPath.From(path), out byte[] bytes));
            host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument doc = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
            Assert.Equal(selected.Length, doc.PageCount);
            for (int index = 0; index < selected.Length; index++)
            {
                PdfDictionary page = doc.PageReferences[index].Page;
                PdfArray annotations = Assert.IsType<PdfArray>(Resolve(doc, Get(page, "Annots")));
                PdfDictionary link = Assert.IsType<PdfDictionary>(Resolve(doc, annotations[0]));
                PdfDictionary action = shape == "action" ? Assert.IsType<PdfDictionary>(Resolve(doc, Get(link, "A"))) : link;
                PdfArray destination = Assert.IsType<PdfArray>(Resolve(doc, Get(action, shape == "action" ? "D" : "Dest")));
                CheckTarget(doc, destination[0], shape == "shared" ? 3 : selected[index] % 3 + 1, selected);
                if (form)
                {
                    PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(doc, annotations[1]));
                    Assert.Equal(doc.PageReferences[index].Source, Get(widget, "P"));
                }
            }
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(doc, Get(doc.Engine.Trailer, "Root")));
            PdfDictionary info = Assert.IsType<PdfDictionary>(Resolve(doc, Get(doc.Engine.Trailer, "Info")));
            QpdfObject privateReference = Get(info, "Private");
            PdfDictionary data = Assert.IsType<PdfDictionary>(Resolve(doc, privateReference));
            CheckTarget(doc, Get(data, "First"), 1, selected); CheckTarget(doc, Get(data, "Last"), 3, selected);
            PdfArray vector = Assert.IsType<PdfArray>(Resolve(doc, Get(data, "Vector")));
            CheckTarget(doc, vector[0], 1, selected); CheckTarget(doc, vector[1], 3, selected);
            PdfStream metadata = Assert.IsType<PdfStream>(Resolve(doc, Get(catalog, "Metadata")));
            Assert.Equal(privateReference, Get(metadata.Dictionary, "Private"));
            PdfDictionary names = Assert.IsType<PdfDictionary>(Resolve(doc, Get(catalog, "Names")));
            PdfDictionary tree = Assert.IsType<PdfDictionary>(Resolve(doc, Get(names, "EmbeddedFiles")));
            PdfArray entries = Assert.IsType<PdfArray>(Resolve(doc, Get(tree, "Names")));
            PdfDictionary spec = Assert.IsType<PdfDictionary>(Resolve(doc, entries[1]));
            Assert.Equal(privateReference, Get(spec, "Private"));
            int pages = 0;
            foreach (var entry in doc.Engine.XRefEntries)
            {
                if (entry.Value.Kind == XrefEntryKind.Free) { continue; }
                if (doc.Engine.Load(entry.Key) is PdfDictionary dictionary && Get(dictionary, "Type") is PdfName type && type.RawSpan.SequenceEqual("Page"u8)) { pages++; }
            }
            Assert.Equal(selected.Length, pages);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static void CheckTarget(QpdfDocument doc, QpdfObject value, int original, IReadOnlyList<int> selected)
    {
        if (!selected.Contains(original)) { Assert.IsType<PdfNull>(value); return; }
        PdfDictionary target = Assert.IsType<PdfDictionary>(Resolve(doc, value));
        Assert.Equal("A" + original, Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(target, "Marker")).ValueSpan));
        Assert.Contains(doc.PageReferences, page => Equals(page.Source, value));
    }
    private static QpdfObject Get(PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value) ? value : PdfNull.Instance;
    private static QpdfObject Resolve(QpdfDocument doc, QpdfObject value)
        => value is PdfReference reference ? doc.GetObject(reference.ObjectNumber) : value;
}

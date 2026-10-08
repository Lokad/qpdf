using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class InlineResourceDefaultsTests
{
    private static readonly byte[] Image = "BI /W 4 /H 1 /BPC 8 /CS /G ID abcd EI\n"u8.ToArray();
    private static readonly string[] Routes = ["argv", "job", "mixed"];
    private const string FormHeader = " /Type /XObject /Subtype /Form /BBox [0 0 10 10]";
    private const string RepairWarning = "Page Resources is missing or invalid; repairing.";

    private static byte[] Fixture(bool form, string resources, byte[] content, string ancestor, string? extra)
    {
        string pageResources = form ? " /Resources << /XObject << /Fm1 5 0 R >> >>" : resources;
        byte[] pageContent = form ? "/Fm1 Do\n"u8.ToArray() : content;
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R]" + ancestor + " >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R" + pageResources + " >>", null),
            (4, "<< /Length " + pageContent.Length + " >>", pageContent)
        ];
        if (form) { objects.Add((5, "<< /Length " + content.Length + FormHeader + resources + " >>", content)); }
        if (extra is not null) { objects.Add((6, extra, null)); }
        if (ancestor.Contains("/Font", StringComparison.Ordinal))
        { objects.Add((7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null)); }
        int last = objects.Max(item => item.Number);
        for (int number = 5; number < last; number++)
        { if (!objects.Any(item => item.Number == number)) { objects.Add((number, "42", null)); } }
        return CheckTests.Assemble(objects, 1);
    }

    private static async Task<(InMemoryQpdfHost Host, QpdfExecutionResult Result)> Execute(
        byte[] input, string route, long minimum, bool keep, string selection, bool seed)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        if (selection == "multi") { host.AddInput(QpdfPath.From("inputs/b.pdf"), input, QpdfInputOwnership.Copy); }
        if (seed)
        {
            await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("outputs/a.pdf"), CancellationToken.None);
            await prior.WriteAsync(new byte[] { 9, 8, 7 }, CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        Dictionary<string, object> settings = new()
        {
            ["externalizeInlineImages"] = "", ["iiMinBytes"] = minimum.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["compressStreams"] = "n", ["decodeLevel"] = "none", ["objectStreams"] = "disable",
            ["removeUnreferencedResources"] = "no"
        };
        if (keep) { settings["preserveUnreferenced"] = ""; }
        if (selection != "whole")
        {
            List<object> pages = [new { file = ".", range = "1" }];
            if (selection == "multi") { pages.Add(new { file = "inputs/b.pdf", range = "1" }); }
            settings["pages"] = pages;
        }
        QpdfExecutionResult result;
        if (route == "job")
        {
            settings["inputFile"] = "inputs/a.pdf"; settings["outputFile"] = "outputs/a.pdf";
            result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None);
        }
        else
        {
            List<string> args = ["inputs/a.pdf"];
            if (route == "mixed")
            {
                host.AddInput(QpdfPath.From("inputs/settings.json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings)), QpdfInputOwnership.Copy);
                args.Add("--job-json-file=inputs/settings.json");
            }
            else
            {
                args.AddRange(["--externalize-inline-images", "--ii-min-bytes=" + minimum.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--compress-streams=n", "--decode-level=none", "--object-streams=disable", "--remove-unreferenced-resources=no"]);
                if (keep) { args.Add("--preserve-unreferenced"); }
                if (selection != "whole")
                {
                    args.AddRange(["--pages", ".", "1"]);
                    if (selection == "multi") { args.AddRange(["inputs/b.pdf", "1"]); }
                    args.Add("--");
                }
            }
            args.Add("outputs/a.pdf");
            result = await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(args), CancellationToken.None);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        return (host, result);
    }

    private static async Task<QpdfDocument> Open(InMemoryQpdfHost host)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] output));
        InMemoryQpdfHost reader = new(); reader.AddInput(QpdfPath.From("result.pdf"), output, QpdfInputOwnership.Copy);
        return await QpdfDocument.OpenAsync(reader, QpdfPath.From("result.pdf"), CancellationToken.None);
    }

    private static QpdfObject? Resolve(QpdfDocument document, QpdfObject? value)
    {
        if (value is PdfReference reference)
        { Assert.True(((IIndirectResolver)document.Engine).TryResolve(reference, out value)); }
        return value;
    }
    private static QpdfObject? Get(QpdfDocument document, PdfDictionary dictionary, string name)
        => dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject? value) ? Resolve(document, value) : null;
    private static PdfStream Content(QpdfDocument document, bool form)
    {
        PdfDictionary page = document.PageReferences[0].Page;
        if (!form) { return Assert.IsType<PdfStream>(Get(document, page, "Contents")); }
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(document, page, "Resources"));
        PdfDictionary objects = Assert.IsType<PdfDictionary>(Get(document, resources, "XObject"));
        return Assert.IsType<PdfStream>(Get(document, objects, "Fm1"));
    }
    private static List<PdfStream> Images(QpdfDocument document)
    {
        List<PdfStream> images = [];
        foreach ((int number, var entry) in document.Engine.XRefEntries)
        {
            if (number <= 0 || entry.Kind == Lokad.Qpdf.Reading.XrefEntryKind.Free) { continue; }
            int generation = entry.Kind == Lokad.Qpdf.Reading.XrefEntryKind.Uncompressed ? entry.Second : 0;
            QpdfObject? value = Resolve(document, new PdfReference(number, generation));
            if (value is PdfStream stream && Get(document, stream.Dictionary, "Subtype") is PdfName subtype && subtype.RawSpan.SequenceEqual("Image"u8))
            { images.Add(stream); }
        }
        return images;
    }
    private static void Warnings(QpdfExecutionResult result, int count)
    {
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(count == 0 ? QpdfExitCode.Success : QpdfExitCode.Warning, result.ExitCode);
        Assert.Equal(count, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Equal(QpdfDiagnosticCode.UnsupportedFeature, diagnostic.Code);
            Assert.Equal(QpdfDiagnosticSeverity.Warning, diagnostic.Severity);
        });
    }

    public static IEnumerable<object[]> PreparationCases()
    {
        foreach (string route in Routes)
        foreach (bool form in new[] { false, true })
        foreach (string resources in new[] { " /Resources << >>", " /Resources << /XObject null >>", " /Resources << /XObject 6 0 R >>" })
        { yield return [route, form, resources]; }
    }
    [Theory]
    [MemberData(nameof(PreparationCases))]
    public async Task BelowThresholdStillPreparesAnEmptyXObjectDictionary(string route, bool form, string resources)
    {
        byte[] input = Fixture(form, resources, Image, string.Empty, resources.Contains("6 0 R", StringComparison.Ordinal) ? "<< >>" : null);
        (InMemoryQpdfHost host, QpdfExecutionResult result) = await Execute(input, route, 1000L, false, "whole", false);
        Warnings(result, 0);
        await using QpdfDocument document = await Open(host);
        PdfDictionary dictionary = form ? Content(document, true).Dictionary : document.PageReferences[0].Page;
        PdfDictionary prepared = Assert.IsType<PdfDictionary>(Get(document, dictionary, "Resources"));
        Assert.Empty(Assert.IsType<PdfDictionary>(Get(document, prepared, "XObject")).Keys);
        Assert.Equal(Image, Content(document, form).Data);
    }

    public static IEnumerable<object[]> RepairCases()
    {
        foreach (string route in Routes)
        foreach (string resources in new[] { "", " /Resources null", " /Resources 3", " /Resources [1 2]" })
        foreach (bool below in new[] { false, true })
        { yield return [route, resources, below]; }
    }
    [Theory]
    [MemberData(nameof(RepairCases))]
    public async Task MissingAndMalformedPagesRepairWithOneWarning(string route, string resources, bool below)
    {
        (InMemoryQpdfHost host, QpdfExecutionResult result) = await Execute(Fixture(false, resources, Image, string.Empty, null), route, below ? 1000L : 0L, false, "single", false);
        Warnings(result, 1); Assert.Equal(RepairWarning, result.Diagnostics[0].Message);
        await using QpdfDocument document = await Open(host);
        PdfDictionary prepared = Assert.IsType<PdfDictionary>(Get(document, document.PageReferences[0].Page, "Resources"));
        PdfDictionary objects = Assert.IsType<PdfDictionary>(Get(document, prepared, "XObject"));
        Assert.Equal(below ? 0 : 1, objects.Count);
        Assert.Equal(below ? Image : "/IIm1 Do\n\n"u8.ToArray(), Content(document, false).Data);
    }

    public static IEnumerable<object[]> FormCases()
    {
        foreach (string route in Routes)
        foreach (string kind in new[] { "missing", "null", "integer", "array" })
        foreach (string sequence in new[] { "one", "two", "below" })
        foreach (bool keep in new[] { false, true })
        { yield return [route, kind, sequence, keep]; }
    }
    [Theory]
    [MemberData(nameof(FormCases))]
    public async Task MalformedFormsKeepValuesAndPreserveOnlyRequestedOrphans(string route, string kind, string sequence, bool keep)
    {
        string resources = kind switch { "missing" => "", "null" => " /Resources null", "integer" => " /Resources 3", _ => " /Resources [1 2]" };
        byte[] content = sequence == "two" ? [.. Image, .. Image] : Image;
        bool below = sequence == "below"; int count = sequence == "two" ? 2 : 1;
        int warnings = kind == "null" ? (below ? 0 : 1) : (below ? 0 : 2 * count) + (kind is "integer" or "array" ? 1 : 0);
        (InMemoryQpdfHost host, QpdfExecutionResult result) = await Execute(Fixture(true, resources, content, string.Empty, null), route, below ? 1000L : 0L, keep, "whole", false);
        Warnings(result, warnings);
        await using QpdfDocument document = await Open(host);
        PdfStream form = Content(document, true);
        Assert.Equal(below || kind == "null" ? content : Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("/IIm1 Do\n\n", count))), form.Data);
        if (kind == "integer") { Assert.Equal(3L, Assert.IsType<PdfInteger>(Get(document, form.Dictionary, "Resources")).Value); }
        else if (kind == "array") { Assert.Equal(2, Assert.IsType<PdfArray>(Get(document, form.Dictionary, "Resources")).Count); }
        else { Assert.False(form.Dictionary.TryGet(PdfName.FromAscii("Resources"), out _)); }
        List<PdfStream> images = Images(document);
        Assert.Equal(keep && !below ? (kind == "null" ? 1 : count) : 0, images.Count);
        Assert.All(images, image => Assert.Equal("abcd "u8.ToArray(), image.Data));
    }

    public static IEnumerable<object[]> FailureCases()
    {
        foreach (string route in Routes)
        foreach (bool broken in new[] { false, true })
        foreach (bool form in new[] { false, true })
        { yield return [route, form, broken]; }
    }
    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task LaterParseFailureKeepsPageResourcesOrPreservesTheFormTarget(string route, bool form, bool broken)
    {
        byte[] tail = broken ? "BI /W [ ID xy EI\n"u8.ToArray() : "BI /W 6 0 R /H 1 /BPC 8 /CS /G ID abcd EI\n"u8.ToArray();
        byte[] content = [.. Image, .. tail];
        (InMemoryQpdfHost host, QpdfExecutionResult result) = await Execute(Fixture(form, " /Resources << >>", content, string.Empty, "4"), route, 0L, false, "whole", true);
        if (form)
        {
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
            Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] prior)); Assert.Equal(new byte[] { 9, 8, 7 }, prior);
            return;
        }
        Warnings(result, 1);
        await using QpdfDocument document = await Open(host);
        Assert.Equal(content, Content(document, false).Data);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(document, document.PageReferences[0].Page, "Resources"));
        Assert.Single(Assert.IsType<PdfDictionary>(Get(document, resources, "XObject")).Keys);
        Assert.Equal("abcd "u8.ToArray(), Assert.Single(Images(document)).Data);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    public async Task InheritedResourcesRetainNamesWithoutRepair(string route)
    {
        byte[] input = Fixture(false, " /Resources null", Image, " /Resources << /Font << /IIm1 7 0 R >> >>", null);
        (InMemoryQpdfHost host, QpdfExecutionResult result) = await Execute(input, route, 0L, false, "whole", false);
        Warnings(result, 0);
        await using QpdfDocument document = await Open(host);
        Assert.Equal("/IIm2 Do\n\n"u8.ToArray(), Content(document, false).Data);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(document, document.PageReferences[0].Page, "Resources"));
        Assert.Single(Assert.IsType<PdfDictionary>(Get(document, resources, "Font")).Keys);
    }

    [Theory]
    [InlineData("whole")]
    [InlineData("single")]
    [InlineData("multi")]
    public async Task PreservedOldContentAndUnattachedImagesSurviveEachCopyRoute(string selection)
    {
        byte[] input = Fixture(false, " /Resources << /XObject 3 >>", Image, string.Empty, null);
        (InMemoryQpdfHost host, QpdfExecutionResult result) = await Execute(input, "argv", 0L, true, selection, false);
        Warnings(result, selection == "multi" ? 2 : 1);
        await using QpdfDocument document = await Open(host);
        Assert.Equal(selection == "multi" ? 2 : 1, Images(document).Count);
        int oldContents = 0;
        foreach ((int number, var entry) in document.Engine.XRefEntries)
        {
            if (number <= 0 || entry.Kind == Lokad.Qpdf.Reading.XrefEntryKind.Free) { continue; }
            if (Resolve(document, new PdfReference(number, entry.Second)) is PdfStream stream && stream.Data.AsSpan().SequenceEqual(Image)) { oldContents++; }
        }
        Assert.Equal(selection == "multi" ? 2 : 1, oldContents);
    }

    public static IEnumerable<object[]> InheritedMalformedCases()
    {
        foreach (string route in Routes)
        foreach (bool indirect in new[] { false, true })
        foreach (bool below in new[] { false, true })
        { yield return [route, indirect, below]; }
    }
    [Theory]
    [MemberData(nameof(InheritedMalformedCases))]
    public async Task ValidAncestorDoesNotRepairMalformedLeafResources(string route, bool indirect, bool below)
    {
        string resources = indirect ? " /Resources 6 0 R" : " /Resources 3";
        byte[] input = Fixture(false, resources, Image, " /Resources << /Font << /IIm1 7 0 R >> >>", indirect ? "3" : null);
        (InMemoryQpdfHost host, QpdfExecutionResult result) = await Execute(input, route, below ? 1000L : 0L, false, "single", true);
        if (indirect)
        {
            Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] prior)); Assert.Equal(new byte[] { 9, 8, 7 }, prior);
            return;
        }
        Warnings(result, below ? 1 : 3);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Message == RepairWarning);
        await using QpdfDocument document = await Open(host);
        Assert.Equal(3L, Assert.IsType<PdfInteger>(Get(document, document.PageReferences[0].Page, "Resources")).Value);
        Assert.Equal(below ? Image : "/IIm1 Do\n\n"u8.ToArray(), Content(document, false).Data);
        Assert.Empty(Images(document));
    }

    public static IEnumerable<object[]> ColorspaceCases()
    {
        foreach (string route in Routes)
        foreach (string kind in new[] { "missing", "null", "integer", "array" })
        { yield return [route, kind]; }
    }
    [Theory]
    [MemberData(nameof(ColorspaceCases))]
    public async Task NamedColorspacesPreserveMalformedResourceFailureOrder(string route, string kind)
    {
        string resources = kind switch { "missing" => "", "null" => " /Resources null", "integer" => " /Resources 3", _ => " /Resources [1 2]" };
        byte[] content = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Image).Replace("/CS /G", "/CS /Local", StringComparison.Ordinal));
        (InMemoryQpdfHost host, QpdfExecutionResult result) = await Execute(Fixture(true, resources, content, string.Empty, null), route, 0L, true, "whole", false);
        Warnings(result, kind == "null" ? 1 : kind == "missing" ? 4 : 5);
        Assert.EndsWith("returning null for attempted key retrieval", result.Diagnostics[0].Message);
        if (kind != "null") { Assert.Equal("Unable to resolve image colorspace; keeping the original name.", result.Diagnostics[1].Message); }
        await using QpdfDocument document = await Open(host);
        Assert.Equal(kind == "null" ? content : "/IIm1 Do\n\n"u8.ToArray(), Content(document, true).Data);
        List<PdfStream> images = Images(document);
        Assert.Equal(kind == "null" ? 0 : 1, images.Count);
        if (images.Count != 0) { Assert.Equal("Local"u8.ToArray(), Assert.IsType<PdfName>(Get(document, images[0].Dictionary, "ColorSpace")).RawBytes); }
    }

    public static IEnumerable<object[]> IndirectFormCases()
    {
        foreach (string route in Routes)
        foreach (string value in new[] { "3", "[1 2]", "true", "/Value" })
        { yield return [route, value]; }
    }
    [Theory]
    [MemberData(nameof(IndirectFormCases))]
    public async Task IndirectMalformedFormResourcesFailEvenBelowThreshold(string route, string value)
    {
        byte[] input = Fixture(true, " /Resources 6 0 R", Image, string.Empty, value);
        (InMemoryQpdfHost host, QpdfExecutionResult result) = await Execute(input, route, 1000L, false, "whole", true);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] prior)); Assert.Equal(new byte[] { 9, 8, 7 }, prior);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    [InlineData("mixed")]
    public async Task RepairsOnUnselectedPagesRemainVisible(string route)
    {
        byte[] input = CheckTests.Assemble(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 5 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + Image.Length + " >>", Image),
            (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 6 0 R >>", null),
            (6, "<< /Length 4 >>", "q Q\n"u8.ToArray())
        ], 1);
        (InMemoryQpdfHost host, QpdfExecutionResult result) = await Execute(input, route, 0L, false, "single", false);
        Warnings(result, 1); Assert.Equal(RepairWarning, result.Diagnostics[0].Message);
        await using QpdfDocument document = await Open(host);
        Assert.Equal(1, document.PageCount); Assert.Equal("/IIm1 Do\n\n"u8.ToArray(), Content(document, false).Data);
    }
}

using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceRoleTests
{
    private static readonly byte[] Program = "1 0 0 rg 0 0 20 10 re f"u8.ToArray();

    private static byte[] Fixture(string subtype, string shape)
    {
        string type = subtype switch { "absent" => "", "null" => "/Subtype null", "other" => "/Subtype /Other", "reference" => "/Subtype 12 0 R", _ => "/Subtype /Form" };
        string normal = shape switch { "stream" => "5 0 R", "states" => "<< /On 5 0 R >>", _ => "9 0 R" };
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /OriginalAP 5 0 R /OriginalPrivate 8 0 R /OriginalStates 10 0 R /OriginalCycle 11 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Annots [6 0 R] /PrivatePayload 5 0 R >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()),
            (5, "<< /BBox [0 0 20 10] /Resources << >> " + type + " /Length " + Program.Length + " >>", Program),
            (6, "<< /Type /Annot /Subtype /Stamp /Rect [20 30 80 60] /F 4 /AS /On /AP << /N " + normal + " /R 5 0 R /D 5 0 R /Private 8 0 R /PrivateStates 10 0 R /PrivateCycle 11 0 R >> >>", null),
            (7, "null", null), (8, "<< /Subtype /Form /BBox [0 0 1 1] /Resources << >> /Matrix [1 0 0 1 17 23] /Length 3 >>", "q Q"u8.ToArray()),
            (9, "<< /On 5 0 R >>", null), (10, "<< /On 8 0 R >>", null), (11, "<< /Self 11 0 R >>", null), (12, "/Form", null)], 1);
    }

    private static QpdfObject Get(QpdfDocument document, PdfDictionary node, string key)
    { Assert.True(node.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value; }
    private static PdfDictionary Dict(QpdfDocument document, PdfDictionary node, string key) => Assert.IsType<PdfDictionary>(Get(document, node, key));

    private static async Task<byte[]> Run(InMemoryQpdfHost host, byte[] source, string option, string value, bool job)
    {
        host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        List<string> argv = ["input", "--stream-data=uncompress"];
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["streamData"] = "uncompress" };
        if (option == "flatten") { argv.Add("--flatten-annotations=" + value); settings["flattenAnnotations"] = value; }
        else { argv.AddRange(["--rotate=" + value, "--flatten-rotation"]); settings["rotate"] = new[] { value }; settings["flattenRotation"] = true; }
        argv.Add("output");
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); return bytes;
    }

    public static IEnumerable<object[]> FlattenCases()
    {
        foreach (string subtype in new[] { "absent", "null", "other", "form", "reference" })
        foreach (string mode in new[] { "all", "print", "screen" })
        foreach (bool job in new[] { false, true }) { yield return [subtype, mode, job]; }
    }

    [Theory]
    [MemberData(nameof(FlattenCases))]
    public async Task FlatteningNormalizesOnlyTheAppearanceEdgeAndPreservesOriginalHeaders(string subtype, string mode, bool job)
    {
        PdfStream escaped;
        using (InMemoryQpdfHost host = new())
        {
            byte[] bytes = await Run(host, Fixture(subtype, "stream"), "flatten", mode, job);
            host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
            PdfDictionary catalog = Dict(document, document.Engine.Trailer, "Root"), page = document.PageReferences[0].Page;
            escaped = Assert.IsType<PdfStream>(Get(document, catalog, "OriginalAP"));
            Assert.Same(escaped, Get(document, page, "PrivatePayload"));
            bool hasSubtype = escaped.Dictionary.TryGet(PdfName.FromAscii("Subtype"), out QpdfObject raw);
            Assert.Equal(subtype != "absent", hasSubtype);
            if (subtype == "null") { Assert.IsType<PdfNull>(raw); }
            if (subtype == "reference") { Assert.IsType<PdfReference>(raw); }
            if (subtype is "other" or "form") { Assert.Equal(PdfName.FromAscii(subtype == "other" ? "Other" : "Form"), raw); }
            PdfDictionary objects = Dict(document, Dict(document, page, "Resources"), "XObject");
            using PdfDictionary.EntryEnumerator entries = objects.GetEntryEnumerator(); Assert.True(entries.MoveNext());
            PdfStream normalized = Assert.IsType<PdfStream>(Get(document, objects, entries.Current.Key.DisplayText));
            Assert.Equal(PdfName.FromAscii("Form"), Get(document, normalized.Dictionary, "Subtype"));
            Assert.Equal(subtype is "form" or "reference", ReferenceEquals(escaped, normalized));
            Assert.Equal(Program, normalized.RawMemory.ToArray()); Assert.False(page.TryGet(PdfName.FromAscii("Annots"), out _));
        }
        Assert.Equal(Program, escaped.RawMemory.ToArray());
    }

    public static IEnumerable<object[]> RotationCases()
    {
        foreach (string shape in new[] { "stream", "states", "reference-states" })
        foreach (int angle in new[] { 0, 90, 180, 270 })
        foreach (bool job in new[] { false, true }) { yield return [shape, angle, job]; }
    }

    [Theory]
    [MemberData(nameof(RotationCases))]
    public async Task RotationTransformsActualAppearancesAndKeepsPrivateAPValuesOpaque(string shape, int angle, bool job)
    {
        PdfStream escaped;
        using (InMemoryQpdfHost host = new())
        {
            byte[] bytes = await Run(host, Fixture("form", shape), "rotation", angle.ToString(System.Globalization.CultureInfo.InvariantCulture), job);
            host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
            PdfDictionary catalog = Dict(document, document.Engine.Trailer, "Root"), page = document.PageReferences[0].Page;
            PdfArray annotations = Assert.IsType<PdfArray>(Get(document, page, "Annots")); PdfReference annotation = Assert.IsType<PdfReference>(annotations[0]);
            PdfDictionary ap = Dict(document, Assert.IsType<PdfDictionary>(document.GetObject(annotation.ObjectNumber)), "AP");
            escaped = Assert.IsType<PdfStream>(Get(document, ap, "Private")); Assert.Same(escaped, Get(document, catalog, "OriginalPrivate"));
            Assert.Same(Get(document, catalog, "OriginalStates"), Get(document, ap, "PrivateStates"));
            PdfDictionary cycle = Dict(document, ap, "PrivateCycle"); Assert.Same(cycle, Get(document, cycle, "Self"));
            Assert.Same(Get(document, catalog, "OriginalCycle"), cycle);
            QpdfObject normal = Get(document, ap, "N"); PdfStream actual = shape == "stream" ? Assert.IsType<PdfStream>(normal) : Assert.IsType<PdfStream>(Get(document, Assert.IsType<PdfDictionary>(normal), "On"));
            Assert.Equal(angle != 0, actual.Dictionary.TryGet(PdfName.FromAscii("Matrix"), out _));
            Assert.Equal(Program, actual.RawMemory.ToArray());
        }
        Assert.Equal("q Q", Encoding.ASCII.GetString(escaped.RawMemory.Span));
        Assert.True(escaped.Dictionary.TryGet(PdfName.FromAscii("Matrix"), out QpdfObject rawMatrix));
        PdfArray matrix = Assert.IsType<PdfArray>(rawMatrix);
        Assert.Equal(17, Assert.IsType<PdfInteger>(matrix[4]).Value); Assert.Equal(23, Assert.IsType<PdfInteger>(matrix[5]).Value);
    }

}

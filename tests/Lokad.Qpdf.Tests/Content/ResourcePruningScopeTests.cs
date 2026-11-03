using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ResourcePruningScopeTests
{
    private static byte[] Fixture(byte[] page, string contents, string resources, byte[]? form, string formHeader, bool inherited)
        => CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 " + (inherited ? "/Resources 9 0 R" : "") + " >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents " + contents + " " + (inherited ? "" : "/Resources 9 0 R") + " >>", null),
            (4, "<< /Length " + page.Length + " >>", page),
            (5, form is null ? "null" : "<< /Subtype /Form /BBox [0 0 1 1] /Length " + form.Length + " " + formHeader + " >>", form),
            (6, "<< /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Length 3 >>", new byte[] { 255, 0, 0 }),
            (7, "<< /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Length 3 >>", new byte[] { 0, 255, 0 }),
            (8, "null", null), (9, resources, null),
            (10, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (11, "[4 0 R]", null), (12, "[]", null), (13, "[13 0 R]", null)], 1);

    private static async Task<QpdfExecutionResult> Rewrite(InMemoryQpdfHost host, byte[] input, bool job, bool automatic, bool coalesce)
    {
        host.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Transfer);
        string pruning = automatic ? "auto" : "yes";
        List<string> argv = ["input", "--pages", ".", "1", "--", "--remove-unreferenced-resources=" + pruning, "--compress-streams=n", "--decode-level=none"];
        if (coalesce) { argv.Add("--coalesce-contents"); }
        argv.Add("output");
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", pages = new[] { new { file = ".", range = "1" } },
                removeUnreferencedResources = pruning, compressStreams = "n", decodeLevel = "none", coalesceContents = coalesce }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        return result;
    }

    private static PdfDictionary Dictionary(QpdfDocument document, PdfDictionary dictionary, string name)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject raw));
        return Assert.IsType<PdfDictionary>(raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw);
    }

    public static IEnumerable<object[]> FormCases()
    {
        foreach (string shape in new[] { "readable", "unknown", "flate", "literal", "hex" })
        foreach (string resources in new[] { "absent", "null", "indirect-null" })
        foreach (bool automatic in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [shape, resources, automatic, job]; }
    }

    [Theory]
    [MemberData(nameof(FormCases))]
    public async Task FailedFormAnalysisKeepsTheEnclosingScope(string shape, string resources, bool automatic, bool job)
    {
        byte[] data = Encoding.ASCII.GetBytes("/AA Do" + (shape == "literal" ? " (" : shape == "hex" ? " <" : ""));
        string header = resources == "absent" ? "" : resources == "null" ? "/Resources null" : "/Resources 8 0 R";
        if (shape is "unknown" or "flate") { header += " /Filter /" + (shape == "unknown" ? "Unknown" : "FlateDecode"); }
        byte[] input = Fixture("q 60 0 0 60 20 20 cm /Form Do Q"u8.ToArray(), "4 0 R", "<< /XObject << /Form 5 0 R /AA 6 0 R /Unused 7 0 R >> >>", data, header, automatic);
        using InMemoryQpdfHost host = new(); QpdfExecutionResult result = await Rewrite(host, input, job, automatic, false);
        Assert.Equal(shape == "readable" ? QpdfExitCode.Success : QpdfExitCode.Warning, result.ExitCode);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary objects = Dictionary(document, Dictionary(document, document.PageReferences[0].Page, "Resources"), "XObject");
        Assert.True(objects.TryGet(PdfName.FromAscii("AA"), out _));
        Assert.Equal(shape != "readable", objects.TryGet(PdfName.FromAscii("Unused"), out _));
        if (shape != "readable") { Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature); }
    }

    public static IEnumerable<object[]> CategoryCases()
    {
        foreach (bool font in new[] { false, true })
        foreach (string local in new[] { "absent", "right", "wrong" })
        foreach (bool escaped in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [font, local, escaped, job]; }
    }

    [Theory]
    [MemberData(nameof(CategoryCases))]
    public async Task ASpellingInTheOtherCategoryDoesNotProveALocalBinding(bool font, string local, bool escaped, bool job)
    {
        string name = escaped ? "A#41" : "AA";
        byte[] program = Encoding.ASCII.GetBytes(font ? "BT /" + name + " 0.2 Tf (text) Tj ET" : "/" + name + " Do");
        string localResources = local == "absent" ? "" : "/Resources " + ((local == "right") == font
            ? "<< /Font << /AA 10 0 R >> >>" : "<< /XObject << /AA 6 0 R >> >>");
        byte[] input = Fixture("q 60 0 0 60 20 20 cm /Form Do Q"u8.ToArray(), "4 0 R",
            "<< /XObject << /Form 5 0 R /AA 6 0 R /Unused 7 0 R >> /Font << /AA 10 0 R /Unused 10 0 R >> >>", program, localResources, false);
        using InMemoryQpdfHost host = new(); QpdfExecutionResult result = await Rewrite(host, input, job, false, false);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary resources = Dictionary(document, document.PageReferences[0].Page, "Resources");
        Assert.Equal(local == "wrong", Dictionary(document, resources, "XObject").TryGet(PdfName.FromAscii("Unused"), out _));
        Assert.Equal(local == "wrong", Dictionary(document, resources, "Font").TryGet(PdfName.FromAscii("Unused"), out _));
    }

    public static IEnumerable<object[]> ContentCases()
    {
        foreach (string shape in new[] { "stream", "array", "indirect-array", "empty", "indirect-empty", "null", "indirect-null", "scalar", "null-element", "nested-array", "cycle-array" })
        foreach (bool coalesce in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [shape, coalesce, job]; }
    }

    [Theory]
    [MemberData(nameof(ContentCases))]
    public async Task PruningSharesContentsClassificationWithTheOtherTransforms(string shape, bool coalesce, bool job)
    {
        string contents = shape switch { "stream" => "4 0 R", "array" => "[4 0 R]", "indirect-array" => "11 0 R", "empty" => "[]",
            "indirect-empty" => "12 0 R", "null" => "null", "indirect-null" => "8 0 R", "null-element" => "[4 0 R 8 0 R]", "nested-array" => "[11 0 R]", "cycle-array" => "13 0 R", _ => "17" };
        bool damaged = shape is "scalar" or "null-element" or "nested-array" or "cycle-array";
        bool used = shape is "stream" or "array" or "indirect-array";
        byte[] input = Fixture("q 60 0 0 60 20 20 cm /AA Do Q"u8.ToArray(), contents, "<< /XObject << /AA 6 0 R /Unused 7 0 R >> >>", null, "", false);
        using InMemoryQpdfHost host = new(); QpdfExecutionResult result = await Rewrite(host, input, job, false, coalesce);
        Assert.Equal(damaged ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary objects = Dictionary(document, Dictionary(document, document.PageReferences[0].Page, "Resources"), "XObject");
        Assert.Equal(used || damaged, objects.TryGet(PdfName.FromAscii("AA"), out _));
        Assert.Equal(damaged, objects.TryGet(PdfName.FromAscii("Unused"), out _));
    }
}

using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AppearanceFontReadinessTests
{
    private static readonly byte[] Original = "/Tx BMC BT /F0 10 Tf 2 5 Td (OLD) Tj ET EMC"u8.ToArray();

    public static IEnumerable<object[]> Cases()
    {
        foreach (string binding in new[] { "missing", "null", "indirect-null", "malformed", "no-appearance", "field-only" })
        foreach (string operation in new[] { "whole", "selected", "overlay" })
        foreach (bool flatten in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [binding, operation, flatten, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task MissingOrMalformedFontKeepsAppearanceReadinessAndWidget(string binding, string operation, bool flatten, bool job)
    {
        RecordingMemoryBudget budget = new(16L * 1024 * 1024);
        using (IMemoryScope owner = budget.CreateScope())
        {
            PdfStream? escaped = null;
            using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
            {
                host.AddInput(QpdfPath.From("input"), operation == "overlay" ? Target() : Source(binding), QpdfInputOwnership.Transfer);
                if (operation == "overlay") { host.AddInput(QpdfPath.From("stamp"), Source(binding), QpdfInputOwnership.Transfer); }
                List<string> arguments = ["input", "--generate-appearances", "--compress-streams=n"];
                Dictionary<string, object> settings = new()
                { ["inputFile"] = "input", ["outputFile"] = "output", ["generateAppearances"] = true, ["compressStreams"] = "n" };
                if (operation == "selected")
                { arguments.AddRange(["--pages", ".", "1", "--"]); settings["pages"] = new[] { new { file = ".", range = "1" } }; }
                if (operation == "overlay")
                { arguments.AddRange(["--overlay", "stamp", "--"]); settings["overlay"] = new { file = "stamp" }; }
                if (flatten) { arguments.Add("--flatten-annotations=all"); settings["flattenAnnotations"] = "all"; }
                arguments.Add("output");
                QpdfExecutionResult result = job
                    ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
                    : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
                Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("usable font", StringComparison.Ordinal));
                Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
                host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
                await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
                {
                    PdfDictionary catalog = Assert.IsType<PdfDictionary>(Value(document, document.Engine.Trailer, "Root"));
                    PdfDictionary form = Assert.IsType<PdfDictionary>(Value(document, catalog, "AcroForm"));
                    Assert.True(Assert.IsType<PdfBoolean>(Value(document, form, "NeedAppearances")).Value);
                    PdfArray annotations = Assert.IsType<PdfArray>(Value(document, Assert.Single(document.PageReferences).Page, "Annots"));
                    Assert.Equal(1, annotations.Count);
                    PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
                    if (binding == "no-appearance") { Assert.Null(Value(document, widget, "AP")); }
                    else
                    {
                        escaped = Assert.IsType<PdfStream>(Value(document, Assert.IsType<PdfDictionary>(Value(document, widget, "AP")), "N"));
                        Assert.Equal(Original, escaped.CopyData(owner, CancellationToken.None));
                    }
                }
                Assert.Equal(0, host.OpenLeaseCount);
            }
            if (escaped is not null) { Assert.Equal(Original, escaped.CopyData(owner, CancellationToken.None)); }
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    private static byte[] Source(string binding)
    {
        string local = binding switch
        { "null" => "/F1 null", "indirect-null" => "/F1 8 0 R", "malformed" => "/F1 42", _ => "" };
        string defaults = binding == "malformed" ? "/DR << /Font << /F1 9 0 R >> >>" : "";
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (field) /V <e9> /F 4 /Rect [20 40 80 60] /DA (/F1 10 Tf 0 g) "
                + (binding == "field-only" ? "/DR << /Font << /F1 9 0 R >> >> " : "")
                + (binding == "no-appearance" ? "" : "/AP << /N 5 0 R >>") + " >>", null),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 60 20] /Resources << /Font << /F0 7 0 R " + local + " >> >> /Length " + Original.Length + " >>", Original),
            (6, "<< /Fields [4 0 R] /NeedAppearances true " + defaults + " >>", null),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null),
            (8, "null", null), (9, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>", null)], 1);
    }

    private static byte[] Target() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /DR << /Font << /F1 4 0 R >> >> >> >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
        (4, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>", null)], 1);

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static QpdfObject? Value(QpdfDocument document, PdfDictionary dictionary, string name)
        => dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value) ? Resolve(document, value) : null;
}

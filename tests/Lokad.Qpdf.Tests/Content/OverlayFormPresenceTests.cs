using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayFormPresenceTests
{
    public static IEnumerable<object?[]> FieldArrays()
    {
        foreach ((string? fields, bool registered) in new (string?, bool)[]
        { (null, false), ("null", false), ("8 0 R", false), ("[]", true), ("42", true), ("9 0 R", true) })
        foreach (bool generate in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [fields, registered, generate, job]; }
    }

    [Theory]
    [MemberData(nameof(FieldArrays))]
    public Task FieldsClassifiesResolvedNullAsAbsent(string? fields, bool registered, bool generate, bool job)
        => AssertRegistration(Source(fields, "/FT /Tx /T (field) /V (NEW)", generate), registered, generate, job);

    public static IEnumerable<object[]> Identities()
    {
        foreach (string role in new[] { "T", "FT", "Kids" })
        foreach (string representation in new[] { "null", "8 0 R", "present" })
        foreach (bool generate in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [role, representation, generate, job]; }
    }

    [Theory]
    [MemberData(nameof(Identities))]
    public Task FieldIdentityIgnoresDirectAndIndirectNull(string role, string representation, bool generate, bool job)
    {
        string value = representation == "present" ? role switch { "T" => "(field)", "FT" => "/Tx", _ => "[]" } : representation;
        return AssertRegistration(Source("[]", "/" + role + " " + value, generate), representation == "present", generate, job);
    }

    private static async Task AssertRegistration(byte[] source, bool registered, bool generate, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null)], 1), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), source, QpdfInputOwnership.Transfer);
        List<string> arguments = ["input", "--overlay", "stamp", "--"];
        Dictionary<string, object> settings = new()
        { ["inputFile"] = "input", ["outputFile"] = "output", ["overlay"] = new { file = "stamp" } };
        if (generate) { arguments.Add("--generate-appearances"); settings["generateAppearances"] = true; }
        arguments.Add("output");
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(Value(document, document.Engine.Trailer, "Root"));
            if (registered)
            {
                PdfDictionary form = Assert.IsType<PdfDictionary>(Value(document, catalog, "AcroForm"));
                Assert.Equal(1, Assert.IsType<PdfArray>(Value(document, form, "Fields")).Count);
            }
            else { Assert.Null(Value(document, catalog, "AcroForm")); }
            Assert.Equal(1, Assert.IsType<PdfArray>(Value(document, Assert.Single(document.PageReferences).Page, "Annots")).Count);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    private static byte[] Source(string? fields, string identity, bool need)
    {
        byte[] appearance = "0 0 1 rg 0 0 60 20 re f"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Widget /Rect [20 40 80 60] /DA (/F1 10 Tf 0 g) /AP << /N 5 0 R >> " + identity + " >>", null),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 60 20] /Resources << /Font << /F1 7 0 R >> >> /Length " + appearance.Length + " >>", appearance),
            (6, "<< " + (fields is null ? "" : "/Fields " + fields) + " /NeedAppearances " + (need ? "true" : "false")
                + " /DA (/F1 10 Tf 0 g) /DR << /Font << /F1 7 0 R >> >> >>", null),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", null),
            (8, "null", null), (9, "[4 0 R]", null)], 1);
    }

    private static QpdfObject? Value(QpdfDocument document, PdfDictionary dictionary, string name)
        => dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)
            ? value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value : null;
}

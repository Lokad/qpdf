using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationBorderFrameTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string kind in new[] { "border", "bs", "bs-direct", "defaults", "bs-defaults", "solid-unused", "beveled-unused", "inset-unused", "underline-unused", "unknown-unused", "zero", "bs-null" })
        foreach (string operation in new[] { "flatten", "overlay" })
        foreach (string route in new[] { "argv", "job" }) { yield return [kind, operation, route]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task BorderFramesRespectDefaultsPrecedenceAndIndependentOriginalValues(string kind, string operation, string route)
    {
        RecordingMemoryBudget budget = new(8L * 1024 * 1024);
        using (IMemoryScope owner = budget.CreateScope())
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
            byte[] source = Source(kind, route == "job");
            host.AddInput(QpdfPath.From("input"), operation == "flatten" ? source : Target(), QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("stamp"), source, QpdfInputOwnership.Transfer);
            QpdfExecutionResult result;
            if (route == "argv")
            {
                List<string> args = ["input", "--compress-streams=n", "--decode-level=none"];
                args.AddRange(operation == "flatten" ? ["--flatten-rotation"] : ["--overlay", "stamp", "--"]);
                args.Add("output"); result = await Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
            }
            else
            {
                Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["compressStreams"] = "n", ["decodeLevel"] = "none" };
                if (operation == "flatten") { settings["flattenRotation"] = true; }
                else { settings["overlay"] = new { file = "stamp" }; }
                result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None);
            }
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
            PdfDictionary escaped;
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
            {
                PdfDictionary page = Assert.Single(document.PageReferences).Page;
                PdfArray annotations = Assert.IsType<PdfArray>(Value(document, page, "Annots"));
                Assert.Equal(1, annotations.Count);
                PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
                PdfDictionary original = Assert.IsType<PdfDictionary>(Value(document, annotation, "Private")); escaped = original;
                double scale = operation == "overlay" ? 2 : 1;
                bool style = kind is "bs" or "bs-direct" or "bs-defaults" or "zero" || kind.EndsWith("-unused", StringComparison.Ordinal);
                if (style)
                {
                    PdfDictionary actual = Assert.IsType<PdfDictionary>(Value(document, annotation, "BS"));
                    PdfDictionary prior = Assert.IsType<PdfDictionary>(Value(document, original, "BS"));
                    Assert.Equal(Value(document, prior, "S"), Value(document, actual, "S"));
                    if (operation == "flatten" || kind == "zero")
                    {
                        if (kind != "bs-direct") { Assert.Same(prior, actual); }
                    }
                    if (kind == "bs-defaults")
                    {
                        Assert.False(prior.TryGet(PdfName.FromAscii("W"), out _)); Assert.False(prior.TryGet(PdfName.FromAscii("D"), out _));
                        if (scale == 2)
                        { Assert.Equal(2, Numeric(Value(document, actual, "W"))); Assert.Equal(new double[] { 6 }, Numbers(document, Value(document, actual, "D"))); }
                        Assert.False(annotation.TryGet(PdfName.FromAscii("Border"), out _));
                    }
                    else
                    {
                        Assert.Equal(kind == "zero" ? 0 : 3 * scale, Numeric(Value(document, actual, "W")));
                        Assert.Equal(kind == "zero" ? 0 : 3, Numeric(Value(document, prior, "W")));
                        if (kind is "bs" or "bs-direct")
                        {
                            Assert.Equal(new double[] { 4 * scale, 2 * scale }, Numbers(document, Value(document, actual, "D")));
                            Assert.Equal(new double[] { 4, 2 }, Numbers(document, Value(document, prior, "D")));
                        }
                        else { Assert.Equal("Bad"u8.ToArray(), Assert.IsType<PdfName>(Value(document, actual, "D")).RawSpan.ToArray()); }
                        Assert.Same(Value(document, original, "Border"), Value(document, annotation, "Border"));
                    }
                }
                else if (kind == "defaults")
                {
                    if (operation == "flatten") { Assert.False(annotation.TryGet(PdfName.FromAscii("Border"), out _)); }
                    else { Assert.Equal(new double[] { 0, 0, 2 }, Numbers(document, Value(document, annotation, "Border"))); }
                }
                else
                {
                    PdfArray border = Assert.IsType<PdfArray>(Value(document, annotation, "Border"));
                    Assert.Equal(4, border.Count);
                    Assert.Equal(new double[] { 5 * scale, 2 * scale, 3 * scale }, Dimensions(document, border));
                    Assert.Equal(new double[] { 4 * scale, 2 * scale }, Numbers(document, border[3]));
                    PdfArray prior = Assert.IsType<PdfArray>(Value(document, original, "Border"));
                    Assert.Equal(new double[] { 2, 5, 3 }, Dimensions(document, prior));
                    Assert.Equal(new double[] { 4, 2 }, Numbers(document, prior[3]));
                }
            }
            host.Dispose(); Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(escaped.TryGet(PdfName.FromAscii("Marker"), out QpdfObject marker));
            Assert.Equal("original"u8.ToArray(), Assert.IsType<PdfByteString>(marker).Value);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedBorderValuesKeepTheirProjectionIdentityAndPrivateOriginals(bool commonStyle)
    {
        string style = commonStyle ? "<< /W 3 /S /S /N 10 0 R >>" : "<< /W 3 /S /D /D 9 0 R >>";
        byte[] payload = "1 0 0 rg 0 0 40 20 re f"u8.ToArray();
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Rotate 90 /UserUnit 2 /Resources << >> /Annots [4 0 R 5 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Link /Rect [10 20 50 40] /F 4 /Private 6 0 R /BS 7 0 R" + (commonStyle ? " /AP 7 0 R" : "") + " >>", null),
            (5, "<< /Type /Annot /Subtype /Link /Rect [10 50 50 70] /F 20 /Private 6 0 R /BS " + (commonStyle ? "7" : "8") + " 0 R" + (commonStyle ? " /AP 7 0 R" : "") + " >>", null),
            (6, "<< /Style 7 0 R /OtherStyle 8 0 R /Dash 9 0 R /Appearance 10 0 R >>", null),
            (7, style, null), (8, "<< /W 4 /S /D /D 9 0 R >>", null), (9, "[4 2]", null),
            (10, "<< /Type /XObject /Subtype /Form /BBox [0 0 40 20] /Resources << >> /Length " + payload.Length + " >>", payload)], 1);
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Target(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), source, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--overlay", "stamp", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        PdfArray annotations = Assert.IsType<PdfArray>(Value(document, document.PageReferences[0].Page, "Annots"));
        PdfDictionary first = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
        PdfDictionary second = Assert.IsType<PdfDictionary>(Resolve(document, annotations[1]));
        PdfDictionary a = Assert.IsType<PdfDictionary>(Value(document, first, "BS")), b = Assert.IsType<PdfDictionary>(Value(document, second, "BS"));
        PdfDictionary original = Assert.IsType<PdfDictionary>(Value(document, first, "Private"));
        Assert.Equal(6, Numeric(Value(document, a, "W")));
        Assert.Equal(3, Numeric(Value(document, Assert.IsType<PdfDictionary>(Value(document, original, "Style")), "W")));
        if (commonStyle)
        {
            Assert.Same(a, b);
            PdfDictionary ap = Assert.IsType<PdfDictionary>(Value(document, first, "AP"));
            PdfDictionary upright = Assert.IsType<PdfDictionary>(Value(document, second, "AP"));
            Assert.NotSame(a, ap); Assert.NotSame(ap, upright);
            Assert.Equal(3, Numeric(Value(document, ap, "W"))); Assert.Equal(3, Numeric(Value(document, upright, "W")));
            Assert.Same(Value(document, original, "Appearance"), Value(document, a, "N"));
            Assert.NotSame(Value(document, original, "Appearance"), Value(document, ap, "N"));
            Assert.Same(Value(document, original, "Appearance"), Value(document, upright, "N"));
        }
        else
        {
            Assert.NotSame(a, b); Assert.Equal(8, Numeric(Value(document, b, "W")));
            Assert.Same(Value(document, a, "D"), Value(document, b, "D"));
            Assert.NotSame(Value(document, original, "Dash"), Value(document, a, "D"));
            Assert.Equal(new double[] { 8, 4 }, Numbers(document, Value(document, a, "D")));
            Assert.Equal(new double[] { 4, 2 }, Numbers(document, Value(document, original, "Dash")));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ImplicitDefaultsShareOneProjectionAcrossOrdinaryAndNoRotateUses(bool dashed, bool nullReference)
    {
        using InMemoryQpdfHost host = new();
        string style = dashed ? " /BS << /S /D" + (nullReference ? " /D 6 0 R" : "") + " >>"
            : nullReference ? " /Border 6 0 R" : "";
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Rotate 90 /UserUnit 2 /Resources << >> /Annots [4 0 R 5 0 R] >>", null),
            (4, "<< /Subtype /Link /Rect [10 20 90 80] /F 4" + style + " >>", null),
            (5, "<< /Subtype /Link /Rect [10 20 90 80] /F 20" + style + " >>", null), (6, "null", null)], 1);
        host.AddInput(QpdfPath.From("input"), Target(), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), source, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host, new(["input", "--overlay", "stamp", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        PdfArray annotations = Assert.IsType<PdfArray>(Value(document, Assert.Single(document.PageReferences).Page, "Annots"));
        Assert.Equal(2, annotations.Count);
        PdfDictionary first = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
        PdfDictionary second = Assert.IsType<PdfDictionary>(Resolve(document, annotations[1]));
        if (dashed)
        {
            first = Assert.IsType<PdfDictionary>(Value(document, first, "BS"));
            second = Assert.IsType<PdfDictionary>(Value(document, second, "BS"));
        }
        QpdfObject? a = Value(document, first, dashed ? "D" : "Border");
        QpdfObject? b = Value(document, second, dashed ? "D" : "Border");
        Assert.Same(a, b); Assert.Equal(dashed ? new double[] { 6 } : [0, 0, 2], Numbers(document, a));
    }

    private static byte[] Source(string kind, bool indirect)
    {
        string border = kind.EndsWith("-unused", StringComparison.Ordinal) || kind == "zero" ? "/Bad" : kind is "defaults" or "bs-defaults" ? "null"
            : "[" + (indirect ? "6 0 R" : "2") + " 5 " + (kind is "bs" or "bs-direct" ? "99 [1 1]" : "3 [4 2]") + "]";
        string bs = kind switch
        {
            "bs" or "bs-direct" => "<< /W 3 /S /D /D [4 2] >>",
            "bs-defaults" => "<< /S /D >>", "solid-unused" => "<< /W 3 /S /S /D /Bad >>",
            "beveled-unused" => "<< /W 3 /S /B /D /Bad >>", "inset-unused" => "<< /W 3 /S /I /D /Bad >>",
            "underline-unused" => "<< /W 3 /S /U /D /Bad >>", "unknown-unused" => "<< /W 3 /S /Other /D /Bad >>",
            "zero" => "<< /W 0 /S /D /D /Bad >>", _ => "null"
        };
        string fields = kind is "defaults" or "bs-defaults" ? "" : " /Border 5 0 R";
        if (kind is not ("border" or "defaults")) { fields += " /BS " + (kind == "bs-direct" ? bs : "7 0 R"); }
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Rotate 90 /UserUnit 2 /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Link /Rect [10 20 90 80] /F 4 /Private 8 0 R" + fields + " >>", null),
            (5, border, null), (6, "2", null), (7, bs, null), (8, "<< /Border 5 0 R /BS 7 0 R /Marker (original) >>", null)], 1);
    }

    private static byte[] Target() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 500] /Resources << >> >>", null)], 1);

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static QpdfObject? Value(QpdfDocument document, PdfDictionary dictionary, string key)
        => dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject raw) ? Resolve(document, raw) : null;
    private static double Numeric(QpdfObject? value)
        => value switch { PdfInteger integer => integer.Value, PdfReal real => real.Value, _ => throw new InvalidOperationException("Expected a numeric border value.") };
    private static double[] Dimensions(QpdfDocument document, PdfArray value)
        => [Numeric(Resolve(document, value[0])), Numeric(Resolve(document, value[1])), Numeric(Resolve(document, value[2]))];
    private static double[] Numbers(QpdfDocument document, QpdfObject? value)
    {
        PdfArray array = Assert.IsType<PdfArray>(value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value);
        double[] numbers = new double[array.Count];
        for (int index = 0; index < numbers.Length; index++) { numbers[index] = Numeric(Resolve(document, array[index])); }
        return numbers;
    }
}

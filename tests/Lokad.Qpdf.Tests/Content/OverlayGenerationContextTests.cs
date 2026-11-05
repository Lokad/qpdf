using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayGenerationContextTests
{
    private static readonly string FontDefinition = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding"
        + " /FirstChar 32 /LastChar 126 /Widths [" + string.Join(" ", Enumerable.Repeat("600", 95)) + "] >>";
    private static readonly byte[] OldAppearance = "/Tx BMC BT /F1 10 Tf 2 5 Td (old) Tj ET EMC"u8.ToArray();

    public static IEnumerable<object[]> DefaultsCases()
    {
        foreach (bool inline in new[] { false, true })
        foreach (bool appearance in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [inline, appearance, job]; }
    }

    [Theory]
    [MemberData(nameof(DefaultsCases))]
    public async Task GenerationUsesSourceDefaultsAndResourcesBeforePublication(bool inline, bool appearance, bool job)
    {
        RecordingMemoryBudget budget = new(16L * 1024 * 1024);
        using (IMemoryScope owner = budget.CreateScope())
        {
            PdfByteString escaped;
            using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
            {
                host.AddInput(QpdfPath.From("stamp"), Source(inline, appearance, 0, 1, true), QpdfInputOwnership.Transfer);
                host.AddInput(QpdfPath.From("input"), Target(false), QpdfInputOwnership.Transfer);
                QpdfExecutionResult reference = await Qpdf.ExecuteAsync(host,
                    new(["stamp", "--generate-appearances", "--compress-streams=n", "expected"]), CancellationToken.None);
                Assert.True(reference.ExitCode == QpdfExitCode.Success,
                    string.Join("\n", reference.Diagnostics.Select(d => d.Message))); Assert.Empty(reference.Diagnostics);
                Assert.True(host.TryGetOutput(QpdfPath.From("expected"), out byte[] expected));
                host.AddInput(QpdfPath.From("expected-readback"), expected, QpdfInputOwnership.Transfer);
                byte[] expectedBody;
                await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("expected-readback"), CancellationToken.None))
                { expectedBody = Appearance(document, Widget(document)).CopyData(owner, CancellationToken.None); }

                QpdfExecutionResult result = await Place(host, job);
                Assert.True(result.ExitCode == QpdfExitCode.Success,
                    string.Join("\n", result.Diagnostics.Select(d => d.Message))); Assert.Empty(result.Diagnostics);
                Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
                host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
                await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
                {
                    PdfDictionary widget = Widget(document); PdfStream stream = Appearance(document, widget);
                    PdfDictionary resources = Assert.IsType<PdfDictionary>(Value(document, stream.Dictionary, "Resources"));
                    PdfDictionary fonts = Assert.IsType<PdfDictionary>(Value(document, resources, "Font"));
                    Assert.True(fonts.Count > 0);
                    Assert.Equal(expectedBody, stream.CopyData(owner, CancellationToken.None));
                    PdfDictionary original = Assert.IsType<PdfDictionary>(Value(document, widget, "Private"));
                    escaped = Assert.IsType<PdfByteString>(Value(document, original, "DA"));
                }
                Assert.Equal(0, host.OpenLeaseCount);
            }
            Assert.Equal("/F1 10 Tf 0 g"u8.ToArray(), escaped.ValueSpan.ToArray());
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    public static IEnumerable<object[]> MissingCases()
    {
        foreach ((int rotation, int unit) in new[] { (0, 1), (0, 2), (90, 1), (90, 2) })
        foreach (bool job in new[] { false, true }) { yield return [rotation, unit, job]; }
    }

    [Theory]
    [MemberData(nameof(MissingCases))]
    public async Task MissingAppearanceKeepsSourceLayoutCoordinatesAndOrientation(int rotation, int unit, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("stamp"), Source(true, false, rotation, unit, true), QpdfInputOwnership.Transfer);
        // A primary font with equivalent metrics isolates source-frame behavior
        // from the separate missing imported-resource context regression.
        host.AddInput(QpdfPath.From("input"), Target(true), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Place(host, job);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None);
        PdfStream appearance = Appearance(document, Widget(document));
        Assert.Equal(new double[] { 0, 0, 60, 20 }, Numbers(document, Value(document, appearance.Dictionary, "BBox")));
        double[] matrix = Value(document, appearance.Dictionary, "Matrix") is QpdfObject raw
            ? Numbers(document, raw) : [1, 0, 0, 1, 0, 0];
        double scale = double.Hypot(matrix[0], matrix[1]);
        Assert.Equal(rotation == 0 ? new double[] { 1, 0, 0, 1 } : [0, -1, 1, 0],
            new double[] { matrix[0] / scale, matrix[1] / scale, matrix[2] / scale, matrix[3] / scale });
    }

    private static ValueTask<QpdfExecutionResult> Place(InMemoryQpdfHost host, bool job)
        => job ? Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
        { inputFile = "input", outputFile = "output", compressStreams = "n", generateAppearances = true, overlay = new { file = "stamp" } }), CancellationToken.None)
        : Qpdf.ExecuteAsync(host, new(["input", "--overlay", "stamp", "--", "--generate-appearances", "--compress-streams=n", "output"]), CancellationToken.None);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OnlySuccessfulImportsContributeTheGenerationGate(bool active, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
                + (active ? "" : "/TrimBox [200 0 300 100]") + " >>", null)], 1), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), Source(true, true, 0, 1, false), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("needed"), Source(true, true, 0, 1, true), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new
            { inputFile = "input", outputFile = "output", generateAppearances = true, compressStreams = "n", overlay = new { file = "stamp", to = "1" }, underlay = new { file = "needed", to = "2" } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--overlay", "stamp", "--to=1", "--", "--underlay", "needed", "--to=2", "--", "--generate-appearances", "--compress-streams=n", "output"]), CancellationToken.None);
        Assert.Equal(active ? QpdfExitCode.Success : QpdfExitCode.Warning, result.ExitCode);
        if (!active) { Assert.Contains("empty media intersection", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal); }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            PdfArray annotations = Assert.IsType<PdfArray>(Value(document, document.PageReferences[0].Page, "Annots"));
            PdfDictionary widget = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
            PdfStream appearance = Appearance(document, widget);
            if (active) { Assert.Contains("(WXYZ) Tj", System.Text.Encoding.ASCII.GetString(appearance.RawMemory.Span), StringComparison.Ordinal); }
            else { Assert.Equal(OldAppearance, appearance.RawMemory.ToArray()); }
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }

    internal static byte[] Source(bool inline, bool appearance, int rotation, int unit, bool need)
        => CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Rotate " + rotation.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " /UserUnit " + unit.ToString(System.Globalization.CultureInfo.InvariantCulture) + " /Resources << /Font << /F1 7 0 R >> >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (field) /V (WXYZ) /F 4 /Rect [20 40 80 60] /Private << /DA 8 0 R >>"
                + (inline ? " /DA 8 0 R /Q 2" : "") + (appearance ? " /AP << /N 5 0 R >>" : "") + " >>", null),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 60 20] /Resources << /Font << /F1 7 0 R >> >> /Length "
                + OldAppearance.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " >>", OldAppearance),
            (6, "<< /Fields [4 0 R] /NeedAppearances " + (need ? "true" : "false") + " /DR << /Font << /F1 7 0 R >> >> /DA 8 0 R /Q 2 >>", null),
            (7, FontDefinition, null), (8, "(/F1 10 Tf 0 g)", null)], 1);

    private static byte[] Target(bool font)
    {
        List<(int Id, string Body, byte[]? Stream)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R" + (font ? " /AcroForm << /Fields [] /DR << /Font << /F1 4 0 R >> >> >>" : "") + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 500] /Resources << >> >>", null)];
        if (font) { objects.Add((4, FontDefinition, null)); }
        return CheckTests.Assemble(objects, 1);
    }

    private static PdfDictionary Widget(QpdfDocument document)
    {
        PdfArray annotations = Assert.IsType<PdfArray>(Value(document, Assert.Single(document.PageReferences).Page, "Annots"));
        Assert.Equal(1, annotations.Count); return Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
    }
    private static PdfStream Appearance(QpdfDocument document, PdfDictionary widget)
        => Assert.IsType<PdfStream>(Value(document, Assert.IsType<PdfDictionary>(Value(document, widget, "AP")), "N"));
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static QpdfObject? Value(QpdfDocument document, PdfDictionary dictionary, string name)
        => dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value) ? Resolve(document, value) : null;
    private static double[] Numbers(QpdfDocument document, QpdfObject? value)
    {
        PdfArray array = Assert.IsType<PdfArray>(value); double[] numbers = new double[array.Count];
        for (int index = 0; index < array.Count; index++)
        { numbers[index] = Resolve(document, array[index]) switch { PdfInteger n => n.Value, PdfReal n => n.Value, _ => throw new InvalidOperationException("Expected a number.") }; }
        return numbers;
    }
}

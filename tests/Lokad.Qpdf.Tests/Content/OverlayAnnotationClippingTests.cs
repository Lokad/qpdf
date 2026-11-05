using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayAnnotationClippingTests
{
    private static readonly byte[] Original = "1 0 0 rg 0 0 40 40 re f 0 0 1 rg 0 0 10 40 re f"u8.ToArray();
    private static readonly byte[] Other = "0 1 0 rg 0 0 40 40 re f"u8.ToArray();

    public static IEnumerable<object[]> Cases()
    {
        foreach (string position in new[] { "inside", "partial", "outside" })
        foreach (bool states in new[] { false, true })
        foreach (bool underlay in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [position, states, underlay, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SourceClipKeepsFullRectAndPrivateAppearances(string position, bool states, bool underlay, bool job)
    {
        RecordingMemoryBudget budget = new(16L * 1024 * 1024);
        using (IMemoryScope owner = budget.CreateScope())
        {
            PdfStream escaped, rendered; byte[] renderedBytes;
            using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
            {
                host.AddInput(QpdfPath.From("input"), Target(), QpdfInputOwnership.Transfer);
                host.AddInput(QpdfPath.From("stamp"), Source(position, states), QpdfInputOwnership.Transfer);
                string operation = underlay ? "underlay" : "overlay";
                QpdfExecutionResult result = job
                    ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object>
                    { ["inputFile"] = "input", ["outputFile"] = "output", ["compressStreams"] = "n", [operation] = new { file = "stamp" } }), CancellationToken.None)
                    : await Qpdf.ExecuteAsync(host, new(["input", "--" + operation, "stamp", "--", "--compress-streams=n", "output"]), CancellationToken.None);
                Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
                Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
                host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
                await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
                {
                    PdfArray annotations = Assert.IsType<PdfArray>(Value(document, Assert.Single(document.PageReferences).Page, "Annots"));
                    Assert.Equal(2, annotations.Count);
                    PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
                    PdfDictionary inside = Assert.IsType<PdfDictionary>(Resolve(document, annotations[1]));
                    double[] rect = Numbers(document, Value(document, annotation, "Rect"));
                    Assert.Equal(position switch { "partial" => new double[] { 180, 220, 220, 260 }, "outside" => [140, 220, 180, 260], _ => [220, 220, 260, 260] }, rect);
                    PdfDictionary appearance = Assert.IsType<PdfDictionary>(Value(document, annotation, "AP"));
                    QpdfObject normal = Assert.IsType<QpdfObject>(Value(document, appearance, "N"), exactMatch: false);
                    if (states) { normal = Value(document, Assert.IsType<PdfDictionary>(normal), "On") ?? throw new InvalidOperationException(); }
                    PdfStream stream = Assert.IsType<PdfStream>(normal);
                    rendered = stream; renderedBytes = stream.CopyData(owner, CancellationToken.None);
                    if (position == "inside") { Assert.Equal(new double[] { 0, 0, 40, 40 }, Numbers(document, Value(document, stream.Dictionary, "BBox"))); }
                    else
                    {
                        Assert.Equal(rect, Numbers(document, Value(document, stream.Dictionary, "BBox")));
                        string body = System.Text.Encoding.ASCII.GetString(stream.RawMemory.Span);
                        Assert.Contains(position == "outside" ? "0 0 0 0 re W n" : "200 220 20 40 re W n", body, StringComparison.Ordinal);
                        PdfDictionary resources = Assert.IsType<PdfDictionary>(Value(document, stream.Dictionary, "Resources"));
                        PdfDictionary objects = Assert.IsType<PdfDictionary>(Value(document, resources, "XObject"));
                        Assert.Equal(1, objects.Count);
                    }
                    PdfDictionary privateAppearance = Assert.IsType<PdfDictionary>(Value(document,
                        Assert.IsType<PdfDictionary>(Value(document, annotation, "Private")), "AP"));
                    PdfStream original = states
                        ? Assert.IsType<PdfStream>(Value(document, Assert.IsType<PdfDictionary>(Value(document, privateAppearance, "N")), "On"))
                        : Assert.IsType<PdfStream>(Value(document, privateAppearance, "N"));
                    escaped = original;
                    Assert.Equal(Original, original.CopyData(owner, CancellationToken.None));
                    Assert.Null(Value(document, original.Dictionary, "Matrix"));
                    PdfDictionary insideAp = Assert.IsType<PdfDictionary>(Value(document, inside, "AP"));
                    PdfStream insideStream = states
                        ? Assert.IsType<PdfStream>(Value(document, Assert.IsType<PdfDictionary>(Value(document, insideAp, "N")), "On"))
                        : Assert.IsType<PdfStream>(Value(document, insideAp, "N"));
                    Assert.Equal(new double[] { 0, 0, 40, 40 }, Numbers(document, Value(document, insideStream.Dictionary, "BBox")));
                    Assert.Equal(Original, insideStream.CopyData(owner, CancellationToken.None));
                    PdfDictionary normalStates = states ? Assert.IsType<PdfDictionary>(Value(document, appearance, "N")) : appearance;
                    PdfDictionary rollover = states ? Assert.IsType<PdfDictionary>(Value(document, appearance, "R")) : appearance;
                    Assert.Equal(Raw(normalStates, states ? "On" : "N"), Raw(rollover, states ? "On" : "R"));
                    if (states)
                    {
                        Assert.Equal(Raw(normalStates, "Off"), Raw(rollover, "Off"));
                        Assert.Equal(Raw(normalStates, "On"), Raw(appearance, "D"));
                    }
                    else { Assert.NotEqual(Raw(appearance, "N"), Raw(appearance, "D")); }
                    Assert.Same(original, Value(document, appearance, "Private"));
                    if (position != "inside")
                    {
                        PdfDictionary resources = Assert.IsType<PdfDictionary>(Value(document, stream.Dictionary, "Resources"));
                        PdfDictionary objects = Assert.IsType<PdfDictionary>(Value(document, resources, "XObject"));
                        Assert.Same(insideStream, Value(document, objects, "Original"));
                    }
                }
                Assert.Equal(0, host.OpenLeaseCount);
            }
            Assert.Equal(Original, escaped.CopyData(owner, CancellationToken.None));
            Assert.Equal(renderedBytes, rendered.CopyData(owner, CancellationToken.None));
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    internal static byte[] Source(string position, bool states)
    {
        string rect = position switch { "partial" => "[-20 20 20 60]", "outside" => "[-60 20 -20 60]", _ => "[20 20 60 60]" };
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R 9 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Stamp /F 4 /Rect " + rect + " /AP 10 0 R /AS /On /Private << /AP 10 0 R >> >>", null),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 40 40] /Resources << >> /Length " + Original.Length + " >>", Original),
            (6, "<< /Type /XObject /Subtype /Form /BBox [0 0 40 40] /Resources << >> /Length " + Other.Length + " >>", Other),
            (7, "null", null), (8, "null", null),
            (9, "<< /Type /Annot /Subtype /Stamp /F 4 /Rect [20 20 60 60] /AP 10 0 R /AS /On >>", null),
            (10, states ? "<< /N << /On 5 0 R /Off 6 0 R >> /R << /On 5 0 R /Off 6 0 R >> /D 5 0 R /Private 5 0 R >>"
                : "<< /N 5 0 R /R 5 0 R /D 6 0 R /Private 5 0 R >>", null)], 1);
    }

    internal static byte[] Target() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 500] /Resources << >> >>", null)], 1);

    internal static byte[] StateSource(int count)
    {
        string states = "<< " + string.Join(" ", Enumerable.Range(0, count).Select(index => "/s" + index + " " + (5 + index) + " 0 R")) + " >>";
        List<(int Number, string Dictionary, byte[]? Data)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Stamp /F 4 /Rect [-20 20 20 60] /AS /s0 /AP << /N " + states + " /R " + states + " >> >>", null)];
        foreach (int index in Enumerable.Range(0, count))
        { objects.Add((5 + index, "<< /Type /XObject /Subtype /Form /BBox [0 0 40 40] /Resources << >> /Length " + Original.Length + " >>", Original)); }
        return CheckTests.Assemble(objects, 1);
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static QpdfObject? Value(QpdfDocument document, PdfDictionary dictionary, string name)
        => dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value) ? Resolve(document, value) : null;
    private static QpdfObject Raw(PdfDictionary dictionary, string name)
        => dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value) ? value : throw new InvalidOperationException();
    private static double[] Numbers(QpdfDocument document, QpdfObject? value)
    {
        PdfArray array = Assert.IsType<PdfArray>(value); double[] numbers = new double[array.Count];
        for (int index = 0; index < array.Count; index++)
        { numbers[index] = Resolve(document, array[index]) switch { PdfInteger n => n.Value, PdfReal n => n.Value, _ => throw new InvalidOperationException() }; }
        return numbers;
    }
}

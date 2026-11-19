using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayAppearanceIdentityTests
{
    private static readonly byte[] Payload = "1 0 0 rg 0 0 30 20 re f"u8.ToArray();
    private static readonly byte[] DestinationProgram = "0 0 30 20 re f"u8.ToArray();
    private static readonly byte[] SourceProgram = "0 1 0 rg 0 0 30 20 re f"u8.ToArray();

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in new[] { "stream", "states", "indirect-states", "container", "shared-states" })
        foreach (bool resources in new[] { false, true })
        foreach (bool matrix in new[] { false, true })
        { yield return [shape, resources, matrix, false, "overlay", false]; }
        yield return ["shared-states", false, true, true, "overlay", false];
        yield return ["shared-states", false, true, true, "underlay", false];
        yield return ["indirect-states", true, false, true, "underlay", true];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PlacementCopiesAppearanceRolesWithoutChangingPrivateAliases(
        string shape, bool resources, bool matrix, bool mixedFlags, string mode, bool job)
    {
        RecordingMemoryBudget budget = new(32L * 1048576L);
        using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        host.AddInput(QpdfPath.From("source"), Fixture(shape, resources, matrix, mixedFlags), QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("input"), PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << >> /Contents 4 0 R >>", null),
            (4, $"<< /Length {DestinationProgram.Length} >>", DestinationProgram)], 1), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object>
                { ["inputFile"] = "input", ["outputFile"] = "output", ["compressStreams"] = "n", ["decodeLevel"] = "none", [mode] = new { file = "source" } }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--compress-streams=n", "--decode-level=none", "--" + mode, "source", "--", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        PdfStream escapedOriginal, escapedPlaced;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            PdfDictionary page = document.PageReferences[0].Page;
            AssertPlacementPaintOrder(document, page, mode);
            PdfArray annotations = Assert.IsType<PdfArray>(Get(document, page, "Annots"));
            Assert.Equal(2, annotations.Count);
            PdfDictionary first = Assert.IsType<PdfDictionary>(Resolve(document, annotations[0]));
            PdfDictionary second = Assert.IsType<PdfDictionary>(Resolve(document, annotations[1]));
            Assert.Equal(4L, Assert.IsType<PdfInteger>(Get(document, first, "F")).Value);
            Assert.Equal(mixedFlags ? 20L : 4L, Assert.IsType<PdfInteger>(Get(document, second, "F")).Value);
            escapedOriginal = Assert.IsType<PdfStream>(Get(document, first, "Private"));
            Assert.Equal(matrix, escapedOriginal.Dictionary.TryGet(PdfName.FromAscii("Matrix"), out QpdfObject originalMatrix));
            if (matrix) { Assert.Equal(new double[] { 1, 0, 0, 1, 2, 3 }, Numbers(Assert.IsType<PdfArray>(originalMatrix))); }
            Assert.Equal(resources, escapedOriginal.Dictionary.TryGet(PdfName.FromAscii("Resources"), out _));
            Assert.Same(escapedOriginal, Get(document, second, "Private"));
            PdfDictionary privateAp = Assert.IsType<PdfDictionary>(Get(document, first, "PrivateAP"));
            Assert.Same(escapedOriginal, Get(document, privateAp, "Private"));
            PdfDictionary privateStates = Assert.IsType<PdfDictionary>(Get(document, first, "PrivateStates"));
            Assert.Same(escapedOriginal, Get(document, privateStates, "On"));
            PdfDictionary ap = Assert.IsType<PdfDictionary>(Get(document, first, "AP"));
            PdfDictionary otherAp = Assert.IsType<PdfDictionary>(Get(document, second, "AP"));
            escapedPlaced = Normal(document, ap);
            Assert.NotSame(escapedOriginal, escapedPlaced);
            Assert.Same(escapedPlaced, Normal(document, otherAp));
            if (shape == "shared-states") { Assert.Same(Get(document, ap, "N"), Get(document, ap, "R")); }
            else { Assert.Same(escapedPlaced, Get(document, ap, "R")); }
            Assert.Same(escapedPlaced, Get(document, ap, "D"));
            Assert.Same(escapedOriginal, Get(document, ap, "Private"));
            Assert.Same(privateStates, Get(document, ap, "PrivateStates"));
            Assert.Equal(new double[] { 1, 0, 0, 1, matrix ? 102 : 100, matrix ? 103 : 100 },
                Numbers(Assert.IsType<PdfArray>(Get(document, escapedPlaced.Dictionary, "Matrix"))));
            PdfDictionary placedResources = Assert.IsType<PdfDictionary>(Get(document, escapedPlaced.Dictionary, "Resources"));
            Assert.Equal(!resources, placedResources.TryGet(PdfName.FromAscii("Font"), out _));
            if (shape is "container" or "shared-states") { Assert.Same(ap, otherAp); Assert.NotSame(privateAp, ap); }
            if (shape == "shared-states")
            {
                Assert.Same(privateStates, Get(document, ap, "Alternate"));
                Assert.NotSame(privateStates, Get(document, ap, "N"));
            }
        }
        host.Dispose();
        Assert.True(budget.ReservedBytes > 0L);
        Assert.True(escapedOriginal.RawMemory.Span.SequenceEqual(Payload));
        Assert.True(escapedPlaced.RawMemory.Span.SequenceEqual(Payload));
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    private static void AssertPlacementPaintOrder(QpdfDocument document, PdfDictionary page, string mode)
    {
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(document, page, "Resources"));
        PdfDictionary objects = Assert.IsType<PdfDictionary>(Get(document, resources, "XObject"));
        PdfStream destination = Assert.IsType<PdfStream>(Get(document, objects, "Fx0"));
        PdfStream source = Assert.IsType<PdfStream>(Get(document, objects, "Fx1"));
        Assert.Equal(DestinationProgram, destination.RawMemory.ToArray());
        Assert.Equal(SourceProgram, source.RawMemory.ToArray());
        PdfStream contents = Assert.IsType<PdfStream>(Get(document, page, "Contents"));
        string program = Encoding.ASCII.GetString(contents.RawMemory.Span);
        int originalPaint = program.IndexOf("/Fx0 Do", StringComparison.Ordinal);
        int placedPaint = program.IndexOf("/Fx1 Do", StringComparison.Ordinal);
        Assert.True(originalPaint >= 0 && placedPaint >= 0);
        Assert.Equal(mode == "overlay", originalPaint < placedPaint);
    }

    private static PdfStream Normal(QpdfDocument document, PdfDictionary ap)
    {
        QpdfObject value = Get(document, ap, "N");
        return value is PdfStream stream ? stream : Assert.IsType<PdfStream>(Get(document, Assert.IsType<PdfDictionary>(value), "On"));
    }

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;

    private static QpdfObject Get(QpdfDocument document, PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(document, value); }

    private static double[] Numbers(PdfArray array)
        => array.Items.Select(item => item is PdfInteger integer ? (double)integer.Value : Assert.IsType<PdfReal>(item).Value).ToArray();

    private static byte[] Fixture(string shape, bool resources, bool matrix, bool mixedFlags)
    {
        string normal = shape switch { "states" => "<< /On 7 0 R /Off 7 0 R >>", "indirect-states" or "shared-states" => "9 0 R", _ => "7 0 R" };
        string header = "<< /N " + normal + " /R " + (shape == "shared-states" ? "9 0 R" : "7 0 R") + " /D 7 0 R /Private 7 0 R /PrivateStates 9 0 R"
            + (shape == "shared-states" ? " /Alternate 9 0 R" : "") + " >>";
        string ap = shape is "container" or "shared-states" ? "8 0 R" : header;
        string Annot(int flags) => "<< /Type /Annot /Subtype /Stamp /Rect [10 20 40 40] /AS /On /F " + flags
            + " /AP " + ap + " /Private 7 0 R /PrivateAP 8 0 R /PrivateStates 9 0 R >>";
        return PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << /Font << /F1 10 0 R >> >> /Contents 11 0 R /Annots [5 0 R 6 0 R] >>", null),
            (4, "null", null), (5, Annot(4), null), (6, Annot(mixedFlags ? 20 : 4), null),
            (7, "<< /Subtype /Form /BBox [0 0 30 20] /Length " + Payload.Length
                + (resources ? " /Resources << >>" : "") + (matrix ? " /Matrix [1 0 0 1 2 3]" : "") + " >>", Payload),
            (8, header, null), (9, "<< /On 7 0 R /Off 7 0 R >>", null),
            (10, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (11, $"<< /Length {SourceProgram.Length} >>", SourceProgram)], 1);
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class NoRotateFrameTests
{
    public static IEnumerable<object[]> Frames()
    {
        foreach (int angle in new[] { 90, 180, 270 })
        foreach (string mode in new[] { "flatten", "overlay", "underlay" })
        foreach (bool job in new[] { false, true }) { yield return [angle, mode, job, 20]; }
        foreach (string mode in new[] { "flatten", "overlay", "underlay" })
        foreach (bool job in new[] { false, true }) { yield return [90, mode, job, 4]; }
        foreach (string mode in new[] { "flatten", "overlay" })
        foreach (bool job in new[] { false, true }) { yield return [90, mode, job, 28]; }
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public async Task FrameRespectsOrientationFlagsAndKeepsOriginalAliases(int angle, string mode, bool job, int flags)
    {
        int unit = angle == 180 ? 2 : 1;
        RecordingMemoryBudget budget = new(32L * 1048576L);
        using (IMemoryScope owner = budget.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        {
            bool rotatedTarget = job && angle == 270;
            await Run(host, Source(false, false, angle, unit, flags, job), mode, job, rotatedTarget);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("readback"), output, QpdfInputOwnership.Transfer);
            PdfStream escaped;
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
            {
                PdfDictionary page = document.PageReferences[0].Page;
                PdfDictionary annotation = Annotation(document, page, 0);
                Assert.Equal((long)flags, Assert.IsType<PdfInteger>(Resolve(document, Read(annotation, "F"))).Value);
                bool noRotate = (flags & 16) != 0;
                double scale = mode == "flatten" ? 1 : unit;
                (double x, double y) = Point(40, 100, angle);
                (double dx, double dy) = Offset(angle, unit, mode);
                (double ax, double ay) = Destination(scale * x + dx, scale * y + dy, rotatedTarget && mode != "flatten");
                double[] expected = noRotate ? [ax, ay - 20 * scale, ax + 30 * scale, ay]
                    : [80 + dx, 30 + dy, 100 + dx, 60 + dy];
                Assert.Equal(expected, Numbers(document, Read(annotation, "Rect")));
                QpdfObject[] coordinates = Assert.IsType<PdfArray>(Resolve(document, Read(annotation, "L"))).Items;
                double[] actual = coordinates.Select(item => Number(Resolve(document, item))).ToArray();
                double[] points = [42, 82, 68, 98];
                for (int index = 0; index < points.Length; index += 2)
                {
                    (double px, double py) = Point(points[index], points[index + 1], angle);
                    (double ex, double ey) = noRotate ? (scale * (points[index] - 40) + ax, scale * (points[index + 1] - 100) + ay)
                        : Destination(scale * px + dx, scale * py + dy, rotatedTarget && mode != "flatten");
                    Assert.Equal(ex, actual[index]); Assert.Equal(ey, actual[index + 1]);
                }
                Assert.Equal(points, Numbers(document, Read(annotation, "PrivateLine")));
                QpdfObject original = Read(annotation, "PrivateAppearance");
                QpdfObject appearance = Read(Assert.IsType<PdfDictionary>(Resolve(document, Read(annotation, "AP"))), "N");
                if (noRotate) { Assert.Equal(Assert.IsType<PdfReference>(original), Assert.IsType<PdfReference>(appearance)); }
                else { Assert.NotEqual(Assert.IsType<PdfReference>(original), Assert.IsType<PdfReference>(appearance)); }
                escaped = Assert.IsType<PdfStream>(Resolve(document, original));
                Assert.False(escaped.Dictionary.TryGet(PdfName.FromAscii("Matrix"), out _));
            }
            host.Dispose(); Assert.Equal(0L, host.OpenLeaseCount);
            Assert.Equal(PlainAppearance, escaped.RawMemory.ToArray());
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData("flatten", false)]
    [InlineData("flatten", true)]
    [InlineData("overlay", false)]
    [InlineData("overlay", true)]
    [InlineData("underlay", false)]
    [InlineData("underlay", true)]
    public async Task MixedFlagsKeepDistinctFramesAndBindInheritedAppearanceResources(string mode, bool job)
    {
        using InMemoryQpdfHost host = new();
        await Run(host, Source(true, true, 90, 2, 20, job), mode, job, job);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("readback"), bytes, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("readback"), CancellationToken.None))
        {
            PdfDictionary page = document.PageReferences[0].Page;
            PdfDictionary fixedAnnotation = Annotation(document, page, 0), ordinary = Annotation(document, page, 1);
            PdfDictionary fixedAp = Assert.IsType<PdfDictionary>(Resolve(document, Read(fixedAnnotation, "AP")));
            PdfDictionary ordinaryAp = Assert.IsType<PdfDictionary>(Resolve(document, Read(ordinary, "AP")));
            QpdfObject fixedN = Read(fixedAp, "N"), ordinaryN = Read(ordinaryAp, "N");
            bool differentFrames = mode == "flatten" || !job;
            if (differentFrames) { Assert.NotEqual(Assert.IsType<PdfReference>(fixedN), Assert.IsType<PdfReference>(ordinaryN)); }
            else { Assert.Equal(Assert.IsType<PdfReference>(fixedN), Assert.IsType<PdfReference>(ordinaryN)); }
            Assert.Equal(fixedN, Read(fixedAp, "R"));
            Assert.Equal(fixedN, Read(Assert.IsType<PdfDictionary>(Resolve(document, Read(fixedAp, "D"))), "On"));
            PdfStream fixedStream = Assert.IsType<PdfStream>(Resolve(document, fixedN));
            Assert.Equal(differentFrames ? new double[] { 1, 0.2, 0.3, 1, 3, 4 } : new double[] { 2, 0.4, 0.6, 2, 156, 58 },
                Numbers(document, Read(fixedStream.Dictionary, "Matrix")));
            PdfStream original = Assert.IsType<PdfStream>(Resolve(document, Read(fixedAnnotation, "PrivateAppearance")));
            Assert.False(original.Dictionary.TryGet(PdfName.FromAscii("Resources"), out _));
            PdfDictionary resources = mode == "flatten" ? Assert.IsType<PdfDictionary>(Resolve(document, Read(page, "Resources")))
                : Assert.IsType<PdfDictionary>(Resolve(document, Read(fixedStream.Dictionary, "Resources")));
            PdfDictionary fonts = Assert.IsType<PdfDictionary>(Resolve(document, Read(resources, "Font")));
            PdfDictionary font = Assert.IsType<PdfDictionary>(Resolve(document, Read(fonts, "F1")));
            Assert.Equal("Helvetica"u8.ToArray(), Assert.IsType<PdfName>(Read(font, "BaseFont")).RawSpan.ToArray());
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlagsResolutionChecksBothCancellationRoutesBeforeSelectingAFrame(bool caller)
    {
        using CancellationTokenSource cancellation = new();
        CancelingObjects objects = new(cancellation.Cancel) { [1] = new PdfInteger("20", 20) };
        PoolResolver pool = new(objects);
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("F")] = new PdfReference(1, 0) });
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using (ParserMemoryScope scratch = ParserMemoryScope.Create(governor))
        {
            scratch.Reserve(256L);
            long before = governor.Snapshot().LiveMemoryBytes;
            Assert.ThrowsAny<OperationCanceledException>(() => AnnotationCoordinates.SelectNoRotateFrame(annotation,
                [40, 80, 70, 100], [0, -1, 1, 0, 0, 100], pool, governor,
                caller ? cancellation.Token : CancellationToken.None, out _));
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task RefusalDuringNoRotateRewriteKeepsTheOriginalHeaderAndRefundsScratch()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        PdfDictionary source = new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Line"),
            [PdfName.FromAscii("F")] = new PdfInteger("20", 20),
            [PdfName.FromAscii("Rect")] = new PdfArray([new PdfInteger("40", 40), new PdfInteger("80", 80), new PdfInteger("70", 70), new PdfInteger("100", 100)]),
            [PdfName.FromAscii("Payload")] = PdfStream.FromOwnedData(new(new Dictionary<PdfName, QpdfObject>()), new byte[8 * 1048576]),
        });
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(construction);
        GraphCopier copier = GraphCopier.CreateOwned(document.Engine, construction, QpdfLimits.Default, 1, copierMemory);
        copier.AddIndirectObjectWithId(source, 1);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 768L }, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            long before = governor.Snapshot().LiveMemoryBytes, allocated = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<QpdfLimitException>(() => Overlays.TransformOverlayRect(copier, 1, [0, -1, 1, 0, 0, 100], governor, memory, CancellationToken.None));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 65536L);
            Assert.Same(source, copier.CopiedObjects[1]); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    private sealed class CancelingObjects(Action cancel) : Dictionary<int, QpdfObject>, IReadOnlyDictionary<int, QpdfObject>
    {
        bool IReadOnlyDictionary<int, QpdfObject>.TryGetValue(int key, [MaybeNullWhen(false)] out QpdfObject value)
        { bool found = base.TryGetValue(key, out value); cancel(); return found; }
    }

    private static readonly byte[] PlainAppearance = "1 0 0 rg 0 0 30 20 re f 0 1 0 rg 0 0 10 10 re f"u8.ToArray();

    private static byte[] Source(bool mixed, bool inherited, int angle, int unit, int flags, bool indirect)
    {
        byte[] data = inherited ? "BT /F1 8 Tf 1 2 Td (Ab) Tj ET"u8.ToArray() : PlainAppearance;
        string number(int value) => value.ToString(CultureInfo.InvariantCulture);
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Rotate " + number(angle) + " /UserUnit " + number(unit)
                + " /Resources << /Font << /F1 10 0 R >> >> /Annots [4 0 R" + (mixed ? " 8 0 R" : "") + "] >>", null),
            (4, "<< /Type /Annot /Subtype " + (indirect ? "11 0 R" : "/Line")
                + " /Rect " + (indirect ? "[70 100 40 80]" : "[40 80 70 100]") + " /F "
                + (indirect ? "12 0 R" : number(flags)) + " /L 6 0 R /PrivateLine 6 0 R /PrivateAppearance 5 0 R /AP 7 0 R /AS /On >>", null),
            (5, "<< /Type /XObject /Subtype /Form /BBox [0 0 30 20] "
                + (inherited ? "/Matrix [1 .2 .3 1 3 4] " : "/Resources << >> ") + "/Length " + number(data.Length) + " >>", data),
            (6, "[42 82 68 98]", null),
            (7, "<< /N 5 0 R /R 5 0 R /D << /On 5 0 R >> /Private 5 0 R >>", null),
            (8, "<< /Type /Annot /Subtype /Line /Rect [40 120 70 140] /F 4 /L [42 122 68 138] /AP 7 0 R >>", null),
            (9, "null", null), (10, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (11, "/Line", null), (12, number(flags), null),
        ], 1);
    }

    private static async Task Run(InMemoryQpdfHost host, byte[] source, string mode, bool job, bool rotatedTarget)
    {
        byte[] target = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null), (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 500] " + (rotatedTarget ? "/Rotate 90 " : "")
                + "/Resources << /Font << /F1 4 0 R >> >> >>", null),
            (4, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null),
        ], 1);
        host.AddInput(QpdfPath.From("input"), mode == "flatten" ? source : target, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), source, QpdfInputOwnership.Copy);
        Dictionary<string, object> settings = new()
        { ["inputFile"] = "input", ["outputFile"] = "output", ["compressStreams"] = "n", ["decodeLevel"] = "none" };
        if (mode == "flatten") { settings["flattenRotation"] = true; } else { settings[mode] = new { file = "stamp" }; }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(mode == "flatten"
                ? ["input", "--flatten-rotation", "--compress-streams=n", "--decode-level=none", "output"]
                : ["input", "--" + mode, "stamp", "--", "--compress-streams=n", "--decode-level=none", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static (double X, double Y) Point(double x, double y, int angle) => angle switch
    { 90 => (y, 100 - x), 180 => (100 - x, 200 - y), 270 => (200 - y, x), _ => (x, y) };

    private static (double X, double Y) Destination(double x, double y, bool rotated) => rotated ? (500 - y, x) : (x, y);

    private static (double X, double Y) Offset(int angle, int unit, string mode)
        => mode == "flatten" ? (0, 0) : angle is 90 or 270 ? ((500 - 200 * unit) / 2.0, (500 - 100 * unit) / 2.0)
            : ((500 - 100 * unit) / 2.0, (500 - 200 * unit) / 2.0);

    private static PdfDictionary Annotation(QpdfDocument document, PdfDictionary page, int index)
        => Assert.IsType<PdfDictionary>(Resolve(document, Assert.IsType<PdfArray>(Resolve(document, Read(page, "Annots")))[index]));
    private static QpdfObject Read(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
    private static QpdfObject Resolve(QpdfDocument document, QpdfObject value)
        => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
    private static double Number(QpdfObject value) => value switch
    { PdfInteger integer => integer.Value, PdfReal real => real.Value, _ => throw new Xunit.Sdk.XunitException("Expected a geometry number.") };
    private static double[] Numbers(QpdfDocument document, QpdfObject value)
        => Assert.IsType<PdfArray>(Resolve(document, value)).Items.Select(item => Number(Resolve(document, item))).ToArray();
}

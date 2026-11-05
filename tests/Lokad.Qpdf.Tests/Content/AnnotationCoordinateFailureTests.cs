using System.Text.Json;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AnnotationCoordinateFailureTests
{
    public static IEnumerable<object[]> Shapes()
    {
        (string Subtype, string Key, string Value, bool Fails)[] shapes = [
            ("Line", "L", "[1 /Bad 2 3]", true), ("Line", "L", "[1 2]", true),
            ("Polygon", "Vertices", "[1 2 3]", true), ("Ink", "InkList", "[42]", true),
            ("Ink", "InkList", "[[1 2 3]]", true), ("Highlight", "QuadPoints", "[1 2]", true),
            ("Highlight", "QuadPoints", "null", false), ("FreeText", "CL", "[1 2 3 4 5 6 7 8]", true),
            ("FreeText", "CL", "null", false),
            ("Square", "RD", "[1 2]", true), ("Circle", "RD", "[1 /Bad 2 3]", true),
            ("Caret", "RD", "null", false),
            ("Line", "LL", "/Bad", true), ("Line", "CO", "[1]", true),
            ("Line", "CO", "[1 /Bad]", true),
            ("Stamp", "L", "[/Private]", false), ("Private", "Vertices", "/Opaque", false),
            ("Link", "QuadPoints", "null", false), ("Ink", "InkList", "[[] [1 2]]", false)];
        foreach (var shape in shapes)
        foreach (string mode in new[] { "flatten", "overlay", "underlay" })
        foreach (bool job in new[] { false, true })
        {
            bool scaledDistance = shape.Key is "LL" or "CO";
            yield return [shape.Subtype, shape.Key, shape.Value, shape.Fails && (!scaledDistance || mode != "flatten"), mode, job];
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task InvalidRecognizedCoordinatesRefusePublicationAndPrivateFieldsStayOpaque(
        string subtype, string key, string value, bool fails, string mode, bool job)
    {
        RecordingMemoryBudget budget = new(32L * 1048576L);
        using (IMemoryScope owner = budget.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        {
            byte[] source = CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Rotate 90 "
                    + (key is "LL" or "CO" ? "/UserUnit 2 " : "") + "/Resources << >> /Annots [4 0 R] >>", null),
                (4, "<< /Type /Annot /Subtype /" + subtype + " /Rect [10 20 40 40] "
                    + (key is "CO" ? "/Cap true " : "") + "/" + key + " 5 0 R >>", null),
                (5, value, null)], 1);
            byte[] target = CheckTests.Assemble([
                (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << >> >>", null)], 1);
            host.AddInput(QpdfPath.From("input"), mode == "flatten" ? source : target, QpdfInputOwnership.Transfer);
            host.AddInput(QpdfPath.From("stamp"), source, QpdfInputOwnership.Copy);
            await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
            { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
            Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
            if (mode == "flatten") { settings["flattenRotation"] = true; }
            else { settings[mode] = new { file = "stamp" }; }
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(mode == "flatten" ? ["input", "--flatten-rotation", "output"]
                    : ["input", "--" + mode, "stamp", "--", "output"]), CancellationToken.None);
            Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            if (fails)
            {
                Assert.Equal(QpdfExitCode.Error, result.ExitCode);
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("annotation coordinates", StringComparison.Ordinal));
                Assert.Equal("prior"u8.ToArray(), output);
            }
            else { Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); }
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    [Theory]
    [InlineData("refuse")]
    [InlineData("caller")]
    [InlineData("governor")]
    public void PointArraysAdmitBeforeAllocatingAndHonorBothCancellationRoutes(string failure)
    {
        QpdfObject[] items = new QpdfObject[65536]; Array.Fill(items, new PdfInteger("1", 1));
        PdfArray source = PdfArray.FromOwnedItems(items);
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Polygon") });
        using CancellationTokenSource caller = new(), operation = new();
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, operation.Token);
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, caller.Token))
        {
            if (failure == "caller") { caller.Cancel(); }
            if (failure == "governor") { operation.Cancel(); }
            PoolResolver pool = new(new Dictionary<int, QpdfObject>());
            Action transform = () => AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii("Vertices"), source,
                [1, 0, 0, 1, 10, 20], pool, graph);
            long before = GC.GetAllocatedBytesForCurrentThread();
            if (failure == "refuse") { Assert.Throws<QpdfLimitException>(transform); }
            else { Assert.ThrowsAny<OperationCanceledException>(transform); }
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 131072L);
            Assert.Equal(65536, source.Count); Assert.Equal(1L, Assert.IsType<PdfInteger>(source[0]).Value);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("Ink", "InkList")]
    [InlineData("Polygon", "Vertices")]
    [InlineData("Link", "QuadPoints")]
    public void EmptyCoordinateArraysRetainTheirReferenceWithoutFreshStorage(string subtype, string key)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 512L }, CancellationToken.None);
        PdfReference reference = new(1, 0);
        PoolResolver pool = new(new Dictionary<int, QpdfObject> { [1] = new PdfArray([]) });
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii(subtype) });
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None))
        {
            long baseline = governor.Snapshot().LiveMemoryBytes;
            Assert.Same(reference, AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii(key), reference,
                [1, 0, 0, 1, 10, 20], pool, graph));
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NullOptionalCalloutsRetainTheirIdentityWithoutFreshStorage(bool indirect, bool indirectSubtype)
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 512L }, CancellationToken.None);
        QpdfObject raw = indirect ? new PdfReference(1, 0) : PdfNull.Instance;
        PdfName subtype = PdfName.FromAscii("FreeText");
        PoolResolver pool = new(new Dictionary<int, QpdfObject> { [1] = PdfNull.Instance, [2] = subtype });
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Subtype")] = indirectSubtype ? new PdfReference(2, 0) : subtype });
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None))
        {
            long before = governor.Snapshot().LiveMemoryBytes;
            Assert.Same(raw, AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii("CL"), raw,
                [0, -1, 1, 0, 0, 100], pool, graph));
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("Line", "L", false)]
    [InlineData("Line", "L", true)]
    [InlineData("Ink", "InkList", false)]
    [InlineData("Ink", "InkList", true)]
    public void RecognizedRequiredCoordinateNullsKeepTheirLiveHelperFailure(string subtype, string key, bool indirect)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfDictionary annotation = new(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii(subtype) });
        QpdfObject raw = indirect ? new PdfReference(1, 0) : PdfNull.Instance;
        PoolResolver pool = new(new Dictionary<int, QpdfObject> { [1] = PdfNull.Instance });
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, CancellationToken.None))
        {
            Assert.Throws<QpdfParseException>(() => AnnotationCoordinates.TransformEntry(annotation, PdfName.FromAscii(key), raw,
                [0, -1, 1, 0, 0, 100], pool, graph));
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

using System.Globalization;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlaySourceBoxTests
{
    private static byte[] Source(string property, string media, string preferred, bool inherited, bool indirect, int rotation, string unit)
    {
        string selected = "/" + property + " " + (indirect ? "5 0 R" : preferred);
        byte[] content = "% source-boundary\n1 0 0 rg -50 -50 250 250 re f 0 0 1 rg 0 0 150 150 re f\n"u8.ToArray();
        byte[] pdf = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 " + (inherited ? selected : "") + " >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox " + (indirect ? "6 0 R" : media)
                + " /Resources << /PrivateBox 5 0 R >> /Contents 4 0 R /Rotate " + rotation.ToString(CultureInfo.InvariantCulture)
                + " /UserUnit " + unit + " " + (inherited ? "" : selected) + " >>", null),
            (4, "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>", content),
            (5, preferred, null), (6, media, null),
        ], 1);
        "1.7"u8.CopyTo(pdf.AsSpan(5, 3));
        return pdf;
    }

    private static async Task<QpdfExecutionResult> Run(InMemoryQpdfHost host, byte[] source, string mode, bool job,
        QpdfExecutionOptions options, CancellationToken token)
    {
        byte[] target = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>", null),
        ], 1);
        host.AddInput(QpdfPath.From("input"), target, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), source, QpdfInputOwnership.Transfer);
        return job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object>
                { ["inputFile"] = "input", ["outputFile"] = "output", ["streamData"] = "uncompress", [mode] = new { file = "stamp" } }), options, token)
            : await Qpdf.ExecuteAsync(host, new(["input", "--stream-data=uncompress", "--" + mode, "stamp", "--", "output"]), options, token);
    }

    private static async Task<(double[] Box, double[] Matrix, double[] PrivateBox, byte[] Program)?> Inspect(InMemoryQpdfHost host)
    {
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Get(PdfDictionary parent, string name)
        { Assert.True(parent.TryGet(PdfName.FromAscii(name), out QpdfObject raw)); return Resolve(raw); }
        double[] Numbers(QpdfObject value)
        {
            PdfArray array = Assert.IsType<PdfArray>(Resolve(value)); double[] result = new double[array.Count];
            for (int i = 0; i < result.Length; i++)
            { result[i] = Resolve(array[i]) switch { PdfInteger integer => integer.Value, PdfReal real => real.Value, _ => throw new InvalidOperationException() }; }
            return result;
        }
        PdfDictionary page = document.PageReferences[0].Page;
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(page, "Resources"));
        PdfDictionary objects = Assert.IsType<PdfDictionary>(Get(resources, "XObject"));
        if (!objects.TryGet(PdfName.FromAscii("Fx1"), out QpdfObject rawForm)) { return null; }
        PdfStream form = Assert.IsType<PdfStream>(Resolve(rawForm));
        double[] matrix = form.Dictionary.TryGet(PdfName.FromAscii("Matrix"), out QpdfObject rawMatrix)
            ? Numbers(rawMatrix) : [1, 0, 0, 1, 0, 0];
        PdfDictionary own = Assert.IsType<PdfDictionary>(Get(form.Dictionary, "Resources"));
        return (Numbers(Get(form.Dictionary, "BBox")), matrix, Numbers(Get(own, "PrivateBox")), Assert.IsType<PdfStream>(Get(page, "Contents")).Data);
    }

    public static IEnumerable<object[]> Cases()
    {
        (string Property, string Media, string Preferred, string Effective, bool Inherited, int Rotation, string Unit)[] boxes =
        [
            ("CropBox", "[0 0 100 100]", "[20 20 80 80]", "[20 20 80 80]", false, 0, "1"),
            ("CropBox", "[0 0 100 100]", "[-50 -50 150 150]", "[0 0 100 100]", false, 0, "1"),
            ("CropBox", "[0 0 100 100]", "[-50 20 150 80]", "[0 20 100 80]", false, 0, "1"),
            ("TrimBox", "[0 0 100 100]", "[-50 -50 150 150]", "[0 0 100 100]", false, 0, "1"),
            ("CropBox", "[100 100 0 0]", "[150 150 -50 -50]", "[0 0 100 100]", false, 0, "1"),
            ("CropBox", "[10 20 110 120]", "[-40 -30 160 170]", "[10 20 110 120]", false, 90, "2"),
            ("TrimBox", "[10 20 110 120]", "[60 10 160 80]", "[60 20 110 80]", false, 270, "0.5"),
            ("CropBox", "[0 0 100 100]", "[-50 -50 150 150]", "[0 0 100 100]", true, 180, "1"),
        ];
        foreach (var box in boxes)
        foreach (bool indirect in new[] { false, true })
        foreach (string mode in new[] { "overlay", "underlay" })
        foreach (bool job in new[] { false, true })
        { yield return [box.Property, box.Media, box.Preferred, box.Effective, box.Inherited, box.Rotation, box.Unit, indirect, mode, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ImportedBoxesAndFittingUseTheEffectiveMediaIntersection(string property, string media, string preferred,
        string effective, bool inherited, int rotation, string unit, bool indirect, string mode, bool job)
    {
        using InMemoryQpdfHost original = new(), canonical = new();
        string normalMedia = media == "[10 20 110 120]" ? media : "[0 0 100 100]";
        Assert.Equal(QpdfExitCode.Success, (await Run(original, Source(property, media, preferred, inherited, indirect, rotation, unit), mode, job, new(), CancellationToken.None)).ExitCode);
        Assert.Equal(QpdfExitCode.Success, (await Run(canonical, Source(property, normalMedia, effective, inherited, indirect, rotation, unit), mode, job, new(), CancellationToken.None)).ExitCode);
        var actual = await Inspect(original); var control = await Inspect(canonical);
        Assert.True(actual.HasValue); Assert.True(control.HasValue);
        Assert.Equal(control.Value.Box, actual.Value.Box); Assert.Equal(control.Value.Matrix, actual.Value.Matrix); Assert.Equal(control.Value.Program, actual.Value.Program);
        double[] raw = preferred.Trim('[', ']').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(raw, actual.Value.PrivateBox);
    }

    [Theory]
    [InlineData("CropBox", "overlay", false)]
    [InlineData("CropBox", "overlay", true)]
    [InlineData("CropBox", "underlay", false)]
    [InlineData("CropBox", "underlay", true)]
    [InlineData("TrimBox", "overlay", false)]
    [InlineData("TrimBox", "overlay", true)]
    [InlineData("TrimBox", "underlay", false)]
    [InlineData("TrimBox", "underlay", true)]
    public async Task EmptySourceIntersectionWarnsAndSkipsTheStamp(string property, string mode, bool job)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Run(host, Source(property, "[0 0 100 100]", "[200 200 300 300]", false, true, 0, "1"), mode, job, new(), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature && d.Severity == QpdfDiagnosticSeverity.Warning);
        Assert.False((await Inspect(host)).HasValue);
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("null", false)]
    [InlineData("null", true)]
    [InlineData("17", false)]
    [InlineData("17", true)]
    [InlineData("[0 0 100]", false)]
    [InlineData("[0 0 100]", true)]
    [InlineData("[0 null 100 100]", false)]
    [InlineData("[0 null 100 100]", true)]
    public async Task UnusableMediaBoundsSkipOtherwiseValidPreferredBoxes(string media, bool job)
    {
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /TrimBox [0 0 100 100] /Rotate 0 /Resources << >> /Contents 4 0 R "
                + (media == "missing" ? "" : "/MediaBox 5 0 R") + " >>", null),
            (4, "<< /Length 3 >>", "q Q"u8.ToArray()), (5, media == "missing" ? "null" : media, null),
        ], 1);
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Run(host, source, "overlay", job, new(), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Equal("Overlay page media box is invalid; skipping it.", Assert.Single(result.Diagnostics).Message);
        Assert.False((await Inspect(host)).HasValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyIntersectionsDoNotDecodeUnpaintedContent(bool job)
    {
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /CropBox [200 200 300 300] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Filter /FlateDecode /Length 7 >>", "invalid"u8.ToArray()),
        ], 1);
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Run(host, source, "overlay", job, new(), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Equal("Overlay page has an empty media intersection; skipping it.", Assert.Single(result.Diagnostics).Message);
        Assert.Equal(0L, result.Counters.TotalDecodedBytes);
        Assert.False((await Inspect(host)).HasValue);
    }

    public static IEnumerable<object[]> Failures()
    {
        foreach (string failure in new[] { "memory", "output", "cancel" })
        foreach (string mode in new[] { "overlay", "underlay" })
        foreach (bool job in new[] { false, true }) { yield return [failure, mode, job]; }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task RefusalAndCancellationKeepPriorOutputAndCloseSources(string failure, string mode, bool job)
    {
        using InMemoryQpdfHost host = new();
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        using CancellationTokenSource cancel = new();
        if (failure == "cancel") { cancel.Cancel(); }
        QpdfLimits limits = failure switch
        {
            "memory" => QpdfLimits.Default with { MaxMemoryBytes = 4096L },
            "output" => QpdfLimits.Default with { MaxOutputBytes = 16L },
            _ => QpdfLimits.Default,
        };
        QpdfExecutionResult result = await Run(host, Source("CropBox", "[0 0 100 100]", "[-50 -50 150 150]", false, true, 90, "2"), mode, job, new(limits), cancel.Token);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(failure == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(0L, host.OpenLeaseCount); Assert.Equal(0L, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.True(bytes.AsSpan().SequenceEqual("prior"u8));
    }
}

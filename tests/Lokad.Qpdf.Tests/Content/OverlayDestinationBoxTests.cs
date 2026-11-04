using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayDestinationBoxTests
{
    private static byte[] Target(string media, string preferred, string key, bool indirect, int rotate, double unit, bool inherited, bool damaged)
    {
        string bounds = "/" + key + " " + (indirect ? "5 0 R" : preferred);
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /Audit 5 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1" + (inherited ? " " + bounds : "") + " >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox " + media + (inherited ? "" : " " + bounds)
                + " /Rotate " + rotate + " /UserUnit " + unit.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " /Resources << >> /Contents 4 0 R >>", null),
            (4, damaged ? "<< /Length 7 /Filter /FlateDecode >>" : "<< /Length 0 >>", damaged ? "invalid"u8.ToArray() : []),
            (5, preferred, null),
        ], 1);
    }

    private static async Task<QpdfExecutionResult> Run(InMemoryQpdfHost host, byte[] target,
        string mode, bool job, string streamData, QpdfExecutionOptions options, CancellationToken token)
    {
        byte[] sourceData = "1 0 0 rg 0 0 100 100 re f 0 0 1 rg 0 0 50 50 re f"u8.ToArray();
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + sourceData.Length + " >>", sourceData),
        ], 1);
        host.AddInput(QpdfPath.From("input"), target, QpdfInputOwnership.Transfer);
        host.AddInput(QpdfPath.From("stamp"), source, QpdfInputOwnership.Transfer);
        return job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, object>
                { ["inputFile"] = "input", ["outputFile"] = "output", ["streamData"] = streamData, [mode] = new { file = "stamp" } }), options, token)
            : await Qpdf.ExecuteAsync(host, new(["input", "--stream-data=" + streamData, "--" + mode, "stamp", "--", "output"]), options, token);
    }

    private static async Task<(byte[] Program, double[] RawBox, bool Placed)> Inspect(InMemoryQpdfHost host, string key)
    {
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Get(PdfDictionary dictionary, string name)
        { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject raw)); return Resolve(raw); }
        PdfDictionary page = document.PageReferences[0].Page;
        Assert.True(page.TryGet(PdfName.FromAscii(key), out QpdfObject rawBounds));
        PdfArray box = Assert.IsType<PdfArray>(Resolve(rawBounds));
        double[] rawBox = new double[box.Count];
        for (int index = 0; index < box.Count; index++)
        { rawBox[index] = Resolve(box[index]) switch { PdfInteger integer => integer.Value, PdfReal real => real.Value, _ => throw new InvalidOperationException() }; }
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
        PdfArray alias = Assert.IsType<PdfArray>(Get(catalog, "Audit"));
        Assert.Equal(box, alias);
        if (rawBounds is PdfReference) { Assert.Same(box, alias); }
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(page, "Resources"));
        if (!resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject objectsRaw)) { return ([], rawBox, false); }
        PdfDictionary objects = Assert.IsType<PdfDictionary>(Resolve(objectsRaw));
        bool placed = objects.TryGet(PdfName.FromAscii("Fx1"), out _);
        PdfStream program = Assert.IsType<PdfStream>(Get(page, "Contents"));
        return (program.Data, rawBox, placed);
    }

    public static IEnumerable<object[]> EffectiveBoxes()
    {
        (string Media, string Preferred, string Effective, string Key, int Rotate, double Unit, bool Inherited)[] shapes = [
            ("[0 0 100 100]", "[10 10 90 90]", "[10 10 90 90]", "CropBox", 0, 1, false),
            ("[0 0 100 100]", "[-50 -50 150 150]", "[0 0 100 100]", "CropBox", 0, 1, false),
            ("[0 0 100 100]", "[50 -50 150 50]", "[50 0 100 50]", "CropBox", 0, 1, false),
            ("[0 0 100 100]", "[50 -50 150 50]", "[50 0 100 50]", "TrimBox", 90, 2, false),
            ("[100 100 0 0]", "[150 50 50 -50]", "[50 0 100 50]", "CropBox", 180, 1, false),
            ("[10 20 110 120]", "[60 10 160 80]", "[60 20 110 80]", "TrimBox", 270, .5, false),
            ("[0 0 100 100]", "[-20 -10 140 130]", "[0 0 100 100]", "CropBox", 90, 2, false),
            ("[10 20 110 120]", "[60 10 160 80]", "[60 20 110 80]", "CropBox", 180, 3, true),
        ];
        foreach (var shape in shapes)
        foreach (bool indirect in new[] { false, true })
        foreach (string mode in new[] { "overlay", "underlay" })
        foreach (bool job in new[] { false, true })
        { yield return [shape.Media, shape.Preferred, shape.Effective, shape.Key, shape.Rotate, shape.Unit, shape.Inherited, indirect, mode, job]; }
    }

    [Theory]
    [MemberData(nameof(EffectiveBoxes))]
    public async Task FitToEffectiveBoundsAndPreserveOriginalAliases(string media, string preferred, string effective,
        string key, int rotate, double unit, bool inherited, bool indirect, string mode, bool job)
    {
        using InMemoryQpdfHost actual = new(); using InMemoryQpdfHost canonical = new();
        QpdfExecutionOptions options = new(QpdfLimits.Default);
        Assert.Equal(QpdfExitCode.Success, (await Run(actual, Target(media, preferred, key, indirect, rotate, unit, inherited, false), mode, job, "uncompress", options, CancellationToken.None)).ExitCode);
        Assert.Equal(QpdfExitCode.Success, (await Run(canonical, Target(media, effective, key, indirect, rotate, unit, inherited, false), mode, job, "uncompress", options, CancellationToken.None)).ExitCode);
        var observed = await Inspect(actual, key); var expected = await Inspect(canonical, key);
        Assert.True(observed.Placed); Assert.True(expected.Placed);
        Assert.Equal(expected.Program, observed.Program);
        double[] original = preferred.Trim('[', ']').Split(' ').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(original, observed.RawBox);
    }

    public static IEnumerable<object[]> EmptyBoxes()
    {
        foreach (var shape in new[]
        {
            (Media: "[0 0 100 100]", Key: "CropBox", Bounds: "[200 200 300 300]"),
            (Media: "[0 0 100 100]", Key: "TrimBox", Bounds: "[100 0 150 100]"),
            (Media: "[0 0 0 100]", Key: "CropBox", Bounds: "[10 10 90 90]"),
        })
        foreach (string mode in new[] { "overlay", "underlay" })
        foreach (bool job in new[] { false, true })
        { yield return [shape.Media, shape.Key, shape.Bounds, mode, job]; }
    }

    [Theory]
    [MemberData(nameof(EmptyBoxes))]
    public async Task SkipEmptyDestinationBeforeDecodingItsContent(string media, string key, string bounds, string mode, bool job)
    {
        using InMemoryQpdfHost host = new();
        QpdfExecutionResult result = await Run(host, Target(media, bounds, key, true, 0, 1, false, true),
            mode, job, "preserve", new(QpdfLimits.Default), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains("empty media intersection", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
        Assert.Equal(0, result.Counters.TotalDecodedBytes);
        Assert.False((await Inspect(host, key)).Placed);
        Assert.Equal(0, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> Failures()
    {
        foreach (string failure in new[] { "memory", "output", "cancel" })
        foreach (string mode in new[] { "overlay", "underlay" })
        foreach (bool job in new[] { false, true }) { yield return [failure, mode, job]; }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task PreservePriorOutputAndCloseLeasesOnFailure(string failure, string mode, bool job)
    {
        using InMemoryQpdfHost host = new();
        await using (IQpdfOutputTransaction seed = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await seed.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await seed.CommitAsync(CancellationToken.None); }
        using CancellationTokenSource stop = new();
        if (failure == "cancel") { stop.Cancel(); }
        QpdfLimits limits = failure == "memory" ? QpdfLimits.Default with { MaxMemoryBytes = 4096 }
            : failure == "output" ? QpdfLimits.Default with { MaxOutputBytes = 16 } : QpdfLimits.Default;
        QpdfExecutionResult result = await Run(host, Target("[0 0 100 100]", "[50 -50 150 50]", "CropBox", true, 90, 2, false, false),
            mode, job, "uncompress", new(limits), stop.Token);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(failure == "cancel" ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior));
        Assert.Equal("prior"u8.ToArray(), prior);
    }
}

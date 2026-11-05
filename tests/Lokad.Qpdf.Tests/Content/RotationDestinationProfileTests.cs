using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class RotationDestinationProfileTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SplitDoesNotInterpretNavigationInDiscardedCatalogExtras(bool preserve, bool job)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture("[3 0 R /FitH null]", 90, true, false), QpdfInputOwnership.Transfer);
        List<string> arguments = ["input", "--flatten-rotation", "--split-pages=1"];
        if (preserve) { arguments.Add("--preserve-unreferenced"); }
        arguments.Add("out.pdf");
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "out.pdf", flattenRotation = true, splitPages = "1", preserveUnreferenced = preserve }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("out-1.pdf"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument read = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        Assert.False(read.PageReferences[0].Page.TryGet(PdfName.FromAscii("Rotate"), out _));
        PdfDictionary catalog = RotationDestinationTests.Dict(read, read.Engine.Trailer, "Root");
        Assert.False(catalog.TryGet(PdfName.FromAscii("OpenAction"), out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UriActionExtrasRemainOpaqueEvenWhenTheyLookLikeLocalDestinations(bool job)
    {
        byte[] source = CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R /OpenAction << /S /URI /URI (https://source.test/) /D [3 0 R /XYZ null null 1] >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Rotate 90 >>", null)], 1);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), source, QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, "{\"inputFile\":\"input\",\"outputFile\":\"output\",\"flattenRotation\":true}", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--flatten-rotation", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument read = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
        PdfDictionary action = RotationDestinationTests.Dict(read, RotationDestinationTests.Dict(read, read.Engine.Trailer, "Root"), "OpenAction");
        PdfArray retained = Assert.IsType<PdfArray>(RotationDestinationTests.Value(read, action, "D"));
        Assert.IsType<PdfNull>(retained[2]); Assert.IsType<PdfNull>(retained[3]);
    }

    private static readonly (string Name, string Value, bool Unsupported)[] Destinations = [
        ("left-null", "[3 0 R /XYZ null 60 0]", true),
        ("top-null", "[3 0 R /XYZ 25 null null]", true),
        ("both-null", "[3 0 R /XYZ null null 1]", true),
        ("indirect-null", "[3 0 R /XYZ 10 0 R 60 0]", true),
        ("short-xyz", "[3 0 R /XYZ 25 60]", false),
        ("bad-rectangle", "[3 0 R /FitR 25 (bad) 75 120]", false),
        ("bad-zoom", "[3 0 R /XYZ 25 60 /Bad]", false),
        ("negative-zoom", "[3 0 R /XYZ 25 60 -1]", false),
        ("bad-fit", "[3 0 R /FitH (bad)]", false),
        ("unknown-mode", "[3 0 R /Unknown 25]", false),
        ("missing-mode", "[3 0 R]", false)];

    public static IEnumerable<object[]> Cases()
    {
        foreach (var value in Destinations)
        foreach (int angle in value.Unsupported ? new[] { 0, 90, 180, 270 } : new[] { 0, 90 })
        foreach (bool job in new[] { false, true }) { yield return [value.Name, value.Value, value.Unsupported, angle, true, false, job]; }
        foreach (var value in Destinations)
        foreach (bool job in new[] { false, true }) { yield return [value.Name, value.Value, value.Unsupported, 90, false, false, job]; }
        foreach (bool job in new[] { false, true }) { yield return ["unreadable-page", "[3 0 R /FitH null]", true, 90, true, true, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OnlyRetainedDestinationsToActuallyBakedPagesRequireProjection(string name, string destination, bool unsupported, int angle, bool active, bool unreadable, bool job)
    {
        Assert.NotEmpty(name);
        RecordingMemoryBudget budget = new(16L << 20);
        using (IMemoryScope owner = budget.CreateScope())
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
            host.AddInput(QpdfPath.From("input"), Fixture(destination, angle, active, unreadable), QpdfInputOwnership.Transfer);
            await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
            { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", flattenRotation = true }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--flatten-rotation", "output"]), CancellationToken.None);
            bool refusal = angle != 0 && active && !unreadable;
            Assert.Equal(refusal ? QpdfExitCode.Error : unreadable ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            if (refusal)
            {
                Assert.Equal("prior"u8.ToArray(), output);
                Assert.Contains(result.Diagnostics, d => d.Code == (unsupported ? QpdfDiagnosticCode.UnsupportedFeature : QpdfDiagnosticCode.InvalidArguments));
            }
            else
            {
                host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
                await using QpdfDocument input = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
                await using QpdfDocument read = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None);
                PdfArray original = Assert.IsType<PdfArray>(RotationDestinationTests.Value(input, RotationDestinationTests.Dict(input, input.Engine.Trailer, "Root"), "Private"));
                PdfDictionary catalog = RotationDestinationTests.Dict(read, read.Engine.Trailer, "Root");
                PdfArray retained = Assert.IsType<PdfArray>(RotationDestinationTests.Value(read, catalog, "Private"));
                Assert.Equal(original.Count, retained.Count);
                for (int i = 1; i < original.Count; i++)
                { Assert.Equal(RotationDestinationTests.Resolve(input, original[i]), RotationDestinationTests.Resolve(read, retained[i])); }
                if (unreadable)
                { Assert.Equal(90L, Assert.IsType<PdfInteger>(RotationDestinationTests.Value(read, read.PageReferences[0].Page, "Rotate")).Value); }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    internal static byte[] Fixture(string destination, int angle, bool active, bool unreadable)
    {
        List<(int, string, byte[]?)> objects = [
            (1, "<< /Type /Catalog /Pages 2 0 R /Private " + destination + (active ? " /OpenAction " + destination : "") + " >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [10 20 110 220] /Resources << >> /Rotate " + angle + (unreadable ? " /Contents 42" : "") + " >>", null)];
        for (int i = 4; i <= 10; i++) { objects.Add((i, "null", null)); }
        return CheckTests.Assemble(objects, 1);
    }
}

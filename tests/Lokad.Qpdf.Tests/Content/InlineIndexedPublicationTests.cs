using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class InlineIndexedPublicationTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string color in new[] { "/I /RGB", "/Indexed /RGB", "/I /DeviceRGB", "/Indexed /DeviceRGB" })
        foreach (bool qdf in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [color, qdf, job]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ExternalAndQdfImagesKeepPaletteSamplesAndEscapedLifetimes(string color, bool qdf, bool job)
    {
        byte[] samples = Enumerable.Range(0, 64).Select(index => (byte)(index % 2)).ToArray();
        byte[] prefix = Encoding.ASCII.GetBytes("q 80 0 0 80 10 10 cm BI /W 8 /H 8 /BPC 8 /CS [" + color + " 1 <FF00000000FF>] ID ");
        byte[] program = [.. prefix, .. samples, .. " EI Q\n"u8];
        RecordingMemoryBudget budget = new(32L * 1048576L);
        using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner);
        host.AddInput(QpdfPath.From("input"), CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + program.Length + " >>", program)], 1), QpdfInputOwnership.Transfer);
        List<string> arguments = ["input"];
        if (qdf) { arguments.Add("--qdf"); }
        arguments.AddRange(["--externalize-inline-images", "--ii-min-bytes=0", "--compress-streams=n", "--decode-level=none", "output"]);
        Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output", ["externalizeInlineImages"] = true,
            ["iiMinBytes"] = "0", ["compressStreams"] = "n", ["decodeLevel"] = "none" };
        if (qdf) { settings["qdf"] = true; }
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(arguments), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
        PdfByteString lookup; PdfStream image;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
            QpdfObject Get(PdfDictionary dictionary, string name)
            { Assert.True(dictionary.TryGet(PdfName.FromAscii(name), out QpdfObject value)); return Resolve(value); }
            PdfDictionary resources = Assert.IsType<PdfDictionary>(Get(document.PageReferences[0].Page, "Resources"));
            PdfDictionary objects = Assert.IsType<PdfDictionary>(Get(resources, "XObject"));
            Assert.True(objects.TryGet(Assert.Single(objects.Keys), out QpdfObject raw)); image = Assert.IsType<PdfStream>(Resolve(raw));
            PdfArray colors = Assert.IsType<PdfArray>(Get(image.Dictionary, "ColorSpace"));
            Assert.Equal(PdfName.FromAscii("Indexed"), colors[0]); Assert.Equal(PdfName.FromAscii("DeviceRGB"), colors[1]);
            Assert.Equal(1L, Assert.IsType<PdfInteger>(colors[2]).Value); lookup = Assert.IsType<PdfByteString>(colors[3]);
        }
        host.Dispose(); Assert.True(budget.ReservedBytes > 0);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 255 }, lookup.ValueSpan.ToArray());
        // Keep the established delimiter byte along with all declared sample bytes.
        Assert.Equal(samples.Concat([(byte)' ']), image.RawMemory.ToArray());
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }
}

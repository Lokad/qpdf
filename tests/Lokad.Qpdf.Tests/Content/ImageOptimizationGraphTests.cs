using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Image;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ImageOptimizationGraphTests
{
    private sealed class CountingCodec(string outcome) : IQpdfJpegCodec
    {
        internal int Encodes { get; private set; }
        internal int Decodes { get; private set; }
        public void Decode(Stream encoded, QpdfJpegFrame frame, Memory<byte> samples, QpdfJpegCodecContext context)
        { Decodes++; LokadImageJpegCodec.Instance.Decode(encoded, frame, samples, context); }
        public void Encode(ReadOnlyMemory<byte> samples, QpdfJpegFrame frame, int quality, Stream encoded, QpdfJpegCodecContext context)
        {
            Encodes++;
            if (outcome == "failure") { throw new InvalidDataException("Codec rejected the candidate."); }
            if (outcome == "larger") { encoded.Write(new byte[samples.Length + 1]); return; }
            LokadImageJpegCodec.Instance.Encode(samples, frame, quality, encoded, context);
        }
    }

    private static byte[] Fixture(string kind)
    {
        const string image = "<< /Type /XObject /Subtype /Image /Width 200 /Height 200 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 40000 >>";
        byte[] data = Enumerable.Repeat((byte)128, 40000).ToArray();
        bool form = kind == "form";
        string resources = form ? "/Fm0 6 0 R" : "/Im0 5 0 R /Im1 " + (kind == "distinct" ? "6" : "5") + " 0 R";
        byte[] content = Encoding.ASCII.GetBytes("q 200 0 0 200 0 0 cm /" + (form ? "Fm0" : "Im0") + " Do Q\n");
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << " + resources + " >> >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + content.Length + " >>", content),
            (5, image, data),
            (6, form ? "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Im0 5 0 R /Im1 5 0 R /Self 6 0 R >> >> /Length 7 >>"
                : kind == "distinct" ? image : "null", form ? "/Im0 Do"u8.ToArray() : kind == "distinct" ? data : null)], 1);
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string route, string kind, QpdfLimits limits)
    {
        if (route == "job")
        {
            Dictionary<string, object> job = new()
            { ["inputFile"] = "input", ["outputFile"] = "output", ["optimizeImages"] = "", ["jpegQuality"] = 75,
                ["decodeLevel"] = "none", ["compressStreams"] = "n", ["objectStreams"] = "disable" };
            if (kind == "repeat") { job["pages"] = new[] { new { file = ".", range = "1,1" } }; }
            return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new QpdfExecutionOptions(limits), CancellationToken.None);
        }
        List<string> argv = ["input", "--optimize-images", "--jpeg-quality=75", "--decode-level=none", "--compress-streams=n", "--object-streams=disable"];
        if (kind == "repeat") { argv.AddRange(["--pages", ".", "1,1", "--"]); }
        argv.Add("output");
        return Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), new QpdfExecutionOptions(limits), CancellationToken.None);
    }

    public static IEnumerable<object[]> IdentityCases()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (string kind in new[] { "aliases", "form", "repeat", "distinct", "larger", "failure" })
        { yield return [route, kind]; }
    }

    [Theory]
    [MemberData(nameof(IdentityCases))]
    public async Task EachImageIdentityIsAttemptedOnceIncludingSkippedCandidates(string route, string kind)
    {
        CountingCodec codec = new(kind);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = kind == "distinct" ? 131072L : 65536L };
        using InMemoryQpdfHost host = new(limits, codec);
        host.AddInput(QpdfPath.From("input"), Fixture(kind), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, route, kind, limits);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(kind == "distinct" ? 2 : 1, codec.Encodes);
        Assert.Equal(0, codec.Decodes);
        Assert.Equal(kind == "distinct" ? 80000L : 40000L, result.Counters.TotalDecodedBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.Equal(kind is not ("larger" or "failure"), bytes.AsSpan().IndexOf("/DCTDecode"u8) >= 0);
        host.AddInput(QpdfPath.From("verify"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("verify"), CancellationToken.None);
        Assert.Equal(kind == "repeat" ? 2 : 1, document.PageCount);
        if (kind == "aliases")
        {
            PdfDictionary page = document.PageReferences[0].Page;
            PdfDictionary resources = GetDictionary(document, page, "Resources");
            PdfDictionary objects = GetDictionary(document, resources, "XObject");
            Assert.True(objects.TryGet(PdfName.FromAscii("Im0"), out QpdfObject first));
            Assert.True(objects.TryGet(PdfName.FromAscii("Im1"), out QpdfObject second));
            Assert.Equal(Assert.IsType<PdfReference>(first), Assert.IsType<PdfReference>(second));
        }
    }

    private static PdfDictionary GetDictionary(QpdfDocument document, PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject raw));
        return Assert.IsType<PdfDictionary>(raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw);
    }
}

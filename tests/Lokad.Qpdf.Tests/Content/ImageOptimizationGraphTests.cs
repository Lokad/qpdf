using System.IO.Compression;
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
        string subtype = kind switch { "image-indirect" => "7 0 R", "image-stale" => "7 1 R", "image-invalid" => "17", _ => "/Image" };
        byte[] data = Enumerable.Repeat((byte)128, 40000).ToArray();
        string filters = string.Empty;
        if (kind.StartsWith("filtered", StringComparison.Ordinal))
        {
            using MemoryStream compressed = new();
            using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, true)) { zlib.Write(data); }
            data = compressed.ToArray();
            filters = " /Filter /FlateDecode";
            if (kind == "filtered-two")
            {
                data = Encoding.ASCII.GetBytes(Convert.ToHexString(data) + ">");
                filters = " /Filter [/ASCIIHexDecode /FlateDecode]";
            }
        }
        string image = "<< /Type /XObject /Subtype " + subtype + " /Width 200 /Height 200 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length " + data.Length + filters + " >>";
        bool distinct = kind is "distinct" or "filtered-distinct";
        bool form = kind.StartsWith("form", StringComparison.Ordinal);
        string formSubtype = kind switch { "form-indirect" => "8 0 R", "form-stale" => "8 1 R", "form-invalid" => "null", _ => "/Form" };
        string resources = form ? "/Fm0 6 0 R" : "/Im0 5 0 R /Im1 " + (distinct ? "6" : "5") + " 0 R";
        byte[] content = Encoding.ASCII.GetBytes("q 200 0 0 200 0 0 cm /" + (form ? "Fm0" : "Im0") + " Do Q\n");
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << " + resources + " >> >> /Contents 4 0 R >>", null),
            (4, "<< /Length " + content.Length + " >>", content),
            (5, image, data),
            (6, form ? "<< /Type /XObject /Subtype " + formSubtype + " /BBox [0 0 1 1] /Resources << /XObject << /Im0 5 0 R /Im1 5 0 R /Self 6 0 R >> >> /Length 7 >>"
                : distinct ? image : "null", form ? "/Im0 Do"u8.ToArray() : distinct ? data : null),
            (7, "/Image", null), (8, "/Form", null)], 1);
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string route, string kind, QpdfLimits limits, string warningPolicy)
    {
        if (route == "job")
        {
            Dictionary<string, object> job = new()
            { ["inputFile"] = "input", ["outputFile"] = "output", ["optimizeImages"] = "", ["jpegQuality"] = 75,
                ["decodeLevel"] = "none", ["compressStreams"] = "n", ["objectStreams"] = "disable" };
            if (warningPolicy == "suppressed") { job["noWarn"] = ""; }
            if (warningPolicy == "zero") { job["warningExit0"] = ""; }
            if (kind == "repeat") { job["pages"] = new[] { new { file = ".", range = "1,1" } }; }
            return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(job), new QpdfExecutionOptions(limits), CancellationToken.None);
        }
        List<string> argv = ["input", "--optimize-images", "--jpeg-quality=75", "--decode-level=none", "--compress-streams=n", "--object-streams=disable"];
        if (warningPolicy == "suppressed") { argv.Add("--no-warn"); }
        if (warningPolicy == "zero") { argv.Add("--warning-exit-0"); }
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
        QpdfExecutionResult result = await Run(host, route, kind, limits, "normal");
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

    public static IEnumerable<object[]> SubtypeCases()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (string kind in new[] { "image-indirect", "form-indirect", "image-stale", "form-stale", "image-invalid", "form-invalid" })
        { yield return [route, kind]; }
    }

    [Theory]
    [MemberData(nameof(SubtypeCases))]
    public async Task IndirectSubtypeNamesOptimizeWhileStaleAndMalformedNamesStayOpaque(string route, string kind)
    {
        CountingCodec codec = new("success");
        using InMemoryQpdfHost host = new(QpdfLimits.Default, codec);
        host.AddInput(QpdfPath.From("input"), Fixture(kind), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, route, kind, QpdfLimits.Default, "normal");
        bool eligible = kind.EndsWith("-indirect", StringComparison.Ordinal);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(eligible ? 1 : 0, codec.Encodes); Assert.Equal(0, codec.Decodes);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.Equal(eligible, bytes.AsSpan().IndexOf("/DCTDecode"u8) >= 0);
    }

    public static IEnumerable<object[]> FilterCases()
    {
        foreach (string route in new[] { "argv", "job" })
        foreach (string kind in new[] { "filtered", "filtered-two" })
        foreach (bool codec in new[] { false, true })
        foreach (long decoded in new[] { 1000L, 1048576L })
        { yield return [route, kind, codec, decoded]; }
    }

    [Theory]
    [MemberData(nameof(FilterCases))]
    public async Task FilterCeilingsPreserveOpaqueBytesBeforeSampleAndCodecAdmission(string route, string kind, bool suppliedCodec, long decoded)
    {
        CountingCodec codec = new("success");
        QpdfLimits limits = QpdfLimits.Default with { MaxStreamFilters = kind == "filtered-two" ? 1L : 0L, MaxDecodedBytes = decoded };
        using InMemoryQpdfHost host = new(limits, suppliedCodec ? codec : null);
        host.AddInput(QpdfPath.From("input"), Fixture(kind), QpdfInputOwnership.Transfer);
        byte[] original;
        await using (QpdfDocument source = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
        { original = Assert.IsType<PdfStream>(source.GetObject(5)).RawMemory.ToArray(); }
        QpdfExecutionResult result = await Run(host, route, kind, limits, "normal");
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Single(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedFeature && d.Message.Contains("MaxStreamFilters", StringComparison.Ordinal));
        Assert.Equal(0, codec.Encodes); Assert.Equal(0, codec.Decodes);
        Assert.Equal(0L, result.Counters.TotalDecodedBytes); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("verify"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("verify"), CancellationToken.None);
        PdfDictionary objects = GetDictionary(document, GetDictionary(document, document.PageReferences[0].Page, "Resources"), "XObject");
        Assert.True(objects.TryGet(PdfName.FromAscii("Im0"), out QpdfObject image));
        Assert.Equal(original, Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(image).ObjectNumber)).RawMemory.ToArray());
    }

    [Theory]
    [InlineData("argv", "suppressed")]
    [InlineData("job", "suppressed")]
    [InlineData("argv", "zero")]
    [InlineData("job", "zero")]
    public async Task FilterWarningsRetainSuppressionAndExitPolicies(string route, string policy)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxStreamFilters = 0L, MaxDecodedBytes = 1000L };
        using InMemoryQpdfHost host = new(limits);
        host.AddInput(QpdfPath.From("input"), Fixture("filtered"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, route, "filtered", limits, policy);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(policy == "zero" ? QpdfExitCode.Success : QpdfExitCode.Warning, result.ExitCode);
        Assert.Equal(policy == "zero", result.Diagnostics.Any(d => d.Message.Contains("MaxStreamFilters", StringComparison.Ordinal)));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task ZeroFilterCeilingStillAllowsUnfilteredImages(string route)
    {
        CountingCodec codec = new("success");
        QpdfLimits limits = QpdfLimits.Default with { MaxStreamFilters = 0L, MaxDecodedBytes = 65536L };
        using InMemoryQpdfHost host = new(limits, codec);
        host.AddInput(QpdfPath.From("input"), Fixture("aliases"), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, route, "aliases", limits, "normal");
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Equal(1, codec.Encodes);
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("MaxStreamFilters", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("argv", false)]
    [InlineData("job", false)]
    [InlineData("argv", true)]
    [InlineData("job", true)]
    public async Task FilterWarningQuotaFailureStillPreservesPriorOutput(string route, bool byteLimit)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxStreamFilters = 0L, MaxDecodedBytes = 1000L,
            MaxDiagnostics = byteLimit ? 256L : 1L, MaxDiagnosticBytes = byteLimit ? 1L : 65536L };
        using InMemoryQpdfHost host = new(limits);
        host.AddInput(QpdfPath.From("input"), Fixture("filtered-distinct"), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        QpdfExecutionResult result = await Run(host, route, "filtered-distinct", limits, "normal");
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.Equal("prior"u8.ToArray(), bytes); Assert.Equal(0L, host.OpenLeaseCount);
    }
}

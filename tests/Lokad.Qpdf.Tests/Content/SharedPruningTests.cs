using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class SharedPruningTests
{
    private const int PayloadBytes = 65536;
    private static byte[] Program(int length, string prefix)
        => Encoding.ASCII.GetBytes(prefix + "% " + new string('a', length - prefix.Length - 3) + "\n");

    private static byte[] Encode(byte[] bytes)
    {
        using MemoryStream output = new();
        using (ZLibStream encoder = new(output, CompressionLevel.Optimal, true)) { encoder.Write(bytes); }
        return output.ToArray();
    }

    private static byte[] Fixture(int[][] pages, byte[][] programs, int[] contexts, bool audit)
    {
        int firstStream = pages.Length + 3;
        Dictionary<string, int> arrays = new(StringComparer.Ordinal);
        foreach (int[] page in pages)
        {
            if (page.Length != 1) { arrays.TryAdd(string.Join(',', page), firstStream + programs.Length + arrays.Count); }
        }
        int firstResource = firstStream + programs.Length + arrays.Count;
        int contextCount = contexts.Max() + 1;
        int firstImage = firstResource + contextCount;
        int unused = firstImage + contextCount * 2;
        List<(int Number, string Dictionary, byte[]? Data)> objects = [
            (1, $"<< /Type /Catalog /Pages 2 0 R{(audit ? $" /Audit {firstResource} 0 R" : "")} >>", null),
            (2, $"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(' ', Enumerable.Range(3, pages.Length).Select(n => $"{n} 0 R"))}] >>", null)];
        for (int page = 0; page < pages.Length; page++)
        {
            int content = pages[page].Length == 1 ? firstStream + pages[page][0] : arrays[string.Join(',', pages[page])];
            objects.Add((page + 3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources {firstResource + contexts[page]} 0 R /Contents {content} 0 R >>", null));
        }
        for (int index = 0; index < programs.Length; index++)
        {
            byte[] data = Encode(programs[index]);
            objects.Add((firstStream + index, $"<< /Filter /FlateDecode /Length {data.Length} >>", data));
        }
        foreach ((string entries, int number) in arrays)
        { objects.Add((number, $"[{string.Join(' ', entries.Split(',').Select(s => $"{firstStream + int.Parse(s)} 0 R"))}]", null)); }
        for (int context = 0; context < contextCount; context++)
        {
            objects.Add((firstResource + context, $"<< /XObject << /Used {firstImage + context * 2} 0 R /Other {firstImage + context * 2 + 1} 0 R /Unused {unused} 0 R >> >>", null));
            for (int image = 0; image < 2; image++)
            { objects.Add((firstImage + context * 2 + image, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 1 >>", [(byte)((context + image) % 2 == 0 ? 0 : 255)])); }
        }
        objects.Add((unused, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 1 >>", [127]));
        return CheckTests.Assemble(objects, 1);
    }

    internal static byte[] SharedFixture(int count, bool array, bool audit)
        => Fixture(Enumerable.Range(0, count).Select(_ => array ? new[] { 0, 0 } : new[] { 0 }).ToArray(),
            [Program(PayloadBytes, "")], new int[count], audit);

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, QpdfLimits limits, CancellationToken token)
        => Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1-z", "--", "--remove-unreferenced-resources=yes",
            "--normalize-content=n", "--stream-data=preserve", "--compress-streams=n", "output"]), new(limits), token);

    private static QpdfObject Resolve(QpdfDocument document, QpdfObject raw)
        => raw is PdfReference link ? document.GetObject(link.ObjectNumber) : raw;
    private static QpdfObject Get(PdfDictionary dictionary, string key)
    { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject raw)); return raw; }
    private static PdfDictionary Resources(QpdfDocument document, PdfDictionary page)
        => Assert.IsType<PdfDictionary>(Resolve(document, Get(page, "Resources")));
    private static PdfDictionary Objects(QpdfDocument document, PdfDictionary resources)
        => Assert.IsType<PdfDictionary>(Resolve(document, Get(resources, "XObject")));

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(4, false, false)]
    [InlineData(16, false, true)]
    [InlineData(64, false, false)]
    [InlineData(4, true, false)]
    public async Task IdenticalContextsDecodeOnceAndSharePrunedResources(int count, bool array, bool audit)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), SharedFixture(count, array, audit), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, QpdfLimits.Default with { MaxDecodedBytes = PayloadBytes }, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(PayloadBytes, result.Counters.TotalDecodedBytes);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("reread"), output, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread"), CancellationToken.None))
        {
            Assert.Equal(count, document.PageCount);
            List<int> resourceNumbers = [];
            foreach (var page in document.PageReferences)
            {
                QpdfObject resources = Get(page.Page, "Resources");
                if (count > 1) { resourceNumbers.Add(Assert.IsType<PdfReference>(resources).ObjectNumber); }
                else { Assert.IsType<PdfDictionary>(resources); }
                Assert.Empty(Objects(document, Resources(document, page.Page)).Keys);
                QpdfObject contents = Resolve(document, Get(page.Page, "Contents"));
                PdfStream stream = Assert.IsType<PdfStream>(array ? Resolve(document, Assert.IsType<PdfArray>(contents)[0]) : contents);
                Assert.Equal(Encode(Program(PayloadBytes, "")), stream.Data);
            }
            if (count > 1) { Assert.Single(resourceNumbers.Distinct()); }
            if (audit)
            {
                PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(document, Get(document.Engine.Trailer, "Root")));
                PdfDictionary original = Assert.IsType<PdfDictionary>(Resolve(document, Get(catalog, "Audit")));
                Assert.Equal(3, Objects(document, original).Count);
            }
        }
        if (count == 64)
        {
            using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output);
            Assert.Equal(count, independent.NumberOfPages);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(PayloadBytes)]
    [InlineData(300 * 1024)]
    public async Task DistinctResourcesKeepTheirOwnBindingsAndOversizedDecodesRemainUncached(int length)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture([[0], [0]], [Program(length, "/Used Do\n")], [0, 1], false), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal((long)length * (length == PayloadBytes ? 1 : 2), result.Counters.TotalDecodedBytes);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("reread"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread"), CancellationToken.None);
        for (int page = 0; page < 2; page++)
        {
            PdfDictionary objects = Objects(document, Resources(document, document.PageReferences[page].Page));
            Assert.Single(objects.Keys);
            PdfStream image = Assert.IsType<PdfStream>(Resolve(document, Get(objects, "Used")));
            Assert.Equal(new[] { (byte)(page == 0 ? 0 : 255) }, image.Data);
        }
    }

    [Fact]
    public async Task DifferentContentArraysKeepEveryProgramDependency()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Fixture([[0], [0, 1]],
            [Program(PayloadBytes, "/Used Do\n"), Program(PayloadBytes, "/Other Do\n")], [0, 0], false), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(2L * PayloadBytes, result.Counters.TotalDecodedBytes);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
        host.AddInput(QpdfPath.From("reread"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread"), CancellationToken.None);
        Assert.Equal(new[] { "Used" }, Objects(document, Resources(document, document.PageReferences[0].Page)).Keys.Select(k => k.DisplayText));
        Assert.Equal(new[] { "Used", "Other" }, Objects(document, Resources(document, document.PageReferences[1].Page)).Keys.Select(k => k.DisplayText));
    }

    [Fact]
    public async Task EvictedIdentityContextsChargeTheirActualDecodingAgain()
    {
        using InMemoryQpdfHost host = new();
        int[][] pages = Enumerable.Range(0, 40).Append(0).Select(index => new[] { index }).ToArray();
        host.AddInput(QpdfPath.From("input"), Fixture(pages,
            Enumerable.Range(0, 40).Select(_ => Program(PayloadBytes, "")).ToArray(), new int[pages.Length], false), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = await Run(host, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode);
        Assert.Equal(41L * PayloadBytes, result.Counters.TotalDecodedBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

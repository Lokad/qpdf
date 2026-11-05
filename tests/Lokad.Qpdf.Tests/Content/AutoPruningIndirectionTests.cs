using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class AutoPruningIndirectionTests
{
    private static byte[] Source(bool indirectKids, bool indirectSubtype, int payload, bool alias)
    {
        string kids = indirectKids ? "9 0 R" : "[3 0 R 4 0 R]", subtype = indirectSubtype ? "10 0 R" : "/Form";
        byte[] page = "q 60 0 0 60 20 20 cm /Fm Do Q"u8.ToArray(), form = "/Im0 Do"u8.ToArray();
        return CheckTests.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R" + (alias ? " /OriginalResources 8 0 R" : string.Empty) + " >>", null),
            (2, "<< /Type /Pages /Kids " + kids + " /Count 2 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Fm 6 0 R >> >> /Contents 5 0 R >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Fm 7 0 R >> >> /Contents 5 0 R >>", null),
            (5, "<< /Length " + page.Length + " >>", page),
            (6, "<< /Type /XObject /Subtype " + subtype + " /BBox [0 0 1 1] /Resources 8 0 R /Length " + form.Length + " >>", form),
            (7, "<< /Type /XObject /Subtype " + subtype + " /BBox [0 0 1 1] /Resources 8 0 R /Length " + form.Length + " >>", form),
            (8, "<< /XObject << /Im0 11 0 R /Unused 12 0 R >> >>", null),
            (9, "[3 0 R 4 0 R]", null), (10, "/Form", null),
            (11, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>", new byte[] { 255, 0, 0 }),
            (12, "<< /Type /XObject /Subtype /Image /Width 1 /Height " + payload + " /ColorSpace /DeviceGray /BitsPerComponent 8 /Length " + payload + " >>", new byte[payload])], 1);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (bool kids in new[] { false, true })
        foreach (bool subtype in new[] { false, true })
        foreach (int payload in new[] { 3, 16384 })
        foreach (string mode in new[] { "auto", "yes", "no" })
        foreach (bool job in new[] { false, true }) { yield return [kids, subtype, payload, mode, job, false]; }
        foreach (bool kids in new[] { false, true })
        foreach (bool subtype in new[] { false, true })
        foreach (bool job in new[] { false, true }) { yield return [kids, subtype, 16384, "auto", job, true]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SharedFormScopesPruneEquivalentlyAcrossIndirectSyntax(bool kids, bool subtype, int payload, string mode, bool job, bool alias)
    {
        RecordingMemoryBudget budget = new(32L << 20); using IMemoryScope owner = budget.CreateScope();
        PdfStream escaped;
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        {
            host.AddInput(QpdfPath.From("input"), Source(kids, subtype, payload, alias), QpdfInputOwnership.Transfer);
            QpdfExecutionResult result = job
                ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", pages = new[] { new { file = ".", range = "1-z" } }, removeUnreferencedResources = mode, compressStreams = "n", decodeLevel = "none" }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new(["input", "--pages", ".", "1-z", "--", "--remove-unreferenced-resources=" + mode, "--compress-streams=n", "--decode-level=none", "output"]), CancellationToken.None);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
            Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output));
            host.AddInput(QpdfPath.From("read"), output, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
            {
                QpdfObject Resolve(QpdfObject raw) => raw is PdfReference reference ? document.GetObject(reference.ObjectNumber) : raw;
                QpdfObject Get(PdfDictionary dictionary, string key) { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject raw)); return Resolve(raw); }
                Assert.Equal(2, document.PageCount);
                PdfStream ReadForm(PdfDictionary page)
                    => Assert.IsType<PdfStream>(Get(Assert.IsType<PdfDictionary>(Get(Assert.IsType<PdfDictionary>(Get(page, "Resources")), "XObject")), "Fm"));
                PdfStream first = ReadForm(document.PageReferences[0].Page), second = ReadForm(document.PageReferences[1].Page);
                PdfDictionary Objects(PdfStream form)
                    => Assert.IsType<PdfDictionary>(Get(Assert.IsType<PdfDictionary>(Get(form.Dictionary, "Resources")), "XObject"));
                PdfDictionary one = Objects(first), two = Objects(second);
                Assert.Equal(mode == "no", one.TryGet(PdfName.FromAscii("Unused"), out _));
                Assert.Equal(mode == "no", two.TryGet(PdfName.FromAscii("Unused"), out _));
                escaped = Assert.IsType<PdfStream>(Get(one, "Im0"));
                Assert.Same(escaped, Get(two, "Im0"));
                if (alias)
                {
                    PdfDictionary catalog = Assert.IsType<PdfDictionary>(Get(document.Engine.Trailer, "Root"));
                    PdfDictionary original = Assert.IsType<PdfDictionary>(Get(Assert.IsType<PdfDictionary>(Get(catalog, "OriginalResources")), "XObject"));
                    Assert.Equal(payload, Assert.IsType<PdfStream>(Get(original, "Unused")).RawMemory.Length);
                    Assert.Same(escaped, Get(original, "Im0"));
                }
            }
            Assert.Equal(0L, host.OpenLeaseCount);
        }
        Assert.Equal(new byte[] { 255, 0, 0 }, escaped.Data);
        owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }
}

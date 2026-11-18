using System.Reflection;
using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class PageImageBindingReuseTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input");
    private static readonly FieldInfo Work = typeof(ResourceGovernor).GetField("_operationsSinceCheck", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing work observation.");

    private static byte[] Fixture(int forms, bool missingWidth)
    {
        string bindings = "<< /Z 10 17 R /A 10 17 R /Stale 10 0 R /Null null /Dictionary << /Subtype /Image >> "
            + string.Join(' ', Enumerable.Range(0, forms).Reverse().Select(i => $"/Form{i:D4} 11 0 R")) + " >>";
        return PdfTestDocument.Assemble([
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 /Resources 6 0 R >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 50] >>", null),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 50] /Resources 7 0 R >>", null),
            (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 50] /Resources << /XObject 9 0 R >> >>", null),
            (6, "<< /XObject 8 0 R >>", null), (7, "<< /XObject 8 0 R >>", null),
            (8, bindings, null), (9, bindings, null),
            (10, "<< /Subtype /Image /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1"
                + (missingWidth ? "" : " /Width 1") + " >>", "A"u8.ToArray()),
            (11, "<< /Subtype /Form /BBox [0 0 1 1] /Length 3 >>", "q Q"u8.ToArray())],
            1, new Dictionary<int, int> { [10] = 17 }, "1.7", "");
    }

    [Fact]
    public async Task SharedEffectiveXObjectsReuseWorkWhileEqualIndependentDictionariesDoNot()
    {
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = long.MaxValue };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(256, false), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenCore(
            host, Input, limits, governor, diagnostics, QpdfRecoveryMode.Strict, CancellationToken.None);
        long initial = governor.CounterValues.LiveMemoryBytes;
        using (PageImageBindings.Cache cache = PageImageBindings.Cache.Create(governor, diagnostics))
        {
            var first = Read(document, 0, cache, limits, governor, CancellationToken.None);
            var shared = Read(document, 1, cache, limits, governor, CancellationToken.None);
            var independent = Read(document, 2, cache, limits, governor, CancellationToken.None);
            Assert.Equal(new[] { ("A", 10, 17), ("Z", 10, 17) }, first.Bindings);
            Assert.Equal(first.Bindings, shared.Bindings);
            Assert.Equal(first.Bindings, independent.Bindings);
            Assert.True(shared.Work * 10 < first.Work, $"First {first.Work}, shared {shared.Work}.");
            Assert.True(shared.Work * 10 < independent.Work, $"Independent {independent.Work}, shared {shared.Work}.");
            using CancellationTokenSource canceled = new();
            canceled.Cancel();
            Assert.Throws<OperationCanceledException>(() => Read(document, 1, cache, limits, governor, canceled.Token));
        }
        // Reader-owned objects loaded during the first inspection remain admitted.
        Assert.True(governor.CounterValues.LiveMemoryBytes >= initial);
        long warmed = governor.CounterValues.LiveMemoryBytes;
        using (PageImageBindings.Cache cache = PageImageBindings.Cache.Create(governor, diagnostics))
        {
            _ = Read(document, 0, cache, limits, governor, CancellationToken.None);
            _ = Read(document, 1, cache, limits, governor, CancellationToken.None);
        }
        Assert.Equal(warmed, governor.CounterValues.LiveMemoryBytes);
    }

    [Fact]
    public async Task TextAndJsonKeepPerOccurrenceDimensionPolicy()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(1, true), QpdfInputOwnership.Transfer);
        QpdfExecutionResult text = await Qpdf.ExecuteAsync(host, new(["input", "--show-pages", "--with-images"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, text.ExitCode);
        Assert.Equal(6, text.Diagnostics.Count(d => d.Message.Contains("missing /Width", StringComparison.Ordinal)));
        Assert.Equal(3, text.Diagnostics.Count(d => d.Message.Contains("/A: 10 17 R, 0 x 1", StringComparison.Ordinal)));
        QpdfJsonResult result = await Qpdf.GetDocumentJsonAsync(host, Input, new(2, ["pages"]), CancellationToken.None);
        using JsonDocument report = JsonDocument.Parse(result.Json);
        foreach (JsonElement page in report.RootElement.GetProperty("pages").EnumerateArray())
        {
            JsonElement.ArrayEnumerator images = page.GetProperty("images").EnumerateArray();
            Assert.Equal(new[] { "/A", "/Z" }, images.Select(i => i.GetProperty("name").GetString()));
            Assert.All(images, i =>
            {
                Assert.Equal(JsonValueKind.Null, i.GetProperty("width").ValueKind);
                Assert.Equal("10 17 R", i.GetProperty("object").GetString());
            });
        }
        Assert.Empty(result.Warnings);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EvictedAndOversizedScopesKeepTheirOrdinaryBindings()
    {
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = long.MaxValue };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(0, false), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenCore(
            host, Input, limits, governor, diagnostics, QpdfRecoveryMode.Strict, CancellationToken.None);
        using PageImageBindings.Cache cache = PageImageBindings.Cache.Create(governor, diagnostics);
        PageLocation first = MakePage(16);
        var cold = Read(document.Engine, first, cache, limits, governor, CancellationToken.None);
        var warm = Read(document.Engine, first, cache, limits, governor, CancellationToken.None);
        Assert.Equal(cold.Bindings, warm.Bindings);
        Assert.True(cold.Work > warm.Work);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(cold.Bindings, Read(document.Engine, MakePage(16), cache, limits, governor, CancellationToken.None).Bindings);
        }
        var evicted = Read(document.Engine, first, cache, limits, governor, CancellationToken.None);
        Assert.Equal(cold.Bindings, evicted.Bindings);
        Assert.True(evicted.Work > warm.Work);

        PageLocation oversized = MakePage(PageImageBindings.Cache.MaximumBindings + 1);
        var larger = Read(document.Engine, oversized, cache, limits, governor, CancellationToken.None);
        var repeated = Read(document.Engine, oversized, cache, limits, governor, CancellationToken.None);
        Assert.Equal(PageImageBindings.Cache.MaximumBindings + 1, larger.Bindings.Length);
        Assert.Equal(larger.Bindings, repeated.Bindings);
        Assert.Equal(larger.Work, repeated.Work);
    }

    [Fact]
    public Task InspectionAdmissionFailureRetiresBindingsAndKeepsThePriorReport()
    {
        QpdfPath output = QpdfPath.From("report");
        return PreparationAdmissionChecks.VerifyAsync(
            () => PreparationAdmissionChecks.StorageAsync(Fixture(16, false), output),
            (host, token) => Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=pages", output.Value]), token),
            output, 512, ["write", "cancel"]);
    }

    private static PageLocation MakePage(int images)
    {
        PdfDictionary bindings = new(Enumerable.Range(0, images).ToDictionary(
            i => PdfName.FromAscii($"Image{i:D3}"), _ => (QpdfObject)new PdfReference(10, 17)));
        PdfDictionary resources = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("XObject")] = bindings });
        return new(null, new PdfDictionary(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Resources")] = resources }));
    }

    private static ((string, int, int)[] Bindings, long Work) Read(QpdfDocument document, int page,
        PageImageBindings.Cache cache, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
        => Read(document.Engine, document.PageReferences[page], cache, limits, governor, token);

    private static ((string, int, int)[] Bindings, long Work) Read(DocumentEngine engine, PageLocation page,
        PageImageBindings.Cache cache, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
    {
        long before = (long)(Work.GetValue(governor) ?? throw new InvalidOperationException());
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PageImageBindings images = PageImageBindings.Create(engine, page, limits, memory, governor, token, cache);
        List<(string, int, int)> result = [];
        while (images.TryReadNext(out PageImageBindings.Binding binding))
        {
            result.Add((Encoding.ASCII.GetString(binding.Name.RawSpan), binding.Reference.ObjectNumber, binding.Reference.Generation));
        }
        long after = (long)(Work.GetValue(governor) ?? throw new InvalidOperationException());
        return (result.ToArray(), after - before);
    }
}

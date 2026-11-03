using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Content;

public sealed class InlineImageBoundaryTests
{
    private sealed class MemoryHost(IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class CancelPayload(IMemoryBudget inner, CancellationTokenSource cancellation) : IMemoryBudget
    {
        internal bool PayloadAdmitted;
        public IMemoryScope CreateScope() => inner.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            bool admitted = inner.TryReserve(bytes, out IMemoryReservation? found);
            reservation = found is null ? null : new Reservation(found, this);
            if (admitted && bytes >= 131072) { PayloadAdmitted = true; cancellation.Cancel(); }
            return admitted;
        }
        private sealed class Reservation(IMemoryReservation inner, CancelPayload owner) : IMemoryReservation
        {
            public long Bytes => inner.Bytes;
            public bool TryResize(long bytes)
            {
                bool admitted = inner.TryResize(bytes);
                if (admitted && bytes >= 131072) { owner.PayloadAdmitted = true; owner.Cancel(); }
                return admitted;
            }
            public void Dispose() => inner.Dispose();
        }
        private void Cancel() => cancellation.Cancel();
    }

    private static (byte[] Pdf, byte[] Samples) Fixture(string color, bool form)
    {
        int width = color == "mask" ? 9 : color == "packed" ? 3 : 8;
        int height = color == "packed" || color == "rgb" ? 2 : color == "mask" ? 4 : color == "cmyk" ? 2 : 8;
        int components = color is "packed" or "rgb" ? 3 : color == "cmyk" ? 4 : 1;
        int bits = color is "mask" or "packed" ? 1 : 8;
        byte[] samples = Enumerable.Repeat((byte)64, ((width * components * bits + 7) / 8) * height).ToArray();
        byte[] sentinel = color == "packed" ? " EI "u8.ToArray() : color == "mask" ? " EI q Q "u8.ToArray() : "A EI q Q q Q q Q q Q q Q q Q "u8.ToArray();
        sentinel.CopyTo(samples, 0);
        string space = color switch { "rgb" or "packed" => "/CS /RGB", "cmyk" => "/CS /CMYK", "named" => "/CS /Local", "mask" => "/IM true", _ => "/CS /G" };
        string depth = color == "mask" ? "" : "/BPC " + bits;
        string header = $"q 80 0 0 80 10 10 cm BI /W {width} /H {height} {depth} {space} ID ";
        byte[] program = [.. Encoding.ASCII.GetBytes(header), .. samples, .. " EI Q\n"u8];
        string resources = color == "named" ? "<< /ColorSpace << /Local /DeviceGray >> >>" : "<< >>";
        List<(int, string, byte[]?)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources " + (form ? "<< /XObject << /Fm 5 0 R >> >>" : resources) + " /Contents 4 0 R >>", null),
            (4, "<< /Length " + (form ? 6 : program.Length) + " >>", form ? "/Fm Do"u8.ToArray() : program),
        ];
        if (form) { objects.Add((5, "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources " + resources + " /Length " + program.Length + " >>", program)); }
        return (CheckTests.Assemble(objects, 1), samples);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string color in new[] { "gray", "rgb", "cmyk", "packed", "mask", "named" })
        foreach (bool form in new[] { false, true })
        foreach (bool job in new[] { false, true })
        foreach (bool threshold in new[] { false, true }) { yield return [color, form, job, threshold]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task KnownSampleExtentsTakePrecedenceOverEmbeddedTerminators(string color, bool form, bool job, bool threshold)
    {
        (byte[] pdf, byte[] expected) = Fixture(color, form);
        using InMemoryQpdfHost host = new(); host.AddInput(QpdfPath.From("input"), pdf, QpdfInputOwnership.Transfer);
        int minimum = threshold ? expected.Length / 2 : 0;
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", externalizeInlineImages = true, iiMinBytes = minimum.ToString(), compressStreams = "n", decodeLevel = "none", removeUnreferencedResources = "no" }), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["input", "--externalize-inline-images", "--ii-min-bytes=" + minimum, "--compress-streams=n", "--decode-level=none", "--remove-unreferenced-resources=no", "output"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes)); host.AddInput(QpdfPath.From("read"), bytes, QpdfInputOwnership.Transfer);
        PdfStream image;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read"), CancellationToken.None))
        {
            QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
            QpdfObject Get(PdfDictionary dictionary, string key) { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return Resolve(value); }
            PdfDictionary page = document.PageReferences[0].Page;
            PdfDictionary objects = Assert.IsType<PdfDictionary>(Get(Assert.IsType<PdfDictionary>(Get(page, "Resources")), "XObject"));
            if (form) { objects = Assert.IsType<PdfDictionary>(Get(Assert.IsType<PdfDictionary>(Get(Assert.IsType<PdfStream>(Get(objects, "Fm")).Dictionary, "Resources")), "XObject")); }
            image = Assert.IsType<PdfStream>(Get(objects, "IIm1"));
        }
        host.Dispose();
        Assert.True(image.RawMemory.Span.StartsWith(expected));
        Assert.True(image.RawMemory.Span[expected.Length..].IndexOfAnyExcept((byte)' ') < 0);
    }

    private static void ReadLargeImage(ResourceGovernor governor, CancellationToken token)
    {
        byte[] samples = new byte[262144]; "A EI q Q q Q q Q q Q q Q "u8.CopyTo(samples);
        ContentTokenizer tokenizer = InlineImageStorageTests.DataTokenizer(samples, governor, token); int suffix = 1;
        InlineImageStorage.ReadWithResources("/W 1 /H 262144 /BPC 8 /CS /G"u8.ToArray(), tokenizer,
            0L, 0L, null, true, ref suffix, QpdfLimits.Default, governor, new(governor), new(new Dictionary<int, QpdfObject>()), token);
    }

    [Theory]
    [InlineData("Indexed", 1)]
    [InlineData("ICCBased", 3)]
    [InlineData("DeviceN", 2)]
    [InlineData("Separation", 1)]
    [InlineData("CalGray", 1)]
    [InlineData("CalRGB", 3)]
    [InlineData("Lab", 3)]
    public void NamedColorFamiliesSupplyTheirDeclaredSampleComponents(string family, int components)
    {
        QpdfObject parameters = family switch
        {
            "ICCBased" => new PdfStream(new PdfDictionary(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("N")] = new PdfInteger("3", 3) }), []),
            "DeviceN" => new PdfArray([PdfName.FromAscii("Cyan"), PdfName.FromAscii("Spot")]),
            _ => PdfName.FromAscii("DeviceRGB"),
        };
        PdfArray definition = new([PdfName.FromAscii(family), parameters]);
        PdfDictionary spaces = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Local")] = new PdfReference(17, 0) });
        PdfDictionary resources = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("ColorSpace")] = spaces });
        byte[] samples = Enumerable.Repeat((byte)64, 8 * components).ToArray(); " EI q Q "u8.CopyTo(samples);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); int suffix = 1;
        InlineImageEntry image = InlineImageStorage.ReadWithResources("/W 8 /H 1 /BPC 8 /CS /Local /Filter []"u8.ToArray(),
            InlineImageStorageTests.DataTokenizer(samples, governor, CancellationToken.None), 0L, 0L, resources, true,
            ref suffix, QpdfLimits.Default, governor, new(governor), new(new Dictionary<int, QpdfObject> { [17] = definition }), CancellationToken.None)
            ?? throw new InvalidOperationException();
        governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(image.Stream.RawMemory.Span.StartsWith(samples));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteSamplesReceiveAdmissionAndRefundOnRefusal(bool shared)
    {
        RecordingMemoryBudget root = new(65536); using IMemoryScope caller = root.CreateScope();
        ResourceGovernor governor = shared ? new(QpdfLimits.Default, CancellationToken.None, new MemoryHost(caller))
            : new(QpdfLimits.Default with { MaxMemoryBytes = 65536 }, CancellationToken.None);
        using (ParserMemoryScope prior = ParserMemoryScope.Create(governor))
        {
            prior.Reserve(17L); long baseline = governor.Snapshot().LiveMemoryBytes;
            if (shared) { Assert.Throws<QpdfMemoryAdmissionException>(() => ReadLargeImage(governor, CancellationToken.None)); }
            else { Assert.Throws<QpdfLimitException>(() => ReadLargeImage(governor, CancellationToken.None)); }
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterSampleAdmissionRefundsBothOwners(bool governorToken)
    {
        RecordingMemoryBudget root = new(1048576); using IMemoryScope caller = root.CreateScope(); using CancellationTokenSource cancellation = new();
        CancelPayload budget = new(caller, cancellation);
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None, new MemoryHost(budget));
        using (ParserMemoryScope prior = ParserMemoryScope.Create(governor))
        {
            prior.Reserve(17L); long baseline = governor.Snapshot().LiveMemoryBytes;
            Assert.ThrowsAny<OperationCanceledException>(() => ReadLargeImage(governor, governorToken ? CancellationToken.None : cancellation.Token));
            Assert.True(budget.PayloadAdmitted); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, root.ReservedBytes);
    }
}

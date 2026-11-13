using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.Tests.Reading;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class TypedSelectionRecoveryTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Output = QpdfPath.From("output.pdf");

    internal static byte[] Damage(byte[] source, string kind)
    {
        if (kind == "zero-entry")
        {
            int at = source.AsSpan().LastIndexOf("\nxref\n"u8) + 6;
            Assert.True(at >= 6);
            return [.. source.AsSpan(0, at), .. "0 0\n"u8, .. source.AsSpan(at)];
        }
        byte[] bytes = (byte[])source.Clone();
        if (kind == "startxref")
        {
            int at = bytes.AsSpan().LastIndexOf("startxref\n"u8) + 10;
            Assert.True(at >= 10);
            while (bytes[at] is >= (byte)'0' and <= (byte)'9') { bytes[at++] = (byte)'9'; }
        }
        else
        {
            int start = bytes.AsSpan().LastIndexOf("\nxref\n"u8) + 1;
            int end = bytes.AsSpan(start).IndexOf("trailer"u8) + start;
            Assert.True(start > 0 && end > start);
            bytes.AsSpan(start, end - start).Fill((byte)' ');
        }
        return bytes;
    }

    private static byte[] Document(int omittedBytes)
    {
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R 6 0 R] /Count 4 /MediaBox [0 0 200 200] /Resources << /Font << /F1 11 0 R >> /XObject << /Im 12 0 R >> >> >>", null),
        ];
        for (int i = 0; i < 4; i++)
        { objects.Add((3 + i, $"<< /Type /Page /Parent 2 0 R /Contents {7 + i} 0 R >>", null)); }
        for (int i = 0; i < 4; i++)
        {
            byte[] content = Encoding.ASCII.GetBytes($"BT /F1 12 Tf 10 20 Td (Page{i + 1}) Tj ET\n");
            objects.Add((7 + i, $"<< /Length {content.Length} >>", content));
        }
        objects.Add((11, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null));
        byte[] image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cmyk-8x8.jpg"));
        objects.Add((12, $"<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /DCTDecode /Length {image.Length} >>", image));
        if (omittedBytes > 0) { objects.Add((13, $"<< /Length {omittedBytes} >>", new byte[omittedBytes])); }
        return CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData("zero-entry", false)]
    [InlineData("zero-entry", true)]
    [InlineData("startxref", false)]
    [InlineData("missing-xref", false)]
    public async Task StrictDefaultsRejectWhileExplicitRepairPreservesSelectionAndOpaqueResources(string damage, bool preview)
    {
        byte[] bytes = Damage(Document(0), damage), original = (byte[])bytes.Clone();
        using InMemoryQpdfHost host = new(); host.AddInput(Input, bytes, QpdfInputOwnership.Transfer);
        PdfPageSelectionOptions options = preview ? PdfPageSelectionOptions.FirstAndLast(1) : PdfPageSelectionOptions.FromPages([4, 2, 2]);
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.SelectPageAsync(host, Input, Output, options, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.SelectPageAsync(host, Input, Output, options, QpdfRecoveryMode.Strict, CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0L, host.OpenLeaseCount);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(host, Input, Output, options,
            QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None);
        int[] expected = preview ? [1, 4] : [4, 2, 2];
        Assert.Equal(4, result.SourcePageCount); Assert.Equal(expected, result.SourcePages); Assert.Equal(expected.Length, result.PageCount);
        Assert.Contains(result.Warnings, warning => warning.Code == QpdfDiagnosticCode.RecoveryEngaged);
        Assert.Equal(original, bytes); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] output));
        host.AddInput(QpdfPath.From("reread.pdf"), output, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread.pdf"), CancellationToken.None);
        Assert.Equal(expected.Length, document.PageCount);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        byte[] image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cmyk-8x8.jpg"));
        for (int i = 0; i < expected.Length; i++)
        {
            PdfDictionary page = document.PageReferences[i].Page;
            Assert.True(page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject contents));
            Assert.Contains($"(Page{expected[i]})", Encoding.ASCII.GetString(Assert.IsType<PdfStream>(Resolve(contents)).Data), StringComparison.Ordinal);
            Assert.True(page.TryGet(PdfName.FromAscii("Resources"), out QpdfObject rawResources));
            PdfDictionary resources = Assert.IsType<PdfDictionary>(Resolve(rawResources));
            Assert.True(resources.TryGet(PdfName.FromAscii("Font"), out _));
            Assert.True(resources.TryGet(PdfName.FromAscii("XObject"), out QpdfObject xobjects));
            Assert.True(Assert.IsType<PdfDictionary>(Resolve(xobjects)).TryGet(PdfName.FromAscii("Im"), out QpdfObject im));
            Assert.Equal(image, Assert.IsType<PdfStream>(Resolve(im)).Data.ToArray());
        }
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(expected.Length, independent.NumberOfPages);
    }

    [Theory]
    [InlineData("signature-visible.pdf")]
    [InlineData("signature-incremental.pdf")]
    public async Task RecoveredPreviewsPreserveStoredSignatureAppearances(string fixture)
    {
        byte[] source = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        byte[] damaged = Damage(source, "zero-entry"), original = (byte[])damaged.Clone();
        using InMemoryQpdfHost host = new(); host.AddInput(Input, source, QpdfInputOwnership.Copy);
        await Qpdf.SelectPageAsync(host, Input, Output, PdfPageSelectionOptions.FirstAndLast(1), CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] expected));
        host.AddInput(Input, damaged, QpdfInputOwnership.Transfer);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(host, Input, Output,
            PdfPageSelectionOptions.FirstAndLast(1), QpdfRecoveryMode.BoundedRepair, CancellationToken.None);
        Assert.Contains(result.Warnings, d => d.Code == QpdfDiagnosticCode.RecoveryEngaged);
        Assert.Equal(original, damaged); Assert.True(host.TryGetOutput(Output, out byte[] output));
        Assert.Equal(expected, output);
        Assert.True(output.AsSpan().IndexOf("/ByteRange"u8) < 0);
        host.AddInput(QpdfPath.From("read.pdf"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("read.pdf"), CancellationToken.None);
        Assert.Equal(result.PageCount, document.PageCount);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        bool appearance = false;
        foreach (var location in document.PageReferences)
        {
            if (!location.Page.TryGet(PdfName.FromAscii("Annots"), out QpdfObject annots)) { continue; }
            foreach (QpdfObject raw in Assert.IsType<PdfArray>(Resolve(annots)).Items)
            {
                PdfDictionary annotation = Assert.IsType<PdfDictionary>(Resolve(raw));
                if (!annotation.TryGet(PdfName.FromAscii("AP"), out QpdfObject ap)) { continue; }
                Assert.True(Assert.IsType<PdfDictionary>(Resolve(ap)).TryGet(PdfName.FromAscii("N"), out QpdfObject normal));
                Assert.NotEmpty(Assert.IsType<PdfStream>(Resolve(normal)).Data.ToArray()); appearance = true;
            }
        }
        Assert.True(appearance);
    }

    [Fact]
    public async Task ExplicitRepairAuthenticatesAndPreservesProtectedInput()
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Input, Document(0), QpdfInputOwnership.Transfer);
        QpdfOutputEncryption encryption = QpdfOutputEncryption.CreateAes256(QpdfPassword.FromText("reader"), QpdfPassword.FromText("owner"), -4, true);
        await Qpdf.SelectPageAsync(host, Input, Output, PdfPageSelectionOptions.FromPages([1, 2, 3, 4]), null, encryption, CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] encrypted));
        byte[] damaged = Damage(encrypted, "zero-entry"), original = (byte[])damaged.Clone();
        host.AddInput(Input, damaged, QpdfInputOwnership.Transfer);
        await Assert.ThrowsAsync<QpdfSecurityException>(() => Qpdf.SelectPageAsync(host, Input, Output,
            new(1), Array.Empty<PdfPageRotation>(), QpdfPassword.FromText("wrong"), QpdfOutputEncryption.Preserve,
            QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.True(host.TryGetOutput(Output, out byte[] prior)); Assert.Equal(encrypted, prior);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(host, Input, Output,
            PdfPageSelectionOptions.FromPages([4, 1]), Array.Empty<PdfPageRotation>(), QpdfPassword.FromText("reader"),
            QpdfOutputEncryption.Preserve, QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(new[] { 4, 1 }, result.SourcePages); Assert.Equal(original, damaged);
        Assert.Contains(result.Warnings, d => d.Code == QpdfDiagnosticCode.RecoveryEngaged);
        Assert.True(host.TryGetOutput(Output, out byte[] output));
        host.AddInput(QpdfPath.From("protected.pdf"), output, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("protected.pdf"), QpdfPassword.FromText("owner"), QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(2, document.PageCount);
    }

    [Fact]
    public async Task RepairModeRetainsBoundedSelectionForValidSparseInputs()
    {
        byte[] bytes = Document(9 << 20);
        SelectiveInputTests.Host host = new(bytes, 37) { ReplaceFutureInput = true };
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(host, Input, Output, PdfPageSelectionOptions.FirstAndLast(1),
            QpdfRecoveryMode.BoundedRepair, QpdfLimits.Default with { MaxMemoryBytes = 1 << 20 }, CancellationToken.None);
        Assert.Equal(new[] { 1, 4 }, result.SourcePages); Assert.Empty(result.Warnings);
        Assert.InRange(host.ReadBytes, 1, 262144); Assert.Equal(1, host.Opened); Assert.Equal(0, host.Leases);
    }

    [Theory]
    [InlineData("cancel", "read")]
    [InlineData("budget", "read")]
    [InlineData("fault", "write")]
    public async Task RecoveryHonorsPendingIoCancellationAdmissionAndPublication(string outcome, string boundary)
    {
        using RecordingJobHost host = new(boundary, null, outcome == "fault" ? "fault" : "success", true);
        host.Inner.AddInput(Input, Damage(Document(0), "zero-entry"), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        using CancellationTokenSource cancel = new();
        Task<PdfPageSelectionResult> pending = Qpdf.SelectPageAsync(host, Input, Output, new(1),
            QpdfRecoveryMode.BoundedRepair, cancel.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(pending.IsCompleted); Assert.True(host.Budget.ReservedBytes > 0);
            if (outcome == "cancel") { cancel.Cancel(); }
            if (outcome == "budget") { host.Budget.RefuseGrowth = true; }
        }
        finally { host.Resume(); }
        if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); }
        else if (outcome == "budget") { await Assert.ThrowsAsync<QpdfMemoryAdmissionException>(() => pending); }
        else { await Assert.ThrowsAsync<QpdfHostException>(() => pending); }
        Assert.True(host.Inner.TryGetOutput(Output, out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
        Assert.Empty(host.PendingRead.ToArray()); Assert.Empty(host.PendingWrite.ToArray());
        ExecutionJobFixtures.AssertClosed(host, null);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("memory")]
    [InlineData("structure")]
    public async Task ExplicitRecoveryRetainsInputMemoryAndStructuralLimits(string boundary)
    {
        byte[] bytes = Damage(Document(0), "zero-entry");
        using InMemoryQpdfHost host = new(); host.AddInput(Input, bytes, QpdfInputOwnership.Transfer);
        QpdfLimits limits = boundary switch
        {
            "input" => QpdfLimits.Default with { MaxInputBytes = bytes.Length - 1 },
            "memory" => QpdfLimits.Default with { MaxMemoryBytes = 512 },
            _ => QpdfLimits.Default with { MaxContainerSize = 2 },
        };
        await Assert.ThrowsAsync<QpdfLimitException>(() => Qpdf.SelectPageAsync(host, Input, Output, new(1),
            QpdfRecoveryMode.BoundedRepair, limits, CancellationToken.None).AsTask());
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrecoverableOrInvalidPolicyFailsBeforePublication(bool invalidPolicy)
    {
        using InMemoryQpdfHost host = new(); host.AddInput(Input, "%PDF-1.4\nnot a document\n%%EOF\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        if (invalidPolicy)
        { await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Qpdf.SelectPageAsync(host, Input, Output, new(1), (QpdfRecoveryMode)99, CancellationToken.None).AsTask()); }
        else
        { await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.SelectPageAsync(host, Input, Output, new(1), QpdfRecoveryMode.BoundedRepair, CancellationToken.None).AsTask()); }
        Assert.False(host.TryGetOutput(Output, out _)); Assert.Equal(0L, host.OpenLeaseCount);
    }
}

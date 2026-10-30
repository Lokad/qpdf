using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.TestSupport;
using System.Diagnostics.CodeAnalysis;

namespace Lokad.Qpdf.Tests.Content;

public sealed class SignatureSelectionTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf");
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");

    private static byte[] Fixture(string kind, bool reference)
    {
        string signature = "<< " + (kind == "typeless" || kind == "inherited" ? "" : "/Type /Sig ")
            + "/Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /Contents <00> /ByteRange [0 0 0 0]"
            + (reference ? " /Reference [<< /Type /SigRef /TransformMethod /DocMDP /Data 1 0 R /TransformParams << /P 2 /V /1.2 >> >>]" : "") + " >>";
        string field = "/FT /Sig /V 5 0 R";
        string fields = "4 0 R";
        List<(int Number, string Dict, byte[]? StreamData)> extra = [];
        if (kind == "unsigned") { field = "/FT /Sig"; }
        if (kind == "null") { field = "/FT /Sig /V null"; }
        if (kind == "indirect-null")
        { field = "/FT /Sig /V 7 0 R"; extra.Add((6, "null", null)); extra.Add((7, "null", null)); }
        if (kind == "direct") { field = "/FT /Sig /V " + signature; }
        if (kind == "inherited")
        {
            field = "/Parent 6 0 R /V 5 0 R"; fields = "6 0 R";
            extra.Add((6, "<< /FT /Sig /Kids [4 0 R] >>", null));
        }
        if (kind == "indirect")
        {
            field = "/FT 7 0 R /V 5 0 R";
            signature = signature.Replace("/Type /Sig ", "/Type 7 0 R ", StringComparison.Ordinal);
            extra.Add((6, "null", null)); extra.Add((7, "/Sig", null));
        }
        if (kind == "metadata" || kind == "metadata-indirect")
        {
            field = "/FT /Tx /V (text) /Private 5 0 R";
            if (kind == "metadata-indirect")
            {
                signature = signature.Replace("/Type /Sig ", "/Type 7 0 R ", StringComparison.Ordinal);
                extra.Add((6, "null", null)); extra.Add((7, "/Sig", null));
            }
        }
        if (kind == "catalog-only") { field = "/FT /Tx /V (text) /Related 1 0 R"; }
        if (kind == "parent-cycle")
        {
            field = "/Parent 6 0 R /V 5 0 R"; fields = "6 0 R";
            extra.Add((6, "<< /Parent 4 0 R /Kids [4 0 R] >>", null));
        }
        if (kind == "value-cycle")
        {
            field = "/FT /Sig /V 6 0 R";
            extra.Add((6, "7 0 R", null)); extra.Add((7, "6 0 R", null));
        }
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [" + fields + "] >> >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [4 0 R] >>", null),
            (4, "<< /Type /Annot /Subtype /Widget /T (Example) /Rect [0 0 50 20] /P 3 0 R " + field + " >>", null),
            (5, signature, null),
        ];
        objects.AddRange(extra);
        if (kind == "unselected")
        {
            objects[1] = (2, "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>", null);
            objects.Add((6, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>", null));
        }
        return CheckTests.Assemble(objects, 1);
    }

    public static IEnumerable<object[]> RejectionCases()
    {
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (string kind in new[] { "reference", "bare", "typeless", "inherited", "indirect", "direct", "metadata", "metadata-indirect" })
        { yield return [route, kind]; }
    }

    [Theory]
    [MemberData(nameof(RejectionCases))]
    public async Task PopulatedSignatureIsExplicitlyUnsupportedAndPreservesPriorOutput(string route, string kind)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(kind, kind != "bare"), QpdfInputOwnership.Transfer);
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        if (route == "typed")
        {
            QpdfCryptoException failure = await Assert.ThrowsAsync<QpdfCryptoException>(() =>
                Qpdf.SelectPageAsync(host, Input, Output, PdfPageSelectionOptions.FromPages([1]), CancellationToken.None).AsTask());
            Assert.Equal("/Sig", failure.Option);
        }
        else
        {
            QpdfExecutionResult result = await Execute(host, route, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.UnsupportedCryptoOperation && d.Message.Contains("/Sig", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("null")]
    [InlineData("indirect-null")]
    public async Task EmptySignatureFieldsRemainCopyable(string kind)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(kind, true), QpdfInputOwnership.Transfer);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(host, Input, Output,
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        Assert.Equal(1, result.PageCount); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        host.AddInput(QpdfPath.From("reread.pdf"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread.pdf"), CancellationToken.None);
        Assert.Equal(1, document.PageCount);
        Assert.True(bytes.AsSpan().IndexOf("/ByteRange"u8) < 0);
    }

    [Fact]
    public async Task SignatureMetadataCanStillBeInspectedWithoutRewriting()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture("reference", true), QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, Input, CancellationToken.None))
        { Assert.Equal(1, document.PageCount); Assert.IsType<PdfDictionary>(document.GetObject(5)); }
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["input.pdf", "--show-object=5"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("refuse")]
    [InlineData("cancel")]
    public async Task FailedCopyRefundsStorageAndPreservesPriorOutput(string outcome)
    {
        RecordingMemoryBudget root = new(outcome == "refuse" ? 4096L : 1048576L);
        using IMemoryScope sibling = root.CreateScope();
        Assert.True(sibling.TryReserve(4096L, out IMemoryReservation? held));
        using (held)
        using (IMemoryScope invocation = root.CreateScope())
        using (CancellationTokenSource cancellation = new())
        using (InMemoryQpdfHost host = new())
        {
            host.AddInput(Input, Fixture("inherited", true), QpdfInputOwnership.Transfer);
            await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
            { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
            IMemoryBudget budget = outcome == "cancel" ? new CancelingBudget(invocation, cancellation) : invocation;
            Exception? failure = await Record.ExceptionAsync(() => Qpdf.SelectPageAsync(new BudgetHost(host, budget),
                Input, Output, PdfPageSelectionOptions.FromPages([1]), cancellation.Token).AsTask());
            if (outcome == "reject") { Assert.IsType<QpdfCryptoException>(failure); }
            else if (outcome == "refuse") { Assert.IsType<QpdfMemoryAdmissionException>(failure); }
            else { Assert.IsAssignableFrom<OperationCanceledException>(failure); Assert.True(cancellation.IsCancellationRequested); }
            Assert.Equal(4096L, root.ReservedBytes); Assert.Equal(4096L, held.Bytes);
            Assert.Equal(0L, host.OpenLeaseCount);
            Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes);
        }
        Assert.Equal(0L, root.ReservedBytes);
    }

    private sealed class BudgetHost(IQpdfHost host, IMemoryBudget budget) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => budget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => host.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token) => host.CreateOutputAsync(path, token);
    }

    private sealed class CancelingBudget(IMemoryBudget budget, CancellationTokenSource cancellation) : IMemoryBudget
    {
        public IMemoryScope CreateScope() => budget.CreateScope();
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        { bool accepted = budget.TryReserve(bytes, out reservation); if (accepted) { cancellation.Cancel(); } return accepted; }
    }

    private static ValueTask<QpdfExecutionResult> Execute(IQpdfHost host, string route, CancellationToken token)
        => route == "argv" ? Qpdf.ExecuteAsync(host,
            new QpdfCommandInvocation(["input.pdf", "--pages", ".", "1", "--", "output.pdf"]), token)
        : Qpdf.ExecuteJobAsync(host,
            """{"inputFile":"input.pdf","outputFile":"output.pdf","pages":[{"file":".","range":"1"}]}""", token);

    [Theory]
    [InlineData("catalog-only")]
    [InlineData("parent-cycle")]
    [InlineData("value-cycle")]
    public async Task StructuralAndCyclicReferencesStillFailWithoutPublishing(string kind)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture(kind, false), QpdfInputOwnership.Transfer);
        QpdfParseException failure = await Assert.ThrowsAsync<QpdfParseException>(() =>
            Qpdf.SelectPageAsync(host, Input, Output, PdfPageSelectionOptions.FromPages([1]),
                QpdfLimits.Default with { MaxNestingDepth = 16L }, CancellationToken.None).AsTask());
        if (kind == "catalog-only") { Assert.Contains("/Catalog", failure.Message, StringComparison.Ordinal); }
        Assert.Equal(0L, host.OpenLeaseCount); Assert.False(host.TryGetOutput(Output, out _));
    }

    [Fact]
    public async Task SelectingAnUnsignedPageDoesNotResurrectTheUnselectedSignedPage()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(Input, Fixture("unselected", true), QpdfInputOwnership.Transfer);
        await Qpdf.SelectPageAsync(host, Input, Output, PdfPageSelectionOptions.FromPages([2]), CancellationToken.None);
        Assert.True(host.TryGetOutput(Output, out byte[] bytes));
        host.AddInput(QpdfPath.From("reread.pdf"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument result = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread.pdf"), CancellationToken.None);
        Assert.Equal(1, result.PageCount); Assert.True(bytes.AsSpan().IndexOf("/ByteRange"u8) < 0);
    }
}

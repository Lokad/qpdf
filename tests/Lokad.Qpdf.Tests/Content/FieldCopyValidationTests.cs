using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FieldCopyValidationTests
{
    private sealed class CountingObjects : Dictionary<int, QpdfObject>, IReadOnlyDictionary<int, QpdfObject>
    {
        internal int Reads { get; private set; }
        internal Action? OnRead { get; set; }
        bool IReadOnlyDictionary<int, QpdfObject>.TryGetValue(int key, out QpdfObject value)
        {
            Reads++; OnRead?.Invoke();
            bool found = TryGetValue(key, out QpdfObject? item);
            value = item ?? PdfNull.Instance;
            return found;
        }
    }

    private static byte[] Input() => CheckTests.Assemble([
        (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
        (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>", null)], 1);

    private static CountingObjects Fields(int count)
    {
        CountingObjects result = new();
        for (int i = 0; i < count; i++)
        {
            Dictionary<PdfName, QpdfObject> field = new()
            { [PdfName.FromAscii("V")] = new PdfInteger("1", 1) };
            if (i == 0) { field[PdfName.FromAscii("FT")] = PdfName.FromAscii("Tx"); }
            else { field[PdfName.FromAscii("Parent")] = new PdfReference(i + 3, 0); }
            if (i + 1 < count) { field[PdfName.FromAscii("Kids")] = new PdfArray([new PdfReference(i + 5, 0)]); }
            result.Add(i + 4, new PdfDictionary(field));
        }
        return result;
    }

    [Theory]
    [InlineData(32)]
    [InlineData(96)]
    [InlineData(192)]
    public async Task InheritedTypesAreResolvedOncePerSource(int count)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input(), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        CountingObjects fields = Fields(count); document.Engine.SetInspectionObjects(fields);
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = 499L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, limits, 1000, owner);
            // Start at the deepest field to exercise one long inherited lookup.
            copier.ValidateFieldCopy(new PdfReference(count + 3, 0));
            Assert.True(fields.Reads <= 4 * count, $"Unexpected repeated ancestry lookups: {fields.Reads}");
            int before = fields.Reads;
            for (int i = 0; i < count; i++) { copier.ValidateFieldCopy(new PdfReference(i + 4, 0)); }
            Assert.Equal(count, fields.Reads - before);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task FieldContextStillValidatesAnAlreadyCopiedObject()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input(), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        CountingObjects fields = Fields(1);
        fields[4] = new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("FT")] = PdfName.FromAscii("Sig"), [PdfName.FromAscii("V")] = new PdfDictionary(new Dictionary<PdfName, QpdfObject>()) });
        document.Engine.SetInspectionObjects(fields);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope owner = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 100, owner);
        Assert.IsType<PdfReference>(copier.CopyValue(new PdfReference(4, 0), 0));
        Assert.Throws<QpdfCryptoException>(() => copier.ValidateFieldCopy(new PdfReference(4, 0)));
        Assert.Throws<QpdfCryptoException>(() => copier.ValidateFieldCopy(new PdfReference(4, 0)));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("refuse")]
    [InlineData("depth")]
    public async Task FieldTraversalPreservesResourceFailuresAndRefundsStorage(string outcome)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input(), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        CountingObjects fields = Fields(32); document.Engine.SetInspectionObjects(fields);
        fields[4] = new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("FT")] = PdfName.FromAscii("Tx"), [PdfName.FromAscii("V")] = new PdfInteger("0", 0) });
        RecordingMemoryBudget root = new(1048576L);
        using IMemoryScope invocation = root.CreateScope();
        using InMemoryQpdfHost budgetHost = new(QpdfLimits.Default, null, invocation);
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxNestingDepth = outcome == "depth" ? 8L : 256L };
        ResourceGovernor governor = new(limits, cancellation.Token, budgetHost);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, limits, 100, owner);
            // Warm an ancestor first: a cached type must not bypass the depth limit.
            copier.ValidateFieldCopy(new PdfReference(4, 0));
            if (outcome == "cancel") { fields.OnRead = () => { if (fields.Reads == 6) { cancellation.Cancel(); } }; }
            IMemoryReservation? blocker = null;
            if (outcome == "refuse")
            { Assert.True(invocation.TryReserve(root.LimitBytes - root.ReservedBytes, out blocker)); }
            Action validate = () => copier.ValidateFieldCopy(new PdfReference(35, 0));
            using (blocker)
            {
                if (outcome == "cancel") { Assert.ThrowsAny<OperationCanceledException>(validate); }
                else if (outcome == "refuse") { Assert.Throws<QpdfMemoryAdmissionException>(validate); }
                else { Assert.Throws<QpdfParseException>(validate); }
            }
        }
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

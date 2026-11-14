using System.Text;
using Lokad.Qpdf.Content;
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

    private static byte[] SharedAppearanceInput(int count)
    {
        string fields = string.Join(' ', Enumerable.Range(6, count).Select(number => $"{number} 0 R"));
        string resources = string.Join(' ', Enumerable.Range(0, count).Select(index => $"/F{index} {count + 7 + index} 0 R"));
        byte[] appearance = Encoding.ASCII.GetBytes(string.Join('\n', Enumerable.Range(0, count).Select(index => $"/F{index} Do")));
        List<(int Number, string Dict, byte[]? StreamData)> objects = [
            (1, $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [{fields} {6 + count} 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >> >>", null),
            (2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>", null),
            (3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [{fields}] >>", null),
            (4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (5, $"<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /XObject << {resources} >> >> /Length {appearance.Length} >>", appearance),
        ];
        // /P is optional. Independent roots must not rely on page back-links
        // reaching all sibling fields before the shared resources are visited.
        for (int index = 0; index <= count; index++)
        {
            objects.Add((6 + index, $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (Field{index}) /V (Value{index}) /Rect [0 0 10 10] /AP << /N 5 0 R >> >>", null));
        }
        for (int index = 0; index < count; index++)
        {
            byte[] data = Encoding.ASCII.GetBytes($"% leaf{index}\n0 0 1 1 re f\n");
            objects.Add((count + 7 + index, $"<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Length {data.Length} >>", data));
        }
        return CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(128)]
    public async Task RetainedFieldClosureVisitsSharedAppearanceResourcesLinearly(int count)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), SharedAppearanceInput(count), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        CountingObjects objects = new();
        foreach (int number in document.Engine.XRefEntries.Keys.Where(number => number != 0)) { objects[number] = document.GetObject(number); }
        PdfReference[] fields = Enumerable.Range(6, count).Select(number => new PdfReference(number, 0)).ToArray();
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            var result = AcroFormCopy.CollectKeptClosure(objects, [.. fields, fields[0]], fields.Select(field => field.ObjectNumber).ToHashSet(),
                true, QpdfLimits.Default, governor, owner, CancellationToken.None);
            Assert.Equal(fields.Append(fields[0]), result.Kept);
            Assert.Equal(fields.Select(field => field.ObjectNumber).Concat(Enumerable.Range(count + 7, count)).Append(5).Order(), result.Closure.Order());
            Assert.True(objects.Reads <= 6 * count + 16, $"Repeated shared-resource traversal: {objects.Reads} reads for {count} fields.");
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task SelectionRetainsSharedAppearanceBindingsAndPrunesUnselectedFields()
    {
        const int count = 16;
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), SharedAppearanceInput(count), QpdfInputOwnership.Transfer);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(host, QpdfPath.From("input"), QpdfPath.From("output"),
            PdfPageSelectionOptions.FromPages([1]), CancellationToken.None);
        Assert.Empty(result.Warnings); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        host.AddInput(QpdfPath.From("reread"), bytes, QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reread"), CancellationToken.None);
        QpdfObject Resolve(QpdfObject value) => value is PdfReference reference ? document.GetObject(reference.ObjectNumber) : value;
        QpdfObject Get(PdfDictionary dictionary, string key) { Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value)); return value; }
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(Resolve(Get(document.Engine.Trailer, "Root")));
        PdfDictionary form = Assert.IsType<PdfDictionary>(Resolve(Get(catalog, "AcroForm")));
        PdfArray fields = Assert.IsType<PdfArray>(Resolve(Get(form, "Fields")));
        Assert.Equal(count, fields.Count);
        PdfReference? appearance = null;
        for (int index = 0; index < count; index++)
        {
            PdfDictionary field = Assert.IsType<PdfDictionary>(Resolve(fields[index]));
            Assert.Equal($"Field{index}", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(field, "T")).Value));
            Assert.Equal($"Value{index}", Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(Get(field, "V")).Value));
            PdfDictionary ap = Assert.IsType<PdfDictionary>(Resolve(Get(field, "AP")));
            PdfReference binding = Assert.IsType<PdfReference>(Get(ap, "N"));
            appearance ??= binding; Assert.Equal(appearance, binding);
        }
        PdfStream stream = Assert.IsType<PdfStream>(Resolve(Assert.IsType<PdfReference>(appearance)));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(Resolve(Get(stream.Dictionary, "Resources")));
        PdfDictionary xobjects = Assert.IsType<PdfDictionary>(Resolve(Get(resources, "XObject")));
        Assert.Equal(count, xobjects.Count);
        for (int leaf = 0; leaf < count; leaf++)
        {
            PdfStream resource = Assert.IsType<PdfStream>(Resolve(Get(xobjects, $"F{leaf}")));
            Assert.Equal($"% leaf{leaf}\n0 0 1 1 re f\n", Encoding.ASCII.GetString(resource.Data));
        }
        using UglyToad.PdfPig.PdfDocument independent = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Equal(1, independent.NumberOfPages);
    }

    [Fact]
    public void FieldClosureCancellationAndUnionLimitRefundTemporaryStorage()
    {
        CountingObjects objects = new();
        for (int number = 1; number <= 8; number++)
        { objects[number] = new PdfDictionary(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Widget") }); }
        PdfReference[] fields = Enumerable.Range(1, 8).Select(number => new PdfReference(number, 0)).ToArray();
        QpdfLimits limits = QpdfLimits.Default with { MaxContainerSize = 4 };
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(limits, cancellation.Token);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            long baseline = governor.Snapshot().LiveMemoryBytes;
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => AcroFormCopy.CollectKeptClosure(objects, fields,
                objects.Keys.ToHashSet(), true, limits, governor, owner, cancellation.Token));
            Assert.Equal(nameof(QpdfLimits.MaxContainerSize), failure.LimitName);
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
            int before = objects.Reads;
            objects.OnRead = () => { if (objects.Reads == before + 3) { cancellation.Cancel(); } };
            Assert.ThrowsAny<OperationCanceledException>(() => AcroFormCopy.CollectKeptClosure(objects, fields,
                objects.Keys.ToHashSet(), true, QpdfLimits.Default, governor, owner, cancellation.Token));
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

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
            copier.SignaturePolicy.IsSignatureField((PdfDictionary)fields[count + 3]);
            Assert.True(fields.Reads <= 4 * count, $"Unexpected repeated ancestry lookups: {fields.Reads}");
            int before = fields.Reads;
            for (int i = 0; i < count; i++) { copier.SignaturePolicy.IsSignatureField((PdfDictionary)fields[i + 4]); }
            Assert.Equal(0, fields.Reads - before);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task AnnotationArraysReuseIndependentFieldComponents()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input(), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        CountingObjects fields = Fields(1);
        fields[5] = new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        { [PdfName.FromAscii("FT")] = PdfName.FromAscii("Sig") });
        document.Engine.SetInspectionObjects(fields);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 100, owner);
            Assert.True(copier.NeedsSignatureCopy(new PdfArray([
                new PdfArray([new PdfReference(5, 0)]), new PdfReference(4, 0)])));
            Assert.False(copier.NeedsSignatureCopy(new PdfArray([new PdfReference(4, 0)])));
            Assert.True(copier.NeedsSignatureCopy(new PdfArray([new PdfReference(5, 0)])));
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    public async Task CopyingDistinctAnnotationArraysVisitsSharedWidgetsLinearly(int count)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), Input(), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        CountingObjects fields = new();
        fields[4] = new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("FT")] = PdfName.FromAscii("Tx"),
            [PdfName.FromAscii("Kids")] = new PdfArray(Enumerable.Range(0, count)
                .Select(index => (QpdfObject)new PdfReference(index + 5, 0)).ToArray()),
        });
        for (int index = 0; index < count; index++)
        {
            fields[index + 5] = new PdfDictionary(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("Subtype")] = PdfName.FromAscii("Widget"),
                [PdfName.FromAscii("Parent")] = new PdfReference(4, 0),
            });
        }
        document.Engine.SetInspectionObjects(fields);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope owner = ParserMemoryScope.Create(governor))
        {
            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 1000, owner);
            for (int index = 0; index < count; index++)
            { _ = copier.CopyFieldValue(new PdfArray([new PdfReference(index + 5, 0)]), 0); }
            Assert.True(fields.Reads <= 16 * count, $"Repeated component discovery: {fields.Reads} reads for {count} widgets.");
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task FieldContextDisablesASignatureWithoutChangingAPrivateCopy()
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
        PdfReference original = Assert.IsType<PdfReference>(copier.CopyValue(new PdfReference(4, 0), 0));
        PdfReference disabled = Assert.IsType<PdfReference>(copier.CopyFieldValue(new PdfReference(4, 0), 0));
        Assert.NotEqual(original, disabled);
        Assert.True(((PdfDictionary)copier.CopiedObjects[original.ObjectNumber]).TryGet(PdfName.FromAscii("V"), out _));
        Assert.False(((PdfDictionary)copier.CopiedObjects[disabled.ObjectNumber]).TryGet(PdfName.FromAscii("V"), out _));
        Assert.Equal(disabled, copier.CopyFieldValue(new PdfReference(4, 0), 0));
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
            copier.SignaturePolicy.IsSignatureField((PdfDictionary)fields[4]);
            if (outcome == "cancel") { fields.OnRead = () => { if (fields.Reads == 6) { cancellation.Cancel(); } }; }
            IMemoryReservation? blocker = null;
            if (outcome == "refuse")
            { Assert.True(invocation.TryReserve(root.LimitBytes - root.ReservedBytes, out blocker)); }
            Action validate = () => copier.SignaturePolicy.IsSignatureField((PdfDictionary)fields[35]);
            using (blocker)
            {
                if (outcome == "cancel") { Assert.ThrowsAny<OperationCanceledException>(validate); }
                else if (outcome == "refuse") { Assert.Throws<QpdfMemoryAdmissionException>(validate); }
                else { Assert.Throws<QpdfLimitException>(validate); }
            }
        }
        Assert.Equal(0L, root.ReservedBytes);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

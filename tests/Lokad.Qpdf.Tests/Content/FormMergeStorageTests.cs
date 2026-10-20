using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class FormMergeStorageTests
{
    private sealed class Prepared : IDisposable
    {
        private readonly DocumentEngine _engine;
        internal readonly GraphCopier[] Copiers;
        internal readonly CopiedForm[] Forms;
        internal readonly PdfDictionary[] Fields;
        internal Prepared(string first, string second)
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
            PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject>());
            _engine = new(ReadOnlyMemory<byte>.Empty, new XRefTable(0L, new Dictionary<int, XrefEntry>(), trailer, [trailer]),
                QpdfLimits.Default, construction, new DiagnosticBag(construction), QpdfRecoveryMode.Strict);
            Copiers = new GraphCopier[2]; Forms = new CopiedForm[2]; Fields = new PdfDictionary[2];
            string[] names = [first, second];
            for (int i = 0; i < 2; i++)
            {
                byte[] raw = [0xfe, 0xff, ..Encoding.BigEndianUnicode.GetBytes(names[i])];
                Fields[i] = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("T")] = new PdfByteString(raw, false) });
                Copiers[i] = new(_engine, construction, QpdfLimits.Default, i + 1);
                Copiers[i].AddIndirectObjectWithId(Fields[i], i + 1);
                Forms[i] = new([new PdfReference(i + 1, 0)], true, false, false, null, false, [], new Dictionary<PdfName, QpdfObject>());
            }
        }
        public void Dispose() => _engine.Dispose();
    }

    internal static byte[] Fixture(string name)
    {
        byte[] raw = [0xfe, 0xff, ..Encoding.BigEndianUnicode.GetBytes(name)];
        var objects = Inspection.CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray());
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>", null);
        objects[2] = (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Annots [5 0 R] >>", null);
        objects.Add((5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T <" + Convert.ToHexString(raw) + "> /P 3 0 R /Rect [0 0 100 20] >>", null));
        return Inspection.CheckTests.Assemble(objects, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeNamesRefuseBeforeMaterializationAndRefundScratch(bool collide)
    {
        string name = new('A', 1048576); using Prepared prepared = new(name, collide ? name : name + "B");
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 1024 }, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<QpdfLimitException>(() => AcroFormCopy.RenameCollidingFields(prepared.Forms, prepared.Copiers,
            QpdfLimits.Default, governor, CancellationToken.None));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 131072L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        for (int i = 0; i < 2; i++) { Assert.Same(prepared.Fields[i], prepared.Copiers[i].CopiedObjects[i + 1]); }
    }

    [Theory]
    [InlineData("ASCII")]
    [InlineData("Euro\u20ac")]
    [InlineData("Unicode\u2000\ud83d\ude00")]
    [InlineData("")]
    public void RenamedTextKeepsEncodingSemanticsAndEscapesThePhase(string name)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None) { OwnsDocumentCounters = true };
        PdfByteString escaped;
        using (Prepared prepared = new(name, name))
        {
            AcroFormCopy.RenameCollidingFields(prepared.Forms, prepared.Copiers, QpdfLimits.Default, governor, CancellationToken.None);
            PdfDictionary renamed = Assert.IsType<PdfDictionary>(prepared.Copiers[1].CopiedObjects[2]);
            Assert.True(renamed.TryGet(PdfName.FromAscii("T"), out QpdfObject text)); escaped = Assert.IsType<PdfByteString>(text);
        }
        governor.AcceptDocument(); governor.CompletePrivateStorage(); Assert.True(governor.CallerStorageBytes > 0L);
        Assert.Equal(name + "+1", Attachments.PdfText.Decode(escaped.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationLeavesFieldNamesAndPriorAdmissionIntact(bool caller)
    {
        using Prepared prepared = new("Field", "Field"); using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(31L); long live = governor.Snapshot().LiveMemoryBytes;
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => AcroFormCopy.RenameCollidingFields(prepared.Forms, prepared.Copiers,
            QpdfLimits.Default, governor, caller ? cancellation.Token : CancellationToken.None));
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Same(prepared.Fields[1], prepared.Copiers[1].CopiedObjects[2]);
    }
}

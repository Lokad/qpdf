using System.Reflection;
using System.Text;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ResourcePruningJoinTests
{
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(entry => PdfName.FromAscii(entry.Key), entry => entry.Value));
    private static readonly Func<GraphCopier, int, PdfDictionary, QpdfLimits, ResourceGovernor, DiagnosticBag, CancellationToken, PdfDictionary> Prune =
        (typeof(ContentTransforms).GetMethod("PrunePage", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing pruning."))
        .CreateDelegate<Func<GraphCopier, int, PdfDictionary, QpdfLimits, ResourceGovernor, DiagnosticBag, CancellationToken, PdfDictionary>>();
    private sealed class Prepared : IDisposable
    {
        private readonly DocumentEngine _engine;
        private readonly ParserMemoryScope _copyMemory;
        internal readonly GraphCopier Copier;
        internal Prepared(ResourceGovernor governor)
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None); PdfDictionary trailer = Dictionary();
            _engine = new(ReadOnlyMemory<byte>.Empty, new XRefTable(0L, new Dictionary<int, XrefEntry>(), trailer, [trailer]),
                QpdfLimits.Default, construction, new DiagnosticBag(construction), QpdfRecoveryMode.Strict);
            _copyMemory = ParserMemoryScope.Create(governor);
            Copier = GraphCopier.CreateOwned(_engine, governor, QpdfLimits.Default, 1, _copyMemory);
        }
        internal PdfDictionary Run(PdfDictionary page, ResourceGovernor governor, DiagnosticBag diagnostics)
        {
            ((IDictionary<int, QpdfObject>)Copier.CopiedObjects).Add(3, page);
            return Prune(Copier, 3, page, QpdfLimits.Default, governor, diagnostics, CancellationToken.None);
        }
        public void Dispose() { _engine.Dispose(); _copyMemory.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedPageReleasesJoinedAdmissionAcrossSingleAndMultipleStreams(bool multiple)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using Prepared prepared = new(governor); long baseline = governor.Snapshot().LiveMemoryBytes;
        byte[] first = Encoding.ASCII.GetBytes(new string(' ', 16384) + "q");
        PdfStream stream = new(Dictionary(), first);
        QpdfObject contents = multiple ? new PdfArray([stream, new PdfStream(Dictionary(), "Q"u8.ToArray())]) : stream;
        PdfDictionary page = Dictionary(("Resources", Dictionary()), ("Contents", contents));
        Assert.Same(page, prepared.Run(page, governor, new DiagnosticBag(governor)));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(first, stream.Data);
    }

    [Fact]
    public void UnchangedFormReleasesItsJoinedAdmissionBeforePageAnalysisEnds()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using Prepared prepared = new(governor); long baseline = governor.Snapshot().LiveMemoryBytes;
        PdfStream form = new(Dictionary(("Subtype", PdfName.FromAscii("Form")), ("Resources", Dictionary())),
            Encoding.ASCII.GetBytes(new string(' ', 16384) + "q"));
        ((IDictionary<int, QpdfObject>)prepared.Copier.CopiedObjects).Add(5, form);
        PdfDictionary resources = Dictionary(("XObject", Dictionary(("Form", new PdfReference(5, 0)))));
        PdfDictionary page = Dictionary(("Resources", resources), ("Contents", new PdfStream(Dictionary(), "/Form Do"u8.ToArray())));
        Assert.Same(page, prepared.Run(page, governor, new DiagnosticBag(governor)));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        Assert.Same(form, prepared.Copier.CopiedObjects[5]); Assert.Equal(16385, form.Data.Length);
    }

    [Fact]
    public void JoinedChunkBoundarySeparatesNamesFromFollowingOperators()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); using Prepared prepared = new(governor); long baseline = governor.Snapshot().LiveMemoryBytes;
        PdfDictionary fonts = Dictionary(("Used", Dictionary()), ("Unused", Dictionary()));
        PdfDictionary page = Dictionary(("Resources", Dictionary(("Font", fonts))),
            ("Contents", new PdfArray([new PdfStream(Dictionary(), "/Used"u8.ToArray()), new PdfStream(Dictionary(), "12 Tf"u8.ToArray())])));
        PdfDictionary output = prepared.Run(page, governor, new DiagnosticBag(governor));
        Assert.True(output.TryGet(PdfName.FromAscii("Resources"), out QpdfObject resourceValue));
        Assert.True(Assert.IsType<PdfDictionary>(resourceValue).TryGet(PdfName.FromAscii("Font"), out QpdfObject fontValue));
        PdfDictionary retained = Assert.IsType<PdfDictionary>(fontValue);
        Assert.True(retained.TryGet(PdfName.FromAscii("Used"), out _)); Assert.False(retained.TryGet(PdfName.FromAscii("Unused"), out _));
    }
}

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Foundation;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class InlineImageReconstructionTests
{
    private static readonly MethodInfo Rewrite = typeof(Lokad.Qpdf.Content.InlineImages).GetMethod("RewriteContent", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException();
    private static PdfDictionary Dictionary(params (string Key, QpdfObject Value)[] entries)
        => new(entries.ToDictionary(e => PdfName.FromAscii(e.Key), e => e.Value));
    private static object Property(object value, string property) => value.GetType().GetProperty(property)?.GetValue(value) ?? throw new InvalidOperationException(property);
    private static DocumentEngine Engine()
    {
        ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None); PdfDictionary trailer = Dictionary();
        return new(ReadOnlyMemory<byte>.Empty, new XRefTable(0L, new Dictionary<int,XrefEntry>(), trailer, [trailer]), QpdfLimits.Default, construction, new(construction), QpdfRecoveryMode.Strict);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Run(ResourceGovernor governor, byte[] bytes, PdfDictionary? page, long minimum, bool compress, DiagnosticBag diagnostics, CancellationToken token)
    {
        using DocumentEngine engine = Engine(); GraphCopier copier = new(engine, governor, QpdfLimits.Default, 1);
        try { return Rewrite.Invoke(null, [copier, page, bytes, minimum, compress, null, QpdfLimits.Default, governor, diagnostics, token]) ?? throw new InvalidOperationException(); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
    }
    private static byte[] Content(string body) => Encoding.ASCII.GetBytes(body);

    [Theory]
    [InlineData("")]
    [InlineData("q Q\n")]
    [InlineData("% keep comments\n q\tQ\r\n")]
    [InlineData("BI /W 1 /H 1 ID abc EI\n")]
    public void UnchangedContentAndBelowThresholdImagesRetireConstructionStorage(string source)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(61L); long baseline = governor.Snapshot().LiveMemoryBytes;
        object result = Run(governor, Content(source), null, 1024L, false, diagnostics, CancellationToken.None);
        Assert.False(Assert.IsType<bool>(Property(result,"Changed"))); Collect(governor);
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes); Assert.Equal(61L, prior.ReservedBytes); GC.KeepAlive(result);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RewriteKeepsCommentsAndRawOperatorsAroundImages(bool compress)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        object result = Run(governor, Content("% comment\nq\tBI /W 1 /H 1 /CS /G /BPC 8 ID abc EI Q\n"), null, 0L, compress, diagnostics, CancellationToken.None);
        Assert.True(Assert.IsType<bool>(Property(result,"Changed")));
        PdfStream stream = Assert.IsType<PdfStream>(Property(result,"Stream"));
        byte[] data = compress ? Lokad.Qpdf.Filters.FlateFilter.Decode(stream.Data, null, QpdfLimits.Default, new(QpdfLimits.Default,CancellationToken.None)) : stream.Data;
        Assert.Equal("% comment\nq\t/IIm1 Do\n Q\n", Encoding.ASCII.GetString(data));
    }
    [Fact]
    public void SeveralImagesUseOrderedDistinctNamesAndOneResourceDictionary()
    {
        PdfDictionary resources = Dictionary(("XObject",Dictionary(("IIm1",PdfNull.Instance),("IIm3",PdfNull.Instance))));
        ResourceGovernor governor = new(QpdfLimits.Default,CancellationToken.None); DiagnosticBag diagnostics = new(governor);
        object result = Run(governor,Content("BI /W 1 /H 1 ID a EI BI /W 1 /H 1 ID b EI BI /W 1 /H 1 ID c EI\n"),Dictionary(("Resources",resources)),0L,false,diagnostics,CancellationToken.None);
        PdfStream stream=Assert.IsType<PdfStream>(Property(result,"Stream"));
        Assert.Equal("/IIm1 Do\n /IIm2 Do\n /IIm3 Do\n\n",Encoding.ASCII.GetString(stream.Data));
        PdfDictionary rewritten=Assert.IsType<PdfDictionary>(Property(result,"Resources"));
        Assert.True(rewritten.TryGet(PdfName.FromAscii("XObject"),out QpdfObject? value)); PdfDictionary images=Assert.IsType<PdfDictionary>(value);
        Assert.Equal(3,images.Count); Assert.Equal(2,Assert.IsType<PdfDictionary>(Get(resources,"XObject")).Count);
    }
    [Theory]
    [InlineData("BI /W [ ID abc EI\n")]
    [InlineData("BI /Bad#00 1 ID abc EI\n")]
    public void MalformedImageDictionaryKeepsOriginalWarningAndRetiresUnpublishedStorage(string source)
    {
        ResourceGovernor governor=new(QpdfLimits.Default,CancellationToken.None); DiagnosticBag diagnostics=new(governor);
        object result=Run(governor,Content(source),null,0L,false,diagnostics,CancellationToken.None);
        Assert.False(Assert.IsType<bool>(Property(result,"Changed")));
        Assert.Contains(diagnostics.Entries,d=>d.Message=="Unable to filter content stream; not attempting to externalize inline images.");
        GC.KeepAlive(result);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothCanceledTokensAbortBeforePublication(bool caller)
    {
        using CancellationTokenSource cancellation=new(); ResourceGovernor governor=new(QpdfLimits.Default,caller?CancellationToken.None:cancellation.Token);
        DiagnosticBag diagnostics=new(governor); using ParserMemoryScope prior=ParserMemoryScope.Create(governor); prior.Reserve(61L);
        long baseline=governor.Snapshot().LiveMemoryBytes; cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>Run(governor,Content("BI /W 1 ID abc EI\n"),null,0L,false,diagnostics,caller?cancellation.Token:CancellationToken.None));
        Assert.Equal(baseline,governor.Snapshot().LiveMemoryBytes); Assert.Equal(61L,prior.ReservedBytes);
    }
    [Theory]
    [InlineData(1024L)]
    [InlineData(4096L)]
    public void CopyAdmissionFailsBeforeAnOversizedImageOrOutputAllocation(long maximum)
    {
        ResourceGovernor governor=new(QpdfLimits.Default with {MaxMemoryBytes=maximum},CancellationToken.None); DiagnosticBag diagnostics=new(governor);
        using ParserMemoryScope prior=ParserMemoryScope.Create(governor); prior.Reserve(61L);
        byte[] source=Content("BI /W 1 ID "+new string('a',32768)+" EI\n");
        QpdfLimitException error=Assert.Throws<QpdfLimitException>(()=>Run(governor,source,null,0L,false,diagnostics,CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes),error.LimitName); Collect(governor); Assert.Equal(61L,prior.ReservedBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes<=maximum); GC.KeepAlive(source);
    }
    private static QpdfObject Get(PdfDictionary dictionary,string name)
        =>dictionary.TryGet(PdfName.FromAscii(name),out QpdfObject? value)&&value is not null?value:throw new InvalidOperationException();
    private static void Collect(ResourceGovernor governor)
    {for(int round=0;round<4;round++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();governor.ReserveMemory(0L);}}
}

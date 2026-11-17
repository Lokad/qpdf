using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Security;

public sealed class StreamCryptPreparationTests
{
    private static List<NumberedObject> Model(ResourceGovernor governor, ParserMemoryScope source)
    {
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        QpdfObject Parse(string text) => parser.ParseObjectValue(new PdfTokenizer(System.Text.Encoding.ASCII.GetBytes(text)), 0, source);
        PdfDictionary header = Assert.IsType<PdfDictionary>(Parse(
            "<< /Filter 7 0 R /DecodeParms 8 0 R /PrivateFilter 7 0 R /PrivateParms 8 0 R >>"));
        source.Reserve(1048576L + 32L + 64L + 16L * 6);
        PdfStream stream = PdfStream.FromOwnedData(header, new byte[1048576]);
        return new(6) { new(1, PdfNull.Instance), new(2, PdfNull.Instance), new(4, stream),
            new(7, Parse("[/Crypt /FlateDecode]")), new(8, Parse("[<< /Name /Identity >> null]")) };
    }

    [Fact]
    public void PreparedModelBorrowsPayloadAndKeepsPrivateFilterBindings()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using (ParserMemoryScope source = ParserMemoryScope.Create(governor))
        using (ParserMemoryScope prepared = ParserMemoryScope.Create(governor))
        using (OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor))
        {
            List<NumberedObject> model = Model(governor, source); PdfStream original = Assert.IsType<PdfStream>(model[2].Value);
            Assert.Equal(9, OutputModelPreparation.AddEncryptionObject(model, security, prepared, governor));
            PdfStream normalized = Assert.IsType<PdfStream>(model[2].Value);
            Assert.Same(original.AllocationCarrier, normalized.AllocationCarrier);
            Assert.True(normalized.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject filters));
            Assert.Equal(PdfName.FromAscii("FlateDecode"), Assert.Single(Assert.IsType<PdfArray>(filters).Items));
            Assert.True(normalized.Dictionary.TryGet(PdfName.FromAscii("DecodeParms"), out QpdfObject parameters));
            Assert.IsType<PdfNull>(Assert.Single(Assert.IsType<PdfArray>(parameters).Items));
            Assert.True(normalized.Dictionary.TryGet(PdfName.FromAscii("PrivateFilter"), out QpdfObject privateFilter));
            Assert.Equal(7, Assert.IsType<PdfReference>(privateFilter).ObjectNumber);
            Assert.Equal(PdfName.FromAscii("Crypt"), Assert.IsType<PdfArray>(model[3].Value)[0]);
            Assert.Equal(2, Assert.IsType<PdfArray>(model[4].Value).Count);
            Assert.True(original.Dictionary.TryGet(PdfName.FromAscii("Filter"), out QpdfObject originalFilter));
            Assert.Equal(7, Assert.IsType<PdfReference>(originalFilter).ObjectNumber);
            Assert.InRange(prepared.ReservedBytes, 1L, 16384L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("/F (external)", false, true)]
    [InlineData("/Type /Metadata", true, true)]
    [InlineData("/Type /XRef", false, false)]
    public void PreparationPreservesClearStreamExceptionsWithoutTrustingTypeTags(string fields, bool metadata, bool clear)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, false, governor);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        PdfDictionary header = Assert.IsType<PdfDictionary>(parser.ParseObjectValue(new PdfTokenizer(
            System.Text.Encoding.ASCII.GetBytes("<< /Filter /Crypt " + fields + " >>")), 0, source));
        PdfStream original = new(header, "clear"u8.ToArray());
        List<NumberedObject> model = new(2) { new(4, original) };
        int number = OutputModelPreparation.AddEncryptionObject(model, security, source, governor);
        Assert.Equal(5, number);
        PdfStream normalized = Assert.IsType<PdfStream>(model[0].Value);
        Assert.False(normalized.Dictionary.TryGet(PdfName.FromAscii("Filter"), out _));
        PoolResolver resolver = new(model.ToDictionary(item => item.Number, item => item.Value));
        PdfStream encrypted = Assert.IsType<PdfStream>(security.EncryptObject(normalized, 4, 0, metadata, resolver, source, governor));
        if (clear)
        {
            Assert.Equal("clear"u8.ToArray(), encrypted.Data);
            Assert.Same(original.AllocationCarrier, encrypted.AllocationCarrier);
        }
        else { Assert.NotEqual("clear"u8.ToArray(), encrypted.Data); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusalRetiresPreparationWithoutChangingSourceOrPriorStorage(bool shared)
    {
        using RecordingMemoryBudget budget = new(8L << 20);
        using InMemoryQpdfHost storage = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4L << 20 };
        ResourceGovernor governor = new(limits, CancellationToken.None, new MemoryBudgetHost(storage, budget));
        using (ParserMemoryScope source = ParserMemoryScope.Create(governor))
        using (ParserMemoryScope prepared = ParserMemoryScope.Create(governor))
        using (ParserMemoryScope pressure = ParserMemoryScope.Create(governor))
        using (OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor))
        {
            List<NumberedObject> model = Model(governor, source); NumberedObject[] original = [.. model];
            if (shared)
            {
                // Consume existing block slack before arming immediate refusal.
                pressure.Reserve(budget.ReservedBytes - governor.Snapshot().LiveMemoryBytes);
                budget.RefuseGrowth = true;
            }
            else { pressure.Reserve(governor.RemainingMemoryBytes - 64L); }
            long before = governor.Snapshot().LiveMemoryBytes;
            Action prepare = () => OutputModelPreparation.AddEncryptionObject(model, security, prepared, governor);
            if (shared) { Assert.Throws<QpdfMemoryAdmissionException>(prepare); }
            else { Assert.Throws<QpdfLimitException>(prepare); }
            Assert.Equal(original, model); Assert.Equal(0L, prepared.ReservedBytes);
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
            budget.RefuseGrowth = false;
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, budget.ReservedBytes);
    }

    [Fact]
    public void LaterInvalidStageDoesNotPublishEarlierHeaderReplacements()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using ParserMemoryScope prepared = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        List<NumberedObject> model = Model(governor, source);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        PdfDictionary invalid = Assert.IsType<PdfDictionary>(parser.ParseObjectValue(new PdfTokenizer(
            "<< /Filter /Crypt /DecodeParms << /Name /Unknown >> >>"u8.ToArray()), 0, source));
        model.Add(new(10, new PdfStream(invalid, []))); NumberedObject[] original = [.. model];
        long before = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfUnsupportedFeatureException>(() => OutputModelPreparation.AddEncryptionObject(model, security, prepared, governor));
        Assert.Equal(original, model); Assert.Equal(0L, prepared.ReservedBytes); Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void CancellationDuringCryptNameResolutionDoesNotBecomeOpaqueFallback()
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, cancellation.Token);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        PdfDictionary header = Assert.IsType<PdfDictionary>(parser.ParseObjectValue(new PdfTokenizer(
            "<< /Filter /Crypt /DecodeParms << /Name 3 0 R >> >>"u8.ToArray()), 0, source));
        PdfStream stream = new(header, "unchanged"u8.ToArray());
        long before = governor.Snapshot().LiveMemoryBytes;
        CancelingResolver resolver = new(cancellation);
        using (OwnedAllocationBatch graph = OwnedAllocationBatch.Create(governor, cancellation.Token))
        {
            Assert.Throws<OperationCanceledException>(() => StreamCryptFilter.RemoveIdentity(stream, resolver, graph));
            Assert.Equal(1, resolver.Calls); Assert.Equal(0L, graph.Bytes);
        }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Same(header, stream.Dictionary); Assert.Equal("unchanged"u8.ToArray(), stream.Data);
    }

    private sealed class CancelingResolver(CancellationTokenSource cancellation) : IIndirectResolver
    {
        internal int Calls;
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        { Calls++; value = PdfName.FromAscii("Identity"); cancellation.Cancel(); return true; }
    }
}

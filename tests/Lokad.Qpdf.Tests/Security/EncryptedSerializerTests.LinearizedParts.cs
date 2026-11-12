using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Security;

public sealed partial class EncryptedSerializerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProtectedLinearizationPlacesEncryptionDictionaryBeforeHints(bool streamed, bool pageAlias)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        List<NumberedObject> model = Model(governor, source, security);
        if (pageAlias)
        {
            PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
            model[2] = new(3, parser.ParseObjectValue(new PdfTokenizer(Encoding.ASCII.GetBytes(
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Private 7 0 R >>")), 0, source));
        }
        long before = governor.Snapshot().LiveMemoryBytes;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            if (streamed)
            {
                using StreamedParts parts = LinearizedParts.ClassifyStreamed(model, [new[] { 3, 5, 7 }], 7,
                    memory, governor, CancellationToken.None);
                Assert.Equal(7, parts.Part4[^1]); Assert.Equal(parts.HintNumber - 1, parts.Renumber[7]);
                Assert.False(parts.Members.ContainsKey(7)); Assert.False(parts.Members.ContainsKey(3));
                Assert.DoesNotContain(7, parts.Part6); Assert.DoesNotContain(7, parts.Part9);
                Assert.Equal(new[] { 5 }, parts.ContainerMembers[Assert.Single(parts.Containers)]);
                Assert.Equal(model.Count + 1, parts.Renumber.Count);
            }
            else
            {
                using LinearizedParts parts = LinearizedParts.Classify(model, 7, memory, governor, CancellationToken.None);
                Assert.Equal(7, parts.Part4[^1]); Assert.Equal(parts.HintNumber - 1, parts.Renumber[7]);
                Assert.DoesNotContain(7, parts.Part6); Assert.DoesNotContain(7, parts.Part9);
                Assert.Equal(model.Count, parts.Renumber.Count);
            }
        }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        Assert.Same(security.Dictionary, model[^1].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidLinearizedEncryptionObjectsRefundClassification(bool streamed)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        List<NumberedObject> model = Model(governor, source, security);
        foreach (int number in new[] { -1, 1, 2, 3, 4, 100 })
        {
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            long before = governor.Snapshot().LiveMemoryBytes;
            Assert.Throws<QpdfException>(() => Classify(number, memory));
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0, memory.ReservedBytes);
        }

        IDisposable Classify(int number, ParserMemoryScope memory) => streamed
            ? LinearizedParts.ClassifyStreamed(model, [new[] { 5, 7 }], number, memory, governor, CancellationToken.None)
            : LinearizedParts.Classify(model, number, memory, governor, CancellationToken.None);
    }

    [Fact]
    public void ProtectedLinearizedGroupsKeepSignatureDictionariesDirect()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope source = ParserMemoryScope.Create(governor);
        using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
        List<NumberedObject> model = Model(governor, source, security);
        PdfParser parser = new(QpdfLimits.Default, governor, new(governor), null);
        model[4] = new(5, parser.ParseObjectValue(new PdfTokenizer("<< /Type /Sig /Contents <010203> >>"u8.ToArray()), 0, source));
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        using StreamedParts parts = LinearizedParts.ClassifyStreamed(model, [new[] { 5, 7 }], 7,
            memory, governor, CancellationToken.None);
        Assert.Empty(parts.Containers); Assert.False(parts.Members.ContainsKey(5));
        Assert.Contains(5, parts.Part9); Assert.Contains(7, parts.Part4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryProtectedLinearizationClassificationAdmissionFailureRefunds(bool streamed)
    {
        (int before, int growths) = Run(new(16 * 1024 * 1024), false);
        for (int growth = 1; growth <= growths; growth++)
        { _ = Run(new(16 * 1024 * 1024) { RefuseOnGrowth = before + growth }, true); }

        (int Before, int Growths) Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            using ParserMemoryScope source = ParserMemoryScope.Create(governor);
            using OutputSecurity security = OutputSecurity.CreateRevision6([], "owner"u8, -4, true, governor);
            List<NumberedObject> model = Model(governor, source, security);
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            IDisposable Classify() => streamed
                ? LinearizedParts.ClassifyStreamed(model, [new[] { 3, 5, 7 }], 7, memory, governor, CancellationToken.None)
                : LinearizedParts.Classify(model, 7, memory, governor, CancellationToken.None);
            int initial = budget.GrowthCalls; long reserved = budget.ReservedBytes;
            if (refuses) { Assert.Throws<QpdfMemoryAdmissionException>(Classify); Assert.Equal(reserved, budget.ReservedBytes); }
            else { using IDisposable parts = Classify(); }
            int attempts = budget.GrowthCalls - initial;
            memory.Dispose(); security.Dispose(); source.Dispose(); Assert.Equal(0, budget.ReservedBytes);
            return (initial, attempts);
        }
    }
}

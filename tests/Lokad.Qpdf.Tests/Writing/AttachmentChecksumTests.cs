using System.Security.Cryptography;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentChecksumTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32767)]
    [InlineData(32768)]
    [InlineData(32769)]
    [InlineData(1048576)]
    public void DigestMatchesAcrossChunkEdgesAndOutlivesConstruction(int length)
    {
        byte[] input = new byte[length]; new Random(42).NextBytes(input);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        byte[] digest;
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            digest = AttachmentBuilder.Checksum(input, governor, memory, CancellationToken.None);
            Assert.Equal(40L, memory.ReservedBytes);
            Assert.InRange(governor.Snapshot().PeakMemoryBytes, 104L, 512L);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(MD5.HashData(input), digest);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("digest")]
    [InlineData("caller")]
    [InlineData("governor")]
    public void RefusalAndCancellationRefundTemporaryContextAndPreserveEarlierStorage(string failure)
    {
        using CancellationTokenSource caller = new(), operation = new();
        long maximum = failure == "context" ? 272L : failure == "digest" ? 312L : 4096L;
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = maximum }, operation.Token);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            memory.Reserve(17L);
            if (failure == "caller") { caller.Cancel(); }
            if (failure == "governor") { operation.Cancel(); }
            Action hash = () => AttachmentBuilder.Checksum(new byte[65537], governor, memory, caller.Token);
            if (failure is "context" or "digest") { Assert.Throws<QpdfLimitException>(hash); }
            else { Assert.ThrowsAny<OperationCanceledException>(hash); }
            Assert.Equal(17L, memory.ReservedBytes); Assert.Equal(81L, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void SharedContextRefusalRefundsTheCallerOwner()
    {
        RecordingMemoryBudget budget = new(32768L) { RefuseOnGrowth = 2 };
        using (IMemoryScope owner = budget.CreateScope())
        using (InMemoryQpdfHost host = new(QpdfLimits.Default, null, owner))
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
            using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
            {
                long prior = budget.ReservedBytes - governor.Snapshot().LiveMemoryBytes;
                memory.Reserve(prior); // Fill the admitted block before hashing grows it.
                Assert.Throws<QpdfMemoryAdmissionException>(() => AttachmentBuilder.Checksum([], governor, memory, CancellationToken.None));
                Assert.Equal(prior, memory.ReservedBytes);
            }
            governor.CompleteStorage(); Assert.Equal(0L, budget.ReservedBytes);
        }
        Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }
}

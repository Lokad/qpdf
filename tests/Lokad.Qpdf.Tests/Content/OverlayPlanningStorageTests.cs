using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class OverlayPlanningStorageTests
{
    [Fact]
    public async Task EmptyPlacementsDoNotAllocatePerDestination()
    {
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("input");
        host.AddInput(input, Inspection.CheckTests.Assemble(
            Inspection.CheckTests.SinglePage("<< /Length 3 >>", "q Q"u8.ToArray()), 1), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, input, CancellationToken.None);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L * 1024 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope ownership = ParserMemoryScope.Create(governor))
        {
            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, limits, 10, ownership);
            var occurrences = Enumerable.Repeat((copier, 3), 65536).ToArray();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            OverlayApplication result = await Overlays.ApplyAsync(host, input, occurrences, [],
                [new OverlaySpec("input", "", null, null)], false, null, QpdfRecoveryMode.Strict,
                limits, governor, new DiagnosticBag(governor), ownership, CancellationToken.None, 10, null);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.True(bytes < 1024L * 1024, $"Empty placement planning allocated {bytes} bytes.");
            Assert.Empty(result.FieldImports); Assert.Single(result.Copiers); Assert.Equal(10, result.NextId);
            Assert.Equal(1L, host.OpenLeaseCount); // Only this test's pre-opened destination remains.
        }
        governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

using Lokad.Qpdf.Tests.Inspection;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class OwnershipStressTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Layer", "Stress")]
    public Task IndependentLifetimesPreserveSequentialFiniteCycles()
        => OwnershipBatch.RunAsync(output,
            DiagnosticFailureExportTests.StressCases(output)
                .Concat(PresentationOwnershipTests.StressCases())
                .Concat(RetainedLedgerLifetimeTests.StressCases())
                .Concat(CheckDetailOwnershipTests.StressCases())
                .Concat(FormTraversalOwnershipTests.StressCases()));
}

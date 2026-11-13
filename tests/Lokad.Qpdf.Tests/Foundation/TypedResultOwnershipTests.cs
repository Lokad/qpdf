using System.Reflection;
using System.Runtime.CompilerServices;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class TypedResultOwnershipTests
{
    public static IEnumerable<object[]> SuccessAliases()
    {
        for (int i = 0; i < ExecutionJobFixtures.TypedRoutes.Length; i++)
        { yield return [ExecutionJobFixtures.TypedRoutes[i], i % 2 == 0]; }
    }

    private static object Copy(object result) => result switch
    {
        PdfPageSelectionResult r => r with { },
        PdfMultiPageSelectionResult r => r with { },
        QpdfCheckResult r => r with { },
        QpdfJsonResult r => r with { },
        QpdfJsonImportResult r => r with { },
        QpdfAttachmentExtraction r => r with { },
        QpdfAttachmentEditResult r => r with { },
        _ => throw new InvalidOperationException("Unknown public result family."),
    };

    private static object Construct(object result) => result switch
    {
        PdfPageSelectionResult r => new PdfPageSelectionResult(r.SourcePageCount, r.SourcePages, r.PageCount, r.Warnings, r.Counters),
        PdfMultiPageSelectionResult r => new PdfMultiPageSelectionResult(r.PageCount, r.Mapping, r.SourcePageCounts, r.OutputPaths, r.Warnings, r.Counters),
        QpdfCheckResult r => new QpdfCheckResult(r.PageCount, r.Version, r.Checks, r.Diagnostics, r.Counters),
        QpdfJsonResult r => new QpdfJsonResult(r.Json, r.Warnings, r.Counters),
        QpdfJsonImportResult r => new QpdfJsonImportResult(r.PageCount, r.Warnings, r.Counters),
        QpdfAttachmentExtraction r => new QpdfAttachmentExtraction(r.Key, r.ByteCount, r.Warnings, r.Counters),
        QpdfAttachmentEditResult r => new QpdfAttachmentEditResult(r.AttachmentCount, r.Warnings, r.Counters),
        _ => throw new InvalidOperationException("Unknown public result family."),
    };

    [Theory]
    [MemberData(nameof(SuccessAliases))]
    [Trait("Layer", "Contract")]
    public async Task ResultCounterAndCallerCopiesPreserveImmutableValues(string route, bool compressed)
    {
        using RecordingJobHost host = await ExecutionJobFixtures.PrepareAsync(route, compressed, "read", "success");
        Task<object> running = ExecutionJobFixtures.RunAsync(host, route, QpdfLimits.Default, CancellationToken.None);
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(host.Budget.ReservedBytes > 0L); host.Resume();
            object result = await running;
            QpdfExecutionCounters counters = ExecutionJobFixtures.Counters(result);
            Assert.Equal(host.HostCalls, counters.HostOperations); Assert.Equal(0L, counters.OpenHandles);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            object copied = Copy(result), constructed = Construct(result);
            Assert.Equal(result, copied); Assert.Equal(result.GetHashCode(), copied.GetHashCode());
            Assert.Same(counters, ExecutionJobFixtures.Counters(copied));
            Assert.Same(counters, ExecutionJobFixtures.Counters(constructed));
            QpdfExecutionCounters[] counterCopies = [counters with { }, new(counters.LiveMemoryBytes,
                counters.PeakMemoryBytes, counters.TotalDecodedBytes, counters.TotalOutputBytes,
                counters.HostOperations, counters.OpenHandles, counters.DiagnosticCount)];
            host.Dispose();
            Assert.Equal(0L, host.Budget.ReservedBytes); Assert.Equal(0, host.Budget.LiveReservations);
            foreach (QpdfExecutionCounters copy in counterCopies)
            { Assert.Equal(counters, copy); Assert.Equal(counters.GetHashCode(), copy.GetHashCode()); }
            foreach (object value in new[] { result, copied, constructed })
            {
                foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                { Assert.False(property.CanWrite); }
                Assert.Equal(counterCopies[0], ExecutionJobFixtures.Counters(value));
            }
        }
        finally { host.Resume(); await running; }
    }

    [Theory]
    [InlineData("PageSelectionOutcome")]
    [InlineData("MultiPageSelectionOutcome")]
    [InlineData("AttachmentExtractionOutcome")]
    [InlineData("JsonInspectionOutcome")]
    [InlineData("JsonImportOutcome")]
    [InlineData("TypedCheckOutcome")]
    [Trait("Layer", "Contract")]
    public void SharedPrivateOutcomesAreReadonlyValues(string name)
    {
        Type type = typeof(Qpdf).Assembly.GetType("Lokad.Qpdf.Runtime." + name)
            ?? throw new InvalidOperationException("Missing private value outcome: " + name);
        Assert.True(type.IsValueType);
        Assert.True(type.IsDefined(typeof(IsReadOnlyAttribute)));
        Assert.False(type.IsPublic);
    }

}

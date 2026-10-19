using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Inspection;
using Xunit.Abstractions;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(CompletedJobOwnershipCollection))]
public sealed class TypedResultOwnershipTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly QpdfPath Output = QpdfPath.From("output.pdf");

    private readonly record struct Values(long Live, long Peak, long Decoded, long OutputBytes,
        long Calls, long Handles, long Diagnostics);
    private static Values State(QpdfExecutionCounters c) => new(c.LiveMemoryBytes, c.PeakMemoryBytes,         c.TotalDecodedBytes, c.TotalOutputBytes, c.HostOperations, c.OpenHandles, c.DiagnosticCount);

    private sealed class Alias(ResourceGovernor governor, object? value, WeakReference host, WeakReference result,
        WeakReference counters, Values? snapshot)
    {
        internal readonly ResourceGovernor Governor = governor;
        internal object? Value = value;
        internal readonly WeakReference Host = host;
        internal readonly WeakReference Result = result;
        internal readonly WeakReference Counters = counters;
        internal readonly Values? Snapshot = snapshot;
    }

    public static IEnumerable<object[]> PublicJobs() => TypedCounterOwnershipTests.PublicJobs();
    public static IEnumerable<object[]> BudgetJobs() => TypedCounterOwnershipTests.BudgetJobs();
    public static IEnumerable<object[]> MetadataJobs() => TypedCounterOwnershipTests.MetadataJobs();
    public static IEnumerable<object[]> SuccessAliases()
    {
        foreach (object[] row in MetadataJobs())
        foreach (string kind in new[] { "result", "counters", "copy", "constructed" })
        { yield return [row[0], row[1], kind]; }
    }

    private static object Read(object value, string property) => value.GetType().GetProperty(property, Members)?.GetValue(value)
        ?? throw new InvalidOperationException("Missing original public-job fixture property: " + property);
    private static object? Field(object value, string name) => value.GetType().GetField(name, Members)?.GetValue(value);
    private static async Task<object> Invoke(string method, object?[] arguments)
    {
        Task task = (Task)(typeof(TypedCounterOwnershipTests).GetMethod(method, Members)?.Invoke(null, arguments)
            ?? throw new InvalidOperationException("Missing original public-job fixture method: " + method));
        await task;
        return Read(task, "Result");
    }

    private static ResourceGovernor FindGovernor(Task task) => (ResourceGovernor)(typeof(ExecutionCounterTests)
        .GetMethod("FindGovernor", Members)?.Invoke(null, [task, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)])
        ?? throw new InvalidOperationException("Missing actual public governor."));
    private static void Resume(object host) => host.GetType().GetMethod("Resume", Members)?.Invoke(host, []);

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

    private static async Task<Alias> Complete(string route, bool compressed, string boundary, string outcome, string kind, QpdfLimits limits)
    {
        using CancellationTokenSource cancellation = new();
        object host = await Invoke("Prepare", [route, compressed, boundary, outcome]);
        InMemoryQpdfHost inner = (InMemoryQpdfHost)Read(host, "Inner");
        Assert.True(inner.TryGetOutput(Output, out byte[] prior));
        Task<object> running = Invoke("Start", [host, route, limits, cancellation.Token]);
        try
        {
            await ((Task)Read(host, "Entered")).WaitAsync(TimeSpan.FromSeconds(10));
            ResourceGovernor governor = FindGovernor(running);
            Assert.True((long)(Field(governor, "_preparedTypedResultBytes") ?? 0L) > 0L);
            Assert.NotNull(Field(governor, "_preparedTypedCounters"));
            if (outcome == "cancel") { cancellation.Cancel(); } else { Resume(host); }
            object? result = null;
            if (outcome == "cancel") { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
            else if (outcome == "fault") { await Assert.ThrowsAnyAsync<QpdfException>(() => running); }
            else if (outcome == "budget") { await Assert.ThrowsAsync<QpdfLimitException>(() => running); }
            else { result = await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            Assert.True((long)(Field(host, "ReadBytes") ?? 0L) > 0L);
            Assert.Equal(0L, (long)(Field(governor, "_preparedTypedResultBytes") ?? 0L));
            Assert.Null(Field(governor, "_preparedTypedCounters"));
            Assert.Equal(0L, inner.OpenLeaseCount);
            Assert.Equal(0L, governor.Snapshot().OpenHandles);
            Assert.True(governor.Snapshot().PeakMemoryBytes <= limits.MaxMemoryBytes);
            Assert.Equal(new byte[] { 17, 23, 41 }, prior);
            Assert.True(inner.TryGetOutput(Output, out byte[] current));
            if ((int)(Field(host, "Commits") ?? 0) == 0) { Assert.Equal(prior, current); }
            QpdfExecutionCounters? counters = result is null ? null : (QpdfExecutionCounters)Read(result, "Counters");
            if (counters is not null)
            {
                Values final = State(governor.Snapshot());
                Assert.Equal(final with { Live = counters.LiveMemoryBytes }, State(counters));
            }
            object? value = result is null ? null : kind == "result" ? result : kind == "copy" ? Copy(result)
                : kind == "constructed" ? Construct(result) : counters;
            if (kind == "copy" && result is not null)
            {
                Assert.Equal(result, value);
                Assert.Equal(result.GetHashCode(), value?.GetHashCode());
                Assert.Same(counters, Read(value ?? throw new InvalidOperationException(), "Counters"));
            }
            if (value is not null && value is not QpdfExecutionCounters)
            {
                foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                { Assert.False(property.CanWrite); }
                Assert.Same(counters, Read(value, "Counters"));
            }
            return new(governor, value, new(host), new(result), new(counters), counters is null ? null : State(counters));
        }
        finally
        {
            Resume(host);
            try { await running; } catch (QpdfException) { } catch (OperationCanceledException) { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckSurvivor(Alias alias, string kind)
    {
        object value = alias.Value ?? throw new InvalidOperationException();
        QpdfExecutionCounters counters = value is QpdfExecutionCounters c ? c : (QpdfExecutionCounters)Read(value, "Counters");
        Assert.Equal(alias.Snapshot, State(counters));

    }

    [Theory]
    [MemberData(nameof(PublicJobs))]
    [Trait("Layer", "Contract")]
    public async Task PublicTypedOutcomesPreserveCountersAndTargets(string route, bool compressed, string boundary, string outcome)
    {
        Alias alias = await Complete(route, compressed, boundary, outcome, "counters", QpdfLimits.Default);
        if (outcome == "success") { CheckSurvivor(alias, "counters"); }
    }

    [Theory]
    [MemberData(nameof(SuccessAliases))]
    [Trait("Layer", "Contract")]
    public async Task ResultCounterAndCallerCopiesPreserveImmutableValues(string route, bool compressed, string kind)
    {
        Alias alias = await Complete(route, compressed, "read", "success", kind, QpdfLimits.Default);
        CheckSurvivor(alias, kind);
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

using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class JobEffectiveValidationTests
{
    private sealed class Host(InMemoryQpdfHost storage, IMemoryBudget? budget, CancellationTokenSource? cancellation) : IQpdfHost, IQpdfMemoryHost
    {
        internal int Opened, Created;
        public IMemoryBudget? MemoryBudget => budget;
        public async ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
        {
            Opened++; IQpdfInput lease = await storage.OpenInputAsync(path, token);
            cancellation?.Cancel(); return lease;
        }
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created++; return storage.CreateOutputAsync(path, token); }
    }

    private static string Job(string fields) => "{\"empty\":\"\",\"json\":\"2\"," + fields + "}";

    public static IEnumerable<object[]> ShadowedValues()
    {
        yield return ["root", "\"jsonKey\":{\"password\":\"discarded\"},\"jsonKey\":[\"pages\"]"];
        yield return ["deep", "\"jsonKey\":{\"nested\":{\"password\":\"discarded\"}},\"jsonKey\":[\"pages\"]"];
        yield return ["escaped", "\"jsonKey\":{\"password\":\"discarded\"},\"json\\u004bey\":[\"pages\"]"];
        yield return ["nested", "\"global\":{\"maxStreamFilters\":{\"password\":\"discarded\"},\"maxStreamFilters\":\"0\"},\"jsonKey\":[\"pages\"]"];
        yield return ["credential-value", "\"password\":{\"password\":\"discarded\"},\"password\":\"\",\"jsonKey\":[\"pages\"]"];
        yield return ["large", string.Concat(Enumerable.Repeat("\"jsonKey\":{\"password\":\"discarded\"},", 2048)) + "\"jsonKey\":[\"pages\"]"];
    }

    [Theory]
    [MemberData(nameof(ShadowedValues))]
    public async Task OnlyEffectiveValuesParticipateInSemanticValidation(string name, string fields)
    {
        using InMemoryQpdfHost storage = new(); Host host = new(storage, null, null);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, Job(fields), CancellationToken.None);
        Assert.True(result.ExitCode == QpdfExitCode.Success, name + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); Assert.Empty(result.Diagnostics);
        using JsonDocument report = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        Assert.Empty(report.RootElement.GetProperty("pages").EnumerateArray());
        Assert.Equal(0, host.Opened); Assert.Equal(0, host.Created); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EffectiveIncludeAndSetterOrderWorkThroughFileAdapters(bool nested)
    {
        using InMemoryQpdfHost storage = new(); Host host = new(storage, null, null);
        storage.AddInput(QpdfPath.From("child"), """{"jsonKey":["pages"]}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        string source = Job("\"jobJsonFile\":{\"password\":\"discarded\"},\"jobJsonFile\":\"child\",\"jsonKey\":{\"password\":\"discarded\"},\"jsonKey\":[\"pages\"]");
        storage.AddInput(QpdfPath.From("config"), Encoding.UTF8.GetBytes(source), QpdfInputOwnership.Transfer);
        QpdfExecutionResult result = nested
            ? await Qpdf.ExecuteJobAsync(host, """{"jobJsonFile":"config"}""", CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(["--job-json-file=config"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, result.ExitCode); Assert.Empty(result.Diagnostics);
        using JsonDocument report = JsonDocument.Parse(Assert.IsType<string>(result.OutputText));
        Assert.Empty(report.RootElement.GetProperty("pages").EnumerateArray());
        Assert.Equal(2, host.Opened); Assert.Equal(0, host.Created); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Theory]
    [InlineData("\"jsonKey\":[\"pages\"],\"jsonKey\":{\"password\":\"active\"}", QpdfDiagnosticCode.InvalidArguments)]
    [InlineData("\"jsonKey\":[\"pages\"],\"staticAesIv\":\"\",\"jobJsonFile\":\"unopened\"", QpdfDiagnosticCode.UnsupportedCryptoOperation)]
    [InlineData("\"jsonKey\":\"\\ud800\",\"jsonKey\":[\"pages\"]", QpdfDiagnosticCode.InvalidArguments)]
    public async Task ActiveInvalidSettingsAndMalformedEncodingStillFailBeforeHostAccess(string fields, QpdfDiagnosticCode code)
    {
        using InMemoryQpdfHost storage = new(); Host host = new(storage, null, null);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, Job(fields), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status); Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, host.Opened); Assert.Equal(0, host.Created); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public async Task ConfigurationCancellationRetiresPreparedFieldsAndClosesItsInput()
    {
        using InMemoryQpdfHost storage = new(); using CancellationTokenSource cancellation = new();
        storage.AddInput(QpdfPath.From("child"), "{}"u8.ToArray(), QpdfInputOwnership.Transfer);
        Host host = new(storage, null, cancellation);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
            Job("\"jsonKey\":{\"password\":\"discarded\"},\"jsonKey\":[\"pages\"],\"jobJsonFile\":\"child\""), cancellation.Token);
        Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
        Assert.Equal(1, host.Opened); Assert.Equal(0, host.Created); Assert.Equal(0L, storage.OpenLeaseCount);
    }

    [Fact]
    public async Task SharedRefusalRetiresPreparedFieldsAndPreservesExistingOutput()
    {
        int growths = await Run(new(1048576L), false);
        Assert.True(growths > 0);
        for (int refused = 1; refused <= growths; refused++) { _ = await Run(new(1048576L) { RefuseOnGrowth = refused }, true); }

        static async Task<int> Run(RecordingMemoryBudget budget, bool refuses)
        {
            using (IMemoryScope caller = budget.CreateScope())
            using (InMemoryQpdfHost storage = new())
            {
                await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(QpdfPath.From("prior"), CancellationToken.None))
                { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
                Host host = new(storage, caller, null);
                QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host,
                    Job("\"jsonKey\":{\"password\":\"discarded\"},\"jsonKey\":[\"pages\"]"), CancellationToken.None);
                if (refuses)
                {
                    Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
                    Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
                }
                else { Assert.Equal(QpdfExitCode.Success, result.ExitCode); }
                Assert.Equal(0, host.Opened); Assert.Equal(0, host.Created);
                Assert.True(storage.TryGetOutput(QpdfPath.From("prior"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
                Assert.Equal(0L, storage.OpenLeaseCount);
            }
            Assert.Equal(0L, budget.ReservedBytes); return budget.GrowthCalls;
        }
    }
}

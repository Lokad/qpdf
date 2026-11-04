using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.TestSupport;
using Lokad.Qpdf.Tests.Inspection;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class StructuralBudgetPublicationTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input");
    private static readonly QpdfPath Output = QpdfPath.From("output");

    public static IEnumerable<object[]> RetainedRoots()
    {
        foreach (string role in new[] { "catalog", "info", "metadata", "attachment", "outlines", "names", "structure", "mark", "page" })
        foreach (string shape in new[] { "depth", "array", "dictionary", "references" })
        {
            foreach (string route in new[] { "argv-strict", "argv-repair", "job-strict", "job-repair" })
            { yield return [role, shape, route]; }
            if (role is "info" or "metadata" or "attachment" or "page") { yield return [role, shape, "typed"]; }
        }
    }

    [Theory]
    [MemberData(nameof(RetainedRoots))]
    public async Task StructuralRefusalNeverDropsARetainedRootAndCommits(string role, string shape, string route)
    {
        RecordingMemoryBudget budget = new(8L * 1024 * 1024);
        using IMemoryScope owner = budget.CreateScope();
        using InMemoryQpdfHost storage = new(QpdfLimits.Default, null, owner);
        CountingHost host = new(storage);
        storage.AddInput(Input, Fixture(role, shape), QpdfInputOwnership.Transfer);
        QpdfExecutionResult? control = await Run(host, route, QpdfLimits.Default, CancellationToken.None);
        if (control is not null) { Assert.Equal(QpdfExitCode.Success, control.ExitCode); }
        Assert.True(storage.TryGetOutput(Output, out byte[] original));
        Assert.NotEmpty(original);
        storage.AddInput(QpdfPath.From("control"), original, QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(storage, QpdfPath.From("control"), CancellationToken.None))
        {
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.GetObject(1));
            string key = role switch
            {
                "catalog" => "Audit", "info" => "Info", "metadata" => "Metadata", "attachment" or "names" => "Names",
                "outlines" => "Outlines", "structure" => "StructTreeRoot", "mark" => "MarkInfo", _ => "Resources",
            };
            PdfDictionary container = role == "info" ? document.Engine.Trailer
                : role == "page" ? document.PageReferences[0].Page : catalog;
            Assert.True(container.TryGet(PdfName.FromAscii(key), out _));
        }
        await using (IQpdfOutputTransaction prior = await storage.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        host.Created = 0;
        QpdfLimits limits = shape is "array" or "dictionary"
            ? QpdfLimits.Default with { MaxContainerSize = 16L }
            : QpdfLimits.Default with { MaxNestingDepth = 16L };
        string expected = shape is "array" or "dictionary" ? nameof(QpdfLimits.MaxContainerSize) : nameof(QpdfLimits.MaxNestingDepth);
        if (route == "typed")
        {
            QpdfLimitException error = await Assert.ThrowsAsync<QpdfLimitException>(() => Run(host, route, limits, CancellationToken.None));
            Assert.Equal(expected, error.LimitName); Assert.True(error.Requested > error.Allowed); Assert.Equal(16L, error.Allowed);
        }
        else
        {
            QpdfExecutionResult? result = await Run(host, route, limits, CancellationToken.None);
            Assert.NotNull(result); Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            QpdfDiagnostic error = Assert.Single(result.Diagnostics, x => x.Severity == QpdfDiagnosticSeverity.Error);
            Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, error.Code); Assert.Contains(expected, error.Message, StringComparison.Ordinal);
            Assert.Equal(0L, result.Counters.OpenHandles);
        }
        Assert.Equal(0, host.Created); Assert.Equal(0, storage.OpenLeaseCount);
        Assert.True(storage.TryGetOutput(Output, out byte[] after)); Assert.Equal("prior"u8.ToArray(), after);
        storage.Dispose(); owner.Dispose(); Assert.Equal(0L, budget.ReservedBytes); Assert.Equal(0, budget.LiveReservations);
    }

    private static async Task<QpdfExecutionResult?> Run(IQpdfHost host, string route, QpdfLimits limits, CancellationToken token)
    {
        if (route == "typed")
        { await Qpdf.SelectPageAsync(host, Input, Output, new PdfPageSelectionOptions(1), limits, token); return null; }
        if (route.StartsWith("job", StringComparison.Ordinal))
        {
            string job = JsonSerializer.Serialize(new { inputFile = Input.Value, outputFile = Output.Value, suppressRecovery = route == "job-strict" });
            return await Qpdf.ExecuteJobAsync(host, job, new QpdfExecutionOptions(limits), token);
        }
        string[] argv = route == "argv-strict" ? [Input.Value, "--suppress-recovery", Output.Value] : [Input.Value, Output.Value];
        return await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(argv), new QpdfExecutionOptions(limits), token);
    }

    private static byte[] Fixture(string role, string shape)
    {
        string value = shape switch
        {
            "depth" => new string('[', 32) + "0" + new string(']', 32),
            "array" => "[" + string.Join(' ', Enumerable.Repeat("0", 33)) + "]",
            "dictionary" => "<<" + string.Join(' ', Enumerable.Range(0, 33).Select(i => "/K" + i + " 0")) + ">>",
            _ => "20 0 R",
        };
        string catalog = role switch
        {
            "catalog" => "/Audit 5 0 R", "metadata" => "/Metadata 5 0 R", "attachment" => "/Names <</EmbeddedFiles <</Names [(K) 5 0 R]>>>>",
            "outlines" => "/Outlines 5 0 R", "names" => "/Names <</Dests 5 0 R>>", "structure" => "/StructTreeRoot 5 0 R",
            "mark" => "/MarkInfo 5 0 R", _ => "",
        };
        string body = role switch
        {
            "metadata" => "<</Type /Metadata /Subtype /XML /Length 16 /Audit " + value + ">>",
            "attachment" => "<</Type /Filespec /F (K) /EF <</F 6 0 R>> /Audit " + value + ">>",
            "outlines" => "<</Type /Outlines /Count 0 /Audit " + value + ">>",
            "names" => "<</Names [] /Audit " + value + ">>",
            "structure" => "<</Type /StructTreeRoot /K [] /Audit " + value + ">>",
            "mark" => "<</Marked true /Audit " + value + ">>",
            _ => "<</Title (preserved) /Audit " + value + ">>",
        };
        List<(int Number, string Dict, byte[]? StreamData)> objects =
        [
            (1, "<</Type /Catalog /Pages 2 0 R " + catalog + ">>", null),
            (2, "<</Type /Pages /Count 1 /Kids [3 0 R]>>", null),
            (3, "<</Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources " + (role == "page" ? "<</Audit 5 0 R>>" : "<<>>") + ">>", null),
            (4, "null", null),
            (5, body, role == "metadata" ? "<x>preserved</x>"u8.ToArray() : null),
            (6, "<</Type /EmbeddedFile /Length 10>>", "attachment"u8.ToArray()),
        ];
        if (shape == "references")
        {
            for (int i = 7; i < 20; i++) { objects.Add((i, "null", null)); }
            for (int i = 20; i < 52; i++) { objects.Add((i, "<</Next " + (i == 51 ? "null" : (i + 1) + " 0 R") + ">>", null)); }
        }
        byte[] bytes = CheckTests.Assemble(objects, 1);
        return role == "info" ? Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(bytes).Replace("/Root 1 0 R", "/Root 1 0 R /Info 5 0 R", StringComparison.Ordinal)) : bytes;
    }

    private sealed class CountingHost(InMemoryQpdfHost inner) : IQpdfHost, IQpdfMemoryHost
    {
        internal int Created;
        public IMemoryBudget? MemoryBudget => inner.MemoryBudget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token) => inner.OpenInputAsync(path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
        { Created++; return inner.CreateOutputAsync(path, token); }
    }
}

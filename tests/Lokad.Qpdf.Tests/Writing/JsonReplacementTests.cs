using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class JsonReplacementTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Patch = QpdfPath.From("edit.json"),
        Sidecar = QpdfPath.From("body.bin"), Alias = QpdfPath.From("alternate");
    private static readonly string[] Update = [Input.Value, "--update-from-json=edit.json", "--replace-input"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReplacementClosesInputsBeforeStagingAndPublishesAtTheSourceKey()
    {
        using RecordingJobHost host = new("create", Input, "success", true);
        byte[] prior = await SeedAsync(host);
        Task<QpdfExecutionResult> executing = Run(host, Update, QpdfLimits.Default, CancellationToken.None);
        try
        {
            await host.Entered.WaitAsync(Timeout);
            Assert.Equal(new[] { Input, Patch, Sidecar }, host.OpenedPaths);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            Assert.Equal(Input, Assert.Single(host.CreatedPaths));
            Assert.Empty(host.Committed);
            AssertPrior(host, prior);
            host.Resume();
            QpdfExecutionResult result = await executing.WaitAsync(Timeout);
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(Input, Assert.Single(host.Committed));
            Assert.True(host.Inner.TryGetOutput(Input, out byte[] output));
            QpdfPath published = QpdfPath.From("published");
            host.Inner.AddInput(published, output, QpdfInputOwnership.Transfer);
            await using (QpdfDocument document = await QpdfDocument.OpenAsync(host.Inner, published, CancellationToken.None))
            {
                PdfDictionary page = Assert.Single(document.PageReferences).Page;
                Assert.Equal("edited"u8.ToArray(), Assert.IsType<PdfByteString>(Value(page, "ReviewMarker")).Value);
                PdfArray contents = Assert.IsType<PdfArray>(Value(page, "Contents"));
                Assert.Equal(2, contents.Count);
                Assert.Equal("q Q\n"u8.ToArray(), Decode(document, contents[0]));
                Assert.Equal("q 2 3 4 5 re f Q\n"u8.ToArray(), Decode(document, contents[1]));
            }
            ExecutionJobFixtures.AssertClosed(host, result);
        }
        finally { host.Resume(); await executing; }
    }

    [Theory]
    [InlineData("input-close", false)]
    [InlineData("commit", false)]
    [InlineData("write", true)]
    public async Task ReplacementFaultsLeaveThePriorBytes(string boundary, bool cancel)
    {
        using RecordingJobHost host = new(boundary, Input, cancel ? "success" : "fault", true);
        byte[] prior = await SeedAsync(host);
        using CancellationTokenSource token = new();
        Task<QpdfExecutionResult> executing = Run(host, Update, QpdfLimits.Default, token.Token);
        try
        {
            await host.Entered.WaitAsync(Timeout);
            Assert.Equal(1L, host.Inner.OpenLeaseCount);
            if (boundary == "input-close") { Assert.Empty(host.CreatedPaths); }
            else { Assert.Equal(Input, Assert.Single(host.CreatedPaths)); }
            AssertPrior(host, prior);
            if (cancel) { token.Cancel(); } else { host.Resume(); }
            QpdfExecutionResult result = await executing.WaitAsync(Timeout);
            Assert.Equal(cancel ? QpdfExecutionStatus.Canceled : QpdfExecutionStatus.Failed, result.Status);
            Assert.Empty(host.Committed);
            AssertPrior(host, prior);
            ExecutionJobFixtures.AssertClosed(host, result);
        }
        finally { host.Resume(); await executing; }
    }

    [Theory]
    [InlineData("patch", QpdfDiagnosticCode.InvalidArguments)]
    [InlineData("budget", QpdfDiagnosticCode.BudgetExceeded)]
    public async Task ReplacementRejectionsPreserveThePriorTarget(string failure, QpdfDiagnosticCode diagnostic)
    {
        using RecordingJobHost host = new(null, null, "success", true);
        byte[] prior = await SeedAsync(host);
        if (failure == "patch") { host.Inner.AddInput(Patch, "{"u8.ToArray(), QpdfInputOwnership.Transfer); }
        QpdfLimits limits = failure == "budget" ? QpdfLimits.Default with { MaxOutputBytes = 64 } : QpdfLimits.Default;
        QpdfExecutionResult result = await Run(host, Update, limits, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, entry => entry.Code == diagnostic);
        Assert.Empty(host.Committed);
        if (failure == "patch") { Assert.Empty(host.CreatedPaths); Assert.Equal(new[] { Input, Patch }, host.OpenedPaths); }
        AssertPrior(host, prior);
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    [Theory]
    [InlineData("patch", false)]
    [InlineData("patch", true)]
    [InlineData("sidecar", false)]
    [InlineData("sidecar", true)]
    [InlineData("password-file", false)]
    [InlineData("copy-encryption", false)]
    [InlineData("job", false)]
    [InlineData("response", false)]
    public async Task ReplacementRejectsAuxiliaryInputCollisions(string role, bool alias)
    {
        using RecordingJobHost host = new(null, null, "success", true);
        byte[] prior = await SeedAsync(host);
        QpdfPath auxiliary = alias ? Alias : Input;
        string[] args = role switch
        {
            "patch" => [Input.Value, "--update-from-json=" + auxiliary.Value, "--replace-input"],
            "password-file" or "copy-encryption" => [.. Update, "--" + role + "=" + Input.Value],
            "job" => ["--job-json-file=" + Input.Value],
            "response" => ["@" + Input.Value],
            _ => Update,
        };
        if (role == "sidecar") { AddPatch(host.Inner, auxiliary); }
        if (role == "job")
        {
            host.Inner.AddInput(Input,
                """{"inputFile":"input.pdf","updateFromJson":"edit.json","replaceInput":""}"""u8.ToArray(), QpdfInputOwnership.Transfer);
        }
        if (role == "response") { host.Inner.AddInput(Input, Encoding.UTF8.GetBytes(string.Join('\n', Update) + "\n"), QpdfInputOwnership.Transfer); }

        QpdfExecutionResult result = await Run(alias ? new AliasHost(host) : host, args, QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments &&
            d.Message.Contains("replacement target must differ", StringComparison.Ordinal));
        QpdfPath[] opens = role switch
        {
            "patch" when alias => [Input, Input],
            "sidecar" when alias => [Input, Patch, Input],
            "sidecar" => [Input, Patch],
            "job" or "response" => [Input],
            _ => [],
        };
        Assert.Equal(opens, host.OpenedPaths);
        Assert.Empty(host.CreatedPaths);
        AssertPrior(host, prior);
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    [Fact]
    public async Task SameKeyRequiresExplicitReplacementOnAPdfUpdate()
    {
        using RecordingJobHost host = new(null, null, "success", true);
        byte[] prior = await SeedAsync(host);
        foreach (string[] args in new string[][] {
            [Input.Value, "--update-from-json=edit.json", Input.Value],
            [Patch.Value, "--json-input", "--replace-input"],
            [Input.Value, "--json", "--replace-input"],
            [Input.Value, "--update-from-json=edit.json", Input.Value, "--replace-input"],
        })
        {
            QpdfExecutionResult result = await Run(host, args, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.InvalidArguments);
        }
        await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.UpdatePdfFromJsonAsync(
            host, Input, Patch, Input, QpdfLimits.Default, CancellationToken.None).AsTask());
        Assert.Empty(host.OpenedPaths); Assert.Empty(host.CreatedPaths);
        AssertPrior(host, prior);
        ExecutionJobFixtures.AssertClosed(host, null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JobReplacementAuthenticatesAndPreservesProtection(bool correct)
    {
        using RecordingJobHost host = new(null, null, "success", true);
        byte[] source = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-r6-ascii.pdf"));
        await SeedTargetAsync(host.Inner, source);
        PdfReference content;
        QpdfPassword password = QpdfPassword.FromText("reader");
        await using (QpdfDocument original = await QpdfDocument.OpenAsync(host.Inner, Input, password, QpdfLimits.Default, CancellationToken.None))
        { content = Assert.IsType<PdfReference>(Value(Assert.Single(original.PageReferences).Page, "Contents")); }
        byte[] program = "q 5 6 7 8 re f Q\n"u8.ToArray();
        Dictionary<string, object> edits = new()
        {
            [$"obj:{content.ObjectNumber} {content.Generation} R"] = new { stream = new { dict = new { }, data = Convert.ToBase64String(program) } },
        };
        host.Inner.AddInput(Patch, JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2 }, edits } }), QpdfInputOwnership.Transfer);
        string job = JsonSerializer.Serialize(new { inputFile = Input.Value, updateFromJson = Patch.Value, replaceInput = "", password = correct ? "reader" : "incorrect" });
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(host, job, CancellationToken.None);
        if (correct)
        {
            Assert.Equal(QpdfExitCode.Success, result.ExitCode);
            Assert.Equal(Input, Assert.Single(host.Committed));
            Assert.True(host.Inner.TryGetOutput(Input, out byte[] output));
            QpdfPath published = QpdfPath.From("published");
            host.Inner.AddInput(published, output, QpdfInputOwnership.Transfer);
            await using QpdfDocument document = await QpdfDocument.OpenAsync(host.Inner, published, password, QpdfLimits.Default, CancellationToken.None);
            Assert.Equal(6, document.Encryption?.Revision);
            Assert.Equal(program, Decode(document, Value(Assert.Single(document.PageReferences).Page, "Contents")));
        }
        else
        {
            Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code);
            Assert.Equal(Input, Assert.Single(host.OpenedPaths));
            Assert.Empty(host.CreatedPaths);
            AssertPrior(host, source);
        }
        ExecutionJobFixtures.AssertClosed(host, result);
    }

    private static async Task<byte[]> SeedAsync(RecordingJobHost host)
    {
        byte[] source = ExecutionJobFixtures.DocumentBytes(false, "none");
        await SeedTargetAsync(host.Inner, source);
        host.Inner.AddInput(Sidecar, "q 2 3 4 5 re f Q\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        AddPatch(host.Inner, Sidecar);
        return source;
    }

    private static async Task SeedTargetAsync(InMemoryQpdfHost host, byte[] source)
    {
        host.AddInput(Input, source, QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction target = await host.CreateOutputAsync(Input, CancellationToken.None);
        await target.WriteAsync(source, CancellationToken.None); await target.CommitAsync(CancellationToken.None);
    }

    private static void AddPatch(InMemoryQpdfHost host, QpdfPath data)
        => host.AddInput(Patch, Encoding.UTF8.GetBytes($$$"""
            {"qpdf":[{"jsonversion":2},{
              "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,100,100],"/Resources":{},
                "/Contents":["4 0 R","8 0 R"],"/ReviewMarker":"u:edited"}},
              "obj:8 0 R":{"stream":{"dict":{},"datafile":"{{{data.Value}}}"}}
            }]}
            """), QpdfInputOwnership.Transfer);

    private static Task<QpdfExecutionResult> Run(IQpdfHost host, string[] arguments, QpdfLimits limits, CancellationToken token)
        => Qpdf.ExecuteAsync(host, new(arguments), new QpdfExecutionOptions(limits), token).AsTask();

    private static QpdfObject Value(PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value;
    }

    private static byte[] Decode(QpdfDocument document, QpdfObject reference)
        => StreamObservation.Decode(Assert.IsType<PdfStream>(document.GetObject(Assert.IsType<PdfReference>(reference))));

    private static void AssertPrior(RecordingJobHost host, byte[] prior)
    {
        Assert.True(host.Inner.TryGetOutput(Input, out byte[] saved));
        Assert.Equal(prior, saved);
    }

    private sealed class AliasHost(RecordingJobHost inner) : IQpdfHost, IQpdfMemoryHost
    {
        public IMemoryBudget MemoryBudget => inner.MemoryBudget;
        public ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken token)
            => inner.OpenInputAsync(path == Alias ? Input : path, token);
        public ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken token)
            => inner.CreateOutputAsync(path, token);
    }
}

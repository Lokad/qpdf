using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Foundation;

public sealed class NamedPublicationAliasTests
{
    private static readonly QpdfPath Source = QpdfPath.From("source.pdf");
    private static readonly QpdfPath Target = QpdfPath.From("target.pdf");
    private static readonly QpdfPath Capture = QpdfPath.From("capture.bin");
    private static readonly QpdfPath Split = QpdfPath.From("target-1.pdf");
    private static readonly QpdfPath Patch = QpdfPath.From("patch.json");
    private static readonly QpdfPath Sidecar = QpdfPath.From("content.bin");
    private static readonly QpdfPath Primary = QpdfPath.From("other.pdf");
    private static readonly QpdfPath Settings = QpdfPath.From("options.json");
    private static readonly QpdfPath Password = QpdfPath.From("password.txt");
    private static readonly byte[] Prior = [17, 23, 41];

    [Theory]
    [InlineData("rewrite")]
    [InlineData("selected-source")]
    [InlineData("capture-source")]
    [InlineData("capture-main")]
    [InlineData("capture-part")]
    [InlineData("capture-response")]
    [InlineData("capture-password")]
    [InlineData("raw-object")]
    [InlineData("attachment")]
    [InlineData("update-patch")]
    [InlineData("update-sidecar")]
    public async Task ProtectedStorageAliasesRefuseBeforeStaging(string shape)
    {
        using RecordingJobHost host = await Prepare(null);
        string[] args = shape switch
        {
            "rewrite" => [Source.Value, Target.Value],
            "selected-source" => [Primary.Value, "--pages", Source.Value, "1", "--", Target.Value],
            "capture-source" or "capture-main" => [Source.Value, "--linearize", "--linearize-pass1=" + Capture.Value, Target.Value],
            "capture-part" => [Source.Value, "--linearize", "--split-pages=1", "--linearize-pass1=" + Capture.Value, Target.Value],
            "capture-response" => ["@response.txt", "--linearize", "--linearize-pass1=" + Capture.Value, Target.Value],
            "capture-password" => [Source.Value, "--password-file=" + Password.Value, "--linearize", "--linearize-pass1=" + Capture.Value, Target.Value],
            "raw-object" => [Source.Value, "--show-object=4", "--raw-stream-data", Target.Value],
            "attachment" => [Source.Value, "--show-attachment=note", Target.Value],
            _ => [Source.Value, "--update-from-json=" + Patch.Value, Target.Value],
        };
        (QpdfPath alias, QpdfPath storage) = shape switch
        {
            "capture-source" => (Capture, Source),
            "capture-main" => (Capture, Target),
            "capture-part" => (Capture, Split),
            "capture-response" => (Capture, QpdfPath.From("response.txt")),
            "capture-password" => (Capture, Password),
            "update-patch" => (Target, Patch),
            "update-sidecar" => (Target, Sidecar),
            _ => (Target, Source),
        };

        QpdfExecutionResult result = await Qpdf.ExecuteAsync(new OutputAliasHost(host, alias, storage), new(args), CancellationToken.None);

        string message = shape is "capture-main" or "capture-part" ? "collide" : "aliases an input";
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        QpdfDiagnostic diagnostic = Assert.Single(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments);
        Assert.Contains(alias.Value, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(message, diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(storage, host.OpenedPaths[^1]);
        AssertIntact(host, result);
    }

    [Fact]
    public async Task NestedJobFileIdentityIsProtectedAcrossPlanning()
    {
        using RecordingJobHost host = await Prepare(null);
        QpdfExecutionResult result = await Qpdf.ExecuteJobAsync(new OutputAliasHost(host, Target, Settings),
            """{"inputFile":"source.pdf","outputFile":"target.pdf","jobJsonFile":"options.json"}""", CancellationToken.None);

        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments
            && item.Message.Contains(Target.Value, StringComparison.Ordinal));
        Assert.Equal([Settings, Source, Settings], host.OpenedPaths);
        AssertIntact(host, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedPublicationChecksTheAcquiredIdentity(bool binary)
    {
        using RecordingJobHost host = await Prepare(null);
        IQpdfHost aliases = new OutputAliasHost(host, Target, Source);
        QpdfParseException error = binary
            ? await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.ExtractAttachmentAsync(aliases, Source, "note", Target, CancellationToken.None).AsTask())
            : await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.SelectPageAsync(aliases, Source, Target, PdfPageSelectionOptions.FromPages([1]), CancellationToken.None).AsTask());

        Assert.Contains(Target.Value, error.Message, StringComparison.Ordinal);
        Assert.Equal([Source, Source], host.OpenedPaths);
        AssertIntact(host, null);
    }

    [Fact]
    public async Task ReplacementExemptsOnlyThePrimaryAcquisition()
    {
        using RecordingJobHost host = await Prepare(null);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(new OutputAliasHost(host, Primary, Source), new([
            Source.Value, "--replace-input", "--overlay", Primary.Value, "--"]), CancellationToken.None);

        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.InvalidArguments
            && item.Message.Contains(Source.Value, StringComparison.Ordinal));
        Assert.Equal([Source, Source], host.OpenedPaths);
        AssertIntact(host, result);
    }

    [Fact]
    public async Task CaptureProbeCancellationLeavesBothTargetsIntact()
    {
        using RecordingJobHost host = await Prepare(Capture);
        using CancellationTokenSource cancel = new();
        Task<QpdfExecutionResult> execution = Qpdf.ExecuteAsync(host, new([
            Source.Value, "--linearize", "--linearize-pass1=" + Capture.Value, Target.Value]), cancel.Token).AsTask();
        try
        {
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(host.CreatedPaths);
            Assert.Equal(0L, host.Inner.OpenLeaseCount);
            cancel.Cancel();
            QpdfExecutionResult result = await execution;
            Assert.Equal(QpdfExecutionStatus.Canceled, result.Status);
            AssertIntact(host, result);
        }
        finally
        {
            cancel.Cancel();
            host.Resume();
            await ((Task)execution).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    private static async Task<RecordingJobHost> Prepare(QpdfPath? pause)
    {
        RecordingJobHost host = new(pause.HasValue ? "open" : null, pause, "success", true);
        byte[] pdf = DocumentWithAttachment();
        host.Inner.AddInput(Source, pdf, QpdfInputOwnership.Copy);
        host.Inner.AddInput(Primary, pdf, QpdfInputOwnership.Copy);
        host.Inner.AddInput(Target, pdf, QpdfInputOwnership.Copy);
        host.Inner.AddInput(Split, pdf, QpdfInputOwnership.Copy);
        host.Inner.AddInput(QpdfPath.From("response.txt"), "source.pdf\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        host.Inner.AddInput(Password, "ignored\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        host.Inner.AddInput(Settings, "{}"u8.ToArray(), QpdfInputOwnership.Transfer);
        host.Inner.AddInput(Sidecar, "q Q\n"u8.ToArray(), QpdfInputOwnership.Transfer);
        host.Inner.AddInput(Patch, """
            {"qpdf":[{"jsonversion":2},{"obj:4 0 R":{"stream":{"dict":{},"datafile":"content.bin"}}}]}
            """u8.ToArray(), QpdfInputOwnership.Transfer);
        foreach (QpdfPath path in new[] { Source, Target, Split, Capture })
        {
            await using IQpdfOutputTransaction prior = await host.Inner.CreateOutputAsync(path, CancellationToken.None);
            await prior.WriteAsync(Prior, CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        return host;
    }

    private static byte[] DocumentWithAttachment()
    {
        byte[] content = "q Q\n"u8.ToArray();
        var objects = PdfTestDocument.SinglePage("<< /Length 4 >>", content);
        objects[0] = (1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(note) 5 0 R] >> >> >>", null);
        objects.Add((5, "<< /Type /Filespec /F (note) /EF << /F 6 0 R >> >>", null));
        objects.Add((6, "<< /Type /EmbeddedFile /Length 5 >>", "note\n"u8.ToArray()));
        return PdfTestDocument.Assemble(objects, 1);
    }

    private static void AssertIntact(RecordingJobHost host, QpdfExecutionResult? result)
    {
        Assert.Empty(host.CreatedPaths);
        Assert.Empty(host.Committed);
        foreach (QpdfPath path in new[] { Source, Target, Split, Capture })
        {
            Assert.True(host.Inner.TryGetOutput(path, out byte[] bytes));
            Assert.Equal(Prior, bytes);
        }
        ExecutionJobFixtures.AssertClosed(host, result);
    }
}

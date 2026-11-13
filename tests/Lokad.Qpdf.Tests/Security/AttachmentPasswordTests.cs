using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Security;

public sealed class AttachmentPasswordTests
{
    private static readonly QpdfPath Input = QpdfPath.From("input.pdf"), Source = QpdfPath.From("source.pdf"), Output = QpdfPath.From("output.pdf");
    private static readonly byte[] Payload = "Private attachment payload.\n"u8.ToArray();

    public static IEnumerable<object[]> Cases()
    {
        foreach (int revision in new[] { 4, 6 })
        foreach (string route in new[] { "typed", "argv", "job" })
        foreach (bool protectedPrimary in new[] { false, true }) { yield return [revision, route, protectedPrimary]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AttachmentSourcesAuthenticateWithoutChangingPrimaryProtection(int revision, string route, bool protectedPrimary)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, protectedPrimary ? "security-r6-empty.pdf" : "meta-1page.pdf");
        Add(host, Source, $"security-r{revision}-attachment.pdf");
        string password = revision == 4 ? "café•" : "";
        if (route == "typed")
        {
            QpdfAttachmentEditResult result = await Qpdf.CopyAttachmentsAsync(host, Input, Output,
                [new(Source, "copy-", revision == 6 ? null : QpdfPassword.FromText(password))], CancellationToken.None);
            Assert.Equal(1, result.AttachmentCount);
        }
        else
        {
            QpdfExecutionResult result = route == "job" ? await Qpdf.ExecuteJobAsync(host,
                JsonSerializer.Serialize(new { inputFile = Input.Value, outputFile = Output.Value,
                    copyAttachmentsFrom = new[] { new { file = Source.Value, prefix = "copy-", password } } }), CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--copy-attachments-from", Source.Value, "--password=" + password, "--prefix=copy-", "--"]), CancellationToken.None);
            Assert.True(result.Status == QpdfExecutionStatus.Completed, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        }
        await Verify(host, "", protectedPrimary ? 6 : null);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CopiedAttachmentsFollowExplicitOutputPoliciesAndLinearization(bool job, bool remove)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "security-r6-ascii.pdf"); Add(host, Source, "security-r4-attachment.pdf");
        Dictionary<string, object> fields = new() { ["inputFile"] = Input.Value, ["outputFile"] = Output.Value,
            ["password"] = "reader", ["linearize"] = "", ["objectStreams"] = "generate",
            ["copyAttachmentsFrom"] = new[] { new { file = Source.Value, password = "café•", prefix = "copy-" } } };
        List<string> argv = [Input.Value, Output.Value, "--password=reader", "--linearize", "--object-streams=generate",
            "--copy-attachments-from", Source.Value, "--password=café•", "--prefix=copy-", "--"];
        if (remove) { fields["decrypt"] = ""; argv.Add("--decrypt"); }
        else
        {
            fields["encrypt"] = new Dictionary<string, object> { ["userPassword"] = "new-reader", ["ownerPassword"] = "new-owner", ["256bit"] = new { } };
            argv.AddRange(["--encrypt", "new-reader", "new-owner", "256", "--"]);
        }
        QpdfExecutionResult result = job ? await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(fields), CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new(argv), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status); await Verify(host, remove ? "" : "new-owner", remove ? null : 6);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task RawAndHexOwnerCredentialsUseTheSharedDecoder(string route)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Source, "security-r4-attachment.pdf");
        byte[] owner = Convert.FromHexString("6f776e6572e991");
        if (route == "typed")
        {
            QpdfAttachmentCopy request = new(Source, "copy-", QpdfPassword.FromBytes(owner));
            Assert.DoesNotContain("6f776e", request.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("6f776e", JsonSerializer.Serialize(request), StringComparison.OrdinalIgnoreCase);
            _ = await Qpdf.CopyAttachmentsAsync(host, Input, Output, [request], CancellationToken.None);
        }
        else
        {
            QpdfExecutionResult result = route == "job" ? await Qpdf.ExecuteJobAsync(host,
                """{"inputFile":"input.pdf","outputFile":"output.pdf","passwordMode":"hex-bytes","suppressPasswordRecovery":"","copyAttachmentsFrom":[{"file":"source.pdf","password":"6f776e6572e991","prefix":"copy-"}]}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--password-mode=hex-bytes", "--suppress-password-recovery",
                    "--copy-attachments-from", Source.Value, "--password=6f776e6572e991", "--prefix=copy-", "--"]), CancellationToken.None);
            Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        }
        Assert.Equal(Convert.FromHexString("6f776e6572e991"), owner); await Verify(host, "", null);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("argv")]
    [InlineData("job")]
    public async Task WrongSourcePasswordsPreservePriorOutputAndDoNotLeak(string route)
    {
        using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Source, "security-r4-attachment.pdf");
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(Output, CancellationToken.None))
        { await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None); }
        if (route == "typed")
        {
            QpdfSecurityException failure = await Assert.ThrowsAsync<QpdfSecurityException>(async () => await Qpdf.CopyAttachmentsAsync(host, Input, Output,
                [new(Source, "copy-", QpdfPassword.FromText("private-invalid"))], CancellationToken.None));
            Assert.Equal(QpdfSecurityFailure.InvalidPassword, failure.Reason); Assert.DoesNotContain("private-invalid", failure.Message, StringComparison.Ordinal);
        }
        else
        {
            QpdfExecutionResult result = route == "job" ? await Qpdf.ExecuteJobAsync(host,
                """{"inputFile":"input.pdf","outputFile":"output.pdf","copyAttachmentsFrom":[{"file":"source.pdf","password":"private-invalid"}]}""", CancellationToken.None)
                : await Qpdf.ExecuteAsync(host, new([Input.Value, Output.Value, "--copy-attachments-from", Source.Value, "--password=private-invalid", "--"]), CancellationToken.None);
            Assert.Equal(QpdfDiagnosticCode.InvalidPassword, Assert.Single(result.Diagnostics).Code); Assert.DoesNotContain("private-invalid", result.Diagnostics[0].Message, StringComparison.Ordinal);
        }
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); Assert.Equal("prior"u8.ToArray(), bytes); Assert.Equal(0, host.OpenLeaseCount);
    }

    [Fact]
    public async Task EveryCopyAdmissionRefusalRefundsBeforeEncryptedPublication()
    {
        RecordingMemoryBudget baseline = new(4194304); await Run(baseline, false);
        for (int growth = 1; growth <= baseline.GrowthCalls; growth++) { await Run(new(4194304) { RefuseOnGrowth = growth }, true); }
        async Task Run(RecordingMemoryBudget budget, bool refuses)
        {
            using InMemoryQpdfHost host = new(); Add(host, Input, "meta-1page.pdf"); Add(host, Source, "security-r4-attachment.pdf");
            using IMemoryScope caller = budget.CreateScope();
            QpdfExecutionResult result = await Qpdf.ExecuteAsync(new MemoryBudgetHost(host, caller), new([Input.Value, Output.Value,
                "--copy-attachments-from", Source.Value, "--password=café•", "--prefix=copy-", "--",
                "--encrypt", "new-reader", "new-owner", "256", "--", "--linearize", "--object-streams=generate"]), CancellationToken.None);
            Assert.Equal(refuses ? QpdfExecutionStatus.Failed : QpdfExecutionStatus.Completed, result.Status);
            if (refuses) { Assert.Contains(result.Diagnostics, item => item.Code == QpdfDiagnosticCode.BudgetExceeded); Assert.False(host.TryGetOutput(Output, out _)); }
            Assert.Equal(0, host.OpenLeaseCount); Assert.Equal(0, result.Counters.OpenHandles); caller.Dispose(); Assert.Equal(0, budget.ReservedBytes);
        }
    }

    private static async Task Verify(InMemoryQpdfHost host, string password, int? revision)
    {
        Assert.True(host.TryGetOutput(Output, out byte[] bytes)); QpdfPath copy = QpdfPath.From("copy.pdf"); host.AddInput(copy, bytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, copy, QpdfPassword.FromText(password), QpdfLimits.Default, CancellationToken.None))
        { Assert.Equal(1, document.PageCount); Assert.Equal(revision, document.Encryption?.Revision); }
        QpdfExecutionResult extracted = await Qpdf.ExecuteAsync(host, new([copy.Value, "--password=" + password, "--show-attachment=copy-private.bin"]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, extracted.Status); Assert.NotNull(extracted.OutputBytes); Assert.Equal(Payload, extracted.OutputBytes.Value.ToArray());
        QpdfCheckResult check = await Qpdf.CheckAsync(host, copy, new QpdfCheckOptions { Password = QpdfPassword.FromText(password) }, QpdfLimits.Default, CancellationToken.None);
        Assert.DoesNotContain(check.Checks, item => item.Status == QpdfCheckStatus.Failed); Assert.Equal(0, host.OpenLeaseCount);
    }

    private static void Add(InMemoryQpdfHost host, QpdfPath path, string file)
        => host.AddInput(path, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), QpdfInputOwnership.Copy);
}

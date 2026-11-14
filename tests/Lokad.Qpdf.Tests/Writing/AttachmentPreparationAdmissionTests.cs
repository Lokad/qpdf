using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentPreparationAdmissionTests
{
    private static readonly QpdfPath Output = QpdfPath.From("output");

    private static async Task<InMemoryQpdfHost> Storage(string mode)
    {
        byte[] input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf"));
        byte[]? copied = null;
        if (mode.StartsWith("attachment-copy", StringComparison.Ordinal))
        {
            using InMemoryQpdfHost seed = new();
            seed.AddInput(QpdfPath.From("input"), input, QpdfInputOwnership.Copy);
            seed.AddInput(QpdfPath.From("payload"), [17], QpdfInputOwnership.Transfer);
            await Qpdf.AddAttachmentsAsync(seed, QpdfPath.From("input"), Output,
                Enumerable.Range(0, 4).Select(index => new QpdfAttachmentSpec("payload", "key" + index, "name", null, null, null, null, false)).ToArray(), CancellationToken.None);
            Assert.True(seed.TryGetOutput(Output, out copied));
        }
        InMemoryQpdfHost storage = await PreparationAdmissionChecks.StorageAsync(input, Output);
        if (copied is not null) { storage.AddInput(QpdfPath.From("source"), copied, QpdfInputOwnership.Transfer); }
        else { storage.AddInput(QpdfPath.From("payload"), "abc"u8.ToArray(), QpdfInputOwnership.Transfer); }
        return storage;
    }

    private static ValueTask<QpdfExecutionResult> Run(IQpdfHost host, string mode, CancellationToken token)
    {
        if (mode == "attachment-copy-job")
        { return Qpdf.ExecuteJobAsync(host, """{"inputFile":"input","outputFile":"output","copyAttachmentsFrom":[{"file":"source","prefix":"copy-"}]}""", token); }
        if (mode == "attachment-copy")
        { return Qpdf.ExecuteAsync(host, new(["input", "--copy-attachments-from", "source", "--prefix=copy-", "--", "output"]), token); }
        if (mode == "attachment-job")
        { return Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new { inputFile = "input", outputFile = "output", addAttachment = Enumerable.Range(0, 4)
            .Select(index => new { file = "payload", key = index.ToString("D3"), filename = "Euro \u20ac", description = new string('x', 256) }).ToArray() }), token); }
        if (mode != "attachment") { throw new ArgumentOutOfRangeException(nameof(mode)); }
        List<string> arguments = ["input"];
        for (int index = 0; index < 4; index++)
        { arguments.AddRange(["--add-attachment", "payload", "--key=" + index.ToString("D3"), "--filename=Euro \u20ac", "--description=" + new string('x', 256), "--"]); }
        arguments.Add("output");
        return Qpdf.ExecuteAsync(host, new(arguments), token);
    }

    [Theory]
    [InlineData("attachment")]
    [InlineData("attachment-job")]
    [InlineData("attachment-copy")]
    [InlineData("attachment-copy-job")]
    public Task PreparedAttachmentsRefundOwnersAndRespectPublication(string mode)
        => PreparationAdmissionChecks.VerifyAsync(() => Storage(mode), (host, token) => Run(host, mode, token), Output, 64,
            mode.EndsWith("-job", StringComparison.Ordinal) ? [] : ["write"]);
}

using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class JsonDiagnosticPreviewTests
{
    private static string Key(string kind) => kind switch
    {
        "short" => "UnknownKey",
        "large" => new string('K', 262144),
        "pair" => new string('K', 127) + "\U0001f600" + new string('K', 262144),
        _ => throw new ArgumentException(nameof(kind))
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (string route in new[] { "typed", "argv", "job-key", "job-root", "job-global", "job-pages" })
        foreach (string kind in new[] { "short", "large", "pair" }) { yield return [route, kind]; }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task InvalidKeysUseBoundedUsefulPreviewsBeforePublication(string route, string kind)
    {
        string key = Key(kind);
        using InMemoryQpdfHost host = new();
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfLimits limits = QpdfLimits.Default with { MaxDiagnosticBytes = 16 };
        string message;
        if (route == "typed")
        {
            QpdfParseException error = await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.GetDocumentJsonAsync(host,
                QpdfPath.From("input"), QpdfPath.From("output"), new QpdfJsonOptions(2, [key]), limits, CancellationToken.None).AsTask());
            message = error.Message;
        }
        else
        {
            QpdfExecutionResult result;
            if (route == "argv")
            {
                result = await Qpdf.ExecuteAsync(host, new(["input", "--json=2", "--json-key=" + key, "--", "output"]),
                    new(limits), CancellationToken.None);
            }
            else
            {
                Dictionary<string, object> settings = new() { ["inputFile"] = "input", ["outputFile"] = "output" };
                if (route == "job-key") { settings["json"] = "2"; settings["jsonKey"] = new[] { key }; }
                else if (route == "job-root") { settings[key] = 1; }
                else if (route == "job-global") { settings["global"] = new Dictionary<string, object> { [key] = 1 }; }
                else { settings["pages"] = new[] { new Dictionary<string, object> { ["file"] = "input", ["range"] = "1", [key] = 1 } }; }
                result = await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(settings), new(limits), CancellationToken.None);
            }
            Assert.Equal(QpdfExitCode.Error, result.ExitCode); Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
            QpdfDiagnostic error = Assert.Single(result.Diagnostics, d => d.Severity == QpdfDiagnosticSeverity.Error);
            Assert.Equal(QpdfDiagnosticCode.InvalidArguments, error.Code); message = error.Message;
            Assert.Equal(0L, result.Counters.HostOperations);
        }
        Assert.InRange(message.Length, 1, 256);
        if (route == "argv") { Assert.Contains("--json-key must be one of:", message); }
        else if (kind == "short") { Assert.Contains(key, message); Assert.DoesNotContain("...", message); }
        else { Assert.Contains(new string('K', 127), message); Assert.Contains("...", message); }
        _ = new UTF8Encoding(false, true).GetBytes(message);
        Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] output)); Assert.Equal("prior"u8.ToArray(), output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedInvalidKeyAvoidsFullLengthExceptionCopies(bool pairedBoundary)
    {
        using InMemoryQpdfHost host = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024 };
        async Task<QpdfParseException> Reject(QpdfJsonOptions options)
            => await Assert.ThrowsAsync<QpdfParseException>(() => Qpdf.GetDocumentJsonAsync(host, QpdfPath.From("input"),
                QpdfPath.From("output"), options, limits, CancellationToken.None).AsTask());
        await Reject(new(2, ["UnknownKey"]));
        string key = pairedBoundary ? new string('K', 127) + "\U0001f600" + new string('K', 1048576) : new string('K', 1048576);
        QpdfJsonOptions options = new(2, [key]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        QpdfParseException error = await Reject(options);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(error.Message.Length, 1, 256);
        Assert.InRange(allocated, 1L, 65536L);
        Assert.Equal(0L, host.OpenLeaseCount);
    }
}

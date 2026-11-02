using System.Text;
using Lokad.Qpdf.Hosting;

namespace Lokad.Qpdf.Tests.Commands;

public sealed class OperationBlockScopeTests
{
    private static string[] Block(string kind, string[] nested) =>
        ["--" + kind, kind == "add-attachment" ? "payload" : "input", .. nested, "--"];

    public static IEnumerable<object[]> InvalidScopes()
    {
        foreach (string route in new[] { "argv", "response", "mixed" })
        foreach (string kind in new[] { "add-attachment", "copy-attachments-from", "overlay", "underlay" })
        foreach (string option in new[] { "rotate", "static-id", "global" }) { yield return [route, kind, option]; }
    }

    [Theory]
    [MemberData(nameof(InvalidScopes))]
    public async Task MainOptionsInsideOperationBlocksRejectBeforePdfAccess(string route, string kind, string option)
    {
        using InMemoryQpdfHost host = await Host();
        string[] nested = option switch { "rotate" => ["--rotate=90"], "static-id" => ["--static-id"], _ => ["--global", "--max-stream-filters=5", "--"] };
        string[] block = Block(kind, nested);
        List<string> args = option == "static-id"
            ? ["input", "--pages", ".", "1", "--", .. block, "output"]
            : ["input", .. block, "--pages", ".", "1", "--", "output"];
        QpdfExecutionResult result = await Execute(host, route, args);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        if (route == "argv") { Assert.Equal(0L, result.Counters.HostOperations); }
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] prior));
        Assert.Equal("prior"u8.ToArray(), prior); Assert.Equal(0L, host.OpenLeaseCount);
    }

    public static IEnumerable<object[]> LegalScopes()
    {
        foreach (string route in new[] { "argv", "response", "mixed" })
        foreach (string kind in new[] { "add-attachment", "copy-attachments-from", "overlay", "underlay" })
        { yield return [route, kind]; }
    }

    [Theory]
    [MemberData(nameof(LegalScopes))]
    public async Task MainOptionsOutsideOperationBlocksRemainLive(string route, string kind)
    {
        using InMemoryQpdfHost host = await Host();
        QpdfExecutionResult result = await Execute(host, route,
            ["input", "--rotate=90", "--global", "--max-stream-filters=5", "--",
                .. Block(kind, []), "--pages", ".", "1", "--", "output"]);
        Assert.True(result.ExitCode == QpdfExitCode.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.True(host.TryGetOutput(QpdfPath.From("output"), out byte[] bytes));
        Assert.NotEqual("prior"u8.ToArray(), bytes);
        host.AddInput(QpdfPath.From("reopen"), bytes, QpdfInputOwnership.Copy);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("reopen"), CancellationToken.None))
        { Assert.Equal(1, document.PageCount); }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("add-attachment", "--rotate=90")]
    [InlineData("overlay", "--static-id")]
    public async Task InvalidScopedOptionPrecedesMissingTerminator(string kind, string option)
    {
        using InMemoryQpdfHost host = await Host();
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(host,
            new(["input", "--" + kind, kind == "add-attachment" ? "payload" : "input", option]), CancellationToken.None);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, Assert.Single(result.Diagnostics).Code);
        Assert.DoesNotContain("Missing -- terminator", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Equal(0L, result.Counters.HostOperations); Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static async Task<InMemoryQpdfHost> Host()
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("payload"), "PAYLOAD"u8.ToArray(), QpdfInputOwnership.Copy);
        host.AddInput(QpdfPath.From("settings"), "{}"u8.ToArray(), QpdfInputOwnership.Copy);
        await using IQpdfOutputTransaction prior = await host.CreateOutputAsync(QpdfPath.From("output"), CancellationToken.None);
        await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None); await prior.CommitAsync(CancellationToken.None);
        return host;
    }

    private static ValueTask<QpdfExecutionResult> Execute(InMemoryQpdfHost host, string route, List<string> args)
    {
        if (route == "mixed") { args.Insert(0, "--job-json-file=settings"); }
        if (route == "response")
        {
            host.AddInput(QpdfPath.From("args"), Encoding.UTF8.GetBytes(string.Join('\n', args)), QpdfInputOwnership.Copy);
            args = ["@args"];
        }
        return Qpdf.ExecuteAsync(host, new(args), CancellationToken.None);
    }
}

using System.Text;
using System.Text.Json;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class LinearizationTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static async Task<byte[]> FixtureBytes(string fixture)
    {
        return await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), fixture));
    }

    private static QpdfCheckOutcome LinearizationOutcome(QpdfCheckResult result)
    {
        return Assert.Single(result.Checks, check => check.Name == "linearization");
    }

    public static IEnumerable<object[]> FirstPageCases()
    {
        foreach (string value in new[] { "absent", "null", "0", "/Bad", "1.0", "true" })
        foreach (string operation in new[] { "typed", "check", "check-linearization", "show-linearization" })
        foreach (string route in operation == "typed" ? new[] { "typed" } : new[] { "argv", "job" })
        { yield return [value, operation, route]; }
    }

    private static async Task<byte[]> WithFirstPage(string value)
    {
        byte[] bytes = await FixtureBytes("linearized-2page.pdf");
        if (value == "absent") { return bytes; }
        string end = ">>\nendobj\n";
        string replacement = "/P " + value + " " + end;
        return ReplaceUnique(bytes, end + new string(' ', replacement.Length - end.Length), replacement);
    }

    [Theory]
    [MemberData(nameof(FirstPageCases))]
    public async Task OptionalFirstPageDefaultsAndInvalidTypesAgreeAcrossEntryPoints(string value, string operation, string route)
    {
        bool valid = value is "absent" or "null" or "0";
        using InMemoryQpdfHost host = HostWith("input", await WithFirstPage(value));
        if (route == "typed")
        {
            QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
            Assert.Equal(valid, result.Passed);
            QpdfCheckOutcome outcome = LinearizationOutcome(result);
            Assert.Equal(valid ? QpdfCheckStatus.Passed : QpdfCheckStatus.Failed, outcome.Status);
            if (!valid) { Assert.Contains("/P must be an integer", outcome.Detail, StringComparison.Ordinal); }
        }
        else
        {
            string key = operation switch { "check-linearization" => "checkLinearization", "show-linearization" => "showLinearization", _ => "check" };
            QpdfExecutionResult result = route == "argv"
                ? await Qpdf.ExecuteAsync(host, new(["input", "--" + operation]), CancellationToken.None)
                : await Qpdf.ExecuteJobAsync(host, JsonSerializer.Serialize(new Dictionary<string, string> { ["inputFile"] = "input", [key] = "" }), CancellationToken.None);
            Assert.Equal(valid ? QpdfExitCode.Success : QpdfExitCode.Error, result.ExitCode);
            Assert.Equal(valid ? QpdfExecutionStatus.Completed : QpdfExecutionStatus.Failed, result.Status);
            if (!valid) { Assert.Contains(result.Diagnostics, d => d.Message.Contains("/P must be an integer", StringComparison.Ordinal)); }
            else if (operation == "show-linearization") { Assert.Contains(result.Diagnostics, d => d.Message == "first_page: 0"); }
            Assert.Null(result.OutputBytes);
        }
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    public async Task DefaultFirstPageDoesNotBypassStructuralValidation(string value)
    {
        byte[] bytes = ReplaceUnique(await WithFirstPage(value), "/N 2", "/N 3");
        using InMemoryQpdfHost host = HostWith("input", bytes);
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("input"), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Equal(QpdfCheckStatus.Failed, LinearizationOutcome(result).Status);
        Assert.Contains("/N", LinearizationOutcome(result).Detail, StringComparison.Ordinal);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    private static byte[] ReplaceUnique(byte[] input, string needle, string replacement)
    {
        byte[] needleBytes = Encoding.ASCII.GetBytes(needle);
        byte[] replacementBytes = Encoding.ASCII.GetBytes(replacement);
        Assert.Equal(needleBytes.Length, replacementBytes.Length);
        int at = IndexOf(input, needleBytes);
        Assert.NotEqual(-1, at);
        Assert.Equal(-1, IndexOf(input, needleBytes, at + 1));
        byte[] output = new byte[input.Length];
        Buffer.BlockCopy(input, 0, output, 0, input.Length);
        Buffer.BlockCopy(replacementBytes, 0, output, at, replacementBytes.Length);
        return output;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
    {
        for (int i = start; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int k = 0; k < needle.Length; k++)
            {
                if (haystack[i + k] != needle[k])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    [Theory]
    [InlineData("linearized-1page.pdf", 1)]
    [InlineData("linearized-2page.pdf", 2)]
    public async Task ValidLinearizedFilesPass(string fixture, int pages)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes(fixture));
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal(pages, result.PageCount);
        QpdfCheckOutcome linearization = LinearizationOutcome(result);
        Assert.Equal(QpdfCheckStatus.Passed, linearization.Status);
        Assert.Contains("hint tables valid", linearization.Detail, StringComparison.Ordinal);
        if (pages == 2)
        {
            Assert.Contains("3 shared groups", linearization.Detail, StringComparison.Ordinal);
        }
        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("lin-objstm-1page.pdf", 1)]
    [InlineData("lin-objstm-2page.pdf", 2)]
    public async Task StreamedLinearizedFilesPass(string fixture, int pages)
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes(fixture));
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal(pages, result.PageCount);
        QpdfCheckOutcome linearization = LinearizationOutcome(result);
        Assert.Equal(QpdfCheckStatus.Passed, linearization.Status);
        Assert.Contains("hint tables valid", linearization.Detail, StringComparison.Ordinal);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task OrdinaryFilePassesAsNotLinearized()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("minimal-2page.pdf"));
        QpdfCheckResult result = await Qpdf.CheckAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        QpdfCheckOutcome linearization = LinearizationOutcome(result);
        Assert.Equal(QpdfCheckStatus.Passed, linearization.Status);
        Assert.Contains("not linearized", linearization.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectHintDecodingChargesOnceAndEnforcesAggregateAdmission()
    {
        InMemoryQpdfHost host = HostWith("inputs/a.pdf", await FixtureBytes("linearized-2page.pdf"));
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using LinearizationDataOwner owner = Assert.IsType<LinearizationDataOwner>(LinearizationCheck.TryReadOwnedData(
            document.Engine, document.PageCount, QpdfLimits.Default, governor, CancellationToken.None));
        LinearizationData data = owner.Data;
        PdfStream hint = Assert.IsType<PdfStream>(document.Engine.Load(data.HintObject));
        byte[] expected = FlateFilter.Decode(hint.Data, null, QpdfLimits.Default,
            new ResourceGovernor(QpdfLimits.Default, CancellationToken.None));
        Assert.True(expected.Length > 1);
        Assert.Equal(expected.Length, governor.Snapshot().TotalDecodedBytes);
        QpdfLimits limits = QpdfLimits.Default with { MaxDecodedBytes = expected.Length };
        ResourceGovernor exhausted = new(limits, CancellationToken.None);
        exhausted.ChargeDecodedBytes(1L);
        QpdfLimitException error = Assert.Throws<QpdfLimitException>(() => LinearizationCheck.TryReadOwnedData(
            document.Engine, document.PageCount, limits, exhausted, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxDecodedBytes), error.LimitName);
        Assert.Equal(1L, exhausted.Snapshot().TotalDecodedBytes);
    }

    [Fact]
    public async Task PageCountMismatchFails()
    {
        byte[] bytes = ReplaceUnique(await FixtureBytes("linearized-2page.pdf"), "/N 2", "/N 3");
        QpdfCheckResult result = await Qpdf.CheckAsync(HostWith("inputs/a.pdf", bytes), QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        QpdfCheckOutcome linearization = LinearizationOutcome(result);
        Assert.Equal(QpdfCheckStatus.Failed, linearization.Status);
        Assert.Contains("/N", linearization.Detail, StringComparison.Ordinal);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task HintLengthBeyondFileFails()
    {
        byte[] bytes = ReplaceUnique(await FixtureBytes("linearized-2page.pdf"), "/H [ 550 127 ]", "/H [ 550 999 ]");
        QpdfCheckResult result = await Qpdf.CheckAsync(HostWith("inputs/a.pdf", bytes), QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Failed, LinearizationOutcome(result).Status);
    }

    [Fact]
    public async Task HintCountMismatchFails()
    {
        byte[] bytes = ReplaceUnique(await FixtureBytes("linearized-2page.pdf"), "[ 550 127 ]", "[ 550     ]");
        QpdfCheckResult result = await Qpdf.CheckAsync(HostWith("inputs/a.pdf", bytes), QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Failed, LinearizationOutcome(result).Status);
    }

    [Fact]
    public async Task UnknownFirstPageObjectFails()
    {
        byte[] bytes = ReplaceUnique(await FixtureBytes("linearized-2page.pdf"), "/O 7", "/O 0");
        QpdfCheckResult result = await Qpdf.CheckAsync(HostWith("inputs/a.pdf", bytes), QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Failed, LinearizationOutcome(result).Status);
    }

    [Fact]
    public async Task MissingSharedOffsetFails()
    {
        byte[] bytes = ReplaceUnique(await FixtureBytes("linearized-2page.pdf"), "/S 43", "     ");
        QpdfCheckResult result = await Qpdf.CheckAsync(HostWith("inputs/a.pdf", bytes), QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Failed, LinearizationOutcome(result).Status);
    }

    [Fact]
    public async Task CorruptHintEncodingFails()
    {
        byte[] bytes = await FixtureBytes("linearized-2page.pdf");
        byte[] marker = Encoding.ASCII.GetBytes("/S 43");
        int dictAt = IndexOf(bytes, marker);
        Assert.NotEqual(-1, dictAt);
        byte[] streamMarker = Encoding.ASCII.GetBytes("stream\n");
        int dataAt = IndexOf(bytes, streamMarker, dictAt) + streamMarker.Length + 10;
        Assert.True(dataAt < bytes.Length);
        byte[] edited = new byte[bytes.Length];
        Buffer.BlockCopy(bytes, 0, edited, 0, bytes.Length);
        edited[dataAt] ^= 0xFF;
        QpdfCheckResult result = await Qpdf.CheckAsync(HostWith("inputs/a.pdf", edited), QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Failed, LinearizationOutcome(result).Status);
    }

    [Fact]
    public async Task DeclaredRawGarbageFailsFast()
    {
        byte[] bytes = ReplaceUnique(await FixtureBytes("linearized-2page.pdf"), "/Filter /FlateDecode /S 43", "                     /S 43");
        QpdfCheckResult result = await Qpdf.CheckAsync(HostWith("inputs/a.pdf", bytes), QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        Assert.Equal(QpdfCheckStatus.Failed, LinearizationOutcome(result).Status);
    }

    [Fact]
    public async Task DisabledFlagReadsAsNotLinearized()
    {
        byte[] bytes = ReplaceUnique(await FixtureBytes("linearized-2page.pdf"), "/Linearized 1", "/Linearized 2");
        QpdfCheckResult result = await Qpdf.CheckAsync(HostWith("inputs/a.pdf", bytes), QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        QpdfCheckOutcome linearization = LinearizationOutcome(result);
        Assert.Equal(QpdfCheckStatus.Passed, linearization.Status);
        Assert.Contains("not linearized", linearization.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongFileSizeReadsAsNotLinearized()
    {
        byte[] bytes = ReplaceUnique(await FixtureBytes("linearized-2page.pdf"), "/L 1501", "/L 1500");
        QpdfCheckResult result = await Qpdf.CheckAsync(HostWith("inputs/a.pdf", bytes), QpdfPath.From("inputs/a.pdf"), CancellationToken.None);
        QpdfCheckOutcome linearization = LinearizationOutcome(result);
        Assert.Equal(QpdfCheckStatus.Passed, linearization.Status);
        Assert.Contains("not linearized", linearization.Detail, StringComparison.Ordinal);
    }
}

using System.Text;
using Lokad.Qpdf.Hosting;

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

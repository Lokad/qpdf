using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class RecoveryTests
{
    private static string FixtureDir()
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    private static async Task<byte[]> FixtureBytes(string file)
    {
        return await File.ReadAllBytesAsync(Path.Combine(FixtureDir(), file));
    }

    private static InMemoryQpdfHost HostWith(string name, byte[] bytes)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From(name), bytes, QpdfInputOwnership.Copy);
        return host;
    }

    private static int FindMarker(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
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

    private static byte[] WithBrokenStartXref(byte[] bytes)
    {
        byte[] copy = (byte[])bytes.Clone();
        byte[] marker = Encoding.ASCII.GetBytes("startxref\n");
        int at = FindMarker(copy, marker);
        if (at < 0)
        {
            throw new Xunit.Sdk.XunitException("Fixture has no startxref.");
        }

        int digits = at + marker.Length;
        int end = digits;
        while (end < copy.Length && copy[end] >= (byte)'0' && copy[end] <= (byte)'9')
        {
            end += 1;
        }

        if (end == digits)
        {
            throw new Xunit.Sdk.XunitException("Fixture startxref has no offset.");
        }

        for (int i = digits; i < end; i++)
        {
            copy[i] = (byte)'9';
        }

        return copy;
    }

    private static byte[] WithBlankedXrefTable(byte[] bytes)
    {
        byte[] copy = (byte[])bytes.Clone();
        byte[] start = Encoding.ASCII.GetBytes("xref\n");
        int tableAt = FindMarker(copy, start);
        byte[] trailer = Encoding.ASCII.GetBytes("trailer\n");
        int trailerAt = FindMarker(copy, trailer);
        if (tableAt < 0 || trailerAt < 0 || trailerAt <= tableAt)
        {
            throw new Xunit.Sdk.XunitException("Fixture layout is unexpected.");
        }

        for (int i = tableAt; i < trailerAt; i++)
        {
            copy[i] = (byte)' ';
        }

        return copy;
    }

    private static async Task<byte[]> SelectSingle(InMemoryQpdfHost host, string input, string output)
    {
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host, new QpdfCommandInvocation([input, "--pages", ".", "1", "--", output]), CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == QpdfDiagnosticCode.RecoveryEngaged);
        Assert.True(host.TryGetOutput(QpdfPath.From(output), out byte[] published));
        return published;
    }

    [Fact]
    public async Task StrictFailsWhileRepairSucceeds()
    {
        byte[] broken = WithBrokenStartXref(await FixtureBytes("minimal-1page.pdf"));
        InMemoryQpdfHost strictHost = HostWith("inputs/broken.pdf", broken);
        await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(
            strictHost, QpdfPath.From("inputs/broken.pdf"), CancellationToken.None).AsTask());
        Assert.Equal(0L, strictHost.OpenLeaseCount);

        InMemoryQpdfHost host = HostWith("inputs/broken.pdf", broken);
        byte[] published = await SelectSingle(host, "inputs/broken.pdf", "outputs/fixed.pdf");
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(1, output.PageCount);
        PdfStream contents = Assert.IsType<PdfStream>(output.GetObject(4));
        Assert.Contains("Hello", Encoding.ASCII.GetString(contents.Data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuppressRecoveryFailsLikeStrict()
    {
        byte[] broken = WithBrokenStartXref(await FixtureBytes("minimal-1page.pdf"));
        InMemoryQpdfHost host = HostWith("inputs/broken.pdf", broken);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/broken.pdf", "--suppress-recovery", "--pages", ".", "1", "--", "outputs/x.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task RepairedOutputMatchesStrictOutput()
    {
        byte[] intact = await FixtureBytes("minimal-1page.pdf");
        InMemoryQpdfHost strictHost = HostWith("inputs/intact.pdf", intact);
        QpdfExecutionResult strict = await Qpdf.ExecuteAsync(
            strictHost,
            new QpdfCommandInvocation(["inputs/intact.pdf", "--suppress-recovery", "--pages", ".", "1", "--", "outputs/strict.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExitCode.Success, strict.ExitCode);
        Assert.True(strictHost.TryGetOutput(QpdfPath.From("outputs/strict.pdf"), out byte[] expected));

        byte[] blanked = WithBlankedXrefTable(intact);
        InMemoryQpdfHost brokenHost = HostWith("inputs/broken.pdf", blanked);
        await Assert.ThrowsAsync<QpdfParseException>(() => QpdfDocument.OpenAsync(
            brokenHost, QpdfPath.From("inputs/broken.pdf"), CancellationToken.None).AsTask());
        byte[] repaired = await SelectSingle(brokenHost, "inputs/broken.pdf", "outputs/repaired.pdf");
        Assert.Equal(expected, repaired);
    }

    [Fact]
    public async Task StaleCatalogNeverWinsOverActiveTrailer()
    {
        byte[] intact = await FixtureBytes("minimal-1page.pdf");
        byte[] staleCatalog = Encoding.ASCII.GetBytes("\n98 0 obj\n<< /Type /Catalog /Pages 99 0 R >>\nendobj\n99 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        byte[] broken = WithBrokenStartXref(intact);
        byte[] combined = new byte[broken.Length + staleCatalog.Length];
        Buffer.BlockCopy(broken, 0, combined, 0, broken.Length);
        Buffer.BlockCopy(staleCatalog, 0, combined, broken.Length, staleCatalog.Length);
        InMemoryQpdfHost host = HostWith("inputs/stale.pdf", combined);
        byte[] published = await SelectSingle(host, "inputs/stale.pdf", "outputs/fixed.pdf");
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(1, output.PageCount);
    }

    [Fact]
    public async Task DuplicateHeadersResolveLastWins()
    {
        byte[] intact = await FixtureBytes("minimal-1page.pdf");
        byte[] update = Encoding.ASCII.GetBytes("\n3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        byte[] broken = WithBrokenStartXref(intact);
        byte[] combined = new byte[broken.Length + update.Length];
        Buffer.BlockCopy(broken, 0, combined, 0, broken.Length);
        Buffer.BlockCopy(update, 0, combined, broken.Length, update.Length);
        InMemoryQpdfHost host = HostWith("inputs/dup.pdf", combined);
        byte[] published = await SelectSingle(host, "inputs/dup.pdf", "outputs/fixed.pdf");
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        PdfDictionary? page = null;
        foreach (int number in new[] { 3, 4, 5, 6 })
        {
            try
            {
                if (output.GetObject(number) is PdfDictionary candidate
                    && candidate.TryGet(new PdfName(Encoding.ASCII.GetBytes("Type")), out QpdfObject type)
                    && type is PdfName name && name.DisplayText == "Page")
                {
                    page = candidate;
                }
            }
            catch (QpdfParseException)
            {
            }
        }

        Assert.NotNull(page);
        Assert.True(page.TryGet(new PdfName(Encoding.ASCII.GetBytes("MediaBox")), out QpdfObject boxRaw));
        PdfArray box = Assert.IsType<PdfArray>(boxRaw);
        Assert.Equal(100L, Assert.IsType<PdfInteger>(box[2]).Value);
    }

    [Fact]
    public async Task BogusStormStaysBounded()
    {
        byte[] intact = await FixtureBytes("minimal-1page.pdf");
        StringBuilder storm = new();
        for (int i = 0; i < 3000; i++)
        {
            storm.Append("9 0 obj\n<< /Dup ");
            storm.Append(i.ToString());
            storm.Append(" >>\nendobj\n");
        }

        byte[] noise = Encoding.ASCII.GetBytes(storm.ToString());
        byte[] broken = WithBrokenStartXref(intact);
        byte[] combined = new byte[broken.Length + noise.Length];
        Buffer.BlockCopy(broken, 0, combined, 0, broken.Length);
        Buffer.BlockCopy(noise, 0, combined, broken.Length, noise.Length);
        InMemoryQpdfHost host = HostWith("inputs/storm.pdf", combined);
        byte[] published = await SelectSingle(host, "inputs/storm.pdf", "outputs/fixed.pdf");
        host.AddInput(QpdfPath.From("inputs/reread.pdf"), published, QpdfInputOwnership.Copy);
        await using QpdfDocument output = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/reread.pdf"), CancellationToken.None);
        Assert.Equal(1, output.PageCount);
    }

    [Fact]
    public async Task TinyBudgetFailsPredictably()
    {
        byte[] intact = await FixtureBytes("minimal-1page.pdf");
        StringBuilder storm = new();
        for (int i = 0; i < 500; i++)
        {
            storm.Append("9 0 obj\n<< /Dup ");
            storm.Append(i.ToString());
            storm.Append(" >>\nendobj\n");
        }

        byte[] noise = Encoding.ASCII.GetBytes(storm.ToString());
        byte[] broken = WithBrokenStartXref(intact);
        byte[] combined = new byte[broken.Length + noise.Length];
        Buffer.BlockCopy(broken, 0, combined, 0, broken.Length);
        Buffer.BlockCopy(noise, 0, combined, broken.Length, noise.Length);
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 50L };
        InMemoryQpdfHost host = HostWith("inputs/storm.pdf", combined);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/storm.pdf", "--pages", ".", "1", "--", "outputs/x.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task SuppressRecoveryFailsWithoutOutputUnderBudget()
    {
        byte[] broken = WithBrokenStartXref(await FixtureBytes("minimal-1page.pdf"));
        InMemoryQpdfHost host = HostWith("inputs/broken.pdf", broken);
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/broken.pdf", "--suppress-recovery", "--pages", ".", "1", "--", "outputs/x.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task RecoveredOutputYieldsToExhaustedBudget()
    {
        byte[] broken = WithBrokenStartXref(await FixtureBytes("minimal-1page.pdf"));
        InMemoryQpdfHost host = HostWith("inputs/broken.pdf", broken);
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 10L };
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/broken.pdf", "--pages", ".", "1", "--", "outputs/x.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfDiagnosticCode.BudgetExceeded, Assert.Single(result.Diagnostics).Code);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
    }
    private static byte[] WithErrorStorm(byte[] bytes, int count)
    {
        StringBuilder storm = new();
        for (int i = 0; i < count; i++)
        {
            // Cyclic single-digit numbers: each malformed header fails its own
            // trial parse exactly once (multi-digit headers would rescan trailing
            // digits as further candidates).
            storm.Append(6 + (i % 4));
            storm.Append(" 0 obj" + (char)10 + "<< /Broken ");
            storm.Append(i.ToString());
            storm.Append((char)10);
        }

        byte[] noise = Encoding.ASCII.GetBytes(storm.ToString());
        byte[] broken = WithBrokenStartXref(bytes);
        byte[] combined = new byte[broken.Length + noise.Length];
        Buffer.BlockCopy(broken, 0, combined, 0, broken.Length);
        Buffer.BlockCopy(noise, 0, combined, broken.Length, noise.Length);
        return combined;
    }

    [Fact]
    public async Task ErrorsBudgetBoundsRepair()
    {
        byte[] storm = WithErrorStorm(await FixtureBytes("minimal-1page.pdf"), 3);
        InMemoryQpdfHost strict = HostWith("inputs/broken.pdf", storm);
        byte[] repaired = await SelectSingle(strict, "inputs/broken.pdf", "outputs/fixed.pdf");
        Assert.True(repaired.Length > 0);
        InMemoryQpdfHost capped = HostWith("inputs/broken.pdf", storm);
        QpdfExecutionResult denied = await Qpdf.ExecuteAsync(
            capped,
            new QpdfCommandInvocation(["inputs/broken.pdf", "--parser-max-errors=2", "--pages", ".", "1", "--", "outputs/fixed.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, denied.Status);
        QpdfDiagnostic deniedError = Assert.Single(denied.Diagnostics);
        Assert.Equal(QpdfDiagnosticCode.InvalidArguments, deniedError.Code);
        Assert.Contains("maximum of 2 parsable errors", deniedError.Message, StringComparison.Ordinal);
        Assert.False(capped.TryGetOutput(QpdfPath.From("outputs/fixed.pdf"), out byte[] _));
        InMemoryQpdfHost exact = HostWith("inputs/broken.pdf", storm);
        QpdfExecutionResult allowed = await Qpdf.ExecuteAsync(
            exact,
            new QpdfCommandInvocation(["inputs/broken.pdf", "--parser-max-errors=3", "--pages", ".", "1", "--", "outputs/fixed.pdf"]),
            CancellationToken.None);
        Assert.True(allowed.ExitCode == QpdfExitCode.Warning, string.Join(" | ", allowed.Diagnostics.Select(d => d.Code.ToString() + ":" + d.Message)));
        Assert.True(exact.TryGetOutput(QpdfPath.From("outputs/fixed.pdf"), out byte[] _));
    }

    [Fact]
    public async Task ErrorsDefaultFailsLargeStorm()
    {
        byte[] storm = WithErrorStorm(await FixtureBytes("minimal-1page.pdf"), 20);
        InMemoryQpdfHost host = HostWith("inputs/broken.pdf", storm);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/broken.pdf", "--pages", ".", "1", "--", "outputs/fixed.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/fixed.pdf"), out byte[] _));
    }

    [Fact]
    public async Task ErrorsBudgetJobParityHolds()
    {
        byte[] storm = WithErrorStorm(await FixtureBytes("minimal-1page.pdf"), 3);
        InMemoryQpdfHost argvHost = HostWith("inputs/broken.pdf", storm);
        QpdfExecutionResult argvResult = await Qpdf.ExecuteAsync(argvHost, new QpdfCommandInvocation(["inputs/broken.pdf", "--parser-max-errors=5", "--pages", ".", "1", "--", "outputs/a.pdf"]), CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, argvResult.ExitCode);
        Assert.True(argvHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] a));
        InMemoryQpdfHost jobHost = HostWith("inputs/broken.pdf", storm);
        QpdfExecutionResult jobResult = await Qpdf.ExecuteJobAsync(jobHost, "{\"inputFile\": \"inputs/broken.pdf\", \"outputFile\": \"outputs/a.pdf\", \"pages\": [{\"file\": \".\", \"range\": \"1\"}], \"global\": {\"parserMaxErrors\": 5}}", CancellationToken.None);
        Assert.Equal(QpdfExitCode.Warning, jobResult.ExitCode);
        Assert.True(jobHost.TryGetOutput(QpdfPath.From("outputs/a.pdf"), out byte[] b));
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task ErrorsBudgetStrictUnaffected()
    {
        byte[] storm = WithErrorStorm(await FixtureBytes("minimal-1page.pdf"), 3);
        InMemoryQpdfHost host = HostWith("inputs/broken.pdf", storm);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/broken.pdf", "--suppress-recovery", "--parser-max-errors=5", "--pages", ".", "1", "--", "outputs/x.pdf"]),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/x.pdf"), out byte[] _));
    }

    [Fact]
    public async Task ErrorsBudgetTightHostStillBounds()
    {
        byte[] storm = WithErrorStorm(await FixtureBytes("minimal-1page.pdf"), 3);
        QpdfLimits limits = QpdfLimits.Default with { MaxParserErrors = 1L };
        InMemoryQpdfHost host = HostWith("inputs/broken.pdf", storm);
        QpdfExecutionResult result = await Qpdf.ExecuteAsync(
            host,
            new QpdfCommandInvocation(["inputs/broken.pdf", "--pages", ".", "1", "--", "outputs/fixed.pdf"]),
            new QpdfExecutionOptions(limits),
            CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.False(host.TryGetOutput(QpdfPath.From("outputs/fixed.pdf"), out byte[] _));
    }
}

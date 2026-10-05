using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class RecoveryOwnershipTests
{
    private static string Literal(int length) => "(" + new string('A', length) + ")";

    private static QpdfObject Member(PdfDictionary dictionary, string key)
    {
        Assert.True(dictionary.TryGet(PdfName.FromAscii(key), out QpdfObject value));
        return value;
    }

    private static string NullControl(string body) => "null %" + new string('A', body.Length - 7) + "\n";

    private static byte[] Input(string[] bodies, string trailer)
    {
        string source = Encoding.ASCII.GetString(TolerantObjectLoadingTests.Pdf(bodies, true));
        int start = source.LastIndexOf("trailer\n", StringComparison.Ordinal) + "trailer\n".Length;
        int end = source.IndexOf("\nstartxref", start, StringComparison.Ordinal);
        return Encoding.ASCII.GetBytes(source[..start] + trailer + source[end..]);
    }

    private static XRefTable Recover(byte[] input, ResourceGovernor governor, DiagnosticBag diagnostics, QpdfLimits limits, bool strictFirst)
        => strictFirst ? XRefTable.Parse(input, limits, governor, diagnostics, QpdfRecoveryMode.BoundedRepair)
            : XRefRecovery.Recover(input, limits, governor, diagnostics, "authored damage");

    public static IEnumerable<object[]> TrialValues()
    {
        string large = Literal(4096);
        foreach (string body in new[] { large, "<" + string.Concat(Enumerable.Repeat("41", 4096)) + ">", "/" + new string('A', 4096),
            new string('0', 4096) + "7", "[ " + large + " /Name 7 ]", "<< /A " + large + " /B [ /Name 7 ] >>",
            "<< /Length 4096 >>\nstream\n" + new string('A', 4096) + "\nendstream" })
        foreach (bool strictFirst in new[] { false, true }) { yield return [body, strictFirst]; }
    }

    [Theory]
    [MemberData(nameof(TrialValues))]
    public void SuccessfulCandidatesRetainOnlyReconstructedMetadata(string body, bool strictFirst)
    {
        byte[] input = Input(Enumerable.Repeat(body, 50).ToArray(), "<< /Size 55 /Root 1 0 R >>");
        byte[] control = Input(Enumerable.Repeat(NullControl(body), 50).ToArray(), "<< /Size 55 /Root 1 0 R >>");
        Assert.Equal(input.Length, control.Length);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        ResourceGovernor controlGovernor = new(limits, CancellationToken.None);
        XRefTable table = Recover(input, governor, new DiagnosticBag(governor), limits, strictFirst);
        XRefTable expected = Recover(control, controlGovernor, new DiagnosticBag(controlGovernor), limits, strictFirst);
        Assert.Equal(54, table.Entries.Count);
        Assert.Equal(expected.Entries.Count, table.Entries.Count);
        Assert.Equal(1, Assert.IsType<PdfReference>(Member(table.Trailer, "Root")).ObjectNumber);
        Assert.Equal(controlGovernor.Snapshot().LiveMemoryBytes, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork > 0L);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PublicRecoveryFitsFiniteMemoryWithArgvJobParity(bool job, bool catalogs)
    {
        string body = catalogs ? "<< /Type /Catalog /Pages 2 0 R /Unused " + Literal(4096) + " >>" : Literal(4096);
        byte[] input = Input(Enumerable.Repeat(body, 50).ToArray(), "<< /Size 55 /Root 1 0 R >>");
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxMemoryBytes = input.Length + 60000L });
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","showNpages":""}""", options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages"]), options, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(QpdfExitCode.Warning, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.RecoveryEngaged && d.Message.Contains("active trailer", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Message == "inputs/a.pdf: 1");
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Theory]
    [InlineData("<< /Size 5 /Root 1 0 R /Unused VALUE >>")]
    [InlineData("<< /Size 5 /Root 99 0 R /Unused VALUE >>")]
    [InlineData("<< /Size 5 /Root VALUE >>")]
    [InlineData("<< /Size 0 /Root 1 0 R /Unused VALUE >>")]
    [InlineData("<< /Size /Wrong /Root 1 0 R /Unused VALUE >>")]
    [InlineData("<< /Size 5 /Unused VALUE >>")]
    [InlineData("[ VALUE ]")]
    [InlineData("VALUE")]
    public void TailDecisionsReleaseDiscardedSubtrees(string template)
    {
        string large = Literal(4096);
        byte[] input = Input([], template.Replace("VALUE", large, StringComparison.Ordinal));
        byte[] control = Input([], template.Replace("VALUE", NullControl(large), StringComparison.Ordinal));
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        ResourceGovernor controlGovernor = new(limits, CancellationToken.None);
        XRefTable table = XRefRecovery.Recover(input, limits, governor, new DiagnosticBag(governor), "authored damage");
        XRefTable expected = XRefRecovery.Recover(control, limits, controlGovernor, new DiagnosticBag(controlGovernor), "authored damage");
        Assert.Equal(Assert.IsType<PdfReference>(Member(expected.Trailer, "Root")).ObjectNumber,
            Assert.IsType<PdfReference>(Member(table.Trailer, "Root")).ObjectNumber);
        Assert.Equal(controlGovernor.Snapshot().LiveMemoryBytes, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void SelectedTailValuesKeepTheirBytesAndAliases()
    {
        byte[] input = Input([], "<< /Size 5 /Root << /Type /Catalog /Pages 2 0 R >> /Info << /Title " + Literal(4096)
            + " >> /ID [ (first) (second) ] /Unused " + Literal(4096) + " >>");
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        XRefTable table = XRefRecovery.Recover(input, limits, governor, new DiagnosticBag(governor), "authored damage");
        PdfDictionary root = Assert.IsType<PdfDictionary>(Member(table.Trailer, "Root"));
        PdfDictionary info = Assert.IsType<PdfDictionary>(Member(table.Trailer, "Info"));
        PdfByteString title = Assert.IsType<PdfByteString>(Member(info, "Title"));
        Assert.Equal(Encoding.ASCII.GetBytes(new string('A', 4096)), title.Value);
        Assert.Same(root, Member(Assert.Single(table.Trailers), "Root"));
        Assert.Same(info, Member(table.Trailer, "Info"));
        Assert.IsType<PdfArray>(Member(table.Trailer, "ID"));
        Assert.False(table.Trailer.TryGet(new PdfName("Unused"u8.ToArray()), out _));
        Assert.True(governor.Snapshot().LiveMemoryBytes >= title.Value.Length);
    }

    [Theory]
    [InlineData("<< /Size 5 /Root 1 0 R >>", 1)]
    [InlineData("<< /Size 5 /Root 99 0 R >>", 5)]
    [InlineData("<< /Size 5 /Root null >>", 5)]
    public void ActiveTrailerAndLastCatalogPrecedenceRemainIntact(string trailer, int expectedRoot)
    {
        byte[] input = Input(["<< /Type /Catalog /Pages 2 0 R /Unused " + Literal(4096) + " >>"], trailer);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        XRefTable table = XRefRecovery.Recover(input, QpdfLimits.Default, governor, new DiagnosticBag(governor), "authored damage");
        Assert.Equal(expectedRoot, Assert.IsType<PdfReference>(Member(table.Trailer, "Root")).ObjectNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRecoveryReleasesTrialAndEntryReservations(bool diagnostic)
    {
        byte[] input = diagnostic ? Input([Literal(4096)], "<< /Size 6 /Root 1 0 R >>")
            : Input([], "<< /Size 5 /Root 1 0 R /Unused " + Literal(60000) + " >>");
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L, MaxDiagnostics = 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        if (diagnostic) { diagnostics.Add(QpdfDiagnosticCode.RecoveryEngaged, QpdfDiagnosticSeverity.Warning, "prior"); }
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => XRefRecovery.Recover(input, limits, governor, diagnostics, "authored damage"));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork > 0L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringCandidateOrTailReleasesEarlierTrials(bool tail)
    {
        string large = Literal(tail ? 60000 : 16 * 1024 * 1024);
        byte[] input = tail ? Input(Enumerable.Repeat(Literal(4096), 10).ToArray(), "<< /Size 15 /Root 1 0 R /Unused " + large + " >>")
            : Input(Enumerable.Repeat(Literal(4096), 10).Append(large).ToArray(), "<< /Size 16 /Root 1 0 R >>");
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = 1L };
        ResourceGovernor governor = new(limits, cancellation.Token);
        Task<XRefTable> parsing = Task.Factory.StartNew(() => XRefRecovery.Recover(input, limits, governor, new DiagnosticBag(governor), "authored damage"),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        long threshold = tail ? 150000L : 32L * 1024L * 1024L;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (governor.Snapshot().LiveMemoryBytes < threshold && !parsing.IsCompleted
            && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            Thread.SpinWait(16);
        }
        Assert.True(governor.Snapshot().LiveMemoryBytes >= threshold);
        Assert.False(parsing.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await parsing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork > 0L);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StrictXrefRejectionReleasesItsParsedBodyAndEntries(bool classic, bool stream)
    {
        string body = stream ? "<< /Type /Wrong /Length 4096 >>\nstream\n" + new string('A', 4096) + "\nendstream" : Literal(4096);
        string trailer = stream ? "<< /Size 2 /Root 1 0 R /Unused " + Literal(4096) + " >>" : Literal(4096);
        string section = classic ? "xref\n0 1\n0000000000 65535 f \ntrailer\n" + trailer : "1 0 obj\n" + body + "\nendobj";
        byte[] input = Encoding.ASCII.GetBytes("%PDF-1.4\n" + section + "\nstartxref\n9\n%%EOF\n");
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Throws<QpdfParseException>(() => XRefTable.Parse(input, QpdfLimits.Default, governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RepeatedLinearizationTrialsReleaseDiscardedOrConsumedDictionaries(int kind)
    {
        string body = kind == 0 ? "<< /Unused " + Literal(256) + " >>" : "<< /Linearized 1 /L " + (kind == 1 ? "0000000000" : "0000000001") + " /Unused " + Literal(256) + " >>";
        byte[] input = TolerantObjectLoadingTests.Pdf([body], false);
        if (kind == 1) { input = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(input).Replace("/L 0000000000", "/L " + input.Length.ToString("D10"), StringComparison.Ordinal)); }
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        DiagnosticBag diagnostics = new(governor);
        XRefTable table = XRefTable.Parse(input, limits, governor, diagnostics, QpdfRecoveryMode.Strict);
        DocumentEngine engine = new(input, table, limits, governor, diagnostics, QpdfRecoveryMode.Strict);
        QpdfObject cached = engine.Load(1);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        for (int i = 0; i < 50; i++)
        {
            LinearizationInspection result = LinearizationCheck.Inspect(engine, 1, limits, governor, CancellationToken.None);
            Assert.Equal(kind == 1 ? LinearizationStatus.Invalid : LinearizationStatus.NotLinearized, result.Status);
            Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
            Assert.Same(cached, engine.Load(1));
        }
    }

    private static byte[] XrefFragment(bool flate, string index)
    {
        byte[] decoded = [0, 0, 255, 1, 9, 0];
        byte[] encoded = decoded;
        if (flate)
        {
            using MemoryStream compressed = new();
            using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, true)) { zlib.Write(decoded); }
            encoded = compressed.ToArray();
        }
        using MemoryStream source = new();
        void Write(string text) => source.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.4\n1 0 obj\n<< /Type /XRef /Size 2 /Root 1 0 R /W [1 1 1] /Filter "
            + (flate ? "/FlateDecode" : "null") + " /Length " + encoded.Length.ToString("D3") + index + " >>\nstream\n");
        source.Write(encoded);
        Write("\nendstream\nendobj\nstartxref\n9\n%%EOF\n");
        return source.ToArray();
    }

    [Fact]
    public void AcceptedXrefFragmentsReleaseEncodedAndDecodedData()
    {
        ResourceGovernor rawGovernor = new(QpdfLimits.Default, CancellationToken.None);
        ResourceGovernor flateGovernor = new(QpdfLimits.Default, CancellationToken.None);
        XRefTable raw = XRefTable.Parse(XrefFragment(false, ""), QpdfLimits.Default, rawGovernor, new DiagnosticBag(rawGovernor), QpdfRecoveryMode.Strict);
        XRefTable flate = XRefTable.Parse(XrefFragment(true, ""), QpdfLimits.Default, flateGovernor, new DiagnosticBag(flateGovernor), QpdfRecoveryMode.Strict);
        Assert.Equal(raw.Entries[1].First, flate.Entries[1].First);
        // Both dictionaries use three-digit /Length tokens and have equal storage.
        // Only the retained FlateDecode name differs (64 + 11 bytes).
        Assert.Equal(75L, flateGovernor.Snapshot().LiveMemoryBytes - rawGovernor.Snapshot().LiveMemoryBytes);
        Assert.Equal(6L, flateGovernor.Snapshot().TotalDecodedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedLargeXrefIndexCountsRemainParseFailuresAndReleaseOwnership(bool flate)
    {
        byte[] input = XrefFragment(flate, " /Index [1 9223372036854775807]");
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Contains("outside /Size", Assert.Throws<QpdfParseException>(() => XRefTable.Parse(input, QpdfLimits.Default,
            governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict)).Message, StringComparison.Ordinal);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void HybridXrefDiscardsItsUnusedDictionaryValues()
    {
        string large = Literal(4096);
        byte[] Hybrid(string value)
        {
            string stream = "<< /Type /XRef /Size 6 /W [1 1 1] /Index [1 1] /Length 3 /Unused " + value + " >>\nstream\n\u0001\u0009\u0000\nendstream";
            byte[] source = TolerantObjectLoadingTests.Pdf([stream], false);
            int offset = source.AsSpan().IndexOf("5 0 obj\n"u8);
            string text = Encoding.ASCII.GetString(source).Replace("/Size 6 >>", "/Size 6 /XRefStm " + offset + " >>", StringComparison.Ordinal);
            return Encoding.ASCII.GetBytes(text);
        }
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        ResourceGovernor controlGovernor = new(QpdfLimits.Default, CancellationToken.None);
        XRefTable actual = XRefTable.Parse(Hybrid(large), QpdfLimits.Default, governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict);
        XRefTable control = XRefTable.Parse(Hybrid(NullControl(large)), QpdfLimits.Default, controlGovernor, new DiagnosticBag(controlGovernor), QpdfRecoveryMode.Strict);
        Assert.Equal(control.Entries.Count, actual.Entries.Count);
        Assert.Equal(controlGovernor.Snapshot().LiveMemoryBytes, governor.Snapshot().LiveMemoryBytes);
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class XrefCollectionTests
{
    private static byte[] Classic(int count, bool broken)
        => TolerantObjectLoadingTests.Pdf(Enumerable.Repeat("null", count).ToArray(), broken);

    private static byte[] Revisions(int count, int revisions)
    {
        string original = Encoding.ASCII.GetString(Classic(count, false));
        int section = original.IndexOf("xref\n", StringComparison.Ordinal);
        string rows = original[section..original.LastIndexOf("startxref\n", StringComparison.Ordinal)];
        StringBuilder source = new(original);
        long previous = section;
        for (int i = 1; i < revisions; i++)
        {
            long current = source.Length;
            source.Append(rows.Replace(" >>", " /Prev " + previous.ToString(CultureInfo.InvariantCulture) + " >>", StringComparison.Ordinal));
            source.Append("startxref\n").Append(current).Append("\n%%EOF\n");
            previous = current;
        }
        return Encoding.ASCII.GetBytes(source.ToString());
    }

    private static byte[] Stream(int count, bool flate, bool hybrid)
    {
        byte[] classic = Classic(count, false);
        string original = Encoding.ASCII.GetString(classic);
        int xref = original.IndexOf("xref\n", StringComparison.Ordinal);
        string prefix = original[..xref];
        int streamNumber = count + 5;
        byte[] decoded = new byte[streamNumber * 7];
        for (int number = 1; number <= streamNumber; number++)
        {
            int offset = number == streamNumber ? prefix.Length : prefix.IndexOf(number.ToString(CultureInfo.InvariantCulture) + " 0 obj\n", StringComparison.Ordinal);
            Assert.True(offset >= 0);
            int at = (number - 1) * 7;
            decoded[at] = 1;
            for (int b = 0; b < 4; b++) { decoded[at + 1 + b] = (byte)(offset >> (24 - b * 8)); }
        }
        byte[] encoded = decoded;
        if (flate)
        {
            using MemoryStream compressed = new();
            using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, true)) { zlib.Write(decoded); }
            encoded = compressed.ToArray();
        }
        using MemoryStream output = new();
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        Write(prefix);
        Write($"{streamNumber} 0 obj\n<< /Type /XRef /Size {streamNumber + 1} /Root 1 0 R /W [1 4 2] /Index [1 {streamNumber}] /Length {encoded.Length}"
            + (flate ? " /Filter /FlateDecode" : "") + " >>\nstream\n");
        output.Write(encoded);
        Write("\nendstream\nendobj\n");
        long startxref = prefix.Length;
        if (hybrid)
        {
            startxref = output.Position;
            Write($"xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size {streamNumber + 1} /Root 1 0 R /XRefStm {prefix.Length} >>\n");
        }
        Write($"startxref\n{startxref}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] Shape(string kind, int count) => kind switch
    {
        "classic" => Classic(count, false),
        "recovery" => Classic(count, true),
        "raw" => Stream(count, false, false),
        "flate" => Stream(count, true, false),
        "hybrid" => Stream(count, false, true),
        _ => throw new ArgumentException("Unknown fixture shape.", nameof(kind)),
    };

    private static XRefTable Parse(byte[] input, ResourceGovernor governor, QpdfLimits limits)
        => XRefTable.Parse(input, limits, governor, new DiagnosticBag(governor), QpdfRecoveryMode.BoundedRepair);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long DictionaryAllocation(int capacity)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Dictionary<int, XrefEntry> dictionary = new(capacity);
        GC.KeepAlive(dictionary);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Theory]
    [InlineData("classic", 0)]
    [InlineData("classic", 128)]
    [InlineData("classic", 2000)]
    [InlineData("recovery", 0)]
    [InlineData("recovery", 128)]
    [InlineData("recovery", 2000)]
    [InlineData("raw", 2000)]
    [InlineData("flate", 2000)]
    [InlineData("hybrid", 2000)]
    public void RetainedAdmissionCoversIndependentlyAllocatedBackingStorage(string kind, int count)
    {
        byte[] input = Shape(kind, count);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        XRefTable table = Parse(input, governor, QpdfLimits.Default);
        int capacity = Assert.IsType<Dictionary<int, XrefEntry>>(table.Entries).EnsureCapacity(0);
        // Warm the actual constructor path before measuring backing storage.
        // Cold BCL/runtime initialization is a separate allocation audit.
        DictionaryAllocation(capacity);
        long storage = DictionaryAllocation(capacity);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= storage,
            $"Retained admission {governor.Snapshot().LiveMemoryBytes} is below independently allocated dictionary storage {storage}, capacity {capacity}.");
        Assert.True(table.Entries.Count >= count + 4);
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("recovery")]
    [InlineData("raw")]
    [InlineData("hybrid")]
    public void FiniteCeilingRejectsLargeTablesAndReleasesFailedOwnership(string kind)
    {
        byte[] input = Shape(kind, 2000);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), Assert.Throws<QpdfLimitException>(() => Parse(input, governor, limits)).LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().TotalWork > 0L);
        Assert.NotNull(Parse(Classic(0, false), governor, limits));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicCeilingRejectsLargeRecoveryBeforePublication(bool job)
    {
        byte[] input = Classic(2000, true);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        QpdfPath target = QpdfPath.From("outputs/a.pdf");
        await using (IQpdfOutputTransaction prior = await host.CreateOutputAsync(target, CancellationToken.None))
        {
            await prior.WriteAsync("prior"u8.ToArray(), CancellationToken.None);
            await prior.CommitAsync(CancellationToken.None);
        }
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxMemoryBytes = input.Length + 100000L });
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","outputFile":"outputs/a.pdf","pages":[{"file":".","range":"1"}]}""", options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--pages", ".", "1", "--", "outputs/a.pdf"]), options, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Failed, result.Status);
        Assert.Equal(QpdfExitCode.Error, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.BudgetExceeded);
        Assert.True(host.TryGetOutput(target, out byte[] output));
        Assert.Equal("prior"u8.ToArray(), output);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public void RepeatedRevisionsReleaseSectionMapsAndVisitedStorage()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        XRefTable table = Parse(Revisions(500, 20), governor, QpdfLimits.Default);
        int capacity = Assert.IsType<Dictionary<int, XrefEntry>>(table.Entries).EnsureCapacity(0);
        long retained = governor.Snapshot().LiveMemoryBytes;
        Assert.Equal(20, table.Trailers.Count);
        Assert.True(retained < DictionaryAllocation(capacity) + table.Trailers.Count * 2048L,
            $"Section maps survive acceptance: {retained} bytes.");
    }

    [Fact]
    public void RepeatedRecoveryHeadersAndDroppedEntriesKeepOnlyFinalStorage()
    {
        string original = Encoding.ASCII.GetString(Classic(0, true));
        string headers = string.Concat(Enumerable.Repeat("5 0 obj\nnull\nendobj\n", 2000));
        string marker = "xref\n";
        byte[] duplicate = Encoding.ASCII.GetBytes(original.Insert(original.IndexOf(marker, StringComparison.Ordinal), headers));
        ResourceGovernor control = new(QpdfLimits.Default, CancellationToken.None);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        XRefTable expected = Parse(Encoding.ASCII.GetBytes(original), control, QpdfLimits.Default);
        XRefTable actual = Parse(duplicate, governor, QpdfLimits.Default);
        Assert.Equal(expected.Entries.Count, actual.Entries.Count);
        Assert.Equal(control.Snapshot().LiveMemoryBytes, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrowthFailurePreservesPreviouslyAcceptedTable(bool recovery)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        XRefTable accepted = Parse(Classic(0, false), governor, limits);
        long baseline = governor.Snapshot().LiveMemoryBytes;
        Assert.Throws<QpdfLimitException>(() => Parse(Classic(2000, recovery), governor, limits));
        Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(XrefEntryKind.Uncompressed, accepted.Entries[1].Kind);
    }

    [Fact]
    public void SparseRecoveryAdmitsFilledFreeSlots()
    {
        string original = Encoding.ASCII.GetString(Classic(0, true));
        string sparse = original.Insert(original.IndexOf("xref\n", StringComparison.Ordinal),
            "2004 0 obj\nnull\nendobj\n%" + new string('A', 6500) + "\n").Replace("/Size 5", "/Size 2005", StringComparison.Ordinal);
        byte[] input = Encoding.ASCII.GetBytes(sparse);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 100000L };
        ResourceGovernor bounded = new(limits, CancellationToken.None);
        Assert.Throws<QpdfLimitException>(() => Parse(input, bounded, limits));
        Assert.Equal(0L, bounded.Snapshot().LiveMemoryBytes);
        ResourceGovernor generous = new(QpdfLimits.Default, CancellationToken.None);
        XRefTable table = Parse(input, generous, QpdfLimits.Default);
        Assert.Equal(2004, table.Entries.Count);
        Assert.Equal(XrefEntryKind.Free, table.Entries[1000].Kind);
        Assert.Equal(XrefEntryKind.Uncompressed, table.Entries[2004].Kind);
    }

    [Fact]
    public void TruncatedTailSizeReleasesTheCompleteScanMap()
    {
        string original = Encoding.ASCII.GetString(Classic(2000, true)).Replace("/Size 2005", "/Size 5", StringComparison.Ordinal);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        ResourceGovernor control = new(QpdfLimits.Default, CancellationToken.None);
        XRefTable table = Parse(Encoding.ASCII.GetBytes(original), governor, QpdfLimits.Default);
        XRefTable expected = Parse(Classic(0, true), control, QpdfLimits.Default);
        Assert.Equal(expected.Entries.Count, table.Entries.Count);
        Assert.Equal(control.Snapshot().LiveMemoryBytes, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingCatalogOrEmptyScanReleasesStorage(bool empty)
    {
        byte[] input = empty ? "%PDF-1.4\nstartxref\n0\n%%EOF\n"u8.ToArray()
            : Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Classic(2000, true))
                .Replace("/Type /Catalog", "/Type /Ignored", StringComparison.Ordinal)
                .Replace("/Root 1 0 R", "/Root null", StringComparison.Ordinal));
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Throws<QpdfParseException>(() => Parse(input, governor, QpdfLimits.Default));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GenerousPublicArgvAndJobBudgetsKeepPageCounts(bool job, bool recovery)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), Classic(2000, recovery), QpdfInputOwnership.Copy);
        QpdfExecutionOptions options = new(QpdfLimits.Default with { MaxMemoryBytes = 1048576L });
        QpdfExecutionResult result = job
            ? await Qpdf.ExecuteJobAsync(host, """{"inputFile":"inputs/a.pdf","showNpages":""}""", options, CancellationToken.None)
            : await Qpdf.ExecuteAsync(host, new QpdfCommandInvocation(["inputs/a.pdf", "--show-npages"]), options, CancellationToken.None);
        Assert.Equal(QpdfExecutionStatus.Completed, result.Status);
        Assert.Equal(recovery ? QpdfExitCode.Warning : QpdfExitCode.Success, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d.Code == QpdfDiagnosticCode.PageCount && d.Message == "inputs/a.pdf: 1");
        Assert.True(result.Counters.PeakMemoryBytes <= options.Limits.MaxMemoryBytes);
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public async Task TypedStrictOpeningEnforcesCollectionAdmission()
    {
        byte[] input = Classic(2000, false);
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/a.pdf"), input, QpdfInputOwnership.Copy);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = input.Length + 100000L };
        await Assert.ThrowsAsync<QpdfLimitException>(() => QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/a.pdf"), limits, CancellationToken.None).AsTask());
        Assert.Equal(0L, host.OpenLeaseCount);
    }

    [Fact]
    public void RevisionLimitFailureReleasesMapsAndVisitedSet()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxRevisions = 10L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        Assert.Equal(nameof(QpdfLimits.MaxRevisions), Assert.Throws<QpdfLimitException>(() => Parse(Revisions(500, 20), governor, limits)).LimitName);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CycleAndDuplicateFailuresReleaseAcceptedSectionStorage(bool cycle)
    {
        string source = Encoding.ASCII.GetString(Revisions(500, 2));
        if (cycle)
        {
            int start = source.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length;
            string offset = source[start..source.IndexOf('\n', start)];
            int previous = source.LastIndexOf(" /Prev ", StringComparison.Ordinal);
            int end = source.IndexOf(" >>", previous, StringComparison.Ordinal);
            source = source[..previous] + " /Prev " + offset + source[end..];
        }
        else
        {
            int trailer = source.LastIndexOf("trailer\n", StringComparison.Ordinal);
            source = source.Insert(trailer, "1 1\n0000000009 00000 n \n");
        }
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfParseException failure = Assert.Throws<QpdfParseException>(() => XRefTable.Parse(Encoding.ASCII.GetBytes(source),
            QpdfLimits.Default, governor, new DiagnosticBag(governor), QpdfRecoveryMode.Strict));
        Assert.Contains(cycle ? "cycles" : "Duplicate", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkFailureReleasesCollectionStorage(bool recovery)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 1000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        Assert.Equal(nameof(QpdfLimits.MaxWork), Assert.Throws<QpdfLimitException>(() => Parse(Classic(2000, recovery), governor, limits)).LimitName);
        Assert.True(governor.Snapshot().TotalWork > 0L);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterCollectionWorkReleasesAllTemporaryOwners(bool recovery)
    {
        // Keep enough work after collection growth for a scheduled observer;
        // every attempt still requires interruption after actual admission.
        const int count = 200000;
        byte[] input = Classic(count, recovery);
        QpdfLimits limits = QpdfLimits.Default with { CancellationCheckInterval = 1L };
        bool interrupted = false;
        for (int attempt = 0; attempt < 8 && !interrupted; attempt++)
        {
            using CancellationTokenSource cancellation = new();
            ResourceGovernor governor = new(limits, cancellation.Token);
            Task<XRefTable> parsing = Task.Factory.StartNew(() => Parse(input, governor, limits), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            bool reached = SpinWait.SpinUntil(() => governor.Snapshot().LiveMemoryBytes >= 50000L || parsing.IsCompleted, TimeSpan.FromSeconds(10));
            Assert.True(reached);
            bool observed = governor.Snapshot().LiveMemoryBytes >= 50000L;
            cancellation.Cancel();
            try
            {
                XRefTable completed = await parsing.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(completed.Entries.Count >= count);
            }
            catch (OperationCanceledException)
            {
                Assert.True(observed);
                Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
                Assert.True(governor.Snapshot().TotalWork > 0L);
                interrupted = true;
            }
        }
        Assert.True(interrupted, "Parsing must be canceled after observed collection admission; completion alone is insufficient.");
    }
}

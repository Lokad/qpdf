using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class WriterEncodingTests
{
    private static object Source(string kind, int size)
    {
        if (kind == "ascii") { return new string('A', size); }
        byte[] raw = new byte[size]; Array.Fill(raw, (byte)128);
        return kind == "name" ? new PdfName(raw) : new PdfByteString(raw, kind == "hex");
    }

    private static WriterEncoding Encode(string kind, object source, QpdfLimits limits, ResourceGovernor governor, CancellationToken token)
        => kind == "ascii" ? WriterEncoding.Ascii((string)source, limits, governor, token)
            : kind == "name" ? WriterEncoding.Name((PdfName)source, limits, governor, token)
            : WriterEncoding.String((PdfByteString)source, limits, governor, token);

    private static int Size(string kind, int count) => kind switch
    {
        "ascii" => count, "name" => 1 + 3 * count, "hex" => 2 + 2 * count, _ => 2 + 4 * count,
    };

    [Fact]
    public void NameSpellingPreservesDelimitersBinaryAndRepairSentinel()
    {
        byte[] source = [65, 0, 35, 32, 47, 40, 41, 60, 62, 91, 93, 123, 125, 37, 127, 128, 255];
        PdfName name = new(source);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterEncoding encoded = WriterEncoding.Name(name, QpdfLimits.Default, governor, CancellationToken.None);
        Assert.Equal("/A##23#20#2F#28#29#3C#3E#5B#5D#7B#7D#25#7F#80#FF", Encoding.ASCII.GetString(encoded.Data.Span));
        Assert.Equal(source, name.RawBytes);
        source[0] = 66;
        Assert.Equal((byte)'A', encoded.Data.Span[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StringSpellingPreservesAuthoredEscapesAndHexCase(bool hex)
    {
        byte[] source = [65, 0, 10, 13, 9, 8, 12, 40, 41, 92, 31, 32, 127, 128, 255, 66];
        PdfByteString value = new(source, hex);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterEncoding encoded = WriterEncoding.String(value, QpdfLimits.Default, governor, CancellationToken.None);
        string expected = hex ? "<41000A0D09080C28295C1F207F80FF42>" : "(A\\000\\n\\r\\t\\b\\f\\(\\)\\\\\\037 \\177\\200\\377B)";
        Assert.Equal(expected, Encoding.ASCII.GetString(encoded.Data.Span));
        Assert.Equal(source, value.Value);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("literal")]
    [InlineData("hex")]
    public void AllByteValuesSurviveReaderRoundtrip(string kind)
    {
        byte[] raw = Enumerable.Range(kind == "name" ? 1 : 0, kind == "name" ? 255 : 256).Select(value => (byte)value).ToArray();
        object source = kind == "name" ? new PdfName(raw) : new PdfByteString(raw, kind == "hex");
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using WriterEncoding encoded = Encode(kind, source, QpdfLimits.Default, governor, CancellationToken.None);
        ResourceGovernor reader = new(QpdfLimits.Default, CancellationToken.None);
        DiagnosticBag diagnostics = new(reader);
        using ParserMemoryScope memory = ParserMemoryScope.Create(reader);
        PdfParser parser = new(QpdfLimits.Default, reader, diagnostics, null);
        QpdfObject parsed = parser.ParseValue(new PdfTokenizer(encoded.Data), 0, memory);
        Assert.Equal(raw, parsed is PdfName name ? name.RawBytes : Assert.IsType<PdfByteString>(parsed).Value);
        Assert.Empty(diagnostics.Entries);
    }

    [Fact]
    public void AsciiMatchesBclReplacementAcrossAllUtf16UnitsAndBoundaries()
    {
        string allUnits = new(Enumerable.Range(0, 65536).Select(value => (char)value).ToArray());
        string boundary = new string('A', 8191) + char.ConvertFromUtf32(0x1F600) + "x" + '\uD800' + "y" + '\uDC00';
        foreach (string source in new[] { allUnits, boundary })
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using WriterEncoding encoded = WriterEncoding.Ascii(source, QpdfLimits.Default, governor, CancellationToken.None);
            Assert.Equal(Encoding.ASCII.GetBytes(source), encoded.Data.ToArray());
            Assert.Equal(3L * source.Length, governor.Snapshot().TotalWork);
        }
    }

    [Theory]
    [InlineData("ascii", "")]
    [InlineData("name", "/")]
    [InlineData("literal", "()")]
    [InlineData("hex", "<>")]
    public void EmptyInputsOwnOnlyTheirActualResult(string kind, string expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        WriterEncoding owner = Encode(kind, Source(kind, 0), QpdfLimits.Default, governor, CancellationToken.None);
        Assert.Equal(expected, Encoding.ASCII.GetString(owner.Data.Span));
        Assert.Equal(128L + (expected.Length == 0 ? 0L : 32L + expected.Length), governor.Snapshot().LiveMemoryBytes);
        owner.Dispose(); owner.Dispose();
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        Assert.Throws<ObjectDisposedException>(() => owner.Data);
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("name")]
    [InlineData("literal")]
    [InlineData("hex")]
    public void ExactCopyAdmissionRejectsBeforeAllocationAndCanRetry(string kind)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 1024L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        object source = Source(kind, 65536);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        using ParserMemoryScope filler = ParserMemoryScope.Create(governor);
        int size = Size(kind, 65536);
        filler.Reserve(limits.MaxMemoryBytes - governor.Snapshot().LiveMemoryBytes - (160L + size - 1L));
        long live = governor.Snapshot().LiveMemoryBytes, before = GC.GetAllocatedBytesForCurrentThread();
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => Encode(kind, source, limits, governor, CancellationToken.None));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 32768L, $"Rejected encoding allocated {allocated} bytes.");
        Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(97L, prior.ReservedBytes);
        filler.Dispose();
        using (WriterEncoding retry = Encode(kind, source, limits, governor, CancellationToken.None))
        {
            Assert.Equal(size, retry.Data.Length);
            Assert.Equal(161L + 160L + size, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("literal")]
    public void WidthScanWorkExhaustionAllocatesNoOwnerOrResult(string kind)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = 9000L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        object source = Source(kind, 65536);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        Assert.Throws<QpdfLimitException>(() => Encode(kind, source, limits, governor, CancellationToken.None));
        Assert.Equal(8192L, governor.Snapshot().TotalWork);
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(161L, governor.Snapshot().PeakMemoryBytes);
    }

    [Theory]
    [InlineData("ascii", 1, 0)]
    [InlineData("name", 3, 1)]
    [InlineData("literal", 4, 2)]
    [InlineData("hex", 2, 2)]
    public void FillWorkExhaustionRetiresThePartialCopy(string kind, int width, int punctuation)
    {
        const int count = 65536;
        bool scan = kind is "name" or "literal";
        long budget = (scan ? count : 0L) + Size(kind, count) + punctuation + (scan ? 2L : 1L) * 8192L + width * 8192L;
        QpdfLimits limits = QpdfLimits.Default with { MaxWork = budget };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        object source = Source(kind, count);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => Encode(kind, source, limits, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName);
        Assert.Equal(budget, governor.Snapshot().TotalWork);
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
        Assert.True(governor.Snapshot().PeakMemoryBytes >= 161L + 160L + Size(kind, count));
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("name")]
    [InlineData("literal")]
    [InlineData("hex")]
    public void OutputQuotaRejectsBeforeAllocation(string kind)
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = Size(kind, 65536) - 1L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        object source = Source(kind, 65536);
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => Encode(kind, source, limits, governor, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), failure.LimitName);
        Assert.Equal(0L, governor.Snapshot().PeakMemoryBytes);
    }

    [Theory]
    [InlineData("ascii", false)]
    [InlineData("ascii", true)]
    [InlineData("name", false)]
    [InlineData("name", true)]
    [InlineData("literal", false)]
    [InlineData("literal", true)]
    [InlineData("hex", false)]
    [InlineData("hex", true)]
    public void EitherCanceledTokenPreservesPriorStorage(string kind, bool governorToken)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        object source = Source(kind, 16384);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Encode(kind, source, QpdfLimits.Default, governor,
            governorToken ? CancellationToken.None : cancellation.Token));
        Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
        Assert.Equal(0L, governor.Snapshot().TotalWork);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Backing(WriterEncoding owner) => new(typeof(WriterEncoding)
        .GetField("_bytes", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
        ?? throw new InvalidOperationException("Expected encoding backing."));

    [Theory]
    [InlineData("ascii", false)]
    [InlineData("ascii", true)]
    [InlineData("name", false)]
    [InlineData("name", true)]
    [InlineData("literal", false)]
    [InlineData("literal", true)]
    [InlineData("hex", false)]
    [InlineData("hex", true)]
    public async Task CancellationAfterObservedVisitsRetiresAttemptStorage(string kind, bool governorToken)
    {
        const int count = 4 * 1024 * 1024;
        object source = Source(kind, count);
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim start = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        long threshold = kind is "name" or "literal" ? 8192L : Size(kind, count) + (kind == "hex" ? 2L : 0L) + 8192L;
        Task worker = Task.Factory.StartNew(() =>
        {
            start.Wait();
            using WriterEncoding encoded = Encode(kind, source, QpdfLimits.Default, governor,
                governorToken ? CancellationToken.None : cancellation.Token);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            start.Set();
            bool observed = SpinWait.SpinUntil(() => governor.Snapshot().TotalWork >= threshold || worker.IsCompleted, TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.True(observed);
            Assert.True(governor.Snapshot().TotalWork >= threshold);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
            GC.KeepAlive(source);
        }
        finally
        {
            start.Set(); cancellation.Cancel();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("name")]
    [InlineData("literal")]
    [InlineData("hex")]
    public void RetainedDisposedOwnerDropsExclusiveBacking(string kind)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        object source = Source(kind, 16384);
        WriterEncoding owner = Encode(kind, source, QpdfLimits.Default, governor, CancellationToken.None);
        WeakReference bytes = Backing(owner);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Assert.True(bytes.IsAlive);
        owner.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Assert.False(bytes.IsAlive);
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(owner); GC.KeepAlive(source);
    }
}

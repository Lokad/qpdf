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
    private sealed class RetainingSink : IByteSink
    {
        internal readonly List<ReadOnlyMemory<byte>> Chunks = new();
        public long Length { get; private set; }
        public ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Chunks.Add(chunk); Length += chunk.Length;
            return ValueTask.CompletedTask;
        }
        internal byte[] Bytes() => Chunks.SelectMany(chunk => chunk.ToArray()).ToArray();
    }

    [Theory]
    [InlineData("DistinctPlainName", "/DistinctPlainName")]
    [InlineData("Needs Escape", "/Needs#20Escape")]
    [InlineData("", "/")]
    public async Task RetainedNameChunksRemainImmutableAfterCallerCopiesChange(string text, string expected)
    {
        byte[] input = Encoding.ASCII.GetBytes(text);
        PdfName name = new(input);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        RetainingSink sink = new();
        await new PdfSerializer(QpdfLimits.Default, governor).AppendValueAsync(name, sink, 0, false, CancellationToken.None);
        Array.Fill(input, (byte)'x');
        byte[] publicCopy = name.RawBytes;
        Array.Fill(publicCopy, (byte)'y');
        Assert.Equal(expected, Encoding.ASCII.GetString(sink.Bytes()));
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public async Task UnescapedNameLimitRejectsBeforeEmittingItsPrefix()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxOutputBytes = 4 };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        RetainingSink sink = new();
        QpdfLimitException failure = await Assert.ThrowsAsync<QpdfLimitException>(async () =>
            await new PdfSerializer(limits, governor).AppendValueAsync(PdfName.FromAscii("Distinct"), sink,
                0, false, CancellationToken.None));
        Assert.Equal(nameof(QpdfLimits.MaxOutputBytes), failure.LimitName);
        Assert.Empty(sink.Chunks);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DictionarySerializationPreservesEntryOrderNullsAndStreamLength(bool owned, bool stream)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Z")] = PdfNull.Instance,
#pragma warning disable CS8625 // Existing dictionary serialization skips CLR-null entries.
            [PdfName.FromAscii("Skipped")] = null,
#pragma warning restore CS8625
            [PdfName.FromAscii("A")] = new PdfArray([PdfBoolean.True]),
            [PdfName.FromAscii("Length")] = new PdfInteger("999", 999),
        };
        PdfDictionary dictionary;
        if (owned)
        {
            PdfNameTable<QpdfObject> table = PdfNameTable<QpdfObject>.Create(memory, governor, IntPtr.Size, CancellationToken.None);
            foreach (KeyValuePair<PdfName, QpdfObject> entry in entries)
            {
                table.Assign(memory, governor, entry.Key, entry.Value, CancellationToken.None);
            }
            table.Freeze(memory, governor, CancellationToken.None);
            dictionary = PdfDictionary.FromOwnedEntries(table);
        }
        else { dictionary = new PdfDictionary(entries); }
        QpdfObject value = stream ? new PdfStream(dictionary, "abc"u8.ToArray()) : dictionary;
        PdfSerializer serializer = new(QpdfLimits.Default, governor);
        using BufferedSink sink = BufferedSink.Create(QpdfLimits.Default, governor, CancellationToken.None);
        await serializer.AppendValueAsync(value, sink, 0, true, CancellationToken.None);
        string expected = stream ? "<</Z null /A [true] /Length 3 >>\nstream\nabc\nendstream"
            : "<</Z null /A [true] /Length 999 >>";
        Assert.Equal(expected, Encoding.ASCII.GetString(sink.ToArray(memory, CancellationToken.None)));
        CountingSink counter = new();
        await serializer.AppendValueAsync(value, counter, 0, true, CancellationToken.None);
        Assert.Equal(expected.Length, counter.Length);
        Assert.True(dictionary.TryGet(PdfName.FromAscii("Length"), out QpdfObject length));
        Assert.Equal(999, Assert.IsType<PdfInteger>(length).Value);
    }

    [Fact]
    public async Task CountingPreservesEscapedNameLiteralAndHexTokenLengths()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfArray value = new([new PdfName([65, 35, 32, 47, 255]),
            new PdfByteString([40, 41, 92, 10, 0, 255], false), new PdfByteString([0, 255], true)]);
        PdfSerializer serializer = new(QpdfLimits.Default, governor);
        CountingSink counter = new();
        using BufferedSink sink = BufferedSink.Create(QpdfLimits.Default, governor, CancellationToken.None);
        await serializer.AppendValueAsync(value, counter, 0, false, CancellationToken.None);
        await serializer.AppendValueAsync(value, sink, 0, false, CancellationToken.None);
        const string expected = """[/A#23#20#2F#FF (\(\)\\\n\000\377) <00FF>]""";
        Assert.Equal(expected.Length, counter.Length);
        Assert.Equal(expected, Encoding.ASCII.GetString(sink.ToArray(memory, CancellationToken.None)));
    }

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

    private static WriterEncoding EncodeObserved(string kind, object source, QpdfLimits limits, ResourceGovernor governor,
        CancellationToken token, Action observe)
        => kind == "ascii" ? WriterEncoding.Ascii((string)source, limits, governor, token, observe)
            : kind == "name" ? WriterEncoding.Name((PdfName)source, limits, governor, token, observe)
            : WriterEncoding.String((PdfByteString)source, limits, governor, token, observe);

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
        using ManualResetEventSlim start = new(), reached = new(), resume = new();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L);
        long threshold = kind is "name" or "literal" ? 8192L : Size(kind, count) + (kind == "hex" ? 2L : 0L) + 8192L;
        Task worker = Task.Factory.StartNew(() =>
        {
            start.Wait();
            using WriterEncoding encoded = EncodeObserved(kind, source, QpdfLimits.Default, governor,
                governorToken ? CancellationToken.None : cancellation.Token, () =>
                {
                    if (governor.Snapshot().TotalWork >= threshold)
                    {
                        reached.Set();
                        if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Encoding observation did not resume."); }
                    }
                });
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            start.Set();
            bool observed = reached.Wait(TimeSpan.FromSeconds(10));
            cancellation.Cancel(); resume.Set();
            Assert.True(observed);
            Assert.True(governor.Snapshot().TotalWork >= threshold);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(161L, governor.Snapshot().LiveMemoryBytes);
            GC.KeepAlive(source);
        }
        finally
        {
            start.Set(); cancellation.Cancel(); resume.Set();
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

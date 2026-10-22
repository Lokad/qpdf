using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class DocumentValueOwnerTests
{
    private static InMemoryQpdfHost Host(byte[] input)
    {
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("a.pdf"), input, QpdfInputOwnership.Copy);
        return host;
    }

    private static ValueTask<QpdfDocument> Open(InMemoryQpdfHost host, ResourceGovernor governor, QpdfLimits limits, bool broken)
        => QpdfDocument.OpenCore(host, QpdfPath.From("a.pdf"), limits, governor, new DiagnosticBag(governor),
            broken ? QpdfRecoveryMode.BoundedRepair : QpdfRecoveryMode.Strict, CancellationToken.None);

    private static void Collect()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private static object Field(object value, string name)
        => value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value)
            ?? throw new InvalidOperationException("Allocation carrier is unavailable.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Roots(QpdfDocument document)
        => [new(document.GetObject(1)), new(document.GetObject(2)), new(document.GetObject(3))];

    [Theory]
    [InlineData("(kept)")]
    [InlineData("(k\\145pt)")]
    [InlineData("<6B 65 70 74>")]
    public void ParsedByteStringsKeepOwnedBytesWhenInputAndPublicCopiesChange(string body)
    {
        byte[] input = Encoding.ASCII.GetBytes(body);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfParser parser = new(QpdfLimits.Default, governor, new DiagnosticBag(governor), null);
        using ParserMemoryScope parsedMemory0 = ParserMemoryScope.Create(governor);
        PdfByteString value = Assert.IsType<PdfByteString>(parser.ParseValue(new PdfTokenizer(input), 0, parsedMemory0));
        parsedMemory0.RetainGraph(); parsedMemory0.Dispose();
        Array.Fill(input, (byte)'X');
        byte[] copy = value.Value;
        Assert.Equal("kept"u8.ToArray(), copy);
        Array.Fill(copy, (byte)'Y');
        Assert.Equal("kept"u8.ToArray(), value.Value);
        byte[] source = "kept"u8.ToArray();
        PdfByteString constructed = new(source, false);
        Array.Fill(source, (byte)'Z');
        Assert.Equal("kept"u8.ToArray(), constructed.Value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ParseAndDrop(PdfParser parser, ResourceGovernor governor, string body, string route)
    {
        byte[] input = Encoding.ASCII.GetBytes(route == "indirect" ? "5 0 obj " + body + " endobj" : body);
        PdfTokenizer tokenizer = new(input);
        using ParserMemoryScope parsedMemory1 = ParserMemoryScope.Create(governor);
        QpdfObject value = route switch
        {
            "member" => parser.ParseObjectValue(tokenizer, 0, parsedMemory1),
            "indirect" => parser.ParseIndirectObject(tokenizer, 0, parsedMemory1).Value,
            _ => parser.ParseValue(tokenizer, 0, parsedMemory1),
        };
        parsedMemory1.RetainGraph(); parsedMemory1.Dispose();
        return new WeakReference(value);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("member")]
    [InlineData("indirect")]
    public void StandalonePayloadsDoNotRemainAnchoredToTheParser(string route)
    {
        const int length = 65536;
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 256L * 1024L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null);
        WeakReference value = ParseAndDrop(parser, governor, "(" + new string('A', length) + ")", route);
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, length, limits.MaxMemoryBytes);
        Collect();
        Assert.False(value.IsAlive);
        GC.KeepAlive(parser); GC.KeepAlive(governor);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NumberAlias ParseNumericAlias(PdfParser parser, ResourceGovernor governor, string token)
    {
        using ParserMemoryScope parsedMemory2 = ParserMemoryScope.Create(governor);
        PdfInteger number = Assert.IsType<PdfInteger>(parser.ParseValue(new PdfTokenizer(Encoding.ASCII.GetBytes(token)), 0, parsedMemory2));
        parsedMemory2.RetainGraph(); parsedMemory2.Dispose();
        return new NumberAlias(number.Text, new(number));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("syntax")]
    [InlineData("cancellation")]
    public void FailedParsingPreservesIndependentAcceptedNumericText(string kind)
    {
        using CancellationTokenSource cancellation = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 300000L };
        ResourceGovernor governor = new(limits, cancellation.Token);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null);
        string token = new string('0', 100000) + "123";
        NumberAlias alias = ParseNumericAlias(parser, governor, token);
        Collect();
        Assert.False(alias.Wrapper.IsAlive);
        long before = governor.Snapshot().LiveMemoryBytes;
        byte[] input = Encoding.ASCII.GetBytes(kind == "memory"
            ? "[ (" + new string('X', 1048576) + ") ]" : "[ (discard) @ ]");
        if (kind == "cancellation")
        {
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
            {
                using ParserMemoryScope parsedMemory3 = ParserMemoryScope.Create(governor);
                _ = parser.ParseValue(new PdfTokenizer(input), 0, parsedMemory3);
            });
        }
        else if (kind == "memory")
        {
            QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
            {
                using ParserMemoryScope parsedMemory4 = ParserMemoryScope.Create(governor);
                _ = parser.ParseValue(new PdfTokenizer(input), 0, parsedMemory4);
            });
            Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
        }
        else { Assert.Throws<QpdfParseException>(() =>
        {
            using ParserMemoryScope parsedMemory5 = ParserMemoryScope.Create(governor);
            _ = parser.ParseValue(new PdfTokenizer(input), 0, parsedMemory5);
        }); }
        Assert.Equal(token, alias.Text);
        Assert.True(alias.Backing.IsAlive);
        Assert.InRange(governor.Snapshot().LiveMemoryBytes, token.Length * 2L, before);
        GC.KeepAlive(alias); GC.KeepAlive(governor);
    }

    [Fact]
    public void FailedLargeParsingReleasesItsAttemptAndCanRepeat()
    {
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        PdfParser parser = new(limits, governor, new DiagnosticBag(governor), null);
        byte[] input = Encoding.ASCII.GetBytes("[ " + string.Concat(Enumerable.Repeat("(kept)", 4096)) + " ]");
        for (int attempt = 0; attempt < 10; attempt++)
        {
            QpdfLimitException error = Assert.Throws<QpdfLimitException>(() =>
            {
                using ParserMemoryScope parsedMemory6 = ParserMemoryScope.Create(governor);
                _ = parser.ParseValue(new PdfTokenizer(input), 0, parsedMemory6);
            });
            Assert.Equal(nameof(QpdfLimits.MaxMemoryBytes), error.LimitName);
            Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        using ParserMemoryScope parsedMemory7 = ParserMemoryScope.Create(governor);
        PdfByteString result = Assert.IsType<PdfByteString>(parser.ParseValue(new PdfTokenizer("(retry)"u8.ToArray()), 0, parsedMemory7));
        parsedMemory7.RetainGraph(); parsedMemory7.Dispose();
        Assert.Equal("retry"u8.ToArray(), result.Value);
        GC.KeepAlive(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedPrivateDocumentPhasesReuseAFiniteGovernor(bool broken)
    {
        byte[] input = TolerantObjectLoadingTests.Pdf(["(unread)"], broken);
        InMemoryQpdfHost host = Host(input);
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 32768L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        List<QpdfDocument> disposed = [];
        List<WeakReference[]> roots = [];
        for (int index = 0; index < 32; index++)
        {
            QpdfDocument document = await Open(host, governor, limits, broken);
            roots.Add(Roots(document));
            await document.DisposeAsync();
            disposed.Add(document);
            governor.CompletePrivateStorage();
            Collect();
            Assert.All(roots.SelectMany(group => group), owner => Assert.False(owner.IsAlive));
            Assert.Equal(0L, host.OpenLeaseCount);
            Assert.Equal(0L, governor.Snapshot().OpenHandles);
        }
        Assert.InRange(governor.Snapshot().PeakMemoryBytes, input.Length, limits.MaxMemoryBytes);
        GC.KeepAlive(disposed);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PdfByteString Child, WeakReference Parent, WeakReference Sibling) ChildOnly(QpdfDocument document)
    {
        PdfArray array = Assert.IsType<PdfArray>(document.GetObject(5));
        return (Assert.IsType<PdfByteString>(array[0]), new(array), new(array[1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChildAliasDoesNotRootItsContainersWhileItsGraphOwnerRemainsCharged(bool broken)
    {
        byte[] input = TolerantObjectLoadingTests.Pdf(["[ (kept) (" + new string('A', 1048576) + ") ]"], broken);
        QpdfDocument document = await Open(Host(input), new ResourceGovernor(QpdfLimits.Default, CancellationToken.None), QpdfLimits.Default, broken);
        (PdfByteString child, WeakReference parent, WeakReference sibling) = ChildOnly(document);
        await document.DisposeAsync();
        Collect();
        Assert.False(parent.IsAlive);
        Assert.False(sibling.IsAlive);
        Assert.Equal("kept"u8.ToArray(), child.Value);
        Assert.True(document.Counters.LiveMemoryBytes >= 1048576L);
        GC.KeepAlive(child); GC.KeepAlive(document);
    }

    private sealed class NumberAlias(string text, WeakReference wrapper)
    {
        internal string? Text = text;
        internal WeakReference Wrapper = wrapper;
        internal WeakReference Backing = new(text);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NumberAlias NumberText(QpdfDocument document)
    {
        PdfInteger number = Assert.IsType<PdfInteger>(document.GetObject(5));
        return new NumberAlias(number.Text, new(number));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckAndClearNumber(NumberAlias alias, QpdfDocument document, string token)
    {
        Assert.False(alias.Wrapper.IsAlive);
        Assert.Equal(token, alias.Text);
        Assert.True(document.Counters.LiveMemoryBytes >= token.Length * 2L);
        alias.Text = null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NumericTextRemainsUsableUntilTheGraphOwnerEnds(bool broken)
    {
        string token = new string('0', 100000) + "123";
        byte[] input = TolerantObjectLoadingTests.Pdf([token], broken);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfDocument document = await Open(Host(input), governor, QpdfLimits.Default, broken);
        NumberAlias alias = NumberText(document);
        await document.DisposeAsync();
        Collect();
        CheckAndClearNumber(alias, document, token);
        Collect();
        Assert.False(alias.Backing.IsAlive);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= token.Length * 2L);
        governor.CompletePrivateStorage();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(document);
    }

    private sealed class KeyAlias(IEnumerable<PdfName> keys, WeakReference wrapper, WeakReference backing)
    {
        internal IEnumerable<PdfName>? Keys = keys;
        internal WeakReference Wrapper = wrapper;
        internal WeakReference Backing = backing;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static KeyAlias KeyView(QpdfDocument document)
    {
        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(document.GetObject(5));
        return new KeyAlias(dictionary.Keys, new(dictionary), new(Field(dictionary, "_entries")));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long CheckAndClearKeys(KeyAlias alias, QpdfDocument document, bool populated)
    {
        Assert.False(alias.Wrapper.IsAlive);
        Assert.True(alias.Backing.IsAlive);
        Assert.NotNull(alias.Keys);
        Assert.Equal(populated ? 1 : 0, alias.Keys.Count());
        long before = document.Counters.LiveMemoryBytes;
        Assert.True(before >= (populated ? 1048576L : 104L));
        alias.Keys = null;
        return before;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task KeyViewsRemainUsableUntilTheGraphOwnerEnds(bool broken, bool populated)
    {
        string body = populated ? "<< /A (" + new string('K', 1048576) + ") >>" : "<< >>";
        byte[] input = TolerantObjectLoadingTests.Pdf([body], broken);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfDocument document = await Open(Host(input), governor, QpdfLimits.Default, broken);
        KeyAlias alias = KeyView(document);
        await document.DisposeAsync();
        Collect();
        long before = CheckAndClearKeys(alias, document, populated);
        Collect();
        Assert.False(alias.Backing.IsAlive);
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
        governor.CompletePrivateStorage();
        Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
        GC.KeepAlive(document);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (object Alias, WeakReference Stream, WeakReference Dictionary, WeakReference Data) StreamAlias(QpdfDocument document, string kind)
    {
        PdfStream stream = Assert.IsType<PdfStream>(document.GetObject(5));
        object alias = kind == "dictionary" ? stream.Dictionary : kind == "raw" ? (object)stream.RawMemory : stream.Data;
        return (alias, new(stream), new(stream.Dictionary), new(stream.AllocationCarrier));
    }

    [Theory]
    [InlineData("dictionary")]
    [InlineData("raw")]
    [InlineData("copy")]
    public async Task StreamDictionaryRawMemoryAndCallerCopyHaveIndependentOwners(string kind)
    {
        byte[] input = TolerantObjectLoadingTests.Pdf(["<< /Length 1048576 >>\nstream\n" + new string('P', 1048576) + "\nendstream"], false);
        QpdfDocument document = await Open(Host(input), new ResourceGovernor(QpdfLimits.Default, CancellationToken.None), QpdfLimits.Default, false);
        (object alias, WeakReference stream, WeakReference dictionary, WeakReference data) = StreamAlias(document, kind);
        await document.DisposeAsync();
        Collect();
        Assert.False(stream.IsAlive);
        Assert.Equal(kind == "dictionary", dictionary.IsAlive);
        Assert.Equal(kind == "raw", data.IsAlive);
        if (kind == "raw")
        {
            Assert.True(document.Counters.LiveMemoryBytes >= 1048576L);
            Assert.Equal((byte)'P', ((ReadOnlyMemory<byte>)alias).Span[100000]);
        }
        else
        {
            Assert.True(document.Counters.LiveMemoryBytes >= 1048576L);
            if (alias is byte[] copy) { Assert.Equal((byte)'P', copy[100000]); }
            else { Assert.Equal(1, Assert.IsType<PdfDictionary>(alias).Count); }
        }
        GC.KeepAlive(alias); GC.KeepAlive(document);
    }

    [Fact]
    public async Task CopiedScalarsAndKeysRemainIndependentOfClrContainerLiveness()
    {
        byte[] input = TolerantObjectLoadingTests.Pdf(["<< /Number 123 /Discard (" + new string('D', 1048576) + ") >>"], false);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        QpdfDocument document = await Open(Host(input), governor, QpdfLimits.Default, false);
        (PdfInteger number, PdfName key, WeakReference parent, WeakReference discarded) = CopySmallPart(document, governor);
        await document.DisposeAsync();
        Collect();
        Assert.False(parent.IsAlive);
        Assert.False(discarded.IsAlive);
        Assert.Equal("123", number.Text);
        Assert.Equal("Number"u8.ToArray(), key.RawBytes);
        Assert.True(governor.PrivateRetainedBytes >= 1048576L);
        GC.KeepAlive(document); GC.KeepAlive(number); GC.KeepAlive(key);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PdfInteger Number, PdfName Key, WeakReference Parent, WeakReference Discarded) CopySmallPart(QpdfDocument document, ResourceGovernor governor)
    {
        PdfDictionary source = Assert.IsType<PdfDictionary>(document.Engine.Load(5));
        PdfName key = source.Keys.Single(item => item.RawSpan.SequenceEqual("Number"u8));
        Assert.True(source.TryGet(key, out QpdfObject value));
        Assert.True(source.TryGet(PdfName.FromAscii("Discard"), out QpdfObject discarded));
        using ParserMemoryScope copierMemory = ParserMemoryScope.Create(governor);
        GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 20, copierMemory);
        PdfDictionary small = new(new Dictionary<PdfName, QpdfObject> { [key] = value });
        PdfDictionary copy = Assert.IsType<PdfDictionary>(copier.CopyValue(small, 0));
        Assert.True(copy.TryGet(key, out QpdfObject copied));
        Assert.Same(value, copied);
        Assert.Same(key, copy.Keys.Single());
        return (Assert.IsType<PdfInteger>(copied), copy.Keys.Single(), new(source), new(discarded));
    }
}

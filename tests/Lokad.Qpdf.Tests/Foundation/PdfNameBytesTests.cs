using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Foundation;

[Collection(nameof(NameTableCollection))]
public sealed class PdfNameBytesTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("415A21", "AZ!")]
    [InlineData("0020237F80FF", "#00#20#23#7F#80#FF")]
    [InlineData("2F3C3E5B5D2829", "/<>[]()")]
    public void DisplayPreservesPrintableAndEscapedByteSpelling(string hex, string expected)
    {
        PdfName name = new(Convert.FromHexString(hex));
        Assert.Equal(expected, name.DisplayText);
        Assert.Equal(Convert.FromHexString(hex), name.RawBytes);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("A#41", "AA")]
    [InlineData("#23#34#31", "#41")]
    [InlineData("#", "#")]
    [InlineData("#4", "#4")]
    [InlineData("#4Z#GG", "#4Z#GG")]
    [InlineData("#00#fF", "\u0000\u00ff")]
    public void BorrowedDecodingMatchesSinglePassLatin1Semantics(string raw, string expected)
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfNameBytes query = PdfNameBytes.Decode(Encoding.Latin1.GetBytes(raw), governor, CancellationToken.None);
        byte[] output = new byte[query.Length]; query.CopyTo(output, governor, CancellationToken.None);
        Assert.Equal(Encoding.Latin1.GetBytes(expected), output); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Fact]
    public void PublicNameConstructionStillCopiesBothInputAndReturnedBytes()
    {
        byte[] input = "ABC"u8.ToArray(); PdfName name = new(input); input[0] = 0;
        byte[] output = name.RawBytes; output[1] = 0; Assert.Equal("ABC"u8.ToArray(), name.RawBytes);
    }

    private static byte[] Long(bool escaped, byte tail)
    {
        byte[] raw = new byte[escaped ? 8192 * 3 : 8192];
        if (escaped) { for (int index = 0; index < raw.Length; index += 3) { raw[index] = (byte)'#'; raw[index + 1] = (byte)'6'; raw[index + 2] = (byte)'1'; } raw[^1] = tail == (byte)'a' ? (byte)'1' : (byte)'2'; }
        else { Array.Fill(raw, (byte)'a'); raw[^1] = tail; }
        return raw;
    }
    [Fact]
    public void DecodedQueriesShareTheRawCollisionEngineAndFundNoScratchBytes()
    {
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        PdfNameTable<byte> table = PdfNameTable<byte>.Create(memory, governor, 1L, CancellationToken.None);
        table.TryAdd(memory, governor, new PdfName(Long(false, (byte)'a')), 1, CancellationToken.None);
        table.TryAdd(memory, governor, new PdfName(Long(false, (byte)'b')), 2, CancellationToken.None);
        table.Freeze(memory, governor, CancellationToken.None); long live = governor.Snapshot().LiveMemoryBytes;
        byte[] raw = Long(true, (byte)'a'); ResourceGovernor lookup = new(QpdfLimits.Default, CancellationToken.None);
        PdfNameBytes query = PdfNameBytes.Decode(raw, lookup, CancellationToken.None);
        Assert.True(table.TryGetValue(query, lookup, CancellationToken.None, out byte value)); Assert.Equal(1, value);
        Assert.True(lookup.Snapshot().TotalWork > 2L * 8192L); Assert.Equal(0L, lookup.Snapshot().LiveMemoryBytes); Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
        ResourceGovernor limited = new(QpdfLimits.Default with { MaxWork = 4096L }, CancellationToken.None);
        Assert.Throws<QpdfLimitException>(() => { PdfNameBytes borrowed = PdfNameBytes.Decode(raw, governor, CancellationToken.None); table.TryGetValue(borrowed, limited, CancellationToken.None, out _); });
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodedCollisionCancellationAfterCompletedBytesPreservesFrozenStorage(bool governorToken)
    {
        ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None); using ParserMemoryScope memory = ParserMemoryScope.Create(owner);
        PdfNameTable<byte> table = PdfNameTable<byte>.Create(memory, owner, 1L, CancellationToken.None);
        table.TryAdd(memory, owner, new PdfName(Long(false, (byte)'a')), 1, CancellationToken.None); table.Freeze(memory, owner, CancellationToken.None);
        long live = owner.Snapshot().LiveMemoryBytes; byte[] raw = Long(true, (byte)'b');
        using CancellationTokenSource source = new(); ResourceGovernor lookup = new(QpdfLimits.Default, governorToken ? source.Token : CancellationToken.None);
        using ManualResetEventSlim reached = new(), resume = new();
        Task worker = Task.Factory.StartNew(() =>
        {
            PdfNameBytes query = PdfNameBytes.Decode(raw, lookup, governorToken ? CancellationToken.None : source.Token);
            table.TryGetObserved(query, lookup, governorToken ? CancellationToken.None : source.Token,
                () => { reached.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException(); } }, out _);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10))); Assert.True(lookup.Snapshot().TotalWork >= 8192L + 256L);
            source.Cancel(); resume.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(live, owner.Snapshot().LiveMemoryBytes); Assert.Equal(0L, lookup.Snapshot().LiveMemoryBytes);
        }
        finally { source.Cancel(); resume.Set(); try { await worker.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
        Assert.True(table.TryGetValue(Long(false, (byte)'a').AsSpan(), owner, CancellationToken.None, out byte value)); Assert.Equal(1, value);
    }
}

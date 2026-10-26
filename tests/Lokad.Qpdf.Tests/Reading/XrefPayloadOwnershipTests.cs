using System.IO.Compression;
using System.Reflection;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.TestSupport;

namespace Lokad.Qpdf.Tests.Reading;

public sealed class XrefPayloadOwnershipTests
{
    private static readonly MethodInfo Decode = typeof(XRefTable).GetMethod("DecodeXRefData", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing live xref decoder.");

    private static (byte[] Plain, byte[] Encoded) Payload()
    {
        byte[] plain = new byte[131072]; new Random(23).NextBytes(plain);
        using MemoryStream buffer = new();
        using (ZLibStream encoder = new(buffer, CompressionLevel.SmallestSize, true)) { encoder.Write(plain); }
        return (plain, buffer.ToArray());
    }

    private static PdfStream Stream(byte[] encoded, bool slice, ParserMemoryScope input)
    {
        PdfDictionary dictionary = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode") });
        byte[] backing = slice ? new byte[encoded.Length + 64] : encoded;
        if (slice) { encoded.CopyTo(backing, 32); }
        input.Reserve(backing.LongLength + 96);
        return slice ? PdfStream.FromImmutableSlice(dictionary, backing.AsMemory(32, encoded.Length)) : PdfStream.FromOwnedData(dictionary, backing);
    }

    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "memory")]
    [InlineData(true, "memory")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public void DecoderBorrowsOwnedInputAndFundsOnlyRequiredScratch(bool slice, string outcome)
    {
        var payload = Payload(); using CancellationTokenSource canceled = new();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = outcome == "memory" ? 300000 : QpdfLimits.Default.MaxMemoryBytes };
        ResourceGovernor governor = new(limits, canceled.Token);
        using ParserMemoryScope input = ParserMemoryScope.Create(governor);
        PdfStream stream = Stream(payload.Encoded, slice, input);
        long before = governor.Snapshot().LiveMemoryBytes;
        using (ParserMemoryScope scratch = ParserMemoryScope.Create(governor))
        {
            object[] arguments = [stream, governor, limits, 0L, 6, scratch];
            if (outcome == "cancel") { canceled.Cancel(); }
            if (outcome == "success")
            {
                ReadOnlyMemory<byte> decoded = Assert.IsType<ReadOnlyMemory<byte>>(Decode.Invoke(null, arguments));
                Assert.Equal(payload.Plain, decoded.ToArray());
                Assert.Equal(payload.Plain.LongLength + 32 + (slice ? payload.Encoded.LongLength + 32 : 0), scratch.ReservedBytes);
                scratch.Dispose(); Assert.Equal(payload.Plain, decoded.ToArray());
            }
            else
            {
                Exception? failure = Assert.Throws<TargetInvocationException>(() => Decode.Invoke(null, arguments)).InnerException;
                if (outcome == "cancel") { Assert.IsAssignableFrom<OperationCanceledException>(failure); }
                else { Assert.IsType<QpdfLimitException>(failure); }
                scratch.Dispose();
                Assert.Equal(before, governor.Snapshot().LiveMemoryBytes);
            }
            Assert.Equal(payload.Encoded, stream.Data);
        }
        input.Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedRefusalRetiresDecodeScratchAndKeepsInput(bool slice)
    {
        var payload = Payload(); RecordingMemoryBudget budget = new(300000);
        using InMemoryQpdfHost host = new(QpdfLimits.Default, null, budget);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None, host);
        using (ParserMemoryScope input = ParserMemoryScope.Create(governor))
        {
            PdfStream stream = Stream(payload.Encoded, slice, input);
            long before = governor.Snapshot().LiveMemoryBytes;
            using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
            Exception? failure = Assert.Throws<TargetInvocationException>(() => Decode.Invoke(null, [stream, governor, QpdfLimits.Default, 0L, 6, scratch])).InnerException;
            Assert.IsType<QpdfMemoryAdmissionException>(failure);
            scratch.Dispose();
            Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(payload.Encoded, stream.Data);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0L, budget.ReservedBytes);
    }

    [Fact]
    public async Task FilteredTableAndPublicValuesSurviveInputClosure()
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), FutureXrefTests.Fixture(1, false, false, true), QpdfInputOwnership.Transfer);
        PdfDictionary trailer; QpdfObject value;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None))
        { trailer = document.Engine.Trailer; value = document.GetObject(5); }
        host.Dispose(); Assert.Equal(0L, host.OpenLeaseCount);
        Assert.True(trailer.TryGet(PdfName.FromAscii("Root"), out QpdfObject root)); Assert.IsType<PdfReference>(root);
        Assert.Equal("stale-value", System.Text.Encoding.ASCII.GetString(Assert.IsType<PdfByteString>(value).ValueMemory.Span));
    }
}

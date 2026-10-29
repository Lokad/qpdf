using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class OriginalIdBoundsTests
{
    private static readonly Func<DocumentEngine, byte[]?> Read =
        (typeof(Qpdf).GetMethod("TryGetOriginalId", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException()).CreateDelegate<Func<DocumentEngine, byte[]?>>();

    private static DocumentEngine Engine(QpdfObject value, ResourceGovernor governor)
    {
        PdfDictionary trailer = new(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("ID")] = value });
        using ParserMemoryScope fixtureStorage = ParserMemoryScope.Create(governor);
        Dictionary<int, XrefEntry> fixtureEntries = ParserContainers.CreateDictionary<int, XrefEntry>(fixtureStorage);
        using ParserMemoryScope fixtureValues = ParserMemoryScope.Create(governor);
        List<PdfDictionary> fixtureTrailers = ParserContainers.CreateList<PdfDictionary>(fixtureStorage, 1, IntPtr.Size);
        fixtureTrailers.Add(trailer);
        XRefTable fixtureTable = XRefTable.CreateOwned(0L, fixtureEntries, trailer, fixtureTrailers,
            governor, fixtureStorage, fixtureValues, false);
        DocumentEngine fixtureEngine;
        try
        {
            fixtureStorage.Reserve(32L);
            fixtureEngine = DocumentEngine.CreateOwned(ReadOnlyMemory<byte>.Empty, fixtureTable, QpdfLimits.Default,
                governor, new(governor), QpdfRecoveryMode.Strict, 32L, valuesEscape: true, hostInput: false, snapshotBorrow: null);
            fixtureStorage.Retain();
        }
        catch { fixtureTable.Dispose(); throw; }
        return fixtureEngine;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(16777216)]
    public void CheckBorrowedLengthBeforeCopying(int length)
    {
        byte[] bytes = new byte[length];
        for (int index = 0; index < bytes.Length; index++) { bytes[index] = (byte)index; }
        PdfByteString id = new(bytes, true);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096 }, CancellationToken.None);
        using DocumentEngine engine = Engine(new PdfArray([id, id]), governor);
        _ = Read(engine);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        byte[]? result = Read(engine);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.InRange(allocated, 0L, 4096L);
        if (length is > 0 and <= 256)
        {
            Assert.Equal(bytes, result);
            Assert.NotSame(Read(engine), result);
            Assert.NotNull(result); result[0] ^= 255;
            Assert.Equal(bytes, Read(engine));
        }
        else { Assert.Null(result); }
    }

    [Fact]
    public void MalformedIdShapesAreIgnored()
    {
        foreach (QpdfObject value in new QpdfObject[] { PdfNull.Instance, new PdfArray([]), new PdfArray([PdfNull.Instance]) })
        {
            ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
            using DocumentEngine engine = Engine(value, governor);
            Assert.Null(Read(engine));
        }
    }
}

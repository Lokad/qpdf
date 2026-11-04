using System.Reflection;
using System.Runtime.InteropServices;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class RawJsonPayloadTests
{
    [Theory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("generalized", false)]
    [InlineData("generalized", true)]
    public async Task OpaquePayloadBorrowsItsExactSegmentWithoutAnIntermediateArray(string level, bool slice)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("input"), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-1page.pdf")), QpdfInputOwnership.Transfer);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("input"), CancellationToken.None);
        int offset = slice ? 7 : 0, length = 1048576;
        byte[] backing = new byte[length + (slice ? 18 : 0)]; new Random(42).NextBytes(backing);
        PdfStream stream = PdfStream.FromImmutableSlice(new(new Dictionary<PdfName, QpdfObject>()), backing.AsMemory(offset, length));
        MethodInfo method = typeof(DocumentJson).GetMethod("ResolveStreamPayload", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing live JSON payload resolver.");
        var resolve = method.CreateDelegate<Func<DocumentEngine, PdfStream, string, QpdfLimits, ResourceGovernor, ParserMemoryScope, (ReadOnlyMemory<byte>, bool)>>();
        QpdfLimits limits = QpdfLimits.Default with { MaxMemoryBytes = 4096L };
        ResourceGovernor governor = new(limits, CancellationToken.None);
        using (ParserMemoryScope memory = ParserMemoryScope.Create(governor))
        {
            (ReadOnlyMemory<byte> data, bool decoded) = resolve(document.Engine, stream, level, limits, governor, memory);
            Assert.False(decoded); Assert.Equal(length, data.Length);
            Assert.True(MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment));
            Assert.Same(backing, segment.Array); Assert.Equal(offset, segment.Offset);
            Assert.Equal(0L, memory.ReservedBytes);
        }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

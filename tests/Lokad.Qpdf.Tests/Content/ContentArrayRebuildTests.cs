using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Tests.Filters;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Content;

[Collection(nameof(NameTableCollection))]
public sealed class ContentArrayRebuildTests
{
    private static readonly MethodInfo Method = typeof(ContentTransforms).GetMethod("ReplaceContentEntries", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException();
    private static PdfDictionary Replace(PdfDictionary page, Prepared prepared, ResourceGovernor governor)
    {
        // Only the borrowed mapping's private dictionary binding differs.
        object mapping = Method.GetParameters()[1].ParameterType == typeof(Dictionary<int, PdfStream>) ? prepared.Legacy : prepared.Mapping;
        try { return Method.Invoke(null, [page, mapping, governor, CancellationToken.None]) as PdfDictionary ?? throw new InvalidOperationException(); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private sealed class Prepared : IDisposable
    {
        private readonly ParserMemoryScope _memory;
        internal readonly PdfStream Original = new(DecoderOwnershipFixtures.Dictionary(), new byte[2 * 1024 * 1024]);
        internal readonly PdfStream Replacement = new(DecoderOwnershipFixtures.Dictionary(), new byte[2 * 1024 * 1024]);
        internal readonly Dictionary<int, PdfStream> Legacy = new();
        internal readonly GovernedIntDictionary<PdfStream> Mapping;
        internal Prepared(bool fill)
        {
            ResourceGovernor construction = new(QpdfLimits.Default, CancellationToken.None);
            _memory = ParserMemoryScope.Create(construction); Mapping = GovernedIntDictionary<PdfStream>.Create(_memory, construction, IntPtr.Size, CancellationToken.None);
            if (fill) { Legacy.Add(3, Replacement); Mapping.Assign(_memory, 3, Replacement); }
        }
        public void Dispose() { _memory.Dispose(); }
    }
    private static QpdfObject Contents(PdfDictionary page)
        => page.TryGet(PdfName.FromAscii("Contents"), out QpdfObject value) ? value : throw new InvalidOperationException();

    [Theory]
    [InlineData("array")]
    [InlineData("actual-null")]
    [InlineData("empty")]
    [InlineData("single")]
    [InlineData("reference")]
    [InlineData("missing")]
    public void RebuildingPreservesBorrowedValuesAndOriginalShapes(string shape)
    {
        using Prepared prepared = new(true); QpdfObject[] items = [prepared.Original, new PdfReference(7, 11), PdfNull.Instance, prepared.Original];
        if (shape == "actual-null") { items.SetValue(null, 1); }
        QpdfObject value = shape switch { "single" => prepared.Original, "reference" => new PdfReference(7, 11), "empty" => new PdfArray([]), _ => new PdfArray(items) };
        PdfDictionary page = shape == "missing" ? DecoderOwnershipFixtures.Dictionary() : DecoderOwnershipFixtures.Dictionary(("Contents", value), ("Keep", prepared.Original));
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        PdfDictionary result = Replace(page, prepared, governor);
        if (shape is "missing" or "reference") { Assert.Same(page, result); governor.CompletePrivateStorage(); Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes); }
        else if (shape == "single") { Assert.Same(prepared.Replacement, Contents(result)); Assert.Same(prepared.Original, Contents(page)); }
        else
        {
            PdfArray rebuilt = Assert.IsType<PdfArray>(Contents(result)); Assert.Equal(shape == "empty" ? 0 : 4, rebuilt.Count);
            if (rebuilt.Count > 0)
            {
                Assert.Same(prepared.Original, rebuilt[0]); Assert.Same(items[1], rebuilt[1]); Assert.Same(PdfNull.Instance, rebuilt[2]); Assert.Same(prepared.Replacement, rebuilt[3]);
                Assert.Same(prepared.Original, Assert.IsType<PdfArray>(Contents(page))[3]);
            }
        }
        Assert.Equal(2 * 1024 * 1024, prepared.Replacement.RawMemory.Length); GC.KeepAlive(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeArrayRejectsMemoryOrWorkBeforePublication(bool work)
    {
        using Prepared prepared = new(false); PdfArray array = new(Enumerable.Repeat<QpdfObject>(prepared.Original, 1024).ToArray());
        PdfDictionary page = DecoderOwnershipFixtures.Dictionary(("Contents", array));
        ResourceGovernor governor = new(work ? QpdfLimits.Default with { MaxWork = 200L } : QpdfLimits.Default with { MaxMemoryBytes = 4096L }, CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(23L); long live = governor.Snapshot().LiveMemoryBytes;
        QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => Replace(page, prepared, governor));
        Assert.Equal(work ? nameof(QpdfLimits.MaxWork) : nameof(QpdfLimits.MaxMemoryBytes), failure.LimitName);
        Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Same(array, Contents(page)); Assert.Equal(1024, array.Count);
    }

    [Fact]
    public void EscapedArrayAndBackingStayFundedAfterThePageDies()
    {
        using Prepared prepared = new(true); ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        PdfArray? array = EscapeArray(prepared, governor); Collect(governor);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 88L); Assert.Same(prepared.Replacement, array[3]);
        object? backing = array.AllocationCarrier; array = null; Collect(governor);
        Assert.True(governor.Snapshot().LiveMemoryBytes >= 64L); GC.KeepAlive(backing);
        backing = null; Collect(governor); governor.CompletePrivateStorage(); Assert.Equal(governor.CallerMetadataBytes + governor.CallerStorageBytes, governor.Snapshot().LiveMemoryBytes);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PdfArray EscapeArray(Prepared prepared, ResourceGovernor governor)
    {
        PdfDictionary page = DecoderOwnershipFixtures.Dictionary(("Contents", new PdfArray([prepared.Original, PdfNull.Instance, prepared.Original, prepared.Original])));
        return Assert.IsType<PdfArray>(Contents(Replace(page, prepared, governor)));
    }
    private static void Collect(ResourceGovernor governor)
    { for (int round = 0; round < 4; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); governor.ReserveMemory(0L); } }
}

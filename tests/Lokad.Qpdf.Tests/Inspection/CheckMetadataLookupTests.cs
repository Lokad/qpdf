using System.Reflection;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Inspection;

public sealed class CheckMetadataLookupTests
{
    private delegate bool Lookup(PdfDictionary dictionary, ReadOnlySpan<byte> key, IIndirectResolver resolver,
        ResourceGovernor governor, CancellationToken token, out PdfArray? array);

    private static Lookup Bind()
    {
        MethodInfo method = typeof(DocumentCheck).GetMethod("TryResolveCheckArray", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing checker array lookup.");
        return method.CreateDelegate<Lookup>();
    }
    private static readonly Lookup Read = Bind();

    private sealed class Resolver(QpdfObject? value, bool found, Action? callback) : IIndirectResolver
    {
        internal int Calls;
        internal PdfReference? Last;
        public bool TryResolve(PdfReference reference, out QpdfObject? resolved)
        { Calls++; Last = reference; callback?.Invoke(); resolved = value; return found; }
    }
    private static PdfDictionary Dictionary(ReadOnlySpan<byte> key, QpdfObject value)
        => new(new Dictionary<PdfName, QpdfObject> { [new PdfName(key.ToArray())] = value });

    [Theory]
    [InlineData("Fields", "direct")]
    [InlineData("Fields", "indirect")]
    [InlineData("Fields", "null")]
    [InlineData("Fields", "missing")]
    [InlineData("Fields", "wrong")]
    [InlineData("Fields", "unresolved")]
    [InlineData("Fields", "wrong-indirect")]
    [InlineData("Kids", "direct")]
    [InlineData("Kids", "indirect")]
    [InlineData("Kids", "null")]
    [InlineData("Kids", "missing")]
    [InlineData("Kids", "wrong")]
    [InlineData("Kids", "unresolved")]
    [InlineData("Kids", "wrong-indirect")]
    public void RegistrationArraysPreserveBorrowingNullShapeAndOneLevelResolution(string name, string shape)
    {
        byte[] key = System.Text.Encoding.ASCII.GetBytes(name); PdfArray value = new([new PdfByteString([65], false)]);
        PdfReference reference = new(42, 7);
        QpdfObject raw = shape == "direct" ? value : shape is "indirect" or "unresolved" or "wrong-indirect" ? reference
            : shape == "wrong" ? PdfBoolean.True : PdfNull.Instance;
        PdfDictionary dictionary = shape == "missing" ? new(new Dictionary<PdfName, QpdfObject>()) : Dictionary(key, raw);
        Resolver resolver = new(shape == "wrong-indirect" ? PdfBoolean.True : value, shape != "unresolved", null);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        bool found = Read(dictionary, key, resolver, governor, CancellationToken.None, out PdfArray? result);
        Assert.Equal(shape is "direct" or "indirect", found);
        if (found) { Assert.Same(value, result); } else { Assert.Null(result); }
        Assert.Equal(shape is "indirect" or "unresolved" or "wrong-indirect" ? 1 : 0, resolver.Calls);
        if (resolver.Calls != 0) { Assert.Same(reference, resolver.Last); }
        Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData("Fields", false)]
    [InlineData("Fields", true)]
    [InlineData("Kids", false)]
    [InlineData("Kids", true)]
    public void EitherPreCanceledContextRejectsBeforeLookupAndResolution(string name, bool governorToken)
    {
        byte[] key = System.Text.Encoding.ASCII.GetBytes(name); PdfDictionary dictionary = Dictionary(key, new PdfReference(42, 7));
        Resolver resolver = new(new PdfArray([]), true, null); using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        Assert.ThrowsAny<OperationCanceledException>(() => Read(dictionary, key, resolver, governor,
            governorToken ? CancellationToken.None : cancellation.Token, out _));
        Assert.Equal(0, resolver.Calls); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolverCallbackCancellationPreventsPublicationAndPreservesPriorAdmission(bool governorToken)
    {
        using CancellationTokenSource cancellation = new(); PdfArray borrowed = new([PdfNull.Instance]);
        Resolver resolver = new(borrowed, true, cancellation.Cancel); PdfDictionary dictionary = Dictionary("Kids"u8, new PdfReference(42, 7));
        ResourceGovernor governor = new(QpdfLimits.Default, governorToken ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope prior = ParserMemoryScope.Create(governor); prior.Reserve(97L); long live = governor.Snapshot().LiveMemoryBytes;
        Assert.ThrowsAny<OperationCanceledException>(() => Read(dictionary, "Kids"u8, resolver, governor,
            governorToken ? CancellationToken.None : cancellation.Token, out _));
        Assert.Equal(1, resolver.Calls);         Assert.Equal(live, governor.Snapshot().LiveMemoryBytes); Assert.Equal(97L, prior.ReservedBytes); Assert.Same(PdfNull.Instance, borrowed[0]);
    }

    [Theory]
    [InlineData("Fields")]
    [InlineData("Kids")]
    public void WarmedDirectRegistrationLookupAllocatesNoQueryOrArrayCopy(string name)
    {
        byte[] key = System.Text.Encoding.ASCII.GetBytes(name); PdfArray borrowed = new([new PdfByteString(new byte[2 * 1024 * 1024], false)]);
        PdfDictionary dictionary = Dictionary(key, borrowed); Resolver resolver = new(null, false, null);
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 128L }, CancellationToken.None);
        for (int index = 0; index < 32; index++) { Read(dictionary, key, resolver, governor, CancellationToken.None, out _); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 128; index++) { Read(dictionary, key, resolver, governor, CancellationToken.None, out _); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); Assert.Equal(0, resolver.Calls); GC.KeepAlive(borrowed);
    }
}

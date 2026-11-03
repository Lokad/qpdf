using System.Reflection;
using System.Runtime.CompilerServices;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Tests.Foundation;

namespace Lokad.Qpdf.Tests.Inspection;

// New owner/primitive types are excluded from the preceding-DLL lane; public
// and private-context-compatible controls live in CheckMetadataLookupTests.
[Collection(nameof(NameTableCollection))]
public sealed class CheckMetadataOwnerTests
{
    private static object Field(object value, string name)
        => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value)
            ?? throw new InvalidOperationException("Missing owned checker field: " + name);

    private static object CreateForm(ResourceGovernor governor)
    {
        Type type = typeof(DocumentCheck).GetNestedType("FormCheckState", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing form owner.");
        return type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, [governor])
            ?? throw new InvalidOperationException("Missing form owner factory.");
    }

    [Fact]
    public void InitialFormIndexesAdmitTheirFullEmptyGeometry()
    {
        ResourceGovernor governor = new(QpdfLimits.Default with { MaxMemoryBytes = 480L }, CancellationToken.None);
        object owner = CreateForm(governor); Assert.Equal(480L, governor.Snapshot().LiveMemoryBytes);
        ((IDisposable)owner).Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); ((IDisposable)owner).Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (object Owner, WeakReference[] Metadata) CompletedForm()
    {
        using CancellationTokenSource source = new(); ResourceGovernor governor = new(QpdfLimits.Default, source.Token); object owner = CreateForm(governor);
        MethodInfo visit = owner.GetType().GetMethod("Visit", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing form visit.");
        for (int index = 0; index < 17; index++) { Assert.True(visit.Invoke(owner, [index, CancellationToken.None]) is true); }
        object scope = Field(owner, "_memory"), visited = Field(owner, "_visited"), widgets = Field(owner, "_widgets"), indexOwner = Field(visited, "_index");
        WeakReference[] metadata = [new(governor), new(source), new(scope), new(visited), new(widgets), new(indexOwner), new(Field(indexOwner, "_entries")), new(Field(indexOwner, "_buckets"))];
        ((IDisposable)owner).Dispose(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes); return (owner, metadata);
    }

    [Fact]
    public void RootedDisposedFormOwnerRetainsNoIndexesScopeOrExecutionState()
    { var alias = CompletedForm(); Collect(); Assert.All(alias.Metadata, reference => Assert.False(reference.IsAlive)); GC.KeepAlive(alias); }

    private static void Collect()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private sealed class Resolver(QpdfObject? value, bool found, Action? callback) : IIndirectResolver
    {
        internal int Calls;
        public bool TryResolve(PdfReference reference, out QpdfObject? result) { Calls++; callback?.Invoke(); result = value; return found; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SharedResolutionPreservesBooleanAndNullResultContracts(bool found, bool hasValue)
    {
        QpdfObject? borrowed = hasValue ? new PdfArray([]) : null; Resolver resolver = new(borrowed, found, null);
        ResourceGovernor governor = new(QpdfLimits.Default, CancellationToken.None);
        Assert.Equal(found, PdfObjectLookup.TryResolve(new PdfReference(42, 7), resolver, governor, CancellationToken.None, out QpdfObject? result));
        Assert.Same(borrowed, result); Assert.Equal(1, resolver.Calls); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
    }
}

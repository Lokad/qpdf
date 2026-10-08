using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Tests.Filters;

public sealed class OwnedPredictorMetadataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedBackingChargesActualEnumerationAndPreservesBorrowedStorage(bool limited)
    {
        PdfDictionary prepared = PredictorMetadataTests.UnknownEntries();
        ResourceGovernor owner = new(QpdfLimits.Default, CancellationToken.None);
        using ParserMemoryScope storage = ParserMemoryScope.Create(owner);
        PdfNameTable<QpdfObject> names = PdfNameTable<QpdfObject>.Create(storage, owner, 8L, CancellationToken.None);
        names.EnsureCapacity(storage, owner, prepared.Count, CancellationToken.None);
        using (var entries = prepared.GetEntryEnumerator())
        { while (entries.MoveNext()) { Assert.True(names.TryAdd(storage, owner, entries.Current.Key, entries.Current.Value, CancellationToken.None)); } }
        names.Freeze(storage, owner, CancellationToken.None); PdfDictionary dictionary = PdfDictionary.FromOwnedEntries(names);
        long before = owner.Snapshot().LiveMemoryBytes;
        ResourceGovernor reader = new(QpdfLimits.Default with { MaxWork = limited ? 64L : 1_000_000L }, CancellationToken.None);
        using ParserMemoryScope metadata = ParserMemoryScope.Create(reader);
        if (limited)
        {
            QpdfLimitException failure = Assert.Throws<QpdfLimitException>(() => PredictorParameters.FromDictionary(dictionary, FlateFilter.FilterName, null,
                metadata, reader, CancellationToken.None));
            Assert.Equal(nameof(QpdfLimits.MaxWork), failure.LimitName); Assert.Equal(0L, metadata.ReservedBytes);
        }
        else
        {
            Assert.Equal(new PredictorParameters(1, 1, 1, 8), PredictorParameters.FromDictionary(dictionary, FlateFilter.FilterName, null,
                metadata, reader, CancellationToken.None));
            Assert.Equal(2562L, reader.Snapshot().TotalWork); Assert.Equal(32L, metadata.ReservedBytes);
        }
        Assert.Equal(before, owner.Snapshot().LiveMemoryBytes); Assert.Equal(512, dictionary.Count);
        foreach (PdfName key in prepared.Keys) { Assert.True(dictionary.TryGet(key, out QpdfObject value)); Assert.Same(PdfNull.Instance, value); }
    }
}

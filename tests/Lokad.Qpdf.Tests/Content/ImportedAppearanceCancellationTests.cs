using System.Collections;
using Lokad.Qpdf.Content;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Tests.Content;

public sealed class ImportedAppearanceCancellationTests
{
    private sealed class CancelingAnnotations(PdfReference widget, Action cancel) : IReadOnlyList<PdfReference>
    {
        public int Count => 65536;
        public int Visited { get; private set; }
        public PdfReference this[int index] => index >= 0 && index < Count ? widget : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<PdfReference> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                if (++Visited == 3) { cancel(); }
                yield return widget;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothCancellationRoutesInterruptTheImportedSurvivorPass(bool caller)
    {
        using InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("source"), OverlayGenerationContextTests.Source(false, true, 0, 1, true), QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("source"), CancellationToken.None))
        {
            using CancellationTokenSource cancellation = new();
            ResourceGovernor governor = new(QpdfLimits.Default, caller ? CancellationToken.None : cancellation.Token);
            using ParserMemoryScope memory = ParserMemoryScope.Create(governor);
            GraphCopier copier = GraphCopier.CreateOwned(document.Engine, governor, QpdfLimits.Default, 10, memory);
            CopiedForm form = Assert.IsType<CopiedForm>(AcroFormCopy.TryCopySourceForm(document.Engine, copier,
                document.PageReferences, true, QpdfLimits.Default, governor, memory, CancellationToken.None, false));
            HashSet<int> generated = ParserContainers.CreateSet<int>(memory);
            List<AcroFormCopy.FieldImport> imports = [new(copier, form, Assert.IsType<PdfByteString>(document.GetObject(8)), 0, true)];
            CancelingAnnotations annotations = new(Assert.Single(form.Fields), cancellation.Cancel);
            long baseline = governor.Snapshot().LiveMemoryBytes; int count = copier.CopiedObjects.Count; int allocator = copier.NextId;
            Assert.ThrowsAny<OperationCanceledException>(() => GenerateAppearances.ApplyImported(copier, annotations,
                imports, 0, QpdfLimits.Default, governor, new(governor), caller ? cancellation.Token : CancellationToken.None,
                generated, memory, ref allocator));
            Assert.Equal(3, annotations.Visited); Assert.Empty(generated);
            Assert.Equal(count, copier.CopiedObjects.Count); Assert.Equal(baseline, governor.Snapshot().LiveMemoryBytes);
            memory.Dispose(); governor.CompletePrivateStorage(); Assert.Equal(0L, governor.Snapshot().LiveMemoryBytes);
        }
        Assert.Equal(0, host.OpenLeaseCount);
    }
}

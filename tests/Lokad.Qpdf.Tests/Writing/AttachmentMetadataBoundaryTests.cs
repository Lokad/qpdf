using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Writing;

public sealed class AttachmentMetadataBoundaryTests
{
    private sealed class Resolver(Action onResolve) : IIndirectResolver
    {
        public bool TryResolve(PdfReference reference, out QpdfObject? value)
        { onResolve(); value = PdfName.FromAscii("text/plain"); return true; }
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("governor")]
    [InlineData("refusal")]
    [InlineData("oom")]
    public void ResolutionChecksCancellationAndPreservesExceptionalOutcomes(string outcome)
    {
        using CancellationTokenSource cancellation = new();
        ResourceGovernor governor = new(QpdfLimits.Default, outcome == "governor" ? cancellation.Token : CancellationToken.None);
        using ParserMemoryScope memory = ParserMemoryScope.Create(governor); memory.Reserve(71L);
        PdfStream stream = new(new PdfDictionary(new Dictionary<PdfName, QpdfObject> { [PdfName.FromAscii("Subtype")] = new PdfReference(6, 0) }), []);
        Exception expected = outcome == "refusal" ? new QpdfMemoryAdmissionException(65536L) : new OutOfMemoryException("injected");
        Resolver resolver = new(() => { if (outcome is "caller" or "governor") { cancellation.Cancel(); } else { throw expected; } });
        long before = governor.Snapshot().LiveMemoryBytes;
        Exception? failure = Record.Exception(() => EmbeddedFiles.ReadStreamMetadata(resolver, stream, null, memory, governor,
            outcome == "governor" ? CancellationToken.None : cancellation.Token));
        if (outcome is "caller" or "governor") { Assert.IsAssignableFrom<OperationCanceledException>(failure); }
        else { Assert.Same(expected, failure); }
        Assert.Equal(before, governor.Snapshot().LiveMemoryBytes); Assert.Equal(71L, memory.ReservedBytes);
    }
}

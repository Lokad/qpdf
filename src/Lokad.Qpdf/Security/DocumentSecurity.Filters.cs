using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Security;

internal sealed partial class DocumentSecurity
{
    private PdfDictionary ReadStreamCipher(PdfDictionary header, IIndirectResolver resolver, bool embedded,
        OwnedAllocationBatch graph, ref PdfCipher cipher)
    {
        PdfName? name = StreamCryptFilter.ReadName(header, resolver, _governor, graph.Token, out int count);
        if (name is null) { return header; }
        cipher = Select(_filters, name, embedded);
        return StreamCryptFilter.Remove(header, count, resolver, graph);
    }

    private QpdfObject Resolve(QpdfObject raw, IIndirectResolver resolver)
        => StreamCryptFilter.Resolve(raw, resolver, _governor, _governor.CancellationToken);
}

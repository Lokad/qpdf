using System.Text;
using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;

internal static class DocumentReferenceChecks
{
    internal static async Task RunAsync()
    {
        using InMemoryQpdfHost host = new();
        QpdfPath input = QpdfPath.From("references");
        host.AddInput(input, Fixture("reference-pairs-1page.pdf"), QpdfInputOwnership.Transfer);
        PdfByteString current; PdfArray pairs; PdfStream stream;
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, input, CancellationToken.None))
        {
            PdfDictionary catalog = (PdfDictionary)document.GetObject(1);
            PdfReference Edge(string key) => catalog.TryGet(new PdfName(Encoding.ASCII.GetBytes(key)), out QpdfObject value)
                ? (PdfReference)value : throw new InvalidOperationException("Missing reference edge.");
            Check(document.GetObject(Edge("Stale")) is PdfNull, "stale pair before cache");
            Check(document.GetObject(Edge("Freed")) is PdfNull && document.GetObject(Edge("Missing")) is PdfNull, "free/missing pairs");
            current = (PdfByteString)document.GetObject(Edge("Live"));
            Check(ReferenceEquals(current, document.GetObject(4)) && document.GetObject(Edge("Stale")) is PdfNull, "number lookup versus exact cached pair");
            pairs = (PdfArray)document.GetObject(Edge("Pairs"));
            stream = (PdfStream)document.GetObject((PdfReference)pairs[4]);
        }
        Check(Encoding.ASCII.GetString(current.Value) == "current" && pairs.Count == 5 && stream.Data.AsSpan().SequenceEqual("q Q"u8), "escaped public values");
        host.AddInput(input, Fixture("objstm-1page.pdf"), QpdfInputOwnership.Transfer);
        await using (QpdfDocument document = await QpdfDocument.OpenAsync(host, input, CancellationToken.None))
        {
            Check(document.GetObject(new PdfReference(6, 1)) is PdfNull, "stale compressed generation");
            Check(document.GetObject(new PdfReference(6, 0)) is PdfDictionary, "compressed generation zero");
            Check(document.GetObject(new PdfReference(6, 1)) is PdfNull, "cached compressed generation");
        }
        Check(host.OpenLeaseCount == 0L, "reference lookup leases");
    }

    private static byte[] Fixture(string name)
    {
        using Stream source = typeof(DocumentReferenceChecks).Assembly.GetManifestResourceStream("Fixtures." + name)
            ?? throw new InvalidOperationException("Missing reference fixture.");
        using MemoryStream bytes = new(); source.CopyTo(bytes); return bytes.ToArray();
    }

    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException("Reference lookup: " + message); } }
}

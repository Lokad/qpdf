using System.IO.Compression;
using System.Text;
using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Hosting;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Tests.Filters;

internal sealed class StubResolver : IIndirectResolver
{
    private readonly Dictionary<int, QpdfObject> _objects = new();

    internal void Add(int number, QpdfObject value)
    {
        _objects[number] = value;
    }

    public bool TryResolve(PdfReference reference, out QpdfObject? value)
    {
        return _objects.TryGetValue(reference.ObjectNumber, out value);
    }
}

public sealed class IndirectFilterTests
{
    private static (QpdfLimits Limits, ResourceGovernor Governor) Governed()
    {
        QpdfLimits limits = QpdfLimits.Default;
        return (limits, new ResourceGovernor(limits, CancellationToken.None));
    }

    private static byte[] ZLibCompress(byte[] input)
    {
        using MemoryStream output = new();
        using (ZLibStream compressor = new(output, CompressionLevel.Optimal))
        {
            compressor.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    private static byte[] PackLzw(IEnumerable<(int Code, int Width)> codes)
    {
        List<byte> output = new();
        int buffer = 0;
        int held = 0;
        foreach ((int code, int width) in codes)
        {
            buffer = (buffer << width) | code;
            held += width;
            while (held >= 8)
            {
                held -= 8;
                output.Add((byte)((buffer >> held) & 0xFF));
            }
        }

        if (held > 0)
        {
            output.Add((byte)((buffer << (8 - held)) & 0xFF));
        }

        return [.. output];
    }

    private static byte[] TinyLzw()
    {
        return PackLzw([(256, 9), (65, 9), (257, 9)]);
    }

    private static PdfDictionary DictWith(string key, QpdfObject value)
    {
        return new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii(key)] = value,
        });
    }

    private static byte[] DecodeWith(PdfDictionary dict, StubResolver resolver, byte[] input)
    {
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        using FilterPlan stages = FilterChain.Resolve(dict, resolver, governor, governor.CancellationToken);
        return FilterChain.Decode(input, stages, limits, governor);
    }

    [Fact]
    public void IndirectFilterNameResolves()
    {
        byte[] payload = Encoding.ASCII.GetBytes("Hello through an indirect filter name.");
        PdfDictionary dict = DictWith("Filter", new PdfReference(9, 0));
        StubResolver resolver = new();
        resolver.Add(9, PdfName.FromAscii("FlateDecode"));
        Assert.Equal(payload, DecodeWith(dict, resolver, ZLibCompress(payload)));
    }

    [Fact]
    public void IndirectFilterArrayResolves()
    {
        byte[] payload = Encoding.ASCII.GetBytes("Hello through an indirect filter array.");
        PdfDictionary dict = DictWith("Filter", new PdfArray([new PdfReference(9, 0)]));
        StubResolver resolver = new();
        resolver.Add(9, PdfName.FromAscii("FlateDecode"));
        Assert.Equal(payload, DecodeWith(dict, resolver, ZLibCompress(payload)));
    }

    [Fact]
    public void IndirectDecodeParmsResolves()
    {
        byte[] raw = [5, 7, 9];
        byte[] predicted = [5, 2, 2];
        PdfDictionary parms = new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Predictor")] = new PdfInteger("2", 2L),
            [PdfName.FromAscii("Columns")] = new PdfInteger("3", 3L),
        });
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode"),
            [PdfName.FromAscii("DecodeParms")] = new PdfReference(10, 0),
        };
        StubResolver resolver = new();
        resolver.Add(10, parms);
        Assert.Equal(raw, DecodeWith(new PdfDictionary(entries), resolver, ZLibCompress(predicted)));
    }

    [Fact]
    public void IndirectPredictorIntegersResolve()
    {
        byte[] raw = [5, 7, 9];
        byte[] predicted = [5, 2, 2];
        PdfDictionary parms = new(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Predictor")] = new PdfReference(11, 0),
            [PdfName.FromAscii("Columns")] = new PdfReference(13, 0),
        });
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode"),
            [PdfName.FromAscii("DecodeParms")] = parms,
        };
        StubResolver resolver = new();
        resolver.Add(11, new PdfInteger("2", 2L));
        resolver.Add(13, new PdfInteger("3", 3L));
        Assert.Equal(raw, DecodeWith(new PdfDictionary(entries), resolver, ZLibCompress(predicted)));
    }

    [Fact]
    public void IndirectEarlyChangeResolves()
    {
        Dictionary<PdfName, QpdfObject> parms = new()
        {
            [PdfName.FromAscii("EarlyChange")] = new PdfReference(12, 0),
        };
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii("LZWDecode"),
            [PdfName.FromAscii("DecodeParms")] = new PdfDictionary(parms),
        };
        StubResolver resolver = new();
        resolver.Add(12, new PdfInteger("0", 0L));
        Assert.Equal([0x41], DecodeWith(new PdfDictionary(entries), resolver, TinyLzw()));
    }

    [Fact]
    public void IndirectNullParmsMeansAbsent()
    {
        byte[] payload = Encoding.ASCII.GetBytes("Null parms stay absent.");
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode"),
            [PdfName.FromAscii("DecodeParms")] = new PdfReference(10, 0),
        };
        StubResolver resolver = new();
        resolver.Add(10, PdfNull.Instance);
        Assert.Equal(payload, DecodeWith(new PdfDictionary(entries), resolver, ZLibCompress(payload)));
    }

    [Fact]
    public void UnresolvableIndirectFilterFailsExplicitly()
    {
        PdfDictionary dict = DictWith("Filter", new PdfReference(99, 0));
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        Assert.Throws<QpdfParseException>(() => FilterChain.Resolve(dict, new StubResolver(), governor, governor.CancellationToken));
        Assert.Throws<QpdfParseException>(() => FilterChain.Resolve(dict, null, governor, governor.CancellationToken));
    }

    [Fact]
    public void InvalidIndirectEarlyChangeFails()
    {
        Dictionary<PdfName, QpdfObject> parms = new()
        {
            [PdfName.FromAscii("EarlyChange")] = new PdfReference(12, 0),
        };
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Filter")] = PdfName.FromAscii("LZWDecode"),
            [PdfName.FromAscii("DecodeParms")] = new PdfDictionary(parms),
        };
        StubResolver resolver = new();
        resolver.Add(12, new PdfInteger("2", 2L));
        (QpdfLimits limits, ResourceGovernor governor) = Governed();
        using FilterPlan stages = FilterChain.Resolve(new PdfDictionary(entries), resolver, governor, governor.CancellationToken);
        QpdfFilterException ex = Assert.Throws<QpdfFilterException>(() => FilterChain.Decode(TinyLzw(), stages, limits, governor));
        Assert.Equal(QpdfFilterFailureReason.InvalidData, ex.Reason);
    }

    private static void EmitXRefEntry(List<byte> output, int type, long second, int third)
    {
        output.Add((byte)type);
        output.Add((byte)((second >> 24) & 0xFF));
        output.Add((byte)((second >> 16) & 0xFF));
        output.Add((byte)((second >> 8) & 0xFF));
        output.Add((byte)(second & 0xFF));
        output.Add((byte)((third >> 8) & 0xFF));
        output.Add((byte)(third & 0xFF));
    }

    private static byte[] BuildObjStmIndirectFilter()
    {
        byte[] hello = Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Hello) Tj ET");
        byte[] fontDecoded = Encoding.ASCII.GetBytes("6 0 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        byte[] compressedFont = ZLibCompress(fontDecoded);
        Dictionary<int, string> bodies = new()
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 6 0 R >> >> >>",
            [7] = "/FlateDecode",
        };
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        Dictionary<int, long> offsets = new();
        long position = header.Length;
        foreach (int number in new[] { 1, 2, 3, 7 })
        {
            byte[] chunk = Encoding.ASCII.GetBytes(number.ToString() + " 0 obj\n" + bodies[number] + "\nendobj\n");
            offsets[number] = position;
            position += chunk.Length;
            parts.Add(chunk);
        }

        byte[] open4 = Encoding.ASCII.GetBytes("4 0 obj\n<< /Length " + hello.Length.ToString() + " >>\nstream\n");
        byte[] close4 = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
        offsets[4] = position;
        position += open4.Length + hello.Length + close4.Length;
        byte[] part4 = new byte[open4.Length + hello.Length + close4.Length];
        Buffer.BlockCopy(open4, 0, part4, 0, open4.Length);
        Buffer.BlockCopy(hello, 0, part4, open4.Length, hello.Length);
        Buffer.BlockCopy(close4, 0, part4, open4.Length + hello.Length, close4.Length);
        parts.Add(part4);

        byte[] open5 = Encoding.ASCII.GetBytes("5 0 obj\n<< /Type /ObjStm /N 1 /First 4 /Filter 7 0 R /Length " + compressedFont.Length.ToString() + " >>\nstream\n");
        byte[] close5 = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
        offsets[5] = position;
        position += open5.Length + compressedFont.Length + close5.Length;
        byte[] part5 = new byte[open5.Length + compressedFont.Length + close5.Length];
        Buffer.BlockCopy(open5, 0, part5, 0, open5.Length);
        Buffer.BlockCopy(compressedFont, 0, part5, open5.Length, compressedFont.Length);
        Buffer.BlockCopy(close5, 0, part5, open5.Length + compressedFont.Length, close5.Length);
        parts.Add(part5);

        List<byte> xrefDecoded = new();
        EmitXRefEntry(xrefDecoded, 0, 0L, 65535);
        foreach (int number in new[] { 1, 2, 3, 4, 5 })
        {
            EmitXRefEntry(xrefDecoded, 1, offsets[number], 0);
        }

        EmitXRefEntry(xrefDecoded, 2, 5L, 0);
        EmitXRefEntry(xrefDecoded, 1, offsets[7], 0);
        offsets[8] = position;
        EmitXRefEntry(xrefDecoded, 1, position, 0);
        byte[] compressedXRef = ZLibCompress([.. xrefDecoded]);
        byte[] open8 = Encoding.ASCII.GetBytes("8 0 obj\n<< /Type /XRef /Size 9 /Root 1 0 R /W [1 4 2] /Filter /FlateDecode /Length " + compressedXRef.Length.ToString() + " >>\nstream\n");
        byte[] close8 = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
        position += open8.Length + compressedXRef.Length + close8.Length;
        byte[] part8 = new byte[open8.Length + compressedXRef.Length + close8.Length];
        Buffer.BlockCopy(open8, 0, part8, 0, open8.Length);
        Buffer.BlockCopy(compressedXRef, 0, part8, open8.Length, compressedXRef.Length);
        Buffer.BlockCopy(close8, 0, part8, open8.Length + compressedXRef.Length, close8.Length);
        parts.Add(part8);

        long startxref = offsets[8];
        byte[] tail = Encoding.ASCII.GetBytes("startxref\n" + startxref.ToString() + "\n%%EOF\n");
        parts.Add(tail);

        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        byte[] output = new byte[total];
        int cursor = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, output, cursor, part.Length);
            cursor += part.Length;
        }

        return output;
    }

    [Fact]
    public async Task ObjectStreamWithIndirectFilterDecodes()
    {
        byte[] bytes = BuildObjStmIndirectFilter();
        InMemoryQpdfHost host = new();
        host.AddInput(QpdfPath.From("inputs/objstm.pdf"), bytes, QpdfInputOwnership.Copy);
        await using QpdfDocument document = await QpdfDocument.OpenAsync(host, QpdfPath.From("inputs/objstm.pdf"), CancellationToken.None);
        PdfDictionary font = Assert.IsType<PdfDictionary>(document.GetObject(6));
        Assert.True(font.TryGet(PdfName.FromAscii("BaseFont"), out QpdfObject baseFont));
        Assert.Equal(PdfName.FromAscii("Helvetica"), baseFont);
        PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
            host, QpdfPath.From("inputs/objstm.pdf"), QpdfPath.From("outputs/sel.pdf"),
            new PdfPageSelectionOptions(1), CancellationToken.None);
        Assert.Equal([1], result.SourcePages);
    }
}

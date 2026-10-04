using System.IO.Compression;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Lokad.Qpdf.Benchmarks;

/// <summary>Builds small synthetic PDF inputs for benchmarks and counter reports.</summary>
internal static class PdfScenarioBuilder
{
    private sealed record ObjectSpec(string Dictionary, byte[]? Stream);

    internal static byte[] BuildTextDocument(int pages)
    {
        List<string> kids = new();
        for (int page = 0; page < pages; page++)
        {
            kids.Add((3 + (2 * page)).ToString() + " 0 R");
        }

        List<ObjectSpec> specs = new()
        {
            new ObjectSpec("<< /Type /Catalog /Pages 2 0 R >>", null),
            new ObjectSpec("<< /Type /Pages /Kids [" + string.Join(" ", kids) + "] /Count " + pages.ToString() + " >>", null),
        };
        for (int page = 0; page < pages; page++)
        {
            int contents = 4 + (2 * page);
            specs.Add(new ObjectSpec("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents " + contents.ToString() + " 0 R /Resources << >> >>", null));
            byte[] text = Encoding.ASCII.GetBytes("BT (Page " + (page + 1).ToString() + ") Tj ET");
            specs.Add(new ObjectSpec("<< /Length " + text.Length.ToString() + " >>", text));
        }

        return Assemble(specs);
    }

    internal static byte[] BuildFilterChain(int stages)
    {
        byte[] payload = Encoding.ASCII.GetBytes("BT (Deep) Tj ET");
        for (int i = 0; i < stages; i++)
        {
            payload = FlateBytes(payload);
        }

        StringBuilder filters = new();
        filters.Append("[ ");
        for (int i = 0; i < stages; i++)
        {
            filters.Append("/FlateDecode ");
        }

        filters.Append("]");
        List<ObjectSpec> specs = new()
        {
            new ObjectSpec("<< /Type /Catalog /Pages 2 0 R >>", null),
            new ObjectSpec("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            new ObjectSpec("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>", null),
            new ObjectSpec("<< /Length " + payload.Length.ToString() + " /Filter " + filters.ToString() + " >>", payload),
        };
        return Assemble(specs);
    }

    internal static byte[] BuildPhotoDocument()
    {
        byte[] jpeg = RenderJpeg(64, 64);
        List<ObjectSpec> specs = new()
        {
            new ObjectSpec("<< /Type /Catalog /Pages 2 0 R >>", null),
            new ObjectSpec("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            new ObjectSpec("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>", null),
            new ObjectSpec("<< /Length 29 >>", Encoding.ASCII.GetBytes("q 64 0 0 64 0 0 cm /Im0 Do Q")),
            new ObjectSpec("<< /Type /XObject /Subtype /Image /Width 64 /Height 64 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpeg.Length.ToString() + " >>", jpeg),
        };
        return Assemble(specs);
    }

    internal static byte[] FlateBytes(byte[] raw)
    {
        using MemoryStream output = new();
        using (ZLibStream compressor = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    private static byte[] RenderJpeg(int width, int height)
    {
        using Image<Rgb24> image = new(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new Rgb24((byte)((x * 255) / width), (byte)((y * 255) / height), 128);
            }
        }

        using MemoryStream output = new();
        image.Save(output, new JpegEncoder { Quality = 90 });
        return output.ToArray();
    }

    private static byte[] Assemble(List<ObjectSpec> specs)
    {
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        parts.Add(header);
        List<long> offsets = new();
        long position = header.Length;
        for (int i = 0; i < specs.Count; i++)
        {
            byte[] open = Encoding.ASCII.GetBytes((i + 1).ToString() + " 0 obj\n" + specs[i].Dictionary + "\n");
            if (specs[i].Stream is byte[] payload)
            {
                byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
                byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
                offsets.Add(position);
                position += open.Length + streamOpen.Length + payload.Length + streamClose.Length;
                byte[] combined = new byte[open.Length + streamOpen.Length + payload.Length + streamClose.Length];
                int at = 0;
                Buffer.BlockCopy(open, 0, combined, at, open.Length);
                at += open.Length;
                Buffer.BlockCopy(streamOpen, 0, combined, at, streamOpen.Length);
                at += streamOpen.Length;
                Buffer.BlockCopy(payload, 0, combined, at, payload.Length);
                at += payload.Length;
                Buffer.BlockCopy(streamClose, 0, combined, at, streamClose.Length);
                parts.Add(combined);
                continue;
            }

            byte[] close = Encoding.ASCII.GetBytes("endobj\n");
            offsets.Add(position);
            position += open.Length + close.Length;
            byte[] plain = new byte[open.Length + close.Length];
            Buffer.BlockCopy(open, 0, plain, 0, open.Length);
            Buffer.BlockCopy(close, 0, plain, open.Length, close.Length);
            parts.Add(plain);
        }

        int size = specs.Count + 1;
        StringBuilder xref = new();
        xref.Append("xref\n0 " + size.ToString() + "\n");
        xref.Append("0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10") + " 00000 n \n");
        }

        xref.Append("trailer\n<< /Size " + size.ToString() + " /Root 1 0 R >>\n");
        xref.Append("startxref\n" + position.ToString() + "\n%%EOF\n");
        parts.Add(Encoding.ASCII.GetBytes(xref.ToString()));
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
}

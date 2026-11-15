using System.Text;

namespace Lokad.Qpdf.Tests;

// Ordinary classic-PDF fixture framing. Feature owners supply bodies and exact
// payloads/declared lengths; physical repair, security and native vectors remain
// independent. No production PDF writer or parser participates in construction.
internal static class PdfTestDocument
{
    internal static byte[] Assemble(IReadOnlyList<(int Number, string Dict, byte[]? StreamData)> objects, int rootNumber)
        => Assemble(objects, rootNumber, null, "1.4", "");

    internal static byte[] Assemble(IReadOnlyList<(int Number, string Dict, byte[]? StreamData)> objects, int rootNumber,
        IReadOnlyDictionary<int, int>? generations)
        => Assemble(objects, rootNumber, generations, "1.4", "");

    internal static byte[] Assemble(IReadOnlyList<(int Number, string Dict, byte[]? StreamData)> objects, int rootNumber,
        IReadOnlyDictionary<int, int>? generations, string headerVersion, string trailerExtra)
    {
        List<byte[]> parts = new();
        byte[] header = Encoding.ASCII.GetBytes("%PDF-" + headerVersion + "\n");
        parts.Add(header);
        Dictionary<int, long> offsets = new();
        long position = header.Length;
        foreach ((int number, string dict, byte[]? streamData) in objects)
        {
            int generation = generations is not null && generations.TryGetValue(number, out int active) ? active : 0;
            byte[] open = Encoding.ASCII.GetBytes(number.ToString() + " " + generation.ToString() + " obj\n" + dict + "\n");
            if (streamData is null)
            {
                byte[] close = Encoding.ASCII.GetBytes("endobj\n");
                offsets[number] = position;
                position += open.Length + close.Length;
                byte[] plain = new byte[open.Length + close.Length];
                Buffer.BlockCopy(open, 0, plain, 0, open.Length);
                Buffer.BlockCopy(close, 0, plain, open.Length, close.Length);
                parts.Add(plain);
                continue;
            }

            byte[] streamOpen = Encoding.ASCII.GetBytes("stream\n");
            byte[] streamClose = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
            offsets[number] = position;
            position += open.Length + streamOpen.Length + streamData.Length + streamClose.Length;
            byte[] payload = new byte[open.Length + streamOpen.Length + streamData.Length + streamClose.Length];
            int at = 0;
            Buffer.BlockCopy(open, 0, payload, at, open.Length);
            at += open.Length;
            Buffer.BlockCopy(streamOpen, 0, payload, at, streamOpen.Length);
            at += streamOpen.Length;
            Buffer.BlockCopy(streamData, 0, payload, at, streamData.Length);
            at += streamData.Length;
            Buffer.BlockCopy(streamClose, 0, payload, at, streamClose.Length);
            parts.Add(payload);
        }

        int size = 0;
        foreach ((int number, string _, byte[]? _) in objects)
        {
            if (number > size)
            {
                size = number;
            }
        }

        size += 1;
        StringBuilder xref = new();
        xref.Append("xref\n0 " + size.ToString() + "\n");
        xref.Append("0000000000 65535 f \n");
        for (int number = 1; number < size; number++)
        {
            if (!offsets.TryGetValue(number, out long offset))
            {
                throw new Xunit.Sdk.XunitException("Builder is missing object " + number.ToString() + ".");
            }

            int generation = generations is not null && generations.TryGetValue(number, out int active) ? active : 0;
            xref.Append(offset.ToString("D10") + " " + generation.ToString("D5") + " n \n");
        }

        xref.Append("trailer\n<< /Size " + size.ToString() + " /Root " + rootNumber.ToString() + " 0 R" + trailerExtra + " >>\n");
        xref.Append("startxref\n" + position.ToString() + "\n%%EOF\n");
        byte[] tail = Encoding.ASCII.GetBytes(xref.ToString());
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

    internal static byte[] AssembleBodies(IReadOnlyList<string> bodies, Func<int, byte[]?> payload,
        string headerVersion, string trailerExtra)
    {
        List<(int, string, byte[]?)> objects = new(bodies.Count);
        for (int index = 0; index < bodies.Count; index++)
        { int number = index + 1; objects.Add((number, bodies[index], payload(number))); }
        return Assemble(objects, 1, null, headerVersion, trailerExtra);
    }

    internal static List<(int, string, byte[]?)> SinglePage(string contentsDict, byte[] contentsData)
    {
        return new List<(int, string, byte[]?)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R >>", null),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>", null),
            (4, contentsDict, contentsData),
            (5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
        };
    }
}

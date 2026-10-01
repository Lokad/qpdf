using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Attachments;

internal static class AttachmentBuilder
{
    internal static PdfByteString EncodeText(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        bool ascii = true;
        foreach (char c in text)
        {
            if (c > 127)
            {
                ascii = false;
                break;
            }
        }

        if (ascii)
        {
            return new PdfByteString(Encoding.ASCII.GetBytes(text), false);
        }

        byte[] body = Encoding.BigEndianUnicode.GetBytes(text);
        byte[] result = new byte[body.Length + 2];
        result[0] = 0xFE;
        result[1] = 0xFF;
        Buffer.BlockCopy(body, 0, result, 2, body.Length);
        return new PdfByteString(result, true);
    }

    internal static string Basename(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        int cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf((char)92));
        return cut < 0 ? path : path.Substring(cut + 1);
    }

    internal static PdfDictionary BuildStreamDictionary(
        long length,
        byte[] checksum,
        string? mimeType,
        string? creationDate,
        string? modDate,
        ResourceGovernor governor)
    {
        if (checksum is null)
        {
            throw new ArgumentNullException(nameof(checksum));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        governor.ReserveMemory(128L);
        Dictionary<PdfName, QpdfObject> parms = new()
        {
            [PdfName.FromAscii("Size")] = new PdfInteger(length.ToString(System.Globalization.CultureInfo.InvariantCulture), length),
            [PdfName.FromAscii("CheckSum")] = new PdfByteString((byte[])checksum.Clone(), true),
        };
        if (creationDate is not null)
        {
            governor.ReserveMemory(32L);
            parms[PdfName.FromAscii("CreationDate")] = EncodeText(creationDate);
        }

        if (modDate is not null)
        {
            governor.ReserveMemory(32L);
            parms[PdfName.FromAscii("ModDate")] = EncodeText(modDate);
        }

        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("EmbeddedFile"),
            [PdfName.FromAscii("Length")] = new PdfInteger(length.ToString(System.Globalization.CultureInfo.InvariantCulture), length),
            [PdfName.FromAscii("Params")] = new PdfDictionary(parms),
        };
        if (mimeType is not null)
        {
            governor.ReserveMemory(32L);
            entries[PdfName.FromAscii("Subtype")] = PdfName.FromAscii(mimeType);
        }

        return new PdfDictionary(entries);
    }

    internal static PdfDictionary WithFilter(PdfDictionary dictionary, ResourceGovernor governor)
    {
        if (dictionary is null)
        {
            throw new ArgumentNullException(nameof(dictionary));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        Dictionary<PdfName, QpdfObject> entries = new();
        foreach (PdfName key in dictionary.Keys)
        {
            if (dictionary.TryGet(key, out QpdfObject? entry) && entry is not null)
            {
                entries[key] = entry;
            }
        }

        governor.ReserveMemory(64L);
        entries[PdfName.FromAscii("Filter")] = PdfName.FromAscii("FlateDecode");
        return new PdfDictionary(entries);
    }

    internal static PdfDictionary BuildFilespecDictionary(string fileName, string? description, int streamId)
    {
        if (fileName is null)
        {
            throw new ArgumentNullException(nameof(fileName));
        }

        PdfByteString name = EncodeText(fileName);
        PdfReference stream = new(streamId, 0);
        Dictionary<PdfName, QpdfObject> entries = new()
        {
            [PdfName.FromAscii("Type")] = PdfName.FromAscii("Filespec"),
            [PdfName.FromAscii("F")] = name,
            [PdfName.FromAscii("UF")] = name,
            [PdfName.FromAscii("EF")] = new PdfDictionary(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("F")] = stream,
                [PdfName.FromAscii("UF")] = stream,
            }),
        };
        if (description is not null)
        {
            entries[PdfName.FromAscii("Desc")] = EncodeText(description);
        }

        return new PdfDictionary(entries);
    }

    internal static PdfDictionary BuildNameTree(IReadOnlyList<KeyValuePair<string, PdfReference>> pairs, ResourceGovernor governor)
    {
        if (pairs is null)
        {
            throw new ArgumentNullException(nameof(pairs));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        if (pairs.Count == 0)
        {
            throw new QpdfException("Attachment name tree needs at least one entry.");
        }

        List<PdfDictionary> leaves = new();
        for (int i = 0; i < pairs.Count; i += 64)
        {
            governor.ChargeWork(1L);
            int take = Math.Min(64, pairs.Count - i);
            List<QpdfObject> items = new(take * 2);
            for (int j = 0; j < take; j++)
            {
                governor.ReserveMemory(64L);
                items.Add(EncodeText(pairs[i + j].Key));
                items.Add(pairs[i + j].Value);
            }

            leaves.Add(new PdfDictionary(new Dictionary<PdfName, QpdfObject>
            {
                [PdfName.FromAscii("Names")] = new PdfArray(items),
            }));
        }

        if (leaves.Count == 1)
        {
            return leaves[0];
        }

        List<QpdfObject> kids = new(leaves.Count);
        foreach (PdfDictionary leaf in leaves)
        {
            governor.ReserveMemory(32L);
            kids.Add(leaf);
        }

        return new PdfDictionary(new Dictionary<PdfName, QpdfObject>
        {
            [PdfName.FromAscii("Kids")] = new PdfArray(kids),
            [PdfName.FromAscii("Limits")] = new PdfArray(new QpdfObject[]
            {
                EncodeText(pairs[0].Key),
                EncodeText(pairs[pairs.Count - 1].Key),
            }),
        });
    }

    internal static byte[] Checksum(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        return MD5.HashData(data);
    }
}

using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>
/// Shared object-identity remapping for publication paths that renumber the
/// destination graph (split groups, QDF length insertion). References remap
/// through the table; arrays, dictionaries and stream headers rebuild;
/// anything else passes through.
/// </summary>
internal static class ObjectRemap
{
    internal static QpdfObject RemapIds(QpdfObject value, Dictionary<int, int> renumber, ResourceGovernor governor)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (renumber is null)
        {
            throw new ArgumentNullException(nameof(renumber));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        governor.ChargeWork(1L);
        if (value is PdfReference reference)
        {
            if (!renumber.TryGetValue(reference.ObjectNumber, out int mapped))
            {
                throw new QpdfException($"Publication reached unmapped object {reference.ObjectNumber}.");
            }

            return new PdfReference(mapped, 0);
        }

        if (value is PdfArray array)
        {
            List<QpdfObject> items = new(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                items.Add(RemapIds(array[i], renumber, governor));
            }

            return new PdfArray(items);
        }

        if (value is PdfDictionary dictionary)
        {
            Dictionary<PdfName, QpdfObject> entries = new();
            foreach (PdfName key in dictionary.Keys)
            {
                if (dictionary.TryGet(key, out QpdfObject? entry) && entry is not null)
                {
                    entries[key] = RemapIds(entry, renumber, governor);
                }
            }

            return new PdfDictionary(entries);
        }

        if (value is PdfStream stream)
        {
            if (RemapIds(stream.Dictionary, renumber, governor) is not PdfDictionary remapped)
            {
                throw new QpdfException("Publication reached a non-dictionary stream header.");
            }

            byte[] data = stream.Data;
            byte[] preserved = new byte[data.Length];
            Buffer.BlockCopy(data, 0, preserved, 0, data.Length);
            return new PdfStream(remapped, preserved);
        }

        return value;
    }
}
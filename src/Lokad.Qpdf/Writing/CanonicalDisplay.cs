using Lokad.Qpdf.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Writing;

/// <summary>
/// Single-line canonical object display for inspection output. Dictionaries
/// sort keys by raw bytes with upstream-compatible spacing so managed dumps
/// compare line for line against the reference build on canonical fixtures.
/// </summary>
internal static class CanonicalDisplay
{
    internal static void AddDiagnostic(
        QpdfObject value,
        QpdfLimits limits,
        ResourceGovernor governor,
        IIndirectResolver? resolver,
        DiagnosticBag diagnostics)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (limits is null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (governor is null)
        {
            throw new ArgumentNullException(nameof(governor));
        }

        limits.Validate();
        GovernedText text;
        using (DisplayBuffer builder = DisplayBuffer.Create(governor, true))
        {
            AppendValue(value, builder, 0, limits, governor, resolver);
            text = builder.Finish();
        }
        using (text) { diagnostics.AddOwned(QpdfDiagnosticCode.ObjectListing, QpdfDiagnosticSeverity.Info, text); }
    }

    private static void AppendValue(
        QpdfObject value,
        DisplayBuffer builder,
        int depth,
        QpdfLimits limits,
        ResourceGovernor governor,
        IIndirectResolver? resolver)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (depth > limits.MaxNestingDepth)
        {
            throw new QpdfParseException("Display depth exceeds the nesting budget.", null, null);
        }

        governor.NoteOperation();
        if (value is PdfNull)
        {
            builder.Append("null");
            return;
        }

        if (value is PdfBoolean flag)
        {
            builder.Append(flag.Value ? "true" : "false");
            return;
        }

        if (value is PdfInteger integer)
        {
            builder.AppendInteger(integer.Value);
            return;
        }

        if (value is PdfReal real)
        {
            builder.AppendText(real.Text);
            return;
        }

        if (value is PdfName name)
        {
            AppendName(name, builder, governor);
            return;
        }

        if (value is PdfByteString text)
        {
            AppendString(text, builder, governor);
            return;
        }

        if (value is PdfArray array)
        {
            builder.Append("[ ");
            for (int i = 0; i < array.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                AppendValue(array[i], builder, depth + 1, limits, governor, resolver);
            }

            builder.Append(" ]");
            return;
        }

        if (value is PdfDictionary dictionary)
        {
            long reserved = dictionary.Count == 0 ? 0L : checked(32L + 2L * IntPtr.Size * dictionary.Count);
            governor.CheckCancellation();
            governor.ReserveMemory(reserved);
            try
            {
                KeyValuePair<PdfName, QpdfObject>[] keys = dictionary.Count == 0
                    ? Array.Empty<KeyValuePair<PdfName, QpdfObject>>()
                    : new KeyValuePair<PdfName, QpdfObject>[dictionary.Count];
                using PdfDictionary.EntryEnumerator entries = dictionary.GetEntryEnumerator();
                int index = 0;
                while (entries.MoveNext())
                {
                    governor.CheckCancellation();
                    keys[index++] = entries.Current;
                }
                GovernedSort.Sort<KeyValuePair<PdfName, QpdfObject>, RawKeyComparer>(keys, new(governor), governor, CancellationToken.None);
                builder.Append("<<");
                foreach (KeyValuePair<PdfName, QpdfObject> entry in keys)
                {
                    QpdfObject member = entry.Value;
                    if (member is null or PdfNull)
                    {
                        continue;
                    }
                    // Test reference nullness without expanding live graphs.
                    if (member is PdfReference indirect && resolver is not null)
                    {
                        governor.CheckCancellation();
                        if (!resolver.TryResolve(indirect, out QpdfObject? resolved) || resolved is null or PdfNull)
                        {
                            continue;
                        }
                    }
                    builder.Append(' ');
                    AppendName(entry.Key, builder, governor);
                    builder.Append(' ');
                    AppendValue(member, builder, depth + 1, limits, governor, resolver);
                }
                builder.Append(" >>");
            }
            finally { governor.ReleaseMemory(reserved); }
            return;
        }

        if (value is PdfReference reference)
        {
            builder.AppendInteger(reference.ObjectNumber);
            builder.Append(' ');
            builder.AppendInteger(reference.Generation);
            builder.Append(" R");
            return;
        }

        throw new QpdfException($"Unsupported value of type {value.GetType().Name} during display.");
    }


    private static void AppendName(PdfName name, DisplayBuffer builder, ResourceGovernor governor)
    {
        ReadOnlySpan<byte> raw = name.RawSpan;
        governor.CheckCancellation();
        builder.Append('/');
        for (int i = 0; i < raw.Length; i++)
        {
            CheckChunkCancellation(i, governor);
            byte b = raw[i];
            if (b == 0)
            {
                builder.Append('#');
            }
            else if (IsRegular(b))
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('#');
                builder.Append(HexDigit(b >> 4));
                builder.Append(HexDigit(b & 0x0F));
            }
        }
    }

    private static bool IsRegular(byte b)
    {
        if (b < 0x21 || b > 0x7E)
        {
            return false;
        }

        return b != (byte)'#' && b != (byte)'(' && b != (byte)')' && b != (byte)'<'
            && b != (byte)'>' && b != (byte)'[' && b != (byte)']' && b != (byte)'{'
            && b != (byte)'}' && b != (byte)'/' && b != (byte)'%';
    }

    private static void AppendString(PdfByteString text, DisplayBuffer builder, ResourceGovernor governor)
    {
        ReadOnlySpan<byte> raw = text.ValueSpan;
        if (PdfJsonWriter.UseHexString(raw, governor))
        {
            builder.Append('<');
            for (int i = 0; i < raw.Length; i++)
            {
                CheckChunkCancellation(i, governor);
                byte b = raw[i];
                builder.Append(HexDigit(b >> 4));
                builder.Append(HexDigit(b & 0x0F));
            }

            builder.Append('>');
            return;
        }

        builder.Append('(');
        for (int i = 0; i < raw.Length; i++)
        {
            CheckChunkCancellation(i, governor);
            byte b = raw[i];
            switch (b)
            {
                case (byte)'\n':
                    builder.Append("\\n");
                    break;
                case (byte)'\r':
                    builder.Append("\\r");
                    break;
                case (byte)'\t':
                    builder.Append("\\t");
                    break;
                case (byte)'\b':
                    builder.Append("\\b");
                    break;
                case (byte)'\f':
                    builder.Append("\\f");
                    break;
                case (byte)'(':
                case (byte)')':
                case (byte)'\\':
                    builder.Append('\\');
                    builder.Append((char)b);
                    break;
                default:
                    if (b is >= 32 and <= 126 or >= 160)
                    {
                        builder.Append((char)b);
                    }
                    else
                    {
                        builder.Append('\\');
                        builder.Append((char)('0' + (b >> 6)));
                        builder.Append((char)('0' + ((b >> 3) & 7)));
                        builder.Append((char)('0' + (b & 7)));
                    }
                    break;
            }
        }

        builder.Append(')');
    }

    private static void CheckChunkCancellation(int position, ResourceGovernor governor)
    {
        if ((position & 255) == 0)
        {
            governor.CheckCancellation();
        }
    }

    private static char HexDigit(int value)
    {
        return (char)(value < 10 ? '0' + value : 'a' + value - 10);
    }

    private readonly struct RawKeyComparer(ResourceGovernor governor) : IComparer<KeyValuePair<PdfName, QpdfObject>>
    {
        public int Compare(KeyValuePair<PdfName, QpdfObject> left, KeyValuePair<PdfName, QpdfObject> right)
            => GovernedCollections.CompareNames(governor, left.Key, right.Key);
    }
}

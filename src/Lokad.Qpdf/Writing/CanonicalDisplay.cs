using System.Globalization;
using System.Text;
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
    internal static string Render(
        QpdfObject value,
        QpdfLimits limits,
        ResourceGovernor governor)
    {
        return Render(value, limits, governor, null);
    }

    internal static string Render(
        QpdfObject value,
        QpdfLimits limits,
        ResourceGovernor governor,
        IIndirectResolver? resolver)
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
        StringBuilder builder = new();
        AppendValue(value, builder, 0, limits, governor, resolver);
        return builder.ToString();
    }

    private static void AppendValue(
        QpdfObject value,
        StringBuilder builder,
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

        governor.ChargeWork(1L);
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
            builder.Append(integer.Value.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (value is PdfReal real)
        {
            builder.Append(real.Text);
            return;
        }

        if (value is PdfName name)
        {
            AppendName(name, builder);
            return;
        }

        if (value is PdfByteString text)
        {
            AppendString(text, builder);
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
            List<PdfName> names = new(dictionary.Keys);
            List<KeyValuePair<byte[], PdfName>> keys = new(names.Count);
            foreach (PdfName key in names)
            {
                keys.Add(new KeyValuePair<byte[], PdfName>(key.RawBytes, key));
            }

            keys.Sort(static (left, right) =>
            {
                int shared = Math.Min(left.Key.Length, right.Key.Length);
                for (int i = 0; i < shared; i++)
                {
                    if (left.Key[i] != right.Key[i])
                    {
                        return left.Key[i].CompareTo(right.Key[i]);
                    }
                }

                return left.Key.Length.CompareTo(right.Key.Length);
            });
            builder.Append("<<");
            foreach (KeyValuePair<byte[], PdfName> entry in keys)
            {
                if (!dictionary.TryGet(entry.Value, out QpdfObject? member) || member is null or PdfNull)
                {
                    continue;
                }

                // Null dictionary members are omitted, including references
                // that the document resolves as null or absent. Keep live
                // references in indirect form rather than expanding graphs.
                if (member is PdfReference indirect && resolver is not null)
                {
                    governor.ChargeWork(1L);
                    governor.CheckCancellation();
                    if (!resolver.TryResolve(indirect, out QpdfObject? resolved) || resolved is null or PdfNull)
                    {
                        continue;
                    }
                }

                builder.Append(' ');
                AppendName(entry.Value, builder);
                builder.Append(' ');
                AppendValue(member, builder, depth + 1, limits, governor, resolver);
            }

            builder.Append(" >>");
            return;
        }

        if (value is PdfReference reference)
        {
            builder.Append(reference.ObjectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(reference.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(" R");
            return;
        }

        throw new QpdfException($"Unsupported value of type {value.GetType().Name} during display.");
    }


    private static void AppendName(PdfName name, StringBuilder builder)
    {
        byte[] raw = name.RawBytes;
        builder.Append('/');
        foreach (byte b in raw)
        {
            if (IsRegular(b))
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

    private static void AppendString(PdfByteString text, StringBuilder builder)
    {
        byte[] raw = text.Value;
        if (text.Hex)
        {
            builder.Append('<');
            foreach (byte b in raw)
            {
                builder.Append(HexDigit(b >> 4));
                builder.Append(HexDigit(b & 0x0F));
            }

            builder.Append('>');
            return;
        }

        builder.Append('(');
        foreach (byte b in raw)
        {
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
                    builder.Append((char)b);
                    break;
            }
        }

        builder.Append(')');
    }

    private static char HexDigit(int value)
    {
        return (char)(value < 10 ? '0' + value : 'a' + value - 10);
    }
}

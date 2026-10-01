namespace Lokad.Qpdf.Commands;

/// <summary>Validated job plan shared by every entry path.</summary>
internal abstract record JobPlan(QpdfPath Input);

/// <summary>Validated single-page selection.</summary>
internal sealed record SelectionPlan(QpdfPath Input, QpdfPath Output, int Page, QpdfRecoveryMode Recovery) : JobPlan(Input)
{
    internal static int ParsePageNumber(string range)
    {
        if (range is null || range.Length == 0 || range.Length > 10)
        {
            throw new QpdfParseException($"Invalid page range '{range}'.", 0L, null);
        }

        foreach (char c in range)
        {
            if (c < '0' || c > '9')
            {
                throw new QpdfParseException($"Invalid page range '{range}'. Only a single page number is supported.", 0L, null);
            }
        }

        long value = 0L;
        foreach (char c in range)
        {
            value = (value * 10L) + (c - '0');
        }

        if (value < 1L || value > int.MaxValue)
        {
            throw new QpdfParseException($"Invalid page range '{range}'.", 0L, null);
        }

        return (int)value;
    }

    internal static QpdfPath CheckedPath(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new QpdfParseException("Empty file path.", 0L, null);
        }

        if (value.Length > QpdfPath.MaxLength || value.Contains('\0'))
        {
            throw new QpdfParseException("File path is not a valid logical path.", 0L, null);
        }

        return QpdfPath.From(value);
    }
}

/// <summary>Validated structural inspection.</summary>
internal sealed record CheckPlan(QpdfPath Input, QpdfRecoveryMode Recovery) : JobPlan(Input);

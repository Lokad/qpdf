namespace Lokad.Qpdf;

/// <summary>
/// A logical host path. Paths never grant filesystem, process, or network
/// authority; every referenced byte goes through <see cref="IQpdfHost" />.
/// </summary>
public readonly record struct QpdfPath
{
    /// <summary>Maximum logical path length in characters.</summary>
    public const int MaxLength = 4096;

    /// <summary>Gets the logical path value.</summary>
    public string Value { get; }

    private QpdfPath(string value)
    {
        Value = value;
    }

    /// <summary>Creates a logical path from a caller-supplied value.</summary>
    /// <exception cref="ArgumentException">The value is empty or too long.</exception>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public static QpdfPath From(string value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (value.Length == 0 || value.Length > MaxLength)
        {
            throw new ArgumentException($"Logical path must be 1..{MaxLength} characters.", nameof(value));
        }

        if (value.Contains('\0'))
        {
            throw new ArgumentException("Logical path must not contain NUL characters.", nameof(value));
        }

        return new QpdfPath(value);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }
}

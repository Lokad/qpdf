using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

/// <summary>PDF header version, such as 1.4 or 2.0.</summary>
public sealed record PdfVersion
{
    internal const long StorageBytes = 32L;
    private VersionMemory? _storage;

    /// <summary>Creates a version.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A component is negative.</exception>
    public PdfVersion(int major, int minor)
    {
        if (major < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major), "Version components must not be negative.");
        }

        if (minor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minor), "Version components must not be negative.");
        }

        Major = major;
        Minor = minor;
    }

    private PdfVersion(PdfVersion other)
    { Major = other.Major; Minor = other.Minor; }

    internal void OwnStorage(VersionMemory storage)
    {
        if (_storage is not null) { throw new InvalidOperationException("Version storage is already owned."); }
        _storage = storage;
    }

    /// <summary>Compares the version components.</summary>
    public bool Equals(PdfVersion? other) => other is not null && Major == other.Major && Minor == other.Minor;

    /// <summary>Returns a hash of the version components.</summary>
    public override int GetHashCode() => HashCode.Combine(Major, Minor);

    /// <summary>Gets the major component.</summary>
    public int Major { get; }

    /// <summary>Gets the minor component.</summary>
    public int Minor { get; }

    /// <summary>Returns the dotted major.minor text.</summary>
    public override string ToString()
    {
        return $"{Major}.{Minor}";
    }
}

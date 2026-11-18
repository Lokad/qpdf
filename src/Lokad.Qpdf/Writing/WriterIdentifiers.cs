namespace Lokad.Qpdf.Writing;

/// <summary>Identifier argument policy shared by the physical writers.</summary>
internal static class WriterIdentifiers
{
    internal static void Validate(byte[]? id1, byte[]? id2)
    {
        ArgumentNullException.ThrowIfNull(id1);
        ArgumentNullException.ThrowIfNull(id2);
        if (id1.Length == 0 || id1.Length > 256)
        { throw new ArgumentOutOfRangeException(nameof(id1), "Document identifiers must hold 1..256 bytes."); }
        if (id2.Length == 0 || id2.Length > 256)
        { throw new ArgumentOutOfRangeException(nameof(id2), "Document identifiers must hold 1..256 bytes."); }
    }
}

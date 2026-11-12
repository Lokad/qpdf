using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Attachments;

namespace Lokad.Qpdf;

/// <summary>A per-input password represented as text or borrowed raw bytes.</summary>
/// <remarks>Keep borrowed bytes stable until the opening operation completes.
/// The library clears its temporary password buffers, but does not clear caller
/// storage or immutable strings. String formatting never reveals the password.</remarks>
public sealed class QpdfPassword
{
    private readonly string? _text;
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly bool _allowRecovery;

    private QpdfPassword(string? text, ReadOnlyMemory<byte> bytes, bool allowRecovery)
    { _text = text; _bytes = bytes; _allowRecovery = allowRecovery; }

    /// <summary>Uses UTF-8 without normalization, with a bounded PDFDocEncoding fallback for R4.</summary>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public static QpdfPassword FromText(string text)
    { ArgumentNullException.ThrowIfNull(text); return new(text, default, true); }

    internal static QpdfPassword FromText(string text, bool allowRecovery)
    { ArgumentNullException.ThrowIfNull(text); return new(text, default, allowRecovery); }

    /// <summary>Borrows exact password bytes, including an explicitly empty password.</summary>
    public static QpdfPassword FromBytes(ReadOnlyMemory<byte> bytes) => new(null, bytes, false);

    /// <summary>Returns a redacted description.</summary>
    public override string ToString() => "QpdfPassword [redacted]";

    internal bool TryWritePdfDocBytes(Span<byte> destination, out int count)
    {
        count = 0;
        if (_text is null || !_allowRecovery) { return false; }
        int length = Math.Min(_text.Length, destination.Length);
        for (int index = 0; index < length; index++)
        { if (!PdfText.TryEncodePdfDocByte(_text[index], out destination[index])) { return false; } }
        count = length; return true;
    }

    internal int WriteModernBytes(Span<byte> destination)
    {
        if (destination.Length != 127) { throw new ArgumentException("Modern password scratch must hold 127 bytes.", nameof(destination)); }
        if (_text is null)
        {
            int count = Math.Min(_bytes.Length, destination.Length);
            _bytes.Span[..count].CopyTo(destination); return count;
        }
        // At most 127 UTF-16 units are needed to supply 127 UTF-8 bytes. Encode
        // one additional unit so truncation may split a multibyte sequence,
        // matching qpdf's byte limit rather than truncating whole characters.
        Span<byte> encoded = stackalloc byte[384];
        try
        {
            int written = Encoding.UTF8.GetBytes(_text.AsSpan(0, Math.Min(_text.Length, 128)), encoded);
            int count = Math.Min(written, destination.Length);
            encoded[..count].CopyTo(destination); return count;
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }
}

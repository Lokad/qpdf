using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Runtime;

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
    private readonly bool _hexEncoded;

    private QpdfPassword(string? text, ReadOnlyMemory<byte> bytes, bool allowRecovery, bool hexEncoded)
    { _text = text; _bytes = bytes; _allowRecovery = allowRecovery; _hexEncoded = hexEncoded; }

    /// <summary>Uses UTF-8 without normalization, with a bounded PDFDocEncoding fallback for R4.</summary>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public static QpdfPassword FromText(string text)
    { ArgumentNullException.ThrowIfNull(text); return new(text, default, true, false); }

    internal static QpdfPassword FromText(string text, bool allowRecovery)
    { ArgumentNullException.ThrowIfNull(text); return new(text, default, allowRecovery, false); }

    internal static QpdfPassword FromHexText(string text, bool allowRecovery)
    { ArgumentNullException.ThrowIfNull(text); return new(text, default, allowRecovery, true); }

    /// <summary>Borrows exact password bytes, including an explicitly empty password.</summary>
    public static QpdfPassword FromBytes(ReadOnlyMemory<byte> bytes) => new(null, bytes, false, false);

    /// <summary>Returns a redacted description.</summary>
    public override string ToString() => "QpdfPassword [redacted]";

    internal bool TryWritePdfDocBytes(ReadOnlySpan<byte> utf8Prefix, Span<byte> destination, out int count)
    {
        count = 0;
        if (_text is null || !_allowRecovery) { return false; }
        if (_hexEncoded)
        {
            while (!utf8Prefix.IsEmpty && count < destination.Length)
            {
                if (Rune.DecodeFromUtf8(utf8Prefix, out Rune rune, out int consumed) != OperationStatus.Done
                    || !rune.IsBmp || !PdfText.TryEncodePdfDocByte((char)rune.Value, out destination[count]))
                { return false; }
                utf8Prefix = utf8Prefix[consumed..]; count++;
            }
            return true;
        }
        int length = Math.Min(_text.Length, destination.Length);
        for (int index = 0; index < length; index++)
        { if (!PdfText.TryEncodePdfDocByte(_text[index], out destination[index])) { return false; } }
        count = length; return true;
    }

    internal int WriteModernBytes(Span<byte> destination, ResourceGovernor governor)
    {
        if (destination.Length != 127) { throw new ArgumentException("Modern password scratch must hold 127 bytes.", nameof(destination)); }
        if (_text is null)
        {
            int count = Math.Min(_bytes.Length, destination.Length);
            _bytes.Span[..count].CopyTo(destination); return count;
        }
        if (_hexEncoded) { return WriteHexBytes(_text, destination, governor); }
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

    private static int WriteHexBytes(string text, Span<byte> destination, ResourceGovernor governor)
    {
        int count = 0, high = -1;
        for (int index = 0; index < text.Length; index++)
        {
            if ((index & 255) == 0) { governor.CheckCancellation(); }
            char value = text[index];
            int digit = value is >= '0' and <= '9' ? value - '0'
                : value is >= 'A' and <= 'F' ? value - 'A' + 10
                : value is >= 'a' and <= 'f' ? value - 'a' + 10 : -1;
            if (digit < 0) { continue; }
            if (high < 0) { high = digit; continue; }
            byte decoded = (byte)((high << 4) | digit);
            // Native command passwords end at their first NUL. Ignore non-hex
            // characters and pad an odd final nibble, as qpdf's hex mode does.
            if (decoded == 0) { return count; }
            destination[count++] = decoded;
            if (count == destination.Length) { return count; }
            high = -1;
        }
        if (high > 0) { destination[count++] = (byte)(high << 4); }
        return count;
    }
}

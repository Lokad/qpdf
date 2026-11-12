using System.Security.Cryptography;
using System.Text;
using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;

namespace Lokad.Qpdf;

/// <summary>A per-input password represented as text or borrowed raw bytes.</summary>
/// <remarks>Keep borrowed bytes stable until the operation using this credential completes.
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

    /// <summary>Uses UTF-8 without normalization, with bounded UTF-8, PDFDoc, WinAnsi and MacRoman encoding recovery.</summary>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public static QpdfPassword FromText(string text)
    { ArgumentNullException.ThrowIfNull(text); return new(text, default, true, false); }

    internal static QpdfPassword FromText(string text, bool allowRecovery)
    { ArgumentNullException.ThrowIfNull(text); return new(text, default, allowRecovery, false); }

    internal static QpdfPassword FromHexText(string text, bool allowRecovery)
    { ArgumentNullException.ThrowIfNull(text); return new(text, default, allowRecovery, true); }

    /// <summary>Borrows exact password bytes, including an explicitly empty password.</summary>
    public static QpdfPassword FromBytes(ReadOnlyMemory<byte> bytes) => new(null, bytes, false, false);

    internal static QpdfPassword FromCommandBytes(ReadOnlyMemory<byte> bytes, bool allowRecovery)
        => new(null, bytes, allowRecovery, false);

    /// <summary>Returns a redacted description.</summary>
    public override string ToString() => "QpdfPassword [redacted]";

    internal bool HasSameInput(QpdfPassword other, ResourceGovernor governor)
    {
        if (ReferenceEquals(this, other)) { return true; }
        if (_allowRecovery != other._allowRecovery || _hexEncoded != other._hexEncoded
            || (_text is null) != (other._text is null)) { return false; }
        if (_text is string text && other._text is string otherText)
        {
            if (text.Length != otherText.Length) { return false; }
            for (int offset = 0; offset < text.Length; offset += Math.Min(32768, text.Length - offset))
            {
                governor.CheckCancellation();
                int count = Math.Min(32768, text.Length - offset);
                if (!text.AsSpan(offset, count).SequenceEqual(otherText.AsSpan(offset, count))) { return false; }
            }
            return true;
        }
        if (_bytes.Length != other._bytes.Length) { return false; }
        for (int offset = 0; offset < _bytes.Length; offset += Math.Min(65536, _bytes.Length - offset))
        {
            governor.CheckCancellation();
            int count = Math.Min(65536, _bytes.Length - offset);
            if (!_bytes.Span.Slice(offset, count).SequenceEqual(other._bytes.Span.Slice(offset, count))) { return false; }
        }
        return true;
    }

    internal const int RecoveryPrefixBytes = 512;

    internal bool TryWriteAlternativeBytes(Span<byte> destination, int candidate,
        ResourceGovernor governor, out int count)
    {
        count = 0;
        if (!_allowRecovery) { return false; }
        governor.CheckCancellation();
        if (_text is string text && !_hexEncoded)
        {
            if (candidate >= 3) { return false; }
            int length = Math.Min(text.Length, destination.Length);
            for (int index = 0; index < length; index++)
            {
                governor.CheckCancellation();
                if (!PdfText.TryEncodeSingleByte(text[index], (PdfSingleByteEncoding)candidate, out destination[index])) { return false; }
            }
            count = length; return true;
        }
        Span<byte> prefix = stackalloc byte[RecoveryPrefixBytes];
        try
        {
            int length;
            if (_text is string hex) { length = WriteHexBytes(hex, prefix, governor); }
            else
            {
                length = Math.Min(_bytes.Length, prefix.Length);
                _bytes.Span[..length].CopyTo(prefix);
            }
            return PasswordEncodingRecovery.TryConvert(prefix[..length], destination, candidate, governor, out count);
        }
        finally { CryptographicOperations.ZeroMemory(prefix); }
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
        PasswordPrefixDecoder decoder = new(true);
        for (int index = 0; index < text.Length; index++)
        {
            if ((index & 255) == 0) { governor.CheckCancellation(); }
            decoder.Push(text[index], destination);
            if (decoder.Complete(destination.Length)) { break; }
        }
        return decoder.Finish(destination, text.Length, false);
    }
}

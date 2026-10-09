using System.Globalization;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Admits display capacities, growth overlap and the final immutable copy.</summary>
internal sealed class DisplayBuffer : IDisposable
{
    private const long OwnerBytes = 64L;
    private readonly ResourceGovernor _governor;
    private readonly bool _diagnostic;
    private readonly bool _compact;
    private TextChunk? _head;
    private TextChunk? _current;
    private int _length;
    private long _utf8Bytes;
    private long _reservedBytes;
    private bool _highSurrogate;
    private bool _disposed;

    internal ResourceGovernor Governor => _governor;

    internal long Utf8Bytes => _utf8Bytes;

    private DisplayBuffer(ResourceGovernor governor, bool diagnostic, bool compact)
    {
        _governor = governor;
        _diagnostic = diagnostic;
        _compact = compact;
        _reservedBytes = OwnerBytes;
    }

    internal static DisplayBuffer Create(ResourceGovernor governor, bool diagnostic)
        => CreateCore(governor, diagnostic, false);

    internal static DisplayBuffer CreateJson(ResourceGovernor governor)
        => CreateCore(governor, false, true);

    private static DisplayBuffer CreateCore(ResourceGovernor governor, bool diagnostic, bool compact)
    {
        governor.CheckCancellation();
        if (diagnostic) { governor.CheckDiagnostic(0L); }
        governor.ReserveMemory(OwnerBytes);
        try { return new DisplayBuffer(governor, diagnostic, compact); }
        catch { governor.ReleaseMemory(OwnerBytes); throw; }
    }

    internal void Append(char value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // UTF-8 uses replacement for isolated surrogates. A high/low pair uses
        // four bytes: three admitted for its high unit, then one for its low.
        int bytes = value <= 127 ? 1 : value <= 2047 ? 2
            : _highSurrogate && char.IsLowSurrogate(value) ? 1 : 3;
        long nextBytes = checked(_utf8Bytes + bytes);
        if (_diagnostic) { _governor.CheckDiagnostic(nextBytes); }
        if (_length >= Array.MaxLength)
        {
            throw new QpdfLimitException(nameof(QpdfLimits.MaxMemoryBytes), checked(2L * (_length + 1L)), 2L * Array.MaxLength);
        }
        TextChunk chunk = EnsureCapacity(value);
        chunk.Append(value);
        _length++;
        _utf8Bytes = nextBytes;
        _highSurrogate = char.IsHighSurrogate(value);
    }

    internal void Append(string text)
    {
        AppendText(text);
    }

    internal void AppendText(string text)
        => AppendSpan(text.AsSpan());

    internal void AppendSpan(ReadOnlySpan<char> text)
    {
        for (int offset = 0; offset < text.Length; offset += Math.Min(256, text.Length - offset))
        {
            int count = Math.Min(256, text.Length - offset);
            _governor.ChargeWork(count);
            _governor.CheckCancellation();
            for (int i = offset; i < offset + count; i++) { Append(text[i]); }
        }
    }

    internal void AppendParts(ReadOnlySpan<DiagnosticPart> parts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Span<char> digits = stackalloc char[20];
        Span<char> escaped = stackalloc char[3];
        foreach (DiagnosticPart part in parts)
        {
            _governor.ChargeWork(1L);
            _governor.CheckCancellation();
            int written = part.WriteNumber(_governor, digits);
            AppendSpan(part.Prefix);
            AppendSpan(digits.Slice(digits.Length - written, written));
            foreach (byte value in part.RawName)
            {
                _governor.ChargeWork(1L);
                int count = PdfName.WriteDisplayByte(value, escaped);
                AppendSpan(escaped.Slice(0, count));
            }
        }
    }

    internal void AppendInteger(long value)
    {
        Span<char> digits = stackalloc char[20];
        if (!value.TryFormat(digits, out int written, provider: CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Integer formatting exceeded its bounded buffer.");
        }
        for (int i = 0; i < written; i++) { Append(digits[i]); }
    }

    internal GovernedText Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_diagnostic) { _governor.CheckDiagnostic(_utf8Bytes); }
        return GovernedText.Create(_governor, _head, _length, _utf8Bytes);
    }

    private TextChunk EnsureCapacity(char value)
    {
        if (_current is not null && _current.Used < _current.Capacity && _current.Accepts(value)) { return _current; }
        _governor.CheckCancellation();
        // JSON overlaps a live reader and diagnostics. Bound the final chunk's
        // slack to 511 characters while retaining linear allocation/copy work.
        int capacity = _current is null || !_current.Accepts(value) ? 128 : Math.Min(_compact ? 512 : 8192, 2 * _current.Capacity);
        // Keep full chunks until final-copy completion. New chunks do not copy
        // or replace old capacities, avoiding quadratic work and large slack.
        bool compact = _compact && value <= 255;
        long reserved = checked(64L + 32L + (compact ? 1L : 2L) * capacity);
        _governor.ReserveMemory(reserved);
        try
        {
            _governor.ChargeWork(capacity);
            _governor.CheckCancellation();
            TextChunk next = compact ? new CompactChunk(capacity) : new TextChunk(capacity);
            if (_current is null) { _head = next; }
            else { _current.Next = next; }
            _current = next;
            _reservedBytes = checked(_reservedBytes + reserved);
            return next;
        }
        catch
        {
            _governor.ReleaseMemory(reserved);
            throw;
        }
    }

    // Preserve raw UTF-16 values in wide chunks, including unmatched
    // surrogates. The usual ASCII/Latin-1 JSON fragments need only one byte
    // per character while the reader and its repair scratch remain live.
    private sealed class CompactChunk(int capacity) : TextChunk
    {
        private readonly byte[] _bytes = new byte[capacity];
        internal override int Capacity => _bytes.Length;
        internal override bool Accepts(char value) => value <= 255;
        internal override void Append(char value) { _bytes[Used++] = (byte)value; }
        internal override void CopyTo(int offset, Span<char> target)
        {
            for (int index = 0; index < target.Length; index++) { target[index] = (char)_bytes[offset + index]; }
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _head = null;
        _current = null;
        _governor.ReleaseMemory(_reservedBytes);
        _reservedBytes = 0L;
    }
}

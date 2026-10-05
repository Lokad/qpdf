using System.Globalization;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Admits display capacities, growth overlap and the final immutable copy.</summary>
internal sealed class DisplayBuffer : IDisposable
{
    private const long OwnerBytes = 64L;
    private readonly ResourceGovernor _governor;
    private readonly bool _diagnostic;
    private TextChunk? _head;
    private TextChunk? _current;
    private int _length;
    private long _utf8Bytes;
    private long _reservedBytes;
    private bool _highSurrogate;
    private bool _disposed;

    private DisplayBuffer(ResourceGovernor governor, bool diagnostic)
    {
        _governor = governor;
        _diagnostic = diagnostic;
        _reservedBytes = OwnerBytes;
    }

    internal static DisplayBuffer Create(ResourceGovernor governor, bool diagnostic)
    {
        governor.CheckCancellation();
        if (diagnostic) { governor.CheckDiagnostic(0L); }
        governor.ReserveMemory(OwnerBytes);
        try { return new DisplayBuffer(governor, diagnostic); }
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
        TextChunk chunk = EnsureCapacity();
        chunk.Buffer[chunk.Used++] = value;
        _length++;
        _utf8Bytes = nextBytes;
        _highSurrogate = char.IsHighSurrogate(value);
    }

    internal void Append(string text)
    {
        AppendText(text);
    }

    internal void AppendText(string text)
    {
        for (int offset = 0; offset < text.Length; offset += Math.Min(256, text.Length - offset))
        {
            int count = Math.Min(256, text.Length - offset);
            _governor.ChargeWork(count);
            _governor.CheckCancellation();
            for (int i = offset; i < offset + count; i++) { Append(text[i]); }
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

    private TextChunk EnsureCapacity()
    {
        if (_current is not null && _current.Used < _current.Buffer.Length) { return _current; }
        _governor.CheckCancellation();
        int capacity = _current is null ? 128 : Math.Min(8192, 2 * _current.Buffer.Length);
        // Keep full chunks until final-copy completion. New chunks do not copy
        // or replace old capacities, avoiding quadratic work and large slack.
        long reserved = checked(64L + 32L + 2L * capacity);
        _governor.ReserveMemory(reserved);
        try
        {
            _governor.ChargeWork(capacity);
            _governor.CheckCancellation();
            TextChunk next = new(capacity);
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

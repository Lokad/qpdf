namespace Lokad.Qpdf.Security;

// Bounded command-password decoding. Public raw-byte credentials bypass this
// native command convention (first NUL terminates the password).
internal struct PasswordPrefixDecoder
{
    private readonly bool _hex;
    private int _count;
    private int _high;
    private bool _terminated;

    internal PasswordPrefixDecoder(bool hex) { _hex = hex; _count = 0; _high = -1; _terminated = false; }
    internal bool Complete(int capacity) => _terminated || _count == capacity;

    internal void Push(char value, Span<byte> destination)
    {
        if (Complete(destination.Length)) { return; }
        int decoded = value;
        if (_hex)
        {
            int digit = value is >= '0' and <= '9' ? value - '0'
                : value is >= 'A' and <= 'F' ? value - 'A' + 10
                : value is >= 'a' and <= 'f' ? value - 'a' + 10 : -1;
            if (digit < 0) { return; }
            if (_high < 0) { _high = digit; return; }
            decoded = (_high << 4) | digit; _high = -1;
        }
        if (decoded == 0) { _terminated = true; return; }
        destination[_count++] = (byte)decoded;
    }

    internal int Finish(Span<byte> destination, long lineBytes, bool newline)
    {
        if (_hex)
        {
            if (!Complete(destination.Length) && _high > 0) { destination[_count++] = (byte)(_high << 4); }
            _high = -1;
        }
        else if (newline && !_terminated && lineBytes == _count && _count > 0 && destination[_count - 1] == 13)
        { destination[--_count] = 0; }
        return _count;
    }
}

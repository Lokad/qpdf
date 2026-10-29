using System.Text;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Writing;

namespace Lokad.Qpdf.Json;

/// <summary>Owns JSON text and every publication candidate through failure or public escape.</summary>
internal sealed class PreparedJsonOutput : IDisposable
{
    private readonly ResourceGovernor _governor;
    private ParserMemoryScope? _memory;
    private string? _text;
    private RetainedStorage.Receipt? _textReceipt;
    private bool _textEscaped;
    internal List<JsonPayload> Sidecars { get; }
    internal JsonPayload? Main { get; private set; }

    private PreparedJsonOutput(ResourceGovernor governor, ParserMemoryScope memory, List<JsonPayload> sidecars)
    { _governor = governor; _memory = memory; Sidecars = sidecars; }

    internal static PreparedJsonOutput Create(ResourceGovernor governor)
    {
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(80L);
            return new(governor, memory, ParserContainers.CreateList<JsonPayload>(memory));
        }
        catch { memory.Dispose(); throw; }
    }

    private ParserMemoryScope Memory => _memory ?? throw new ObjectDisposedException(nameof(PreparedJsonOutput));

    // These transient vectors share the already admitted serialization scope.
    // A second scope wrapper would overlap parser repair scratch unnecessarily.
    internal List<int> CreateObjectNumbers() => ParserContainers.CreateList<int>(Memory);

    internal void AddObjectNumber(List<int> numbers, int value)
        => ParserContainers.AddItem(Memory, numbers, value, 4L);

    internal void ReleaseObjectNumbers(List<int> numbers)
    {
        long reserved = 32L + (numbers.Capacity == 0 ? 0L : 32L + 4L * numbers.Capacity);
        numbers.Clear(); numbers.Capacity = 0;
        Memory.Release(reserved);
    }

    internal void Prepare(GovernedText text, bool publishMain, bool escapeText, QpdfLimits limits)
    {
        text.CheckOwner(_governor);
        if (text.Utf8Bytes > limits.MaxOutputBytes)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), text.Utf8Bytes, limits.MaxOutputBytes); }
        if (publishMain)
        {
            if (text.Utf8Bytes > Array.MaxLength)
            { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), text.Utf8Bytes, Array.MaxLength); }
            int length = checked((int)text.Utf8Bytes);
            long reserved = ArrayBytes(length);
            Memory.Reserve(reserved);
            _governor.CheckCancellation();
            byte[] bytes = length == 0 ? Array.Empty<byte>() : new byte[length];
            int written = 0;
            ReadOnlySpan<char> remaining = text.Value;
            while (!remaining.IsEmpty)
            {
                int count = Math.Min(256, remaining.Length);
                if (count < remaining.Length && char.IsHighSurrogate(remaining[count - 1]) && char.IsLowSurrogate(remaining[count])) { count--; }
                _governor.CheckCancellation();
                written += Encoding.UTF8.GetBytes(remaining[..count], bytes.AsSpan(written));
                remaining = remaining[count..];
            }
            if (written != length) { throw new InvalidOperationException("JSON UTF-8 sizing disagrees with encoding."); }
            Main = JsonPayload.Create(_governor, Memory, bytes, reserved, null);
        }
        if (escapeText)
        {
            _textReceipt = _governor.RetainAllocation(text.StringReservation);
            _text = text.Value;
            text.TransferToRetainedStorage(_governor);
        }
    }

    internal JsonPayload AddSidecar(string prefix, int number, ReadOnlySpan<byte> bytes, QpdfLimits limits)
    {
        _governor.CheckCancellation();
        Span<byte> digits = stackalloc byte[20];
        int length = WriterNumber.Format(number, digits, _governor);
        if (prefix.Length > QpdfPath.MaxLength - 1 - length || prefix.Contains('\0')
            || !GovernedText.IsValidUtf16(prefix, _governor))
        { throw new QpdfParseException("JSON stream file name is not a valid logical path.", null, null); }
        if (bytes.Length > limits.MaxOutputBytes)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), bytes.Length, limits.MaxOutputBytes); }
        using DisplayBuffer nameBuffer = DisplayBuffer.Create(_governor, false);
        nameBuffer.Append(prefix); nameBuffer.Append('-');
        foreach (byte digit in digits[..length]) { nameBuffer.Append((char)digit); }
        using GovernedText name = nameBuffer.Finish();
        long reserved = ArrayBytes(bytes.Length);
        Memory.Reserve(reserved);
        _governor.CheckCancellation();
        byte[] copy = bytes.Length == 0 ? Array.Empty<byte>() : new byte[bytes.Length];
        GovernedBufferStream.CopyBytes(bytes, copy, _governor, CancellationToken.None, CancellationToken.None);
        JsonPayload payload = JsonPayload.Create(_governor, Memory, copy, reserved, name);
        try
        {
            // Capacity growth copies the old references before retiring them.
            _governor.CheckCancellation();
            ParserContainers.AddItem(Memory, Sidecars, payload, IntPtr.Size);
            return payload;
        }
        catch { payload.Dispose(); throw; }
    }

    internal string TransferText()
    {
        _ = Memory;
        if (_textReceipt is not null && !_textEscaped) { _governor.PromoteAllocation(_textReceipt); }
        _textEscaped = true;
        return _text ?? string.Empty;
    }

    private static long ArrayBytes(int length) => length == 0 ? 0L : (24L + length + 7L) & ~7L;

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        if (memory is null) { return; }
        _memory = null;
        Main?.Dispose(); Main = null;
        foreach (JsonPayload payload in Sidecars) { payload.Dispose(); }
        Sidecars.Clear();
        Sidecars.Capacity = 0;
        if (!_textEscaped && _text is not null && _textReceipt is not null)
        { _governor.DropAllocation(_textReceipt); }
        _text = null; _textReceipt = null;
        memory.Dispose();
    }
}

/// <summary>Keeps a private publication candidate funded through host writes and cleanup.</summary>
internal sealed class JsonPayload : IDisposable
{
    private readonly ResourceGovernor _governor;
    private RetainedStorage.Receipt? _dataReceipt, _nameReceipt;
    internal string Name { get; private set; }
    internal byte[] Data { get; private set; }

    private JsonPayload(ResourceGovernor governor, byte[] data, string name)
    { _governor = governor; Data = data; Name = name; }

    internal static JsonPayload Create(ResourceGovernor governor, ParserMemoryScope memory,
        byte[] bytes, long byteReservation, GovernedText? name)
    {
        governor.CheckCancellation();
        memory.Reserve(64L);
        JsonPayload payload = new(governor, bytes, name?.Value ?? string.Empty);
        try
        {
            if (byteReservation != 0L) { payload._dataReceipt = memory.RetainAllocationReceipt(byteReservation); }
            if (name is not null)
            {
                payload._nameReceipt = governor.RetainAllocation(name.StringReservation);
                name.TransferToRetainedStorage(governor);
            }
            return payload;
        }
        catch { payload.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_dataReceipt is not null) { _governor.DropAllocation(_dataReceipt); }
        if (_nameReceipt is not null) { _governor.DropAllocation(_nameReceipt); }
        _dataReceipt = null; _nameReceipt = null; Data = Array.Empty<byte>(); Name = string.Empty;
    }
}

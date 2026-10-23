using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Json;

internal static partial class JsonImport
{
    private static QpdfObject DecodeNumber(JsonElement value, ResourceGovernor governor,
        ParserMemoryScope memory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); governor.CheckCancellation();
        ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(value);
        int exponentAt = raw.IndexOfAny((byte)'e', (byte)'E');
        if (exponentAt >= 0) { return ExpandNumber(raw, exponentAt, governor, memory, token); }
        if (!raw.Contains((byte)'.'))
        {
            if (raw.Length > 20 || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
            { throw new QpdfParseException("qpdf JSON integer exceeds the supported 64-bit range.", 0L, null); }
            return new PdfInteger(value.GetRawText(), integer);
        }
        double real = PdfRealConversion.Convert(raw, 0L, governor);
        if (!double.IsFinite(real))
        { throw new QpdfParseException("qpdf JSON real exceeds the supported finite range.", 0L, null); }
        token.ThrowIfCancellationRequested();
        return new PdfReal(value.GetRawText(), real);
    }

    private static QpdfObject ExpandNumber(ReadOnlySpan<byte> raw, int exponentAt,
        ResourceGovernor governor, ParserMemoryScope memory, CancellationToken token)
    {
        int exponentStart = exponentAt + 1;
        bool negativeExponent = raw[exponentStart] == (byte)'-';
        if (raw[exponentStart] is (byte)'+' or (byte)'-') { exponentStart++; }
        long exponent = 0L;
        long bound = negativeExponent ? -(long)int.MinValue : int.MaxValue;
        for (int i = exponentStart; i < raw.Length; i++)
        {
            if ((i & 4095) == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
            exponent = exponent * 10L + raw[i] - (byte)'0';
            if (exponent > bound)
            { throw new QpdfParseException("qpdf JSON exponent exceeds the supported 32-bit range.", 0L, null); }
        }
        if (negativeExponent) { exponent = -exponent; }
        bool negative = raw[0] == (byte)'-';
        int start = negative ? 1 : 0;
        ReadOnlySpan<byte> mantissa = raw[start..exponentAt];
        bool nonzero = false;
        for (int i = 0; i < mantissa.Length; i++)
        {
            if ((i & 4095) == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
            if (mantissa[i] is >= (byte)'1' and <= (byte)'9') { nonzero = true; break; }
        }
        if (!nonzero) { return new PdfInteger(negative ? "-0" : "0", 0L); }
        int point = mantissa.IndexOf((byte)'.');
        int digits = mantissa.Length - (point >= 0 ? 1 : 0);
        long shifted = (point >= 0 ? point : digits) + exponent;
        long length = start + (shifted <= 0 ? 2L - shifted + digits
            : shifted >= digits ? shifted : digits + 1L);
        // Two spare bytes keep an integral real outside Int64 readable as a real.
        if (length > Array.MaxLength - 2L)
        { throw new QpdfParseException("Expanded qpdf JSON number exceeds managed storage capacity.", 0L, null); }
        int capacity = (int)length + 2;
        // The model estimate already covers the source token. Admit only growth;
        // the temporary byte packet overlaps its final immutable string.
        memory.Reserve(2L * Math.Max(0L, capacity - raw.Length));
        long scratch = GovernedBufferStream.ArrayOverheadBytes + capacity;
        governor.ReserveMemory(scratch);
        try
        {
            byte[] bytes = GC.AllocateUninitializedArray<byte>(capacity);
            for (int offset = 0; offset < capacity; offset += Math.Min(4096, capacity - offset))
            {
                token.ThrowIfCancellationRequested(); governor.CheckCancellation();
                bytes.AsSpan(offset, Math.Min(4096, capacity - offset)).Fill((byte)'0');
            }
            if (negative) { bytes[0] = (byte)'-'; }
            int destination = start;
            if (shifted <= 0) { bytes[start + 1] = (byte)'.'; destination += checked((int)(2L - shifted)); }
            int digit = 0;
            for (int i = 0; i < mantissa.Length; i++)
            {
                if ((i & 4095) == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
                if (mantissa[i] == (byte)'.') { continue; }
                if (shifted > 0 && shifted < digits && digit == shifted) { bytes[destination++] = (byte)'.'; }
                bytes[destination++] = mantissa[i]; digit++;
            }
            int used = (int)length;
            bool fractional = shifted < digits;
            if (fractional)
            {
                while (bytes[used - 1] == (byte)'0')
                {
                    if ((used & 4095) == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
                    used--;
                }
                if (bytes[used - 1] == (byte)'.') { used--; fractional = false; }
            }
            int first = start;
            while (first + 1 < used && bytes[first] == (byte)'0' && bytes[first + 1] != (byte)'.')
            {
                if ((first & 4095) == 0) { token.ThrowIfCancellationRequested(); governor.CheckCancellation(); }
                first++;
            }
            if (negative) { bytes[--first] = (byte)'-'; }
            long integer = 0L;
            bool integral = !fractional && used - first <= 20
                && long.TryParse(bytes.AsSpan(first, used - first), NumberStyles.Integer, CultureInfo.InvariantCulture, out integer);
            if (!fractional && !integral) { bytes[used++] = (byte)'.'; bytes[used++] = (byte)'0'; }
            double real = integral ? 0.0 : PdfRealConversion.Convert(bytes.AsSpan(first, used - first), 0L, governor);
            if (!double.IsFinite(real))
            { throw new QpdfParseException("qpdf JSON real exceeds the supported finite range.", 0L, null); }
            string text = string.Create(used - first, (Bytes: bytes, Offset: first, Governor: governor, Token: token), static (target, state) =>
            {
                for (int offset = 0; offset < target.Length; offset += Math.Min(4096, target.Length - offset))
                {
                    state.Token.ThrowIfCancellationRequested(); state.Governor.CheckCancellation();
                    int count = Math.Min(4096, target.Length - offset);
                    for (int i = 0; i < count; i++) { target[offset + i] = (char)state.Bytes[state.Offset + offset + i]; }
                }
            });
            return integral ? new PdfInteger(text, integer) : new PdfReal(text, real);
        }
        finally { governor.ReleaseMemory(scratch); }
    }
}

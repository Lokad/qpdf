using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Objects;

/// <summary>Shared display escaping and ordinal comparison over borrowed name bytes.</summary>
internal static class PdfNameDisplay
{
    internal static int WriteByte(byte value, Span<char> target)
    {
        if (value >= 0x21 && value <= 0x7E && value != (byte)'#')
        {
            target[0] = (char)value;
            return 1;
        }
        target[0] = '#'; target[1] = "0123456789ABCDEF"[value >> 4]; target[2] = "0123456789ABCDEF"[value & 15];
        return 3;
    }

    internal static int Compare(PdfName left, PdfName right, ResourceGovernor governor, CancellationToken token)
    {
        ReadOnlySpan<byte> leftRaw = left.RawSpan, rightRaw = right.RawSpan;
        Span<char> leftText = stackalloc char[3], rightText = stackalloc char[3];
        int leftByte = 0, rightByte = 0, leftChar = 0, rightChar = 0, leftLength = 0, rightLength = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested(); governor.CheckCancellation(); governor.ChargeWork(1L);
            if (leftChar == leftLength)
            {
                if (leftByte == leftRaw.Length) { return rightChar < rightLength || rightByte < rightRaw.Length ? -1 : 0; }
                leftLength = WriteByte(leftRaw[leftByte++], leftText); leftChar = 0;
            }
            if (rightChar == rightLength)
            {
                if (rightByte == rightRaw.Length) { return 1; }
                rightLength = WriteByte(rightRaw[rightByte++], rightText); rightChar = 0;
            }
            int result = leftText[leftChar++] - rightText[rightChar++];
            if (result != 0) { return result; }
        }
    }
}

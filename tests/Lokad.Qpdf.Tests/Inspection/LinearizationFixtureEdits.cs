using System.Text;

namespace Lokad.Qpdf.Tests.Inspection;

internal static class LinearizationFixtureEdits
{
    internal static byte[] ReplaceUnique(byte[] input, string needle, string replacement)
    {
        byte[] needleBytes = Encoding.ASCII.GetBytes(needle);
        byte[] replacementBytes = Encoding.ASCII.GetBytes(replacement);
        Assert.Equal(needleBytes.Length, replacementBytes.Length);
        int at = input.AsSpan().IndexOf(needleBytes);
        Assert.True(at >= 0, $"Missing fixture bytes '{needle}'.");
        Assert.Equal(-1, input.AsSpan(at + 1).IndexOf(needleBytes));
        byte[] output = (byte[])input.Clone();
        replacementBytes.AsSpan().CopyTo(output.AsSpan(at));
        return output;
    }
}

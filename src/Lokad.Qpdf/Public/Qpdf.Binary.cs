using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    private static void AppendInspectionBytes(GovernedBufferStream output, ReadOnlyMemory<byte> payload, ResourceGovernor governor)
    {
        governor.ChargeOutputBytes(payload.Length);
        output.Write(payload.Span);
    }
}

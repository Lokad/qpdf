using System.Buffers.Text;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Formats a bounded invariant decimal integer directly into PDF bytes.</summary>
internal static class WriterNumber
{
    internal static int Format(long value, Span<byte> target, ResourceGovernor governor)
    {
        governor.ChargeWork(20L);
        if (!Utf8Formatter.TryFormat(value, target, out int written))
        {
            throw new QpdfException("PDF integer exceeded its bounded formatting buffer.");
        }
        return written;
    }
}

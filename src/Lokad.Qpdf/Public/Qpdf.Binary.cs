using Lokad.Qpdf.Attachments;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

public static partial class Qpdf
{
    private static void AppendInspectionBytes(GovernedBufferStream output, ReadOnlyMemory<byte> payload, ResourceGovernor governor)
    {
        governor.ChargeOutputBytes(payload.Length);
        output.Write(payload.Span);
    }

    private static ReadOnlyMemory<byte> ReadAttachmentData(
        QpdfDocument document, string key, QpdfLimits limits, ResourceGovernor governor,
        DiagnosticBag diagnostics, ParserMemoryScope memory, CancellationToken cancellationToken)
    {
        List<EmbeddedAttachment> found = EmbeddedFiles.ReadAttachments(document.Engine, limits, governor, diagnostics, cancellationToken);
        foreach (EmbeddedAttachment attachment in found)
        {
            governor.CheckCancellation();
            if (string.Equals(attachment.Key, key, StringComparison.Ordinal))
            {
                return EmbeddedFiles.ExtractData(attachment.Stream, attachment.StreamObjectNumber, document.Engine, limits, governor, diagnostics, memory, cancellationToken);
            }
        }
        throw new QpdfParseException("The requested attachment was not found.", 0L, null);
    }
}

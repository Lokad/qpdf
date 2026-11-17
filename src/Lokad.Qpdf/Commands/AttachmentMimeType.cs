using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Commands;

internal static class AttachmentMimeType
{
    // Native command setters require a slash, without imposing a MIME grammar.
    internal static void RequireCommand(string value, ResourceGovernor governor)
    {
        governor.CheckCancellation();
        for (int offset = 0; offset < value.Length;)
        {
            governor.CheckCancellation();
            int count = Math.Min(256, value.Length - offset);
            if (value.AsSpan(offset, count).Contains('/')) { return; }
            offset += count;
        }
        throw new QpdfParseException("Attachment MIME type must be specified as type/subtype.", null, null);
    }
}

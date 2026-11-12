using Lokad.Qpdf.Commands;

namespace Lokad.Qpdf.Jobs;

internal static class EncryptionPermissions
{
    internal const int ModificationBits = 0x528;

    internal static int ModificationMask(string value) => value switch
    {
        "none" => 0,
        "assembly" => 0x400,
        "form" => 0x500,
        "annotate" => 0x520,
        "all" => ModificationBits,
        _ => throw new QpdfParseException("Encryption modify must be none, assembly, form, annotate or all.", 0L, null),
    };

    internal static int Apply(int permissions, string name, string value)
    {
        if (name == "modify") { return (permissions & ~ModificationBits) | ModificationMask(value); }
        if (name == "print")
        {
            int bits = value switch
            {
                "none" => 0, "low" => 4, "full" => 0x804,
                _ => throw new QpdfParseException("Encryption print must be none, low or full.", 0L, null),
            };
            return (permissions & ~0x804) | bits;
        }
        if (value is not ("y" or "n"))
        { throw new QpdfParseException("Encryption permission values must be y or n.", 0L, null); }
        int bit = name switch
        {
            "accessibility" => 0, // Revision 6 always permits accessibility.
            "extract" => 0x10, "annotate" => 0x20, "form" => 0x100,
            "assemble" => 0x400, "modifyOther" => 8,
            _ => throw new QpdfParseException("Unknown encryption permission.", 0L, null),
        };
        return value == "y" ? permissions | bit : permissions & ~bit;
    }
}

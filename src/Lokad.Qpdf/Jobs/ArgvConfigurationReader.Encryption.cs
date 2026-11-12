using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Jobs;

internal static partial class ArgvConfigurationReader
{
    private static Dictionary<string, object> ReadEncryption(IReadOnlyList<string> argv, ref int cursor, ResourceGovernor governor)
    {
        int end = EncryptionArgumentBlock.FindEnd(argv, cursor, governor);
        int index = cursor + 1;
        string user = string.Empty, owner = string.Empty;
        string? bits = null;
        if (index < end && !argv[index].StartsWith("--", StringComparison.Ordinal))
        {
            if (end - index < 3) { throw Invalid("Encryption requires user password, owner password and key length.", cursor); }
            user = argv[index++]; owner = argv[index++]; bits = argv[index++];
        }
        else
        {
            while (index < end)
            {
                governor.CheckCancellation();
                ReadOnlySpan<char> name = ArgvParser.OptionName(argv[index]);
                if (name is not ("user-password" or "owner-password" or "bits"))
                { throw Invalid("Encryption requires --bits after any password options.", index); }
                string value = EncryptionValue(argv, ref index, end);
                if (name is "user-password") { user = value; }
                else if (name is "owner-password") { owner = value; }
                else { bits = value; index++; break; }
                index++;
            }
        }
        if (bits is "40" or "128") { throw new QpdfCryptoException("--encrypt"); }
        if (bits != "256") { throw Invalid("Encryption key length must be 256 in this profile.", cursor); }
        Dictionary<string, object> options = new(StringComparer.Ordinal);
        for (; index < end; index++)
        {
            governor.CheckCancellation();
            string token = argv[index];
            ReadOnlySpan<char> name = ArgvParser.OptionName(token);
            if (!token.StartsWith("--", StringComparison.Ordinal)) { throw Invalid("Invalid encryption option.", index); }
            if (name is "cleartext-metadata") { options["cleartextMetadata"] = Bare(token, index); continue; }
            if (name is "allow-insecure" or "force-R5") { throw new QpdfCryptoException("encryption option"); }
            if (name is not ("accessibility" or "annotate" or "assemble" or "extract" or "form" or "modify-other" or "modify" or "print"))
            {
                if (ArgvParser.IsCryptoOption(name)) { throw new QpdfCryptoException("encryption option"); }
                throw Invalid("Unknown encryption option.", index);
            }
            string key = Key(name), value = EncryptionValue(argv, ref index, end);
            _ = EncryptionPermissions.Apply(-4, key, value);
            if (key == "modify")
            {
                int mask = EncryptionPermissions.ModificationMask(value);
                options["assemble"] = (mask & 0x400) != 0 ? "y" : "n";
                options["form"] = (mask & 0x100) != 0 ? "y" : "n";
                options["annotate"] = (mask & 0x20) != 0 ? "y" : "n";
                options["modifyOther"] = (mask & 8) != 0 ? "y" : "n";
            }
            else { options[key] = value; }
        }
        cursor = end + 1;
        return new() { ["userPassword"] = user, ["ownerPassword"] = owner, ["256bit"] = options };
    }

    private static string EncryptionValue(IReadOnlyList<string> argv, ref int index, int end)
    {
        string token = argv[index];
        int equals = token.IndexOf('=');
        if (equals >= 0) { return ConfigurationText.Slice(argv, token, equals + 1); }
        if (index + 1 >= end || argv[index + 1].StartsWith("--", StringComparison.Ordinal))
        { throw Invalid("Missing encryption option parameter.", index); }
        return argv[++index];
    }
}

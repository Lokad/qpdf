using System.Text.Json;
using Lokad.Qpdf.Commands;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Jobs;

internal static partial class JobPlanner
{
    private static void ParseEncryption(JobValue value, ref string user, ref string owner,
        ref int permissions, ref bool metadata, ResourceGovernor governor, ConfigurationText textStorage)
    {
        if (value.ValueKind != JsonValueKind.Object)
        { throw new QpdfParseException("Job encrypt must be an object.", 0L, null); }
        user = string.Empty; owner = string.Empty;
        foreach (JobSetting property in value.EnumerateObject())
        {
            governor.CheckCancellation(); governor.NoteOperation();
            switch (property.Name)
            {
                case "userPassword": case "ownerPassword": case "256bit": break;
                case "40bit": case "128bit": throw new QpdfCryptoException("encrypt");
                default: throw new QpdfParseException("Unknown job encrypt key.", 0L, null);
            }
        }
        if (value.TryGetProperty("userPassword", out JobValue userValue)) { user = RequireString(userValue, "userPassword", textStorage); }
        if (value.TryGetProperty("ownerPassword", out JobValue ownerValue)) { owner = RequireString(ownerValue, "ownerPassword", textStorage); }
        if (!value.TryGetProperty("256bit", out JobValue options) || options.ValueKind != JsonValueKind.Object)
        { throw new QpdfParseException("Job encrypt requires a 256bit object.", 0L, null); }
        // Native job JSON applies keys in lexical order. The argv adapter expands
        // modify into individual permissions so argv retains its setter order.
        // Only the eleven known keys enter this bounded map. Last duplicate wins.
        // Configuration admission already funds these temporary collections.
        SortedDictionary<string, JobSetting> setters = new(StringComparer.Ordinal);
        foreach (JobSetting property in options.EnumerateObject())
        {
            governor.CheckCancellation(); governor.NoteOperation();
            if (property.Name is not ("cleartextMetadata" or "forceR5" or "allowInsecure" or "accessibility"
                or "annotate" or "assemble" or "extract" or "form" or "modifyOther" or "modify" or "print"))
            { throw new QpdfParseException("Unknown 256-bit encryption option.", 0L, null); }
            setters[property.Name] = property;
        }
        foreach (JobSetting property in setters.Values)
        {
            governor.CheckCancellation(); governor.NoteOperation();
            switch (property.Name)
            {
                case "cleartextMetadata": metadata = !RequireBareFlag(property.Value, "cleartextMetadata", textStorage); break;
                case "forceR5": case "allowInsecure":
                    if (RequireBareFlag(property.Value, property.Name, textStorage)) { throw new QpdfCryptoException(property.Name); }
                    break;
                case "accessibility": case "annotate": case "assemble": case "extract": case "form":
                case "modifyOther": case "modify": case "print":
                    permissions = EncryptionPermissions.Apply(permissions, property.Name, RequireString(property.Value, property.Name, textStorage));
                    break;
                default: throw new QpdfParseException("Unknown 256-bit encryption option.", 0L, null);
            }
        }
    }
}

using Lokad.Qpdf.Objects;
using Lokad.Qpdf.Reading;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Syntax;
using Lokad.Qpdf.Security;

namespace Lokad.Qpdf.Writing;

internal static class OutputVersionPolicy
{
    internal static (PdfVersion Version, int Extension) RequireObjectStreamVersion(
        PdfVersion version, int extension, bool hasCompressed, ResourceGovernor governor)
        => hasCompressed && PdfVersion.CompareParts(version.Major, version.Minor, 1, 5) < 0
            ? (governor.PrivateVersion(1, 5), 0) : (version, extension);

    internal static (int Major, int Minor, int Extension) ParseWriterVersion(string text, string option)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        // The component bounds below allow at most 4 + 1 + 4 + 1 + 6 characters.
        if (text.Length > 4 + 1 + 4 + 1 + 6)
        {
            throw new QpdfParseException(option + " requires a version such as 1.5.", 0L, null);
        }

        string[] parts = text.Split((char)46);
        if (parts.Length < 2 || parts.Length > 3)
        {
            throw new QpdfParseException(option + " requires a version such as 1.5.", 0L, null);
        }

        if (parts[0].Length == 0 || parts[0].Length > 4 || parts[1].Length == 0 || parts[1].Length > 4)
        {
            throw new QpdfParseException(option + " requires a version such as 1.5.", 0L, null);
        }

        int major = ParseVersionComponent(parts[0], option);
        int minor = ParseVersionComponent(parts[1], option);
        int extension = 0;
        if (parts.Length == 3)
        {
            if (parts[2].Length == 0 || parts[2].Length > 6)
            {
                throw new QpdfParseException(option + " requires a version such as 1.5.", 0L, null);
            }

            extension = ParseVersionComponent(parts[2], option);
        }

        bool supported = (major == 1 && minor >= 0 && minor <= 7) || (major == 2 && minor == 0);
        if (!supported)
        {
            throw new QpdfParseException(option + " supports versions 1.0 through 1.7 and 2.0.", 0L, null);
        }

        if (extension < 0)
        {
            throw new QpdfParseException(option + " requires a non-negative extension level.", 0L, null);
        }

        return (major, minor, extension);
    }

    internal static int ParseVersionComponent(string part, string option)
    {
        long value = 0L;
        foreach (char c in part)
        {
            if (c < (char)48 || c > (char)57)
            {
                throw new QpdfParseException(option + " requires a version such as 1.5.", 0L, null);
            }

            value = checked(value * 10L + (c - (char)48));
        }

        if (value > 999999L)
        {
            throw new QpdfParseException(option + " requires a version such as 1.5.", 0L, null);
        }

        return (int)value;
    }

    internal static QpdfDocument.VersionParts? AuthoredVersionFloor(bool filespec, bool embeddedFiles, bool labels)
    {
        int minor = filespec ? 7 : embeddedFiles ? 4 : labels ? 3 : 0;
        return minor == 0 ? null : new QpdfDocument.VersionParts(1, minor);
    }

    internal static void IncludeVersionRequirement(ref PdfVersion version, ref int extension,
        QpdfDocument.VersionParts? requirement, int requiredExtension, ResourceGovernor governor)
    {
        if (requirement is not QpdfDocument.VersionParts source) { return; }
        int order = PdfVersion.CompareParts(source.Major, source.Minor, version.Major, version.Minor);
        if (order > 0)
        {
            version = governor.PrivateVersion(source.Major, source.Minor);
            extension = requiredExtension;
        }
        else if (order == 0) { extension = Math.Max(extension, requiredExtension); }
    }

    internal static (PdfVersion Version, int Extension) NegotiateVersion(PdfVersion inputMax, int inputExtension, string? minText, string? forceText, ResourceGovernor governor)
    {
        if (inputMax is null)
        {
            throw new ArgumentNullException(nameof(inputMax));
        }

        if (forceText is not null)
        {
            (int major, int minor, int ext) = ParseWriterVersion(forceText, "--force-version");
            return (governor.PrivateVersion(major, minor), ext);
        }

        // The basic writer uses PDF 1.2 name escaping, compression and layout
        // syntax. A conservative profile floor avoids classifying opaque graphs.
        if (PdfVersion.CompareParts(inputMax.Major, inputMax.Minor, 1, 2) < 0)
        { inputMax = governor.PrivateVersion(1, 2); inputExtension = 0; }

        if (minText is null)
        {
            return (inputMax, inputExtension);
        }

        (int minMajor, int minMinor, int minExt) = ParseWriterVersion(minText, "--min-version");
        int order = PdfVersion.CompareParts(inputMax.Major, inputMax.Minor, minMajor, minMinor);
        if (order > 0)
        {
            return (inputMax, inputExtension);
        }

        if (order < 0)
        {
            return (governor.PrivateVersion(minMajor, minMinor), minExt);
        }

        return (inputMax, Math.Max(inputExtension, minExt));
    }


    internal static void RequireEncryptionVersion(OutputSecurity? security, ref PdfVersion version, ref int extension,
        string? forcedVersion, ResourceGovernor governor)
    {
        if (security is null) { return; }
        int minor = security.Revision >= 5 ? 7 : security.Revision == 4 ? 6 : security.Revision == 3 ? 4 : 3;
        int requiredExtension = security.Revision == 6 ? 8 : security.Revision == 5 ? 3 : 0;
        int order = PdfVersion.CompareParts(version.Major, version.Minor, 1, minor);
        if (forcedVersion is not null && (order < 0 || order == 0 && extension < requiredExtension))
        { throw new QpdfUnsupportedFeatureException("The forced PDF version cannot support the requested output encryption."); }
        IncludeVersionRequirement(ref version, ref extension, new QpdfDocument.VersionParts(1, minor), requiredExtension, governor);
    }

}

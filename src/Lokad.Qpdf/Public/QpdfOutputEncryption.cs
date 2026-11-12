using System.Security.Cryptography;
using Lokad.Qpdf.Runtime;
using Lokad.Qpdf.Security;

namespace Lokad.Qpdf;

/// <summary>Preserves, removes or replaces password encryption on a derived PDF.</summary>
/// <remarks>Credentials are borrowed until the operation completes. This immutable
/// request holds no encryption key. Formatting never reveals its passwords.</remarks>
public sealed class QpdfOutputEncryption
{
    private enum Policy : byte { Preserve, Remove, Aes256, Template }
    private readonly Policy _policy;
    private readonly QpdfPassword? _userPassword;
    private readonly QpdfPassword? _ownerPassword;
    private readonly int _permissions;
    private readonly bool _encryptMetadata;
    private readonly QpdfPath? _templateInput;

    private QpdfOutputEncryption(Policy policy, QpdfPassword? user, QpdfPassword? owner, int permissions, bool metadata, QpdfPath? templateInput)
    { _policy = policy; _userPassword = user; _ownerPassword = owner; _permissions = permissions; _encryptMetadata = metadata; _templateInput = templateInput; }

    /// <summary>Retains supported input protection, or leaves an unencrypted input unencrypted.</summary>
    /// <remarks>Revision 4 crypt filters normalize to AES-128, including RC4 input.</remarks>
    public static QpdfOutputEncryption Preserve { get; } = new(Policy.Preserve, null, null, -4, true, null);

    /// <summary>Writes an unencrypted PDF after authenticating the input when necessary.</summary>
    public static QpdfOutputEncryption Remove { get; } = new(Policy.Remove, null, null, -4, true, null);

    /// <summary>Copies supported protection from an independently authenticated template document.</summary>
    /// <remarks>Either valid template password retains both password entries, permissions and
    /// metadata policy. A plaintext template removes protection. An absent password tries empty.
    /// Revision 4 crypt filters normalize to AES-128, including RC4 templates.
    /// The host opens the template, and its reader closes before output publication.</remarks>
    public static QpdfOutputEncryption CopyFrom(QpdfPath input, QpdfPassword? password)
        => new(Policy.Template, password, null, -4, true, input);

    /// <summary>Creates fresh AES-256 revision 6 protection using both supplied passwords.</summary>
    /// <param name="userPassword">The user password, encoded as UTF-8 or exact supplied bytes, limited to 127 bytes.</param>
    /// <param name="ownerPassword">The owner password; it must be nonempty when the user password is nonempty.</param>
    /// <param name="permissions">The signed PDF permission word. Use -4 to allow all operations.
    /// Reserved bits and accessibility must be set as required by revision 6.</param>
    /// <param name="encryptMetadata">Whether document-level metadata is encrypted.</param>
    /// <exception cref="ArgumentNullException">A password is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Reserved permission bits have invalid values.</exception>
    public static QpdfOutputEncryption CreateAes256(QpdfPassword userPassword, QpdfPassword ownerPassword,
        int permissions, bool encryptMetadata)
    {
        ArgumentNullException.ThrowIfNull(userPassword); ArgumentNullException.ThrowIfNull(ownerPassword);
        // Variable bits: print, modify, extract, annotate, forms, assembly and
        // high-quality printing. Accessibility (bit 10) stays enabled for R6.
        if ((permissions & ~0x0d3c) != unchecked((int)0xfffff2c0))
        { throw new ArgumentOutOfRangeException(nameof(permissions), "Invalid revision 6 permission bits."); }
        return new(Policy.Aes256, userPassword, ownerPassword, permissions, encryptMetadata, null);
    }

    /// <summary>Returns a description without credentials.</summary>
    public override string ToString() => _policy switch
    {
        Policy.Preserve => "QpdfOutputEncryption [preserve]",
        Policy.Remove => "QpdfOutputEncryption [remove]",
        Policy.Template => "QpdfOutputEncryption [template; password redacted]",
        _ => "QpdfOutputEncryption [AES-256; passwords redacted]",
    };

    internal bool PreservesInput => _policy == Policy.Preserve;
    internal QpdfPath? TemplateInput => _templateInput;
    internal QpdfPassword? TemplatePassword => _policy == Policy.Template ? _userPassword : null;

    internal OutputSecurity? CreateOutput(ResourceGovernor governor)
    {
        governor.CheckCancellation();
        if (_policy != Policy.Aes256) { return null; }
        QpdfPassword user = _userPassword ?? throw new InvalidOperationException("Missing output user credential.");
        QpdfPassword owner = _ownerPassword ?? throw new InvalidOperationException("Missing output owner credential.");
        using ParserMemoryScope scratch = ParserMemoryScope.Create(governor);
        scratch.Reserve(1024L);
        Span<byte> userBytes = stackalloc byte[127], ownerBytes = stackalloc byte[127];
        try
        {
            int userLength = user.WriteModernBytes(userBytes, governor), ownerLength = owner.WriteModernBytes(ownerBytes, governor);
            if (userLength != 0 && ownerLength == 0)
            { throw new QpdfUnsupportedFeatureException("New AES-256 protection with a nonempty user password requires a nonempty owner password."); }
            return OutputSecurity.CreateRevision6(userBytes[..userLength], ownerBytes[..ownerLength], _permissions, _encryptMetadata, governor);
        }
        finally { CryptographicOperations.ZeroMemory(userBytes); CryptographicOperations.ZeroMemory(ownerBytes); }
    }
}

namespace Lokad.Qpdf;

/// <summary>Distinguishes credential failures from unsupported security handlers.</summary>
public enum QpdfSecurityFailure
{
    /// <summary>No password was supplied and the empty password did not authenticate.</summary>
    PasswordRequired,
    /// <summary>The supplied password did not authenticate.</summary>
    InvalidPassword,
    /// <summary>The security handler or selected crypt filter is unsupported.</summary>
    UnsupportedHandler,
}

/// <summary>A password or security-handler failure, containing no password or key data.</summary>
/// <remarks>Malformed encryption data instead throws <see cref="QpdfParseException"/>.</remarks>
public sealed class QpdfSecurityException : QpdfException
{
    /// <summary>Gets the failure category.</summary>
    public QpdfSecurityFailure Reason { get; }

    /// <summary>Creates a security failure with a fixed, non-secret message.</summary>
    public QpdfSecurityException(QpdfSecurityFailure reason) : base(reason switch
    {
        QpdfSecurityFailure.PasswordRequired => "The encrypted input requires a password.",
        QpdfSecurityFailure.InvalidPassword => "The supplied password did not authenticate.",
        QpdfSecurityFailure.UnsupportedHandler => "The PDF security handler or selected crypt filter is not supported.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    }) { Reason = reason; }
}

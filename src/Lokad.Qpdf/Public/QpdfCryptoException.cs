namespace Lokad.Qpdf;

/// <summary>An excluded cryptographic operation was requested.</summary>
public sealed class QpdfCryptoException : QpdfException
{
    /// <summary>Gets the option or key that requested crypto, without any value.</summary>
    public string Option { get; }

    /// <summary>Creates a crypto rejection. Values are never retained.</summary>
    /// <exception cref="ArgumentNullException">The option is null.</exception>
    public QpdfCryptoException(string option)
        : base($"Cryptographic operation '{option}' is permanently out of scope.")
    {
        Option = option ?? throw new ArgumentNullException(nameof(option));
    }
}

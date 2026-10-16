namespace Lokad.Qpdf;

/// <summary>A host's cooperative memory allowance refused admission before allocation.</summary>
/// <remarks>
/// Distinct from cancellation, actual allocation failure and a local
/// <see cref="QpdfLimitException" />. Refusal does not imply rollback of earlier effects.
/// </remarks>
public sealed class QpdfMemoryAdmissionException : QpdfException
{
    /// <summary>Creates a failure for a nonnegative requested capacity estimate.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The estimate is negative.</exception>
    public QpdfMemoryAdmissionException(long requestedBytes)
        : base("The host's shared memory allowance refused admission.")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedBytes);
        RequestedBytes = requestedBytes;
    }

    /// <summary>Gets the reservation or replacement estimate that was refused, in bytes.</summary>
    public long RequestedBytes { get; }
}

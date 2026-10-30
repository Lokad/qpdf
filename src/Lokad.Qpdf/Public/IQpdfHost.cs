namespace Lokad.Qpdf;

/// <summary>Ownership policy for caller-supplied in-memory input bytes.</summary>
public enum QpdfInputOwnership
{
    /// <summary>The host copies the bytes; the caller keeps its array.</summary>
    Copy = 0,

    /// <summary>
    /// Ownership transfers to the host; the caller must not mutate the array
    /// afterwards. Public reads fill independent caller storage.
    /// </summary>
    Transfer = 1,
}

/// <summary>
/// A staged single-output transaction. Uncommitted bytes are never published;
/// disposing without committing discards the staging.
/// </summary>
public interface IQpdfOutputTransaction : IAsyncDisposable
{
    /// <summary>Gets the logical target path.</summary>
    QpdfPath Path { get; }

    /// <summary>
    /// Appends bytes to the staging area under explicit byte budgets. Throws
    /// <see cref="QpdfLimitException" /> before growing past the budget,
    /// <see cref="QpdfHostException" /> on transport failure, and
    /// <see cref="OperationCanceledException" /> on cancellation.
    /// </summary>
    /// <remarks>
    /// Complete consumption or copying before this call completes. Any backing
    /// retained as staging afterward belongs to the host: keep it immutable and
    /// account for its full retained capacity under the host's storage owner.
    /// The library keeps its borrow funded through this call and cleanup.
    /// </remarks>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes staged bytes to the target. On failure the existing target
    /// is left untouched.
    /// </summary>
    /// <remarks>
    /// Establish surviving ownership before the atomic publication boundary.
    /// Honor cancellation before publication; once published, report success.
    /// Ordinary stream writes or closure alone do not implement replacement.
    /// </remarks>
    ValueTask CommitAsync(CancellationToken cancellationToken);

    /// <summary>Abandons staged bytes and releases their budget.</summary>
    ValueTask DiscardAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Caller-supplied capabilities. The library owns PDF semantics and budgets;
/// the host owns identities, bytes, publication, and leases. No method grants
/// filesystem, process, or network authority.
/// </summary>
public interface IQpdfHost
{
    /// <summary>Opens a stable input lease for a logical path.</summary>
    /// <remarks>Acquire the content once; replacement of the logical path cannot change opened bytes.</remarks>
    ValueTask<IQpdfInput> OpenInputAsync(QpdfPath path, CancellationToken cancellationToken);

    /// <summary>Creates a staged-output transaction for a logical path.</summary>
    ValueTask<IQpdfOutputTransaction> CreateOutputAsync(QpdfPath path, CancellationToken cancellationToken);
}

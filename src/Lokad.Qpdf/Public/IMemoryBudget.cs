using System.Diagnostics.CodeAnalysis;

namespace Lokad.Qpdf;

/// <summary>Immediate admission of estimated retained capacity against a host's shared allowance.</summary>
/// <remarks>
/// Implementations are thread-safe. Scopes share the same allowance and reserve
/// nothing when created. Callers serialize scope disposal with all subtree users.
/// A library borrows the supplied budget and never disposes its caller's scope.
/// </remarks>
public interface IMemoryBudget
{
    /// <summary>Creates an empty child owner sharing the root allowance, without a separate cap.</summary>
    /// <exception cref="ObjectDisposedException">This owner has been disposed.</exception>
    IMemoryScope CreateScope();

    /// <summary>Reserves nonnegative bytes immediately; refusal returns false and null without changing accounting.</summary>
    /// <remarks>Zero is valid. Admission never waits for another owner to release memory.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The estimate is negative.</exception>
    /// <exception cref="ObjectDisposedException">This owner has been disposed.</exception>
    bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation);
}

/// <summary>An owner whose disposal refunds its subtree and invalidates all descendants.</summary>
/// <remarks>
/// Disposal is idempotent and non-throwing. It refunds accounting only: the owner
/// must first finish all users, drop accounted storage and complete cleanup.
/// Storage and descendants must not outlive this scope. Disposal neither cancels
/// work nor disposes actual buffers, and is serialized with subtree operations.
/// </remarks>
public interface IMemoryScope : IMemoryBudget, IDisposable;

/// <summary>An admitted estimate whose disposal refunds its charge.</summary>
/// <remarks>Serialize resizing and disposal. Disposal is idempotent and non-throwing.</remarks>
public interface IMemoryReservation : IDisposable
{
    /// <summary>Gets the estimate, or zero after this reservation or any ancestor is disposed.</summary>
    long Bytes { get; }

    /// <summary>Replaces the estimate immediately; refusal leaves it unchanged.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The estimate is negative.</exception>
    /// <exception cref="ObjectDisposedException">This reservation or an ancestor has been disposed.</exception>
    bool TryResize(long bytes);
}

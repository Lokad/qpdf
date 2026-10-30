using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

/// <summary>A stable, offset-addressed input acquired once from the host.</summary>
/// <remarks>
/// Identity, length and opened content remain stable until disposal, including
/// after logical replacement or rename. Reads do not share a cursor or reopen paths.
/// A lease has one caller: await each read before another read or disposal.
/// </remarks>
public interface IQpdfInput : IAsyncDisposable
{
    /// <summary>Gets the logical path the input was opened from.</summary>
    QpdfPath Path { get; }

    /// <summary>Gets the host-assigned stable identity used for alias checks.</summary>
    string Identity { get; }

    /// <summary>Gets the stable length in bytes.</summary>
    long Length { get; }

    /// <summary>Reads up to the destination length at the given offset; short reads are allowed.</summary>
    /// <remarks>
    /// Offset must be between zero and Length inclusive. An empty destination is
    /// a no-op; otherwise zero means stable EOF. The caller admits and owns the
    /// destination before calling. The host borrows it through actual completion
    /// only, funds any private scratch separately, and retains no buffer reference
    /// afterward. Failure or cancellation may modify a prefix but returns no count.
    /// Cancellation must finish pending I/O before completion; callers await it
    /// before reusing the buffer or disposing the input.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The offset is invalid.</exception>
    /// <exception cref="QpdfHostException">The transport failed.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);
}

/// <summary>Ranged-read helpers shared by every engine subsystem.</summary>
public static class QpdfInputReader
{
    /// <summary>Fills an already admitted caller buffer, looping over short reads.</summary>
    /// <remarks>The input and destination remain caller-owned. No storage is acquired.</remarks>
    /// <exception cref="QpdfHostException">The host returns an invalid count or stops before stable EOF.</exception>
    public static ValueTask ReadExactlyAsync(IQpdfInput input, long offset,
        Memory<byte> destination, CancellationToken cancellationToken)
        => ReadExactlyCoreAsync(input, offset, destination, null, cancellationToken);

    private static async ValueTask ReadExactlyCoreAsync(IQpdfInput input, long offset,
        Memory<byte> destination, ResourceGovernor? governor, CancellationToken cancellationToken)
    {
        ValidateRange(input, offset, destination.Length);
        cancellationToken.ThrowIfCancellationRequested();
        int filled = 0;
        while (filled < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested(); governor?.CheckCancellation();
            int read = await input.ReadAsync(offset + filled, destination[filled..], cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); governor?.CheckCancellation();
            if (read <= 0 || read > destination.Length - filled)
            { throw new QpdfHostException(input.Path, QpdfHostFailureReason.NonProgressingRead); }
            filled += read;
        }
    }

    /// <summary>Admits and reads an owned array under the caller's memory owner.</summary>
    /// <remarks>
    /// The result charge remains beneath the borrowed budget until that owner is
    /// disposed. Keep it alive until all users finish. Failure refunds admission
    /// only after the host read has completed. Refusal precedes allocation and I/O.
    /// </remarks>
    public static async ValueTask<byte[]> ReadExactlyToArrayAsync(IQpdfInput input,
        long offset, int count, IMemoryBudget memoryBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memoryBudget);
        ValidateRange(input, offset, count);
        cancellationToken.ThrowIfCancellationRequested();
        if (count == 0) { return Array.Empty<byte>(); }
        IMemoryReservation reservation = MemoryAdmission.Reserve(memoryBudget, count + 32L);
        bool accepted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] result = GC.AllocateUninitializedArray<byte>(count);
            await ReadExactlyAsync(input, offset, result, cancellationToken).ConfigureAwait(false);
            accepted = true;
            return result;
        }
        finally { if (!accepted) { reservation.Dispose(); } }
    }

    // The engine destination owner has already admitted this array.
    internal static async ValueTask<byte[]> ReadAdmittedAsync(IQpdfInput input, long offset,
        int count, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ValidateRange(input, offset, count);
        cancellationToken.ThrowIfCancellationRequested(); governor.CheckCancellation();
        if (count == 0) { return Array.Empty<byte>(); }
        byte[] result = GC.AllocateUninitializedArray<byte>(count);
        await ReadExactlyCoreAsync(input, offset, result, governor, cancellationToken).ConfigureAwait(false);
        governor.CheckCancellation();
        return result;
    }

    private static void ValidateRange(IQpdfInput input, long offset, int count)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (offset < 0 || offset > input.Length || count < 0 || count > input.Length - offset)
        { throw new ArgumentOutOfRangeException(nameof(count), "Read range exceeds the stable input length."); }
    }
}

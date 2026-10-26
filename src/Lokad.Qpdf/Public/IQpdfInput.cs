using System.Runtime.InteropServices;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf;

/// <summary>
/// A stable, seekable input behind the host boundary. Each read returns a
/// freshly owned copy; the library never retains caller-mutable arrays.
/// </summary>
public interface IQpdfInput : IAsyncDisposable
{
    /// <summary>Gets the logical path the input was opened from.</summary>
    QpdfPath Path { get; }

    /// <summary>Gets the host-assigned stable identity used for alias checks.</summary>
    string Identity { get; }

    /// <summary>Gets the stable length in bytes.</summary>
    long Length { get; }

    /// <summary>
    /// Reads up to <paramref name="count" /> bytes at <paramref name="offset" />.
    /// Returns an empty buffer only at end of input. Throws
    /// <see cref="ArgumentOutOfRangeException" /> for invalid ranges,
    /// <see cref="QpdfHostException" /> for transport failures, and
    /// <see cref="OperationCanceledException" /> on cancellation.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(long offset, int count, CancellationToken cancellationToken);
}

/// <summary>Ranged-read helpers shared by every engine subsystem.</summary>
public static class QpdfInputReader
{
    /// <summary>
    /// Reads exactly <paramref name="count" /> bytes, looping over short host
    /// reads. Rejects non-progressing reads and premature end of input with
    /// <see cref="QpdfHostException" /> instead of silently accepting short data.
    /// </summary>
    /// <exception cref="ArgumentNullException">The input is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is invalid.</exception>
    public static ValueTask<byte[]> ReadExactlyAsync(
        IQpdfInput input,
        long offset,
        int count,
        CancellationToken cancellationToken)
        => ReadExactlyCoreAsync(input, offset, count, null, null, cancellationToken);

    /// <summary>Reads owned bytes after admitting their capacity against a caller's memory owner.</summary>
    /// <remarks>
    /// The borrowed budget must outlive the returned bytes. Their charge remains
    /// beneath that caller owner until its disposal; no collection is required.
    /// Short or non-array reads admit assembly overlap before copying. Hosts
    /// account separately for private buffers and excess returned backing capacity.
    /// Refusal fails immediately without retrying. The input lease stays caller-owned.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The input or budget is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is invalid.</exception>
    /// <exception cref="QpdfMemoryAdmissionException">The shared allowance refused admission.</exception>
    public static ValueTask<byte[]> ReadExactlyAsync(
        IQpdfInput input,
        long offset,
        int count,
        IMemoryBudget memoryBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memoryBudget);
        return ReadExactlyCoreAsync(input, offset, count, memoryBudget, null, cancellationToken);
    }

    // The engine already admits the returned array under its destination owner.
    // This path adds only a temporary assembly/read overlap, when actually needed.
    internal static ValueTask<byte[]> ReadAdmittedAsync(IQpdfInput input, long offset, int count,
        ResourceGovernor governor, CancellationToken cancellationToken)
        => ReadExactlyCoreAsync(input, offset, count, null, governor, cancellationToken);

    private static async ValueTask<byte[]> ReadExactlyCoreAsync(
        IQpdfInput input, long offset, int count, IMemoryBudget? memoryBudget, ResourceGovernor? governor,
        CancellationToken cancellationToken)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (offset < 0 || count < 0 || count > input.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Read range exceeds the stable input length.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (count == 0) { return Array.Empty<byte>(); }
        IMemoryReservation? storage = memoryBudget is null ? null
            : MemoryAdmission.Reserve(memoryBudget, count + GovernedBufferStream.ArrayOverheadBytes);
        long overlap = 0L;
        try
        {
            byte[]? result = null;
            bool assembled = false;
            int filled = 0;
            while (filled < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlyMemory<byte> chunk = await input.ReadAsync(offset + filled, count - filled, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (chunk.Length == 0)
                {
                    throw new QpdfHostException(input.Path, QpdfHostFailureReason.NonProgressingRead);
                }

                // Reuse a complete freshly owned array. Other reads assemble
                // without retaining slices of unrelated backing storage.
                if (filled == 0 && chunk.Length == count
                    && MemoryMarshal.TryGetArray(chunk, out ArraySegment<byte> backing)
                    && backing.Offset == 0 && backing.Array is byte[] owned && owned.Length == count)
                {
                    result = owned;
                    break;
                }

                if (result is null)
                {
                    // The first read and its assembly copy coexist. Later
                    // short reads reuse the same admitted overlap allowance.
                    if (storage is not null)
                    { MemoryAdmission.Resize(storage, 2L * (count + GovernedBufferStream.ArrayOverheadBytes)); }
                    if (governor is not null)
                    {
                        long bytes = count + GovernedBufferStream.ArrayOverheadBytes;
                        governor.ReserveMemory(bytes);
                        overlap = bytes;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    governor?.CheckCancellation();
                    result = new byte[count];
                    assembled = true;
                }
                int take = Math.Min(chunk.Length, count - filled);
                for (int copied = 0; copied < take;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int batch = Math.Min(8192, take - copied);
                    chunk.Slice(copied, batch).CopyTo(result.AsMemory(filled + copied, batch));
                    copied += batch;
                }
                filled += take;
                chunk = default;
            }

            if (storage is not null && assembled)
            { MemoryAdmission.Resize(storage, count + GovernedBufferStream.ArrayOverheadBytes); }
            // Refund overlap before the final cancellation check: shrinking
            // the shared reservation can call the host's budget again.
            if (overlap != 0L)
            {
                long bytes = overlap; overlap = 0L;
                governor?.ReleaseMemory(bytes);
            }
            cancellationToken.ThrowIfCancellationRequested();
            governor?.CheckCancellation();
            // Successful storage belongs to the supplied caller scope. Failure
            // before escape instead disposes this reservation in the finally.
            storage = null;
            return result ?? Array.Empty<byte>();
        }
        finally
        {
            storage?.Dispose();
            if (overlap != 0L) { governor?.ReleaseMemory(overlap); }
        }
    }
}


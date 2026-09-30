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
    public static async ValueTask<byte[]> ReadExactlyAsync(
        IQpdfInput input,
        long offset,
        int count,
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

        byte[] result = new byte[count];
        int filled = 0;
        while (filled < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlyMemory<byte> chunk = await input.ReadAsync(offset + filled, count - filled, cancellationToken).ConfigureAwait(false);
            if (chunk.Length == 0)
            {
                throw new QpdfHostException(input.Path, QpdfHostFailureReason.NonProgressingRead);
            }

            int take = Math.Min(chunk.Length, count - filled);
            chunk.Slice(0, take).CopyTo(result.AsMemory(filled, take));
            filled += take;
        }

        return result;
    }
}


namespace Lokad.Qpdf.Runtime;

// Independent exported storage belongs to the supplied caller owner. Values
// themselves carry no budget reference or per-object lifetime machinery.
internal static class CallerMemoryCopy
{
    internal static T[] Copy<T>(ReadOnlySpan<T> source, int itemBytes,
        IMemoryBudget memoryBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memoryBudget);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.IsEmpty) { return Array.Empty<T>(); }
        IMemoryReservation? reservation = MemoryAdmission.Reserve(memoryBudget,
            checked(GovernedBufferStream.ArrayOverheadBytes + (long)source.Length * itemBytes));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            T[] result = new T[source.Length];
            int batchItems = Math.Max(1, 8192 / itemBytes);
            for (int offset = 0; offset < source.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(batchItems, source.Length - offset);
                source.Slice(offset, count).CopyTo(result.AsSpan(offset, count));
                offset += count;
            }
            cancellationToken.ThrowIfCancellationRequested();
            reservation = null;
            return result;
        }
        finally { reservation?.Dispose(); }
    }
}

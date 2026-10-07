using Lokad.Qpdf.Filters;
using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

/// <summary>Owns object-stream header/body assembly and its optional compressed result.</summary>
internal sealed class ObjectStreamPayload : IDisposable
{
    private ParserMemoryScope? _memory;
    private byte[]? _raw;
    private byte[]? _stored;

    internal long First { get; }
    internal bool Compressed { get; }
    internal ReadOnlyMemory<byte> Data => _raw is not null && _stored is not null
        ? _stored : throw new ObjectDisposedException(nameof(ObjectStreamPayload));

    private ObjectStreamPayload(ParserMemoryScope memory, byte[] raw, byte[] stored, long first, bool compressed)
    {
        _memory = memory;
        _raw = raw;
        _stored = stored;
        First = first;
        Compressed = compressed;
    }

    internal static ObjectStreamPayload Create(IReadOnlyList<int> numbers, IReadOnlyDictionary<int, int>? renumber,
        ObjectStreamCapture members, bool compress, int? compressionLevel, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken cancellationToken)
        => CreateCore(numbers, renumber, null, null, members, compress, compressionLevel, limits, governor, cancellationToken);

    internal static ObjectStreamPayload CreateLinearized(IReadOnlyList<int> numbers, GovernedIntDictionary<int> renumber,
        ObjectStreamCapture members, bool compress, int? compressionLevel, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(renumber);
        return CreateCore(numbers, null, renumber, null, members, compress, compressionLevel, limits, governor, cancellationToken);
    }

    internal static ObjectStreamPayload CreateQdf(IReadOnlyList<int> numbers, IReadOnlyDictionary<int, int>? renumber,
        ObjectStreamCapture headers, ObjectStreamCapture bodies, QpdfLimits limits,
        ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return CreateCore(numbers, renumber, null, headers, bodies, false, null, limits, governor, cancellationToken);
    }

    private static ObjectStreamPayload CreateCore(IReadOnlyList<int> numbers, IReadOnlyDictionary<int, int>? renumber,
        GovernedIntDictionary<int>? governedRenumber, ObjectStreamCapture? headers, ObjectStreamCapture bodies, bool compress, int? compressionLevel,
        QpdfLimits limits, ResourceGovernor governor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(governor);
        cancellationToken.ThrowIfCancellationRequested();
        bodies.CheckOwner(governor);
        headers?.CheckOwner(governor);
        governedRenumber?.CheckOwner(governor);
        int count = numbers.Count;
        if (count < 1 || count > 100 || bodies.Count != count || (headers is not null && headers.Count != count))
        {
            throw new QpdfException("Object-stream payload requires 1..100 completed members.");
        }
        ParserMemoryScope memory = ParserMemoryScope.Create(governor);
        try
        {
            memory.Reserve(64L);
            // Each row needs at most ten ID digits, nineteen offset digits and
            // two separators. The fixed stack buffer covers all 100 rows.
            Span<byte> table = stackalloc byte[4096];
            int used = 0;
            long running = 0L;
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                governor.ChargeWork(1L);
                int number = numbers[index];
                if (renumber is not null && !renumber.TryGetValue(number, out number))
                {
                    throw new QpdfException("Object-stream payload lost a member number.");
                }
                if (governedRenumber is not null && !governedRenumber.TryGetValue(number, out number))
                {
                    throw new QpdfException("Object-stream payload lost a member number.");
                }
                if (number <= 0) { throw new QpdfException("Object-stream member numbers must be positive."); }
                if (headers is null && index > 0) { table[used++] = 32; }
                used += WriterNumber.Format(number, table.Slice(used), governor);
                table[used++] = 32;
                long offset = headers is null ? running : checked(running + headers[index].Length - headers[0].Length);
                used += WriterNumber.Format(offset, table.Slice(used), governor);
                if (headers is not null) { table[used++] = 10; }
                running = checked(running + bodies[index].Length + (headers is null ? 0L : headers[index].Length));
            }
            if (headers is null) { table[used++] = 10; }
            long length = checked(running + used);
            long maximum = Math.Min(limits.MaxOutputBytes, Array.MaxLength);
            if (length > maximum) { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), length, maximum); }
            memory.Reserve(checked(32L + length));
            governor.ChargeWork(length);
            cancellationToken.ThrowIfCancellationRequested();
            governor.CheckCancellation();
            byte[] raw = new byte[(int)length];
            GovernedBufferStream.CopyBytes(table.Slice(0, used), raw, governor, cancellationToken, CancellationToken.None);
            int position = used;
            for (int index = 0; index < count; index++)
            {
                if (headers is not null)
                {
                    byte[] header = headers[index];
                    GovernedBufferStream.CopyBytes(header, raw.AsSpan(position, header.Length), governor, cancellationToken, CancellationToken.None);
                    position += header.Length;
                }
                byte[] body = bodies[index];
                GovernedBufferStream.CopyBytes(body, raw.AsSpan(position, body.Length), governor, cancellationToken, CancellationToken.None);
                position += body.Length;
            }
            byte[] stored = compress ? FlateFilter.Encode(raw, compressionLevel, limits, governor, memory, cancellationToken) : raw;
            return new ObjectStreamPayload(memory, raw, stored, checked(used + (headers is null ? 0L : headers[0].Length)), compress);
        }
        catch { memory.Dispose(); throw; }
    }

    public void Dispose()
    {
        ParserMemoryScope? memory = _memory;
        _raw = null;
        _stored = null;
        _memory = null;
        memory?.Dispose();
    }
}

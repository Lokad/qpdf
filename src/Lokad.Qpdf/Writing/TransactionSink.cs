using Lokad.Qpdf.Runtime;

namespace Lokad.Qpdf.Writing;

internal sealed class TransactionSink : ISynchronousByteSink, IOutputCapacityHint
{
    private readonly IQpdfOutputTransaction _transaction;
    private readonly ResourceGovernor _governor;
    private readonly ParserMemoryScope? _staging;
    private readonly long _maximumOutputBytes = long.MaxValue;
    private long _capacityCharge;
    private long _length;

    internal TransactionSink(IQpdfOutputTransaction transaction, ResourceGovernor governor)
    {
        _transaction = transaction;
        _governor = governor;
    }

    internal TransactionSink(IQpdfOutputTransaction transaction, ResourceGovernor governor,
        ParserMemoryScope staging, QpdfLimits limits) : this(transaction, governor)
    {
        _staging = staging;
        _maximumOutputBytes = limits.MaxOutputBytes;
    }

    void IOutputCapacityHint.EnsureCapacity(long length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); _governor.CheckCancellation();
        if (length > _maximumOutputBytes)
        { throw new QpdfLimitException(nameof(QpdfLimits.MaxOutputBytes), length, _maximumOutputBytes); }
        if (_staging is not null && length > _capacityCharge)
        {
            _staging.ReserveHostMemory(length - _capacityCharge);
            _capacityCharge = length;
        }
        GovernedHost.PrepareOutputCapacity(_transaction, length, cancellationToken);
    }

    public long Length => _length;

    public bool CanAppendSynchronously => GovernedHost.SupportsSynchronousWrite(_transaction);

    public void Append(ReadOnlySpan<byte> chunk, CancellationToken cancellationToken)
    {
        _governor.ChargeOutputBytes(chunk.Length);
        GovernedHost.WriteSynchronously(_transaction, chunk, cancellationToken);
        _length = checked(_length + chunk.Length);
    }

    public async ValueTask AppendAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        _governor.ChargeOutputBytes(chunk.Length);
        await _transaction.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        checked
        {
            _length += chunk.Length;
        }
    }
}


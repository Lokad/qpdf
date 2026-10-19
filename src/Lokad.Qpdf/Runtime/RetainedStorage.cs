using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Runtime;


/// <summary>Coarse ownership of admitted graph, result and diagnostic storage.</summary>
// Fresh construction scopes already know their estimates. Retention transfers
// those estimates without discovering aliases, walking graphs or tracking CLR
// objects. Only exclusive candidates need a small explicit rollback receipt.
internal sealed class RetainedStorage(ResourceGovernor governor)
{
    private readonly ResourceGovernor _governor = governor;
    private long _privateBytes;
    private long _callerBytes;
    private long _resultBytes;

    internal sealed class Receipt(RetainedStorage owner, long bytes, bool callerOwned, bool provisional)
    {
        internal readonly RetainedStorage Owner = owner;
        internal long Bytes = bytes;
        internal bool CallerOwned = callerOwned;
        internal bool Provisional = provisional;
        internal bool Active = true;
    }



    internal long CallerBytes => _callerBytes;
    internal long PrivateBytes => _privateBytes;

    private long Keep(long bytes, bool callerOwned)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (callerOwned) { _callerBytes = checked(_callerBytes + bytes); }
        else { _privateBytes = checked(_privateBytes + bytes); }
        return bytes;
    }

    internal long Retain(OwnedAllocationBatch allocations, long reservedBytes)
    {
        allocations.Token.ThrowIfCancellationRequested(); _governor.CheckCancellation();
        if (allocations.ResultLifetime && !_governor.OwnsDocumentCounters) { return RetainResult(reservedBytes); }
        return RetainGraph(reservedBytes);
    }

    internal long RetainGraph(long bytes)
    {
        if (!_governor.OwnsDocumentCounters) { return Keep(bytes, false); }
        return _governor.DocumentAccepted ? Keep(bytes, true) : RetainResult(bytes);
    }

    private bool IsCallerOwned(Receipt receipt)
        => receipt.CallerOwned || (receipt.Provisional && _governor.DocumentAccepted);

    internal long RetainCaller(long bytes) => Keep(bytes, true);
    internal long RetainPrivate(long bytes) => Keep(bytes, false);

    internal long RetainResult(long bytes)
    {
        long next = checked(_resultBytes + bytes);
        Keep(bytes, false);
        _resultBytes = next;
        return bytes;
    }

    internal void AcceptResults()
    {
        _privateBytes -= _resultBytes;
        _callerBytes = checked(_callerBytes + _resultBytes);
        _resultBytes = 0L;
    }

    internal Receipt RetainAllocation(long reservedBytes)
    {
        bool callerOwned = _governor.OwnsDocumentCounters && _governor.DocumentAccepted;
        bool provisional = _governor.OwnsDocumentCounters && !_governor.DocumentAccepted;
        Receipt receipt = new(this, reservedBytes, callerOwned, provisional);
        if (provisional) { RetainResult(reservedBytes); }
        else { Keep(reservedBytes, callerOwned); }
        return receipt;
    }

    internal void GrowAllocation(Receipt receipt, long bytes)
    {
        CheckReceipt(receipt);
        if (receipt.Provisional && !_governor.DocumentAccepted) { RetainResult(bytes); }
        else { Keep(bytes, IsCallerOwned(receipt)); }
        receipt.Bytes = checked(receipt.Bytes + bytes);
    }

    internal void PromoteAllocation(Receipt receipt)
    {
        CheckReceipt(receipt);
        if (IsCallerOwned(receipt)) { return; }
        if (receipt.Provisional) { _resultBytes -= receipt.Bytes; receipt.Provisional = false; }
        _privateBytes -= receipt.Bytes;
        _callerBytes = checked(_callerBytes + receipt.Bytes);
        receipt.CallerOwned = true;
    }

    internal long DropAllocation(Receipt receipt)
    {
        CheckReceipt(receipt);
        receipt.Active = false;
        if (IsCallerOwned(receipt)) { _callerBytes -= receipt.Bytes; }
        else
        {
            _privateBytes -= receipt.Bytes;
            if (receipt.Provisional) { _resultBytes -= receipt.Bytes; }
        }
        return receipt.Bytes;
    }

    private void CheckReceipt(Receipt receipt)
    {
        if (!ReferenceEquals(receipt.Owner, this) || !receipt.Active)
        { throw new InvalidOperationException("Exclusive storage is no longer owned by this operation."); }
    }

    // Diagnostics/check details form one result batch; any of their immutable
    // records can escape. Their bytes follow the caller owner.
    internal long RetainCheckOutcome(long reservedBytes)
        => Keep(reservedBytes, true);

    internal void Complete()
    {
        long bytes = _privateBytes;
        _privateBytes = 0L; _resultBytes = 0L;
        _governor.ReleaseMemory(bytes);
    }
}

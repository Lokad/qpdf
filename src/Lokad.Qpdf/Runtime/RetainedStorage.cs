using Lokad.Qpdf.Syntax;

namespace Lokad.Qpdf.Runtime;

internal readonly record struct DiagnosticWorkState(long Messages);

/// <summary>Coarse ownership of admitted graph, result and diagnostic storage.</summary>
// Fresh construction scopes already know their estimates. Retention transfers
// those estimates without discovering aliases, walking graphs or tracking CLR
// objects. Only exclusive candidates need a small explicit rollback receipt.
internal sealed class RetainedStorage(ResourceGovernor governor)
{
    private const long MessageNodeBytes = 48L;
    private readonly ResourceGovernor _governor = governor;
    private long _privateBytes;
    private long _callerBytes;
    private long _resultBytes;
    private long _memoBytes;
    private long _messageCount;
    private MessageNode? _messages;

    internal sealed class Receipt(RetainedStorage owner, long bytes, bool callerOwned, bool provisional)
    {
        internal readonly RetainedStorage Owner = owner;
        internal long Bytes = bytes;
        internal bool CallerOwned = callerOwned;
        internal bool Provisional = provisional;
        internal bool Active = true;
    }

    private sealed class MessageNode(string message, int hash, MessageNode? next)
    {
        internal readonly string Message = message;
        internal readonly int Hash = hash;
        internal readonly MessageNode? Next = next;
    }

    internal long CallerBytes => _callerBytes;
    internal long PrivateBytes => _privateBytes;
    internal DiagnosticWorkState WorkState => new(_messageCount);

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
    // records can escape. Memo links are private and end with the invocation.
    internal long RetainDiagnostic(long reservedBytes)
        => _governor.OwnsDocumentCounters && !_governor.DocumentAccepted
            ? RetainResult(reservedBytes) : Keep(reservedBytes, true);
    internal long RetainCheckOutcome(long reservedBytes)
        => Keep(reservedBytes, true);

    internal string? FindDiagnosticMessage(string message, int hash)
    {
        for (MessageNode? node = _messages; node is not null; node = node.Next)
        {
            _governor.ChargeWork(1L); _governor.CheckCancellation();
            string candidate = node.Message;
            if (node.Hash != hash || candidate.Length != message.Length) { continue; }
            bool equal = true;
            for (int offset = 0; offset < message.Length; offset += Math.Min(256, message.Length - offset))
            {
                int count = Math.Min(256, message.Length - offset);
                _governor.ChargeWork(count); _governor.CheckCancellation();
                if (!candidate.AsSpan(offset, count).SequenceEqual(message.AsSpan(offset, count))) { equal = false; break; }
            }
            if (equal) { return candidate; }
        }
        return null;
    }

    internal string? FindDiagnosticMessage(ReadOnlySpan<DiagnosticPart> parts, DiagnosticTextInfo info)
    {
        for (MessageNode? node = _messages; node is not null; node = node.Next)
        {
            _governor.ChargeWork(1L); _governor.CheckCancellation();
            string candidate = node.Message;
            if (node.Hash != info.Hash || candidate.Length != info.Length) { continue; }
            if (DiagnosticMessage.Matches(_governor, parts, candidate)) { return candidate; }
        }
        return null;
    }

    internal long RetainCachedDiagnostic(QpdfDiagnostic diagnostic, long reservedBytes, int messageHash)
    {
        _governor.PreflightDiagnosticWork(new(checked(_messageCount + 1L)));
        _governor.ReserveMemory(MessageNodeBytes);
        try
        {
            MessageNode node = new(diagnostic.Message, messageHash, _messages);
            long retained = RetainDiagnostic(reservedBytes);
            _messages = node;
            _messageCount++;
            _memoBytes += MessageNodeBytes;
            return retained;
        }
        catch { _governor.ReleaseMemory(MessageNodeBytes); throw; }
    }

    internal void Complete()
    {
        _messages = null;
        _messageCount = 0L;
        long bytes = checked(_privateBytes + _memoBytes);
        _privateBytes = 0L; _memoBytes = 0L; _resultBytes = 0L;
        _governor.ReleaseMemory(bytes);
    }
}

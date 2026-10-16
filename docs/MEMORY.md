# Cooperative memory admission

`IMemoryBudget`, `IMemoryScope` and `IMemoryReservation` let a host expose its
shared memory allowance without adding a runtime package dependency. A library
adapter forwards to the host's allowance; scopes introduce owners, not separate
limits. Admission is immediate and thread-safe. Refusal leaves accounting
unchanged and fails the operation with `QpdfMemoryAdmissionException`, distinct
from cancellation, a local `QpdfLimitException` and an actual allocation failure.

The budget-aware `QpdfInputReader.ReadExactlyAsync` overload admits fresh input
storage before reading. A complete owned array is reused; short or sliced reads
admit the additional assembly capacity before copying. Failed or canceled reads
refund their admission after outstanding input work finishes. The input lease
remains caller-owned.

Engine reads use the same reader with their destination already admitted. They
reserve additional assembly overlap only when a short or non-array read actually
needs a copy, and refund that temporary charge before returning. Selective input
prefetch uses this boundary rather than keeping a second range-sized allowance.

The caller supplies a scope that outlives the returned bytes, consumes those
bytes and drops all aliases before disposing that scope. Disposal refunds the
charge immediately without waiting for GC; it does not dispose actual storage
or cancel work. Retaining the bytes requires retaining their caller scope.
Do not dispose a scope while its reads, writes or descendants are active.

Hosts separately account for private storage and returned backing capacity
beyond the requested read length. Borrowed inputs retain their existing charge;
independent copies need additional admission. The allowance is an estimate of
owned capacity, not a guarantee about CLR heap size or process RSS.

PDF operations discover the optional `IQpdfMemoryHost.MemoryBudget` capability
once at entry. A per-invocation host view can supply a caller scope while
forwarding storage and JPEG capabilities. Existing local `QpdfLimits` remain
binding, including under `--no-default-limits`. Command adapters report shared
refusal as `BudgetExceeded` with failed status and exit 2; typed calls throw
`QpdfMemoryAdmissionException`.

The engine batches its existing storage estimates into one reservation per
invocation. Small allocations use already admitted capacity; growth normally
adds 16 KiB blocks, with a smaller initial admission for tiny jobs. Replacement
buffers retain both old and new charges during copying. Root charges include
block slack; local job counters continue reporting the existing estimates.
Returned storage remains beneath the supplied caller scope after the call ends.

Results, counter snapshots and PDF versions use allocation-time capacity
estimates without finalizer receipts or references back to a job. Their estimates
remain charged until the caller closes its owner, including when CLR collection
has reclaimed individual values. Typed result/counter construction is funded
before execution so publication cannot succeed and then fail to admit the result.
Abandoning an unreturned typed result refunds that prepared capacity immediately.

`InMemoryQpdfHost(limits, codec, memoryBudget)` also admits its retained input,
staging capacity and publication buffers under a durable caller owner. Existing
constructors preserve local-only behavior. It refunds replaced inputs after their
last lease closes and output storage after replacement or host disposal. Growth
and commit copies admit their overlap before allocation; a complete staging array
retains its charge when published. Refusal preserves a prior target. A private
copy graph that outlives its reader retains one snapshot lease per source until
its own phase finishes, so input replacement cannot refund borrowed bytes early.

`AddInput(path, bytes, reservation)` accepts already admitted, exclusively
transferred storage without another root charge. The host takes responsibility
for reservation disposal on success. The reservation's scope stays unchanged and
must outlive all host users; the estimate includes a 256-byte file-owner/container
allowance, plus two bytes per path character beyond the first 64. Persisted keys
are copied under the durable host owner. Ordinary copy/transfer registration makes
its own admission.

`TryGetOutput(path, budget, out bytes)` gives an exported copy a separate caller
owner. The existing overload uses the host's supplied owner when present.
Exported copies remain charged after host disposal. Use an owner that survives
the host's storage scope, for example a sibling scope beneath the same root.
`Dispose` drops host storage and requires all input and transaction leases to be
closed and private snapshot borrowers finished first; it never disposes a supplied
caller budget.

The engine keeps host staging and already admitted private input snapshots in
its local policy estimate, with shared charges owned by the host. Public document
reads still make independently charged copies. Custom hosts must admit their
private buffers themselves; the execution capability alone does not govern those
buffers. Low-level input callers admit freshly returned bytes before reading;
the budget-aware helper provides that boundary.

The remaining migration concerns the engine's local graph/diagnostic ledgers and
explicit coarse graph owners. These existing ledgers are not the lifetime
contract for the supplied caller scope.

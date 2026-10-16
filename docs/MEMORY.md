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

The migration is incomplete: the engine still uses its existing local value
ledgers, and host-private retained input, staging and publication storage needs
its own admission. Supplying the execution capability alone does not make an
unmodified host's private storage governed. Retained graph ownership and host
storage are being migrated to explicit coarse owners.

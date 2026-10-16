# Cooperative memory admission

`IMemoryBudget`, `IMemoryScope` and `IMemoryReservation` let a host expose its
shared memory allowance without adding a runtime package dependency. A library
adapter forwards to the host's allowance; scopes introduce owners, not separate
limits. Admission is immediate and thread-safe. Refusal leaves accounting
unchanged and fails the operation with `QpdfMemoryAdmissionException`, distinct
from cancellation, a local `QpdfLimitException` and an actual allocation failure.
Parser collection growth makes one admission attempt for its chosen capacity;
refusal preserves existing entries and does not trigger a smaller retry. JPEG
codec boundaries preserve actual `OutOfMemoryException` failures while releasing
their workspace, rather than translating them into invalid image data.
Filter errors show at most 256 name characters plus an ellipsis; the exception
keeps the full, already admitted name alias without duplicating it in the message.
Xref growth uses validated section/merge counts to cap geometric overshoot.
Large capacities within the pinned [.NET 10 preferred-prime table](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Collections/HashHelpers.cs)
use a conservative 2x prime-capacity allowance, replacing the unnecessarily broad
3x estimate there; smaller and out-of-table requests keep the broader allowance.

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

`PdfStream.CopyData`, `PdfByteString.CopyValue` and `PdfName.CopyRawBytes` take
a caller budget and cancellation token for independent byte copies. They admit
the new array before allocating and refund unused admission on failure. A copy
owner can be a sibling of the document owner, so the copy survives document and
graph-owner closure. Empty copies reuse shared arrays without admission.
`PdfArray.CopyItems` admits a shallow reference array: its referents still need
their source graph owner. These explicit exports use one reservation per copy;
the source PDF values carry no budget references or lifetime receipts.
The existing copy properties and caller-created object constructors remain
caller-managed; use the admitted methods or fund an aggregate caller batch.

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
block slack during execution; completion attempts one trim to the retained
estimate. A declined optional shrink keeps the existing charge and stops further
trim attempts without failing an already completed publication. Local counters
report the estimates.
Returned storage remains beneath the supplied caller scope after the call ends.

Results, counter snapshots and PDF versions use allocation-time capacity
estimates without finalizer receipts or references back to a job. Their estimates
remain charged until the caller closes its owner, including when CLR collection
has reclaimed individual values. Typed result/counter construction is funded
before execution so publication cannot succeed and then fail to admit the result.
Abandoning an unreturned typed result refunds that prepared capacity immediately.
Command boundaries also refund private graph storage and unreturned result
preparation after unexpected exceptions, including failed asynchronous cleanup.
They preserve charges belonging to earlier caller-owned results.

QDF and zlib companions keep their input and codec buffers under one working
owner through awaited publication and cleanup. QDF repair admits collection
capacity, buffered member lines and numeric-table formatting overlap before
allocation. Private working charges end before returning the command result.
An output write borrows library storage through completion; a host retaining
that backing afterward must keep it immutable and fund its retained capacity
under its own storage owner.

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

Parser and transform construction scopes transfer their existing estimates to
coarse graph owners. Retention does not walk object references or create weak
tables and finalizer receipts. Private graph charges end with execution cleanup;
private configuration and filter text end with their disposable component owners.
Prepared typed result arrays transfer to the caller when the result is accepted,
and refund with private cleanup when preparation fails.

Public document values keep their graph estimates under the caller's owner after
document disposal. Collection of an individual child or sibling does not refund
part of that coarse estimate. Counter snapshots requested after disposal still
need admission beneath that same live caller owner.
Opening keeps its graph provisional until a document returns; failure refunds
that unreturned graph after cleanup.

Diagnostic messages share an invocation-local memo that is cleared during
cleanup. Returned diagnostic records and text belong to a coarse result batch.
An exclusive prepared buffer uses a small rollback receipt until it is exposed
or abandoned; the receipt is never attached to a returned PDF object.

Custom hosts own identifiers they retain after their callbacks complete. Copy or
admit that private storage under the host's durable owner. The built-in memory
host copies persisted keys and funds them with its retained storage.

JSON import/update uses an operation owner through input cleanup, conversion,
renumbering and publication. A bounded token scan admits DOM capacity before
parsing; conversion admission combines token kinds with encoded string lengths.
Dense containers and large scalar backing therefore receive different estimates.
Converted arrays, dictionaries and byte strings adopt their fresh storage without
extra defensive copies. Updates and reference remapping borrow immutable stream
payloads while their source owner remains live. Remapping, sorting and page walks
use the shared governed helpers; private import storage refunds on every outcome.

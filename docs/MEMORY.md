# Cooperative memory admission

Lokad.qpdf accepts a host's shared allowance through `IQpdfMemoryHost.MemoryBudget`.
The library-local `IMemoryBudget`, `IMemoryScope` and `IMemoryReservation` interfaces
add no runtime package dependency. Adapters forward to the same allowance; scopes
introduce owners, not independent caps or copied remaining balances.

## Admission and ownership

- Reserve estimates before significant allocation or growth. Count retained
  capacity and temporary old/new overlap, rather than cumulative allocated bytes.
  Coarse execution, graph, component and result owners cover major storage;
  small short-lived objects intentionally use approximate allowances.
- Admission is immediate and thread-safe. Refusal leaves existing accounting
  unchanged and fails with `QpdfMemoryAdmissionException`, distinct from local
  `QpdfLimitException`, cancellation and actual `OutOfMemoryException`.
- Zero estimates are valid; negatives reject. Reservation/scope disposal is
  idempotent, nonthrowing and refunds the owned subtree immediately. Disposed
  owners reject new admission/resizing. Serialize disposal with subtree users.
- The supplied budget is borrowed. Dispose only scopes/reservations you create.
  Do not create a new root for a nested invocation or codec call.
- Keep an owner alive while any accounted storage, escaped alias, worker, deferred
  consumer or pending read/write still uses it. Disposal refunds accounting;
  it neither cancels work nor disposes CLR values. No forced GC is needed.
- Local `MaxMemoryBytes` and the shared host allowance both apply. Estimates
  are not guarantees about CLR heap or process RSS; hosts need runtime/native/GC
  headroom and policy for concurrent commands and durable outputs.

Engine refusal is not permission to retry with a smaller collection, spill to
disk, drop optional data or wait for memory. Construction failure rolls back its
fresh ownership; retained source/result storage stays funded. Conservative graph
charges can outlive individual objects without per-object lifetime tracking.

## Input leases

`QpdfPath` is a logical key; `IQpdfHost` owns access and replacement policy.
Each `IQpdfInput` pins its identity, length and contents until disposal. It has
one caller and at most one pending read.

`ReadAsync(offset, destination, token)` fills admitted caller-owned memory and
returns a count. Completion ends the borrow, including after cancellation. Await
it before reusing the destination, starting another read or disposing the lease.
Short reads reuse slices; premature zero/invalid counts are host failures.
Whole-payload readers enforce the smaller of `MaxInputBytes` and the supported
byte-array capacity before admission and reading. Their callers own input close
and the lifetime of the admitted bytes.
`QpdfInputReader.ReadExactlyToArrayAsync` admits an independent copy under the
supplied budget. Hosts separately fund their retained backing and private copies.

## Escaped values and results

Keep the caller's supplied scope alive through use of returned PDF values,
selection metadata, diagnostics, text and byte results, even after document/host
disposal. Public graph storage uses a coarse caller owner; collecting one alias
does not refund its share. Unreturned construction/private storage refunds after
cleanup. A child result owner under a disposed execution scope does not survive it.

`PdfStream.CopyData`, `PdfByteString.CopyValue` and `PdfName.CopyRawBytes` accept
a caller budget/token for admitted independent copies. A sibling owner can keep
them alive after source cleanup. `PdfArray.CopyItems` is shallow: referents still
need their source owner. Existing copy properties and caller-created object
constructors remain caller-managed; fund them as an aggregate when needed.

Fileless binary inspection can transfer an exact-size staging array to the
execution result. Results expose only initialized payload bytes. Their backing
stays admitted under the caller's scope.
Single decoded attachments can transfer their own backing to the result or
named publication. Raw and combined payloads keep their source or staging
owner until publication or result preparation finishes.

## Host transaction flow

1. Create unpublished staging under a host owner sharing the root allowance.
2. Await each `WriteAsync`; completion means the host consumed/copied the borrow.
   Retained backing remains immutable and funded by the host.
3. Establish storage ownership that survives the invocation before `CommitAsync`.
   Moving a receipt does not extend its ancestor scope's lifetime.
4. Once replacement succeeds, report commit success even if cancellation
   arrives afterward.
5. Await pending work and discard/dispose before refunding abandoned staging.

Split PDFs and first-pass captures release their serialized staging after
their individual transactions close. The built-in memory host funds persistent
keys and bytes separately from invocation scratch.
Known output lengths can pre-size staging without changing durable ownership.
[Caller publication guarantees](SUPPORTED-PROFILE.md#governance)

## JPEG codecs

Hosts supply `IQpdfJpegCodec` through `IQpdfJpegCodecHost`. Each call borrows
`QpdfJpegCodecContext.MemoryBudget` and `CancellationToken`; create empty child
scopes and reserve/resize before major scratch or output growth. Input/output and
sample buffers supplied by the engine are already funded. A replacement buffer
needs old/new overlap admission until the old storage is released.

Budget operations and encoded-output growth share the job's memory lock. Finish
all workers and release borrowed aliases before disposing owners or returning.
Shrink/disposal remains possible during canceled cleanup. A codec's boolean
admission probe can decline an optional candidate and complete using its existing
funded capacity; failure after refusal preserves the memory-admission category.
Actual allocation failures remain OutOfMemoryException.

Decode streams expose their admitted input segment through
`MemoryStream.TryGetBuffer`. Borrow only that segment for the call and keep it
immutable. All supported color models use the same buffer and budget contract.
[JPEG capabilities](SUPPORTED-PROFILE.md#jpeg-codec-capabilities)

## Engine guarantees and limits

Standalone page counts can read compact classic-xref metadata and bounded object
extents through the opened input lease. Inputs below 128 KiB use the complete
reader; larger candidates require at most 64 KiB of xref/trailer data and 256 KiB
of prefetched extents, plus a 256-byte EOF probe. Extents above 64 KiB are omitted
until required by validation. Parsed containers and traversal need their own
admission in addition to these acquisition bounds.

Encrypted, incremental, hybrid/xref-stream, oversized-metadata and uncertain
inputs use complete acquisition on the same lease. Missing required extents,
reader warnings or inconsistent declared counts can require that fallback.
[Admission and ownership](#admission-and-ownership) ·
[Input profile](SUPPORTED-PROFILE.md#input-profile)

Authenticated document opening borrows caller password text/bytes only until the
open operation completes. Bounded encoding, derivation and cipher scratch are
admitted and cleared. A document owns its authenticated file key and clears it on
disposal or failed opening. Plaintext PDF values follow ordinary graph ownership
and may outlive the document; they are not zeroed while retained callers use them.
Caller-owned bytes and immutable password strings are never claimed to be erased.
Command password files fund separate 512-byte active and candidate prefixes plus
at most 512 bytes of read scratch. Scratch remains admitted through input close;
both owned prefixes clear before their storage retires.
[Command setter semantics](SUPPORTED-PROFILE.md#command-password-files-and-modes)
define how each file changes the credential.

The engine prepares admitted result and diagnostic storage before publication.
`QpdfExecutionCounters.HostOperations` counts actual opens, reads and writes.
[Caller limits and diagnostics](SUPPORTED-PROFILE.md#governance) ·
[Benchmark methodology](BENCHMARKS.md)

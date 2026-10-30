# Cooperative memory admission

Failure diagnostic views borrow admitted history and terminal storage. Four cached
integers record visible counts and prefix overlap; their fixed allowance is
56 bytes. Freezing scans the existing lists linearly without a new collection,
including during canceled or refused cleanup. Counts and terminal lookup avoid
history searches, and enumeration uses indexed passes without per-record boxes.

Page selection admits known page/source list capacities once under existing
construction owners, with coarse shared-budget growth. Expanded ranges and
previews receive admission and cancellation checks. Included ranges grow one
result list; exclusions test intervals and position filters compact that list
without materialized spans, exclusion sets or a second result. Each source reuses one
kept-page snapshot; multisource range/snapshot scratch refunds when that source
finishes. Preservation reuses its cycle set instead of allocating one per page.
Copied graph pool tables and page-child backing stay funded through publication.
Returned immutable selection metadata keeps its separate caller ownership.

Unreferenced preservation admits its candidate list and uses the same copy-table
growth as ordinary copying. One reusable list records new source identities for
the current candidate, allowing linear rollback without scanning earlier copies.
Both lists refund after preservation. Split retention IDs use the existing group
owner; ordinary output builds no discarded return set. Failed graph estimates
remain within the copy phase's coarse owner until that phase closes.

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
Caller-supplied codecs borrow `QpdfJpegCodecContext.MemoryBudget` and its
`CancellationToken` for one call. Create empty child scopes, then use
`TryReserve`/`TryResize` before acquiring or growing significant scratch.
These owners share the PDF job's local and host allowances. Refusal leaves the
previous estimate unchanged; a codec failure after refusal preserves the original
budget category at the PDF boundary. A successful fallback may still return.
Shrink and disposal remain available during canceled cleanup. A replacement
buffer needs admission for both buffers until old storage is released; samples
and supplied input/output are already funded. Budget operations are thread-safe;
finish all workers before owner disposal or return. Streams and samples have one
caller. Calls have independent owners, and their borrowed contexts cannot escape.
Codec scope operations and encoded-output capacity changes share the job's
memory-accounting lock, including shared-budget callbacks and refunds. Jobs
that never invoke a codec keep their existing allocation path without this lock.
Late receipt disposal cannot refund another owner's storage. The development
Lokad.Image adapter forwards interfaces without a private owner tree or blanket
workspace charge. The core has no runtime codec dependency.
Filter errors show at most 256 name characters plus an ellipsis; the exception
keeps the full, already admitted name alias without duplicating it in the message.
Xref growth uses validated section/merge counts to cap geometric overshoot.
Large capacities within the pinned [.NET 10 preferred-prime table](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Collections/HashHelpers.cs)
use a conservative 2x prime-capacity allowance, replacing the unnecessarily broad
3x estimate there; smaller and out-of-table requests keep the broader allowance.

`IQpdfInput.ReadAsync(offset, destination, token)` fills caller-owned storage.
The caller admits that storage before I/O. Short reads fill successive slices of
the same destination; no host result array or assembly overlap is required.
`QpdfInputReader.ReadExactlyAsync` fills an existing buffer and rejects invalid
counts or premature zero as host failures. `ReadExactlyToArrayAsync` admits its
one result array under a supplied owner. Failed or canceled reads refund only
after outstanding input work finishes. The input lease remains caller-owned.

## Host transaction flow

`QpdfPath` is a logical key. An `IQpdfHost` implementation owns authorization,
storage access and replacement policy; it does not grant ambient filesystem or
network access to the engine. Acquire each `IQpdfInput` once and pin that content,
identity and length through disposal. Await each offset read before the next
read or disposal, even after cancellation. Complete pending I/O before returning
from a read; do not keep a destination reference afterward.

Expose an invocation owner through `IQpdfMemoryHost`. Keep host staging and
durable publication under separate owners sharing the same root allowance.
Write into unpublished staging, and finish each borrowed write before completing
the call. If the host retains its backing, keep that capacity funded and immutable.
Before `CommitAsync`, establish an owner that survives the invocation for the
published bytes; transferring a receipt preserves its original scope lifetime.
Do not publish bytes whose only owner is about to close.

`CommitAsync` is the publication boundary: failure or cancellation before that
boundary leaves the previous target intact. After successful replacement, return
success even if cancellation arrives; do not report a canceled commit that already
published. Writing or closing a stream alone does not implement this contract.
On failure await discard/disposal and outstanding users before refunding staging
or invocation storage. Keep returned bytes, values and diagnostics funded until
their final consumers finish; then dispose their owners. The in-memory host and
the package consumer exercise this ownership sequence and retained outputs.

## Engine ownership details

Attachment extraction borrows unfiltered or fallback bytes from the open document
through output consumption. Filtered input slices are admitted before copying;
decoded arrays stay in a temporary scope through inspection or transactional
publication and are refunded on success, cancellation or output failure.
Attachment metadata uses borrowed PDF bytes and admits decoded strings, checksum
text and collection growth before materialization. Typed listings transfer only
their returned text, records and readonly collection to the caller owner after
input cleanup succeeds; temporary traversal and graph storage refund immediately.

Object stream inspection borrows raw bytes through consumption and checks their
output size before copying. Filtered and normalized buffers stay admitted through
fileless result assembly or transactional publication. Filter chains retain their
independent intermediate decompression limits; normalized output can be smaller
than its source, so its output ceiling is applied during construction.

Object-stream writing admits source/candidate sets, remapping and final group
capacity under the existing copy/selection phase. Sorting scratch refunds before
returning the groups; a failed candidate transfers no partial charge. Split
groups refund after their PDF has been serialized into owned publication bytes.
The unused whole-document grouping pass is omitted for split output.

Split identity remapping always uses the active group owner; there is no
unowned allocation branch. Unchanged values and stream payloads stay borrowed.
Group roots, ordering, renumbering, page children and model lists refund after
complete byte capture. Prepared bytes and publication records keep their batch
lifetime through later commits. QDF provenance maps are admitted and cancellable under the copy,
selection or group phase; suppression skips their construction altogether.

Shared graph reachability borrows arrays and frozen dictionary values through
reverse cursors, keeping their pending storage proportional to nesting depth.
Legacy dictionary storage needs an admitted reverse snapshot, refunded when its
frame finishes. Generation-aware visited references are temporary; only the
completed set of reached object numbers transfers to the requesting phase.
Cycles, missing references and cancellation retain their existing semantics.

Image optimization borrows raw samples from the copied graph where possible.
Decoded samples, separator trimming and JPEG candidates have temporary owners;
rejected candidates refund their storage, and accepted buffers and headers
transfer into the graph without cloning the payload. Header-only form rewrites
borrow the original immutable payload.

Rotation matrices and annotation Form promotion also rebuild only admitted
headers. Direct and shared appearance streams keep their immutable payload
storage; failed header construction refunds its fresh allocations.

Overlay annotation import borrows source arrays and admits memo/list capacity in
a temporary placement scope. Only actual imports trigger one exact destination
array allocation. Page headers replace Resources and Contents together; rectangle
headers use the same admitted dictionary rebuilds. Fresh retained values transfer
to the job owner before installation; failed construction refunds its batch.
Overlay form reachability borrows field arrays through one admitted cursor per
nesting level. Visited nodes have a temporary owner; the reached set stays funded
under overlay planning and refunds when that phase closes.
Imported fields inspect borrowed headers before deciding whether to rebuild.
Changed headers, repaired parent links and direct-child arrays receive admission;
default-program rewrites share their header's allocation batch. Resource unions
admit dictionary growth and transfer the completed dictionary without copying it.
Field traversal scratch refunds after each import, including failed construction.

Default-appearance analysis scans borrowed bytes once, retaining only the last
font and size. It admits decoded font-name bytes and any corrected appearance
before materializing them. Appearance decoding and construction use a temporary
scope and bounded buffers; only the final stream transfers into the graph.
Generation borrows the already copied form's default program and resources.
Missing font bindings are built in the same allocation batch as their appearance,
so failed construction refunds both. Marked-content matching scans borrowed bytes
with admitted container scratch before allocating replacement output.
Field values and choice strings borrow their encoded source and admit final
decoded text and encoded arrays before allocation. Their scratch scope stays
open through appearance construction; failed or canceled conversion refunds its
new reservation. ASCII, WinAnsi and MacRoman use one final array per value.

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
less than two spare blocks during execution to avoid host resize pairs for tiny
scratch at a block boundary. Larger releases shrink immediately; zero live bytes
refund the reservation. Completion attempts one trim to the retained estimate.
A declined optional shrink keeps the existing charge and stops further
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
capacity, member-line offset/length indexes and numeric-table formatting overlap before
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
buffers. Low-level input callers admit their destinations before reading;
the allocating helper provides that boundary when an owned result is needed.

AcroForm JSON borrows parsed bytes and arrays. A field scope funds ancestor
storage, cycle detection and decoded/qualified names until their characters enter
the admitted JSON buffer. Partial names reuse their decoded value; each choice
refunds its text after serialization. Image JSON admits name capacity and inline
descriptor growth per page, and sorts borrowed names with the shared comparator.
Page contents iterate borrowed direct or resolved indirect arrays. These scopes
use the existing coarse shared reservation; they do not reserve from the host
per name, character or comparison. Import text encoding remains covered by its
aggregate model estimate, independently of the graph-edit allocator.

Rotation admits retained arrays, numeric text and replacement dictionaries in a
page batch; traversal sets have a phase owner. Content collection borrows original
stream references and admits its known metadata capacity once. Checking and
rotation share that collector. Temporary matrices and formatting storage refund
after consumption, while rewritten stream headers borrow existing payloads.
Rotated appearance headers use a page-local admitted identity memo. Copies share
the original payload bytes, and annotations on the same page share the copied
stream. Other pages keep their original appearance headers. AP/state dictionaries
and reference metadata receive admission; indirect containers stay indirect.
The memo refunds after each page.
The existing orphan-removal path drops obsolete AP/state/stream objects in at
most three pool scans per copier, after all pages finish. Live aliases and
explicit unreferenced-object preservation keep their originals.
JSON's array lookup inspects an admitted tokenizer header before ordinary array
loading; reporting an uncompressed stream reference does not copy its payload.
Appearance generation borrows annotation arrays for indirect widgets, admits
direct-widget replacement arrays before copying and retains their page rebuild
under a graph batch. Generated identities and the lazy parent index have a phase
owner; per-field ancestor scratch refunds after lookup. Choice arrays and
appearance-state names are borrowed. Flattening uses the same admitted content
collector as rotation and checking, with an exact final content-array capacity.

Image optimization keeps form visitation under a temporary admitted container
owner. Borrowed entry/name lookups avoid per-entry name/display copies. Indirect
stream replacement preserves resource aliases without rebuilding page or form
headers; cycles stop at the admitted visit set and form depth remains bounded.

Attachment construction admits final encoded text/name backing, header tables
and name-tree arrays under the existing graph construction scope. Encoding
checks cancellation in bounded chunks; small fixed vocabulary headers reuse
the admitted name table. Owned input/compression payloads transfer into stream
wrappers without another payload copy. Failure refunds the construction owner;
publication still holds the completed graph and bytes through host cleanup.
Typed attachment projections use known cardinality to admit one exact list and
record allowance. Their temporary owner stays live through execution and refunds
before final typed counters are captured, including failure and cancellation.

Prepared JSON UTF-8 buffers and sidecar names stay under the private preparation
owner through awaited publication and transaction cleanup. Host callbacks borrow
that storage; persistent host copies and identifiers have a host owner. After
cleanup, the library refunds its unreturned publication candidates. Returned JSON
text retains its caller charge independently from published UTF-8 bytes.
Schema testing uses a temporary admitted UTF-8 buffer, sized from the serializer's
existing byte count, and a token reader instead of a full-output DOM.
Encoding checks cancellation in bounded chunks; scanning
checks at each token. Only top-level help/drift keys need decoded storage, with
admitted list growth and unescape capacity. Drift messages use the existing
bounded diagnostic builder and explicit quota failure before publication.

Parser and transform construction scopes transfer their existing estimates to
coarse graph owners. Retention does not walk object references or create weak
tables and finalizer receipts. Private graph charges end with execution cleanup.
Construction batches keep byte estimates rather than allocation counts or object/root
references. Dictionary growth updates its original byte estimate, while failed
handoffs preserve source ownership through cleanup. Private configuration and
filter text end with their disposable component owners.
Loaded job configuration keeps decoded strings under its text owner through plan
consumption and awaited cleanup. Parsing refunds unused copy capacity while
preserving accepted strings until that loaded job closes.
Job JSON of 16 KiB or more uses one bounded density scan before DOM construction,
with aggregate metadata and decoded-string credit. Text input is scanned in place;
file input admits its raw backing and bounded read-copy overlap first. This avoids
charging a long scalar as though every byte introduced another small JSON value.
Small settings keep the inexpensive blanket estimate. Escaped strings remain
conservative, and admission still uses the existing coarse execution reservation.
Mixed argv loading funds serialization scratch separately from retained documents
and copied text. Long arguments use conservative escape/growth estimates; short
arguments keep the cheap bound. Scratch credit refunds after all setters and
child loads finish, while the loaded job retains its persistent configuration.
Prepared typed result arrays transfer to the caller when the result is accepted,
and refund with private cleanup when preparation fails.

Public document values keep their graph estimates under the caller's owner after
document disposal. Collection of an individual child or sibling does not refund
part of that coarse estimate. Counter snapshots requested after disposal still
need admission beneath that same live caller owner.
Opening keeps its graph provisional until a document returns; failure refunds
that unreturned graph after cleanup.

Diagnostics store actual messages under count and UTF-8 byte ceilings, without
message interning or predictions of future formatting. Suppressed warnings keep
only their exit-status flag. Overflow fails explicitly and preserves the accepted
prefix with a bounded terminal error. Actual summaries are admitted before
publication and reused after commit. Their peak estimate precedes presentation
storage; the final counter can include a higher peak. Returned diagnostic records
and text belong to a coarse result batch. An exclusive prepared buffer uses a
small rollback receipt until exposed or abandoned; that receipt is never attached
to a returned PDF object. See [resource API changes](COMPATIBILITY.md#resource-api-and-behavior-changes).

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

Private hint tables and encoded hint payloads remain under their disposable
reader/writer owners through consumption. Tests call these same scoped kernels;
there is no separate escaping helper path or private-signature compatibility
adapter to preserve older test bindings.

Ownership tests use ordinary cases for byte admission, rejected transfers, cleanup
and output faults. Representative collection checks keep public aliases or completed
tasks rooted while checking that private storage is released. They do not depend
on collection round counts, no-GC regions or zero-byte maintenance calls. Coarse
caller storage refunds when its scope ends, independently of individual aliases.

Async writer outcome matrices check admission at real output phases and refunds
with completed tasks kept alive. Direct owner tests verify detachment, disposed
access and collection of released backing. One focused linearized-xref fixture
also observes its construction scope to prove per-buffer funding through either
pass; coarse job refunds alone do not establish that allocation's admission.
Tests avoid asserting collection during an async producer's return, when runtime
stack roots can still exist. Whole-job matrices validate persisted values and
private refunds without requiring collection. Focused component and document
cases retain collection proofs for disposed buffers and escaped public values.

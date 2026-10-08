# Architecture

This describes the implemented architecture. The library ships host contracts, an in-memory host, a per-job governor, diagnostics, and staged output alongside the strict reader, object graph, filter pipeline with the governed JPEG adapter, shared copy engine, content pipeline, deterministic writer, and the typed, argv, and job-JSON entry points.

Three entry points converge on one operation plan: familiar qpdf argv,
qpdf job JSON, and typed operations including bounded page selection. The
operation plan uses one binary reader, object graph, filter pipeline and
writer. PDF object JSON and job JSON are different formats.

Job files load partial configurations iteratively through the host, using stable
input identities for cycle checks. Sorted-key prefixes and reverse suffixes share
the existing operation parser and preserve native setter/duplicate semantics.
Configuration storage stays admitted until execution finishes; input leases close
between files. The loader bounds cumulative bytes, work, nesting and storage.
Mixed argv uses an ordered syntax adapter over that same loader/parser; independent
file chains retain native internal key order without changing argv positional
state. JSON inspection without a file returns immutable execution output text.
Whole-file selection closes its preliminary input before reopening through the
shared copier, so replacement can publish after those input leases close.

Writer compression setters share ordered state across argv and job configurations.
Plans store that state in one byte while retaining raw options for inspection.
QDF fills fields that earlier setters left unset; stream-data setters update both
compression and decoding. Argv/job writers default to generalized decoding and
compression, while typed selection retains opaque stream copying. The shared
stream transformer preserves empty streams and keeps catalog XML metadata
uncompressed. Copied streams resolve filters against their destination pool;
created images use the same owned transformer and encoding policy. Rewritten
content preserves encoded name bytes and their slash prefixes. Broader
transformation defaults and whole-job ownership remain under compatibility audit.

Linearization emits zero-width shared numerators because every numerator is zero.
Hint offsets, lengths and derived widths describe each output's physical object
layout. Complete native hints and independent byte-boundary checks distinguish
those layout differences from encoding choices. The writer scopes bit buffers,
construction collections, tables, compression output and the final hint object
through replay. Independently escaping table arrays retain their own receipts.
The shared hint reader borrows immutable raw stream backing and scopes Flate
decoding, overflow joins, bit cursors and temporary arrays. Check/show retain
parsed tables only through classification, validation and reporting. Independently
escaping tables, entries and identifier/numerator arrays receive separate receipts;
discarded siblings and decoded scratch can collect while source values survive.
The combined hint correction is verified through complete public jobs and the
original native/reference/package controls. Broader ownership, cold state and
BCL/codec physical-memory measurements remain under audit.

Document-open traversal starts at catalog values, so direct containers retain
indirect descendants and indirect roots retain their own physical identities.
Page boundaries are tested before assigning object-stream owners. The ordering
pass includes generated container IDs when placing document-open objects.
The catalog copier preserves non-name PageMode values through its existing graph
copy path. Physical linearization checking uses the shared reader for object
ends and the shared graph classifier for compressed owners. It verifies page
runs, hint spans, shared groups and outlines against the actual input layout.
Show-linearization retains parsed raw fields on physical failures. Broader checker
coverage and broader complete-job ownership remain open.

Argv and job checker plans use the typed checker core and the same opened
document for subsequent count, linearization, xref, object, page/image and
attachment listings, in native inspection order. Serialization-only QDF/linearize
settings do not rewrite inspected objects. Selection, collation and rotation use
a scoped inspection graph over the original reader. Primary object generations
and physical xrefs remain visible; duplicates/imports receive fresh identities,
removed pages resolve to null, and inherited attributes materialize before
checking. Source filename keys retain native alias semantics. The view shares
inheritance/range/copy/collation/rotation helpers and detaches before its scoped
admission releases. Page/content listings preserve reference generations.
Page traversal and declared-count reporting are distinct; failed checking stops
subsequent inspection. Fileless stream/attachment inspection returns caller-owned
binary bytes separately from diagnostics, distinguishing absent and empty payloads.
Failure or cancellation discards staged bytes. Filtered output uses shared governed
normalization; command checking and split writing share admitted reachability
traversal. Overlay/underlay and broader transformation/default/context audits
remain open. Public execution and typed result boundaries refresh immutable
counter snapshots after their core, configuration and input/output scopes finish.
Failures and cancellation retain observed work; typed throwing routes keep their
existing exception contract. Counter copies preserve warning/mapping/text/binary
references and readonly public properties. Later weak-receipt collection can reduce
live governor memory after a returned snapshot. Complete public jobs and committed
reference/package checks verify these counter boundaries. Verbose/progress/memory
summaries still bypass diagnostic count/byte admission before publication
(DIAG-02P); result/presentation storage and diagnostic ownership remain under audit.

The host owns stable input identities and ranged reads, output staging and
publication, scratch storage, descriptors and any supplied environment values.
The library owns PDF semantics, explicit results and per-job memory/work limits.
No runtime path gains filesystem, process or network authority implicitly.

The engine was built bottom-up from strict reading, opaque stream preservation, and a small complete
read/select/write path. The same engine grew into the full page-selection
contract before recovery and advanced transformations arrived. Forms and graph
identity came before general cross-document imports, content analysis before
resource pruning, and the stable writer and replay model before QDF and linearization.

Memory safety includes both safe managed operations and resource governance.
Validate lengths/offsets and checked arithmetic before allocating; bound work,
decompression, graphs and cancellation intervals. ImageSharp and BCL codecs
need explicit allocation/input/output policies; managed code alone is not an
exhaustion limit.

Reader attempts and parsed subtrees carry independent reservation receipts;
discarding an outer attempt preserves previously accepted nested loads. Xref
and cache/guard collections admit their wrappers, capacities and growth overlap.
Cache entries distinguish unread owned subtrees from borrowed values, so
replacement and disposal release only the unread admission. Document opens
transfer their input-copy and exclusive xref-container receipts to the engine;
component engines can instead borrow caller-funded data and tables. Disposal
drops input/table/page-view/lease/cache references before releasing those owners,
including failed construction and host-close faults. Page-list and traversal
capacities are admitted. Returned immutable bytes, trailer/key aliases and
historical page counts stay usable. Accepted parser/cache values transfer only
their own receipts into a per-job weak ownership ledger. Wrappers and independently
escaping numeric strings, dictionary backing/key views and stream buffers have
separate receipts. Collection publishes atomic retirement notifications; the job
thread performs governed index cleanup. Segmented weak tables, index capacities
and growth overlap are admitted. Failed acceptance rolls back its new carriers
without releasing previously accepted aliases. Byte-string parsing transfers its
fresh decoded buffer; public construction and returned copies remain immutable.

JSON serialization uses an exclusive internal reader because its outputs are
text and copied sidecar bytes. It closes that reader before creating output
transactions, preserving prior main/sidecar targets if input disposal fails.
JSON success/failure/cancellation snapshots retain observed work, peaks and
post-cleanup handles. Broader route/presentation counters, diagnostic record/list
admission and owned-message retirement, cold/native state and nested loading
remain under audit; [progress](docs/PROGRESS.md) records the measured gaps.

The solution contains the packable library, tests, public API consumer,
development probe and synthetic benchmark harness. Benchmarks are development
tools; resource claims require independent allocation/retention measurements
bound to the tested source tree. Do not create duplicate engines or empty
project layers solely to match the eventual architecture.

The integrated diagnostic engine admits record/text/list storage and future export
work before accepting a warning. The retained-value ledger tracks independently
escaping aliases without anchoring completed jobs. JSON stages warnings through
input closure before publication. Parser/recovery/copier/remapper raw-name tables
share bounded collision work and immutable backing ownership. Writer row/index,
object-stream capture/header/payload, encoding/order/xref/remap and layout owners
drop dead backing before retiring admission across awaited outcomes. Hint bodies,
parsing/shared/group scratch and complete public-job ownership remain under audit;
these integrated consumers do not establish the wider containment verdict.

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
Response decoding admits its exact UTF-16 string before allocation while encoded
bytes overlap. Its independent text owner survives input close and line splitting;
close failures discard that unreturned owner. Empty read-copy arrays retain their
allocation header admission. Expansion admits its list and backing growth, borrows
caller strings/shared empties and admits generated substrings before allocation.
Independent string receipts cover surviving host names and result diagnostics;
single whole-line responses transfer their decoded string without double admission.
The expansion vector clears and retires before final counter capture. Configuration
parsing shares one text factory across ordinary/mixed argv and direct/nested JSON.
Argv value substrings receive exact admission before allocation; caller strings
and fixed syntax literals remain borrowed. JSON decoded copies transfer their
payload admission from the loader's existing document budget to independent weak
receipts; receipt metadata is admitted separately. Generated input, output, include
and pass-one names remain funded while a host retains them. Disposal releases
the unused text-copy pool immediately after plan construction; independently
escaped strings retire with their aliases.
Page-label first-page/prefix copies, parsed rotation ranges and canonical JSON
object selectors use that same independently retained text admission. Label,
rotation and selector syntax scans borrow spans; temporary split arrays and
numeric substrings are avoided. Typed JSON shares the selector factory.
Typed source snapshots and label/rotation conversions share one scoped
preparation owner under the existing governor. It admits actual list growth,
record/string storage and bounded grammar scratch before allocation, observes
work/cancellation during conversion and holds storage through publication.
Disposal drops all preparation fields before final counter capture. Caller
records/page arrays/prefixes stay borrowed; the existing range cap/defaults stand.
Collation preparation shares a governed span parser across argv and job JSON.
Its scoped owner admits actual vector growth/overlap, registry storage and the
policy wrapper/integer snapshot before allocation, observes work/cancellation
and holds the storage through publication. JSON reuses its validated vector;
disposal detaches all fields before final counter capture. Typed caller policies
remain borrowed. Other parser-created vectors, derived strings, syntax adapters,
plans and broader configuration/DOM controllers remain explicit ownership obligations.
Ordinary argv global-block removal and main-option filtering use one scoped
scratch owner. It admits marker/stripped/main vectors and actual capacity growth
before allocation, borrows original strings, bounds scans/copy work and checks
cancellation. Sorted markers are traversed once. Every field detaches after plan
construction; the owner retires before public input and final counter capture.
Other parser plans/global/context vectors remain explicit obligations.
Mixed argv uses an ordered syntax adapter over that same loader/parser; independent
file chains retain native internal key order without changing argv positional
state. JSON inspection without a file returns immutable execution output text.
The JSON serializer shares governed text chunks across scalar/container encoders,
borrows immutable PDF backing and scopes decoded payloads and sorted key vectors.
Prepared JSON owns main/sidecar candidates and independently escaping text, host
buffers and filenames. Its exclusive reader closes before immutable/publication
copies; schema-note acceptance remains before input close. Exclusive null repair
memoization uses an admitted number set, and aborted parser containers unwind
before their concluding warning allocations. DOM/controller/resource ownership
and generic returned DTO admission remain under audit.
Whole-file selection closes its preliminary input before reopening through the
shared copier, so replacement can publish after those input leases close.
The preliminary reader and overlay/attachment-copy sources use exclusive value
ownership. Their immutable values transfer to the job's scope before source
disposal; input bytes and reader maps retire at disposal. Previously unscoped selection,
merge, overlay and attachment copiers use the existing owned factory. Selection, rotation, overlay
decode/encode and attachment assembly reservations stay scoped through
publication. Output-length admission releases after transaction disposal on
success, failure or cancellation. Fresh transformed streams prepay their weak
retirement before acceptance, so exhausted jobs need no new maintenance work.
These changes target the finite B5S controls; broader ownership remains under audit.

Explicit and source-derived labels use the existing owned-allocation batch and
name-table dictionary builder. Fresh keys, names, integer tokens, prefix buffers,
dictionary/array wrappers and number-tree backing receive admission before
construction and independent prepaid weak receipts. Encoding writes directly to
the owned prefix buffer; borrowed copied style/prefix values keep their original
owners. Label vectors use copy/selection scopes, and split carry vectors close
per output. Whole-file verbatim source trees retain the existing scoped copier
behavior; broader copied-container/reader ownership remains under audit.

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
The shared copier registers selected page identities before descending into
their values, so auxiliary links to later selected pages remap without losing
them. Omitted pages stay excluded and duplicate occurrences remain distinct.
Array-valued AcroForm entries use ordinary catalog graph copying; dictionary
forms use field preservation/rebuilding, and imported fields take precedence
over an opaque primary array. Explicit form removal still drops the entry.
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
admission releases. Overlay/underlay inspection uses the shared placement
pipeline with the original reader as resolver for retained content/resources.
Primary annotation/form graphs retain immutable values and identities; imported
fields use shared collision/default-resource/catalog assembly. Direct trailer
catalog edits belong to the same scoped view and detach with it. Page/content
listings preserve reference generations.
Page traversal and declared-count reporting are distinct; failed checking stops
subsequent inspection. Fileless stream/attachment inspection returns caller-owned
binary bytes separately from diagnostics, distinguishing absent and empty payloads.
Failure or cancellation discards staged bytes. Filtered output uses shared governed
normalization; command checking and split writing share admitted reachability
traversal. Remaining transformation/default/context qualifications are bounded
product checks in the delivery plan. Public execution and typed result boundaries refresh immutable
counter snapshots after their core, configuration and input/output scopes finish.
Failures and cancellation retain observed work; typed throwing routes keep their
existing exception contract. Counter copies preserve warning/mapping/text/binary
references and readonly public properties. Later weak-receipt collection can reduce
live governor memory after a returned snapshot. Complete public jobs and committed
reference/package checks verify these counter boundaries. Argv/job execution holds
verbose/memory diagnostic slots while ordinary diagnostics are accepted. The
governor activates four progress slots before charging the first output byte
or accepting a prepared positive-output length;
fileless jobs with no charged output reserve none. Successful presentation accepts
the held count once; failure/cancellation releases the scoped reservation. Summary
messages reserve their exact UTF-8 quotas as output/decoded/peak counters grow.
Presentation prepares independently owned summary text, records, arrays and
readonly views before commit. Formatting and traversal charge bounded work.
Unexposed candidates retire immediately; escaping aliases keep separate receipts.
A prepaid failure view borrows accepted warnings and terminal diagnostics without
a failure-time list copy. JSON carries suppression/exit flags and admits schema
notes before publishing. Failure cleanup exports already accepted staged warnings
with held work, then restores ordinary cancellation checks.

Split presentation serializes and hashes all main/pass1 outputs into governed
segments before opening output transactions. Its batch owns bytes through
sequential publication, including pass1. JSON prepares main/sidecar lengths and
publication work from its serialized data. Publication-loop work is reserved
before commit; unused steps are released without increasing observed work.
Preparation holds more split bytes than streaming and obeys existing job limits.
Split/pass1 and JSON preparation now applies to every configuration and typed
route, including plain jobs. Batches admit future host opens/writes, one sequential
handle and reusable output-wrapper storage before first commit. Shared input/output
wrappers admit measured 32/40-byte storage before allocation and count each open,
read and write attempt. Disposal drops references and keeps admission through host
close, then retires once even on failure. Unexecuted reservations do not change
observed counters. After history admission, plain publication failures preserve
accepted warnings and their existing informational-note omission. Plain success keeps its original
projection; empty failure views retire. Plain JSON attaches history after reader
closure/staged export, avoiding overlap with exclusive-reader storage. External
host publication and post-commit close behavior remains unchanged.
Staged failure export releases encoded scratch after promotion and attaches plain
history within its admitted work and cancellation boundary. Warning-only plain
history owns its projection, so hidden informational records can retire. NoWarn
preserves ordinary admissions and clears private warning references on the first
visible record; independently escaping records/text keep their receipts. Accepted
warning state preserves warning exits. Generic result/fatal storage and JSON
payload admission remain DIAG-01/JSON-01. DIAG-02P2F is retired by final combined
source/Release/reference/package proof; P3H remains retired and wider ownership
stays open. JSON-01J now funds complete public inline JSON text/publication aliases with final
combined proof; broader DOM/resource ownership remains JSON-01.

Command cores carry private value outcomes. Before processing, the governor
reserves fixed result/counter storage and construction/retirement work. Private
carriers own dedicated weak receipts independently; they need no general
value-ledger insertion. After operation/configuration/publication scopes close,
the governor fills the carriers once and drops its references. Exposed snapshots
remain immutable. Completed retirement is bounded and can finish after
cancellation; active execution still observes cancellation. Limits that cannot
admit the carriers borrow immutable outcomes before job or host work. Caller
record copies preserve exposed values and aliases without copying receipt
ownership. Typed facades pre-admit their public wrapper, counter, dedicated weak receipts
and construction/retirement work before processing, then capture final values
after cleanup. Six private value outcomes replace nine heap public-DTO sites;
command cores do not allocate typed public carriers or counters. Typed boundaries
adopt engine-created snapshot arrays; public constructors still copy caller input.
Each wrapper owns its own receipt without rooting completed job metadata. Caller
copies omit that receipt and preserve exposed equality/hash and aliases. Failure
drops unreturned references and only unused constructor credit; actual receipts
retire with prepaid work. Private outcome slots in async/task frames remain under
the physical MEASURE-01 qualification, and copied backing/mapping under T4.
Library-created versions own independently prepaid weak receipts. Header and
catalog aliases share each owner once; writer negotiation borrows an existing
version or admits a new one before allocation. Versions can share the already
funded result-channel weak link; either retirement channel keeps it alive until
both are empty. Caller copies omit ownership and preserve component equality.
An exclusive JSON reader validates header/catalog version components as value
data and emits them directly, avoiding unused heap versions at the original
repair ceilings. Public/shared documents allocate owned versions before returning;
their header/effective aliases remain usable after disposal. Private document
component fields, governor growth and async frames remain under MEASURE-01.
Public document warning reads borrow shared empty storage or admit independently
owned immutable view/backing snapshots through the shared result collection
factory. Prepaid retirement preserves independent warning aliases and diagnostic
identities after disposal without rooting completed metadata. Later warnings do
not mutate earlier snapshots. Reads obey the original document token and limits;
generic diagnostic/text and broader ownership remain under audit.
Typed selection, JSON and attachment cores attach a scoped warning snapshot to
the shared governor preparation boundary. Each immutable view and backing array
has an independent prepaid receipt; records/text are borrowed. Snapshot
admission occurs before commit and includes warnings accepted during preparation.
Unchanged histories reuse their snapshot across commits. Completion detaches
job references, and returning the prepared collection adds no allocation or
cancellation poll after publication.
Command outcomes borrow the already governed diagnostic history for their
execution presentation, avoiding a second typed snapshot on those routes.
Command and typed scopes share the existing governor preparation slot through
ExecutionPreparation, preserving its established physical frame.
Each public document counter read independently admits snapshot/receipt storage,
a shared weak governor link and construction/retirement work. Completed document
retirement can reconcile after cancellation. Copied backing/mapping remain
DIAG-01R-T; generic diagnostic/list/fatal storage remains DIAG-01.

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

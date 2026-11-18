# Architecture

Lokad.qpdf uses one managed PDF pipeline for typed C#, argv and job JSON.
The engine owns PDF semantics; the application host owns storage and publication.
The [supported profile](SUPPORTED-PROFILE.md) defines scope;
[compatibility](COMPATIBILITY.md) records defaults and native differences.

## Execution flow

```mermaid
flowchart LR
    Entry[Typed / argv / job JSON] --> Reader[Reader and object graph]
    Reader --> Inspect[Inspection results]
    Reader --> Transform[Copy and transform]
    Transform --> Writer[Prepare output]
    Writer --> Host[Host commit]
```

Argv syntax and job JSON feed one semantic planner; typed methods call the same
operation cores and validation. Argv supplies direct settings without serialized
JSON. Actual JSON retains its admitted documents and copied text through plan
consumption, including ordered nested-file loading. Scoped syntax helpers handle
page, placement, attachment and encryption blocks.
Each loaded document prepares its last-property-wins sequence once; a temporary
admitted suffix index preserves include order and retires before plan consumption.
Encoding validation stays independent of semantic validation. Effective root keys
validate before includes load, and nested setters validate in the shared planner.
Writer plans share one option projection after branch-specific source selection.
Typed document opening, checking, JSON, counting, page selection and attachment
operations can explicitly choose the shared bounded repair policy;
multi-source operations use `PdfPageMergeOptions.RecoveryMode` for every input and
encryption template. Existing typed defaults remain strict. Recovery warnings flow
through the existing result snapshot and publication owner.
Typed JSON with and without a report target shares one adapter and JSON core;
result-only none/inline reports do not acquire output transactions.
The reader resolves classic, streamed, hybrid and incremental xrefs into immutable
PDF values. Eligible selections read bounded ranges from classic-xref inputs,
falling back to a complete read on the same lease when necessary.
Standalone page-count inspection shares this bounded prefetch and page-tree walk;
`CountPagesAsync` returns only the validated count, warnings and final counters.
Public document objects continue to use complete input so their synchronous object
resolver remains available after opening. `GetObject(PdfReference)` uses the shared
generation-aware resolver; `GetObject(int)` looks up the current object by number.
Mixed inspections and transformed counts
retain their complete graph preparation.
Input admission and xref parsing form one stage before authentication and page
traversal; callers retain host-lease ownership through success and cleanup.
The writer prepares numbering, lengths, offsets and identifiers before publication;
linearization uses two passes. The writers share identifier argument validation; the serializer owns header/ASCII
emission, counting and bounded sink writes for both physical writers. Stream
payload isolation remains with its payload owner.
Streamed linearization excludes the outline closure from packing. Physical outline
groups receive hints; omission is valid only when classification assigns no group.
Copying and JSON normalization give direct outline roots an owned indirect identity
before layout and packing; private catalog aliases retain their own values.
`OutputModelPreparation` finalizes assembled and split graphs through shared
reachability, encryption numbering, ordering and object-stream grouping.
Catalog contents, version requirements and original-ID inversion have separate
internal owners; source copying supplies their data without duplicating policy.
JSON import binds full object number/generation pairs before lowering to the
writer's numbered model. Declared page traversal and inherited-attribute pushing
first create immutable overlays of the original tree. Generated edges retain
their provenance so their new pairs cannot revive invalid original edges.
The shared remapper validates original source edges
against live xrefs and binds JSON edges against source/declared pairs, borrowing
stream payloads while their backing owner lives. Its binding index is temporary.
Original object-stream membership enters the normalized ID space during binding,
then follows final numbering. Ordinary metadata/catalog policy applies after binding,
before numbering; fresh roles retain independent aliases while shared reachability
retires superseded roles. JSON publication shares output-model grouping,
version/security negotiation and the ordinary or linearized serializer; the page
tree's actual identity determines its packing exclusion.
Command JSON import applies the shared stream preparation policy to bound
objects after protected output normalizes Crypt stages. Physical writers then
apply encryption to the final encoded streams.

## Component map

| Component | Responsibility |
| --- | --- |
| `Public`, `Commands`, `Jobs` | APIs, configuration and operation plans. |
| `Operations` | Job dispatch, selection, source assembly and inspection execution. |
| `Syntax`, `Reading`, `Objects` | Parsing, xrefs, recovery, resolution and immutable values. |
| `Writing` | Graph copying, remapping, serialization, QDF and linearization. |
| `Content`, `Filters`, `Attachments` | Content/image transforms, codecs and embedded files. |
| `Json` | Inspection, import and update over the shared object model. |
| `Runtime`, `Hosting` | Resource admission, diagnostics, cleanup and host adapters. |

The public facade validates and adapts requests. Single-source selection keeps
its selective-reader retry boundary and lightweight copier; multi-source assembly
coordinates primary-source policy, per-input credentials and split occurrences.
Page, label and attachment assembly share their graph helpers with inspection
projection. `OutputPublication` owns identifier preparation, serializers and staged
captures; split captures finish before any target commits.
JSON page-source assembly uses that inspection graph, preserves primary pairs,
and exports new copied identities alongside original objects. Source action,
form, annotation and layer owners bind secondary context before input cleanup;
the graph detaches after serialization and sidecar copying, before publication.
JSON content/image transforms use the same content owners and host JPEG
capability over that graph, including the ordinary inline-image policy.
Source selection determines resource-pruning policy before inherited overlays;
the shared pruning owner applies it to the assembled source occurrences.
JSON export shares the catalog and Info removal policy with PDF publication.
Catalog-only changes avoid additional page preparation. Authored labels use
the selected page order, and replaced label trees need no merge.
Parsed source references keep provenance so new inspection identities cannot
revive old dangling edges. Source-aware pool resolution borrows untouched values;
Form replacement propagation retains borrowed parents and exact source pairs.
During page-source or placement preparation, inspection gathers inherited attributes
once per page and applies effective non-null overrides, including through null leaf
references.
Placement bundles reuse their visited ancestry for staged box/resource/rotation
lookups. Temporary traversal storage retires before content copying; geometry
skips do not resolve unused resources.
Combined attachment reports and extraction use one document. Targeted binary
publication transfers admitted payload backing past reader cleanup through commit;
combined binary payloads use the existing inspection buffer.
Text and JSON page inspection share sorted image bindings from inherited resources;
their dimension diagnostics and raw JSON descriptor projections remain distinct.
Within one inspection, up to eight effective XObject dictionaries can reuse
their sorted bindings. Entries retain at most 64 image references each.
Larger results and analyses that emit diagnostics follow the ordinary path.
Counting, identifier replay and final bytes share `OutputSerialization` format
dispatch while keeping their transaction and buffer lifetimes separate.
Job execution translates exceptions once at its enclosing result/presentation
boundary. The governor coordinates result admission and completion with the same
coarse storage owner, keeping all result capacity available before publication.

## Graph copying

Copies track identities per source, preserving sharing and cycles. Transforms
replace recognized PDF roles while independent private aliases retain originals.
Source resources and context bind before their readers close. Primary-document
metadata and attachments are independent of selected page sources.
Final-root reachability retires superseded graphs; failed copy attempts roll back
new objects and mappings without disturbing earlier shared copies.

Signed inputs use a separate field-role mapping within the same copier. Annotation
and AcroForm edges unregister signature fields and disable their attributes before
following their values. Former widgets become static stamp annotations;
appearance/resource payloads and private aliases use the ordinary graph mapping.
Placement and JSON publication reuse this policy. Reading and inspection retain
the original immutable values and do not validate signatures.
Signature discovery caches each field's Parent/Kids component independently,
including when entered through distinct annotation arrays. Array results combine
those components without marking an unrelated ordinary field as signed.
Retained form fields share one resource closure, so independent roots visit shared
appearances once. Field survival and registration order remain separate; the
closure union retains container, memory and cancellation bounds.
Single- and multi-source selection share post-overlay appearance reachability,
generation completion and flattening decisions in `PageAssembly`. Source binding
and field pruning remain with each caller; the shared operation borrows form
lists and creates no generation graph when appearance work is unnecessary.

Content transforms share rectangle bounds, matrix composition and finite numeric
array extraction in `PdfGeometry`. Feature callers retain clipping, coordinate
order, malformed-value diagnostics and degenerate-box fallbacks.
Literal decoding and canonical encoding share one byte-oriented syntax primitive.
Parsing and normalization admit exact result arrays; normalization keeps physical
source-line markers outside the codec. Writer token ownership stays with its sink.

## Password security

One security handler serves the shared reader and all entry points. The reader
validates the encryption dictionary before traversing encrypted objects, then
owns one authenticated file key. Physical strings and streams decrypt before
caching; object streams decrypt once, before their members parse. Xref sections
are identified by physical offsets, including hybrid links and earlier revisions;
reused object numbers and ordinary `/Type /XRef` tags do not grant exemptions.
Physical xref data, encryption dictionaries and signature Contents remain clear. Metadata follows
its encryption flag, explicit Crypt stages are consumed once, and external-file
streams stay opaque without opening their referenced paths.

| Handler | Authentication and payload handling |
| --- | --- |
| R2/R3 | MD5/password padding and owned RC4; 40-bit R2 and 40..128-bit R3 keys. Short R3 owner keys accept the two documented full-digest/key-length conventions. |
| R4 | Legacy authentication with independent per-object keys for RC4/AES-128 strings, streams and embedded files. AES includes its object-key salt. |
| R5 | SHA-256 authentication and AES-256 payloads; preserved/copied output retains revision 5. |
| R6 | Bounded AES/SHA derivation and AES-256 payloads; all newly created protection uses revision 6. |

AES, hashes and randomness use .NET system cryptography. The owned RC4 primitive
is checked against [RFC 6229](https://www.rfc-editor.org/rfc/rfc6229.txt);
R6 derivation follows the
[ISO algorithm errata](https://pdf-issues.pdfa.org/32000-2-2020/clause07.html#7.6.4.3.4).
Recorded native keys and dictionary entries supplement roundtrip tests.
Length-only writer passes measure encrypted hex strings and padded stream lengths
without deriving object keys, generating IVs or allocating ciphertext. They share
stream exception policy with emission and retain QDF's indirect length objects.
Identifier hashing and layout passes that need actual bytes still produce them.
Payload AES checks padding before allocating exact plaintext, processes bounded
CBC chunks for cancellation and admits exact ciphertext including IV/padding.
Key and derivation buffers remain owned and clear when their lifetimes end.

Credentials belong to each input. Authentication tries the supplied representation
before optional encoding recovery, which shares the appearance text tables.
Password-file and descriptor acquisition uses ordinary stable host leases.
The [authentication profile](SUPPORTED-PROFILE.md#authentication) defines
representations, limits and recovery; [command setters](SUPPORTED-PROFILE.md#command-password-files-and-modes)
define file semantics. [Memory ownership](MEMORY.md#engine-guarantees-and-limits)
covers admitted scratch, secret-buffer clearing and asynchronous close.

The output policy prepares an independent key/dictionary owner before writing.
Preservation and authenticated templates copy both password entries and the first
file identifier without retaining either password. R2/R3 keep RC4; R4 normalizes
to AES-128; R5/R6 retain their revision. New R6 creates a random file key, salts
and password entries, with explicit permissions and metadata treatment. Output
version negotiation enforces the chosen handler's PDF version/extension. Failure
never falls back to plaintext. Freshly protected split files get independent keys.

The primary input controls preservation; page, attachment, placement and template
sources authenticate independently. Copied objects outlive their source readers
and receive the output owner's protection. Grouped page sources require matching
credentials after command setter normalization. Attachment imports reuse completed
graphs for the same logical source and credential; other credentials authenticate
independently. Typed operations, argv and job JSON use these same owners;
argv encryption blocks feed the shared planner and preserve setter order.

Writers encrypt after compression and final object numbering. R2–R4 per-object
keys use the final number/generation and clear after ciphertext preparation.
Object-stream members reuse the container boundary, while xref streams and
security exceptions stay clear. Classic, QDF and two-pass linearized writers
count IVs, padding and encoded lengths before publication. Prepared buffers stay
admitted through pending host writes; copied keys survive source-reader disposal.

The reader, copier and output-model preparation share explicit Crypt-stage
validation and header reconstruction. Copying consumes only valid default/Identity
stages before content processing; JSON-authored stages normalize before physical
writer passes. Fresh headers borrow immutable payloads and keep private filter
aliases independent. Unknown stages never silently become plaintext.

Authenticated JSON exports plaintext values and non-secret encryption facts.
Updates authenticate the source PDF before applying a plaintext patch and an
explicit output policy. JSON creation requires remove/create/template handling
for an encryption declaration; JSON alone cannot authenticate a key. Reports and
predicates may inspect declared settings without traversing pages, but content
operations require successful authentication. See the
[supported profile](SUPPORTED-PROFILE.md) for public contracts and
[compatibility](COMPATIBILITY.md) for named native differences.

## Storage and ownership

`IQpdfHost` supplies stable input leases and staged output transactions. Paths are
logical keys; core uses no ambient filesystem, processes, environment or network.
Input reads borrow caller-owned buffers until completion. Opened identity, length
and bytes remain stable through lease disposal.

Coarse owners fund execution scratch, retained graphs, host storage and escaped
results. Immutable payloads can be borrowed while their backing owner lives;
actual copies and buffer-growth overlap require admission. Pending I/O and workers
finish before cleanup refunds storage. See [memory ownership](MEMORY.md).

Outputs and result state are prepared before commit. Failed or canceled preparation
discards staging. A failed single-target commit preserves prior bytes; multiple
targets commit sequentially, and later failures do not undo earlier commits.

## Limits and cancellation

Page-range validation and expansion share one span reader. Validation builds no
syntax graph; expansion admits temporary value groups and retires their storage
before returning the selected-page list.

Tree walkers own admitted, operation-local reuse indexes. Quiet empty page
subtrees and completed attachment misses reuse prior traversal. Reused label
summaries replay only their own overrides in child order; unique paths write
directly to the result. Cached height still bounds deeper paths, while active-path
cycle checks remain separate.
Attachment import reuse retains at most 32 clean source summaries and 4,096
entry references under temporary admission, retiring the least recently used.
Source leases close before reuse;
warnings, failed copies and conflicts keep ordinary per-request processing.
Source binding and outline inspection share named-destination lookup. It uses
the same identity/depth safeguards for subtree
misses and retains at most 32 query results per operation. Legacy names and
byte-string keys remain distinct; cache eviction changes work only.

Structural checking shares decoded stream bytes between its stream and page
stages. Its operation-local cache keeps at most 32 entries and 1 MiB (also capped
at one sixteenth of the working-memory limit), with 256 KiB per entry. Current-page
chunks pin borrowed arrays until validation finishes. Evicted or oversized streams
use ordinary admitted decoding; every page still validates its own resources.
Decoded counters charge actual decoding, including work after eviction.
Resource pruning uses the same bounded decoded-stream storage and a 32-entry
identity index for successful content/resource contexts. Page-specific annotation
guards run before reuse. Repeated pruned resources gain one shared indirect
identity; a single-page result keeps its direct header. Failed analyses retain
their per-page diagnostics and conservative dependency policy.

Inline-image externalization retains at most 32 successful content/resource
contexts per page phase. Reuse borrows generated image/content objects while
rebuilding independent page headers; original private aliases remain unchanged.
Diagnostic emission versions include suppressed warnings, so presentation does
not change cache eligibility. Decoded counters charge actual distinct work.

One governor spans configuration, reading, transforms, serialization and publication.
It enforces local limits and a host's optional shared memory allowance. Estimates
describe working storage, not CLR heap or process RSS. Structural, cycle and
decompression bounds remain independent of cooperative cancellation; there is no
cumulative CPU fuel or host-call quota. Host-operation counters observe actual
opens, reads and writes. Publication reserves reusable wrapper/staging storage,
without predicting how many calls a host needs. Diagnostics have bounded storage
and explicit overflow.

## Development

The core has no runtime NuGet dependencies. Hosts supply JPEG codecs through
`IQpdfJpegCodecHost`; calls borrow the execution budget and cancellation token.
Four-component decoding requires the codec's `SupportsCmykDecoding` opt-in;
its C/M/Y/K samples retain PDF polarity and share the existing admitted decode
buffer. The nonpackable Lokad.Image adapter supplies development codecs and
uses explicit non-inverting CMYK decoding. Image optimization still admits only
Gray/RGB encoding.

Physical I/O adapters, native qpdf, renderers and PdfPig belong to development
tools and tests. Ordinary tests work without native tools; explicit reference
jobs require them. See [benchmarks](BENCHMARKS.md) for performance evidence.
Reference emit and companion probe commands share requested-input registration
and mapped output export; fresh input arrays transfer to command-owned storage.

Tests distinguish syntax/security vectors, graph/content semantics, physical
layout, memory/publication, native/visual evidence and installed consumers.
Shared semantic owners carry exhaustive cases; adapter checks sample wiring.
Recording hosts and caller budgets observe suspended I/O, admission, refunds and
immutable counters. Narrow observers and owned-type reflection check cancellation
or collectability when final outputs cannot prove those boundaries; they do not
inspect compiler frames or private BCL collection layouts. The
[test ownership guide](https://github.com/Lokad/qpdf/blob/master/tests/Lokad.Qpdf.Tests/README.md)
records fixture, observation and coverage responsibilities.

# Architecture

Lokad.qpdf is a managed PDF engine shared by typed C# operations, qpdf-style
arguments and job JSON. The engine owns PDF semantics; the application's host owns
storage and publication. The [supported profile](docs/SUPPORTED-PROFILE.md) defines
the public contract, and [compatibility](docs/COMPATIBILITY.md) records differences
from native qpdf.

Input leases expose offset-based reads into admitted caller buffers. Each lease
has one caller and one pending read at a time; completion ends the host's buffer
borrow, including on cancellation. Stable identity, length and opened bytes do
not change when a logical path is replaced. Short reads reuse destination slices;
exact readers reject invalid counts and premature zero as transport failures.

JPEG contexts lend the existing memory vocabulary to each codec call. Parent
links and aggregate counts provide subtree refunds without child registries.
The development Lokad.Image adapter only forwards interfaces; PDF boundaries
preserve admission failures through nested codec exceptions. All codec users
finish before the call owner closes. Host publication establishes surviving
storage ownership before commit and finishes pending writes before cleanup.

## Execution flow

```mermaid
flowchart LR
    Entry[Typed / argv / job JSON] --> Reader[Reader and object graph]
    Reader --> Inspect[Inspection]
    Reader --> Transform[Copy and transform]
    Transform --> Writer[Writer]
    Writer --> Host[Host publication]
    Inspect --> Result[Results and diagnostics]
    Host --> Result
```

Argv and job JSON resolve into shared operation plans; they do not implement a
separate reader or writer. Typed operations call the same cores directly. Defaults
can differ by entry point, including recovery and stream decoding; those differences
are part of the compatibility contract.
Job-file loading selects each property partition before sorting it, preserving
forward-prefix/reverse-suffix setter order around nested includes.

Selection cores borrow one immutable settings value containing the existing
feature requests. Typed defaults and command-plan projection are defined once;
source documents, selective-read retries and publication owners stay explicit.
Whole-file rewrites select all pages through the existing governed range resolver
without constructing and cloning a separate page-number vector.
Rotation and both flattening consumers resolve indirect rotation scalars through
the copied object pool before normalization; the existing reference bounds apply.
The accepted deterministic-ID flag validates at parsing; plans carry no unused
flag value because deterministic identifiers are already the default.
Zero-page whole-file jobs use the same graph, catalog and attachment preparation;
zero split groups create no output transaction. Explicit page bounds still apply.

Every graph copier uses an explicit construction owner and the same admitted
map and name-table storage. Recoverable catalog, attachment and unreferenced-root copies
share a reusable, admitted source-number list: rollback removes only new mappings,
copied descendants and allocator slots, preserving earlier shared objects.
Nested name-category attempts keep their enclosing attempt's bookkeeping.
Tests use this production path; there is no unowned
copier mode. Form-resource traversal tracks graph identities, with indirect
form guards independent of resource-scope visits.
Cross-reference tables likewise require an explicit owner. Synthetic fixtures
admit their entry maps, trailer lists and input receipts and transfer them through
the same owned table/engine factories as production, with explicit escape and input
flags. Value scopes use the production whole-graph handoff. Object-display tests
consume the same diagnostic path as inspection; text-transfer tests use live
owned-text producers.
Argv parsing and range expansion require their execution governor and construction
owners. Linearization and inline-image tests exercise the same governed indexes
and allocation batches as production; no unowned parser or dictionary adapters
provide alternate execution modes. Filter decoders require an owned stage plan
and result scope; plans expose indexed access and struct iteration without boxed
enumeration owners.
Hint lifetime tests use typed readers and owners, with private-field inspection
for backing-storage and detachment proofs.
Codec fixtures call the live cancellation-aware implementations directly.
Canonical display, PDF JSON and resource dictionaries share governed unsigned
raw-name comparisons. QDF retains its separate escaped-name ordering.
PDF text decoding likewise requires its construction owner, governor and token;
the encoding map uses the single-byte mapping directly during initialization.
Non-Flate codec output caches
the remaining raw ceiling per stage, accounting for PNG selectors separately
from decoded samples while retaining coarse memory admission.
Hot output writes compare the cached ceiling; sample conversion and failure
construction run only when that ceiling is exceeded.

One resource governor spans configuration loading, reading, transformation,
serialization and publication. Results capture diagnostics and counters after
cleanup. Inspection can return text or bytes without creating a PDF output.
Output-free job inspections share the check plan and its configuration controls;
binary extraction plans handle explicit host output targets. JSON selectors share
one configuration-owned set of numeric identities through inspection; matching
needs no canonical strings, execution clone or repeated scan of selector lists.

## Component map

| Component | Responsibility |
| --- | --- |
| [Public](src/Lokad.Qpdf/Public), [Commands](src/Lokad.Qpdf/Commands), [Jobs](src/Lokad.Qpdf/Jobs) | Public API, invocation parsing, configuration and operation plans. |
| [Syntax](src/Lokad.Qpdf/Syntax), [Reading](src/Lokad.Qpdf/Reading), [Objects](src/Lokad.Qpdf/Objects) | Byte-oriented parsing, xrefs, bounded recovery, object resolution and immutable values. |
| [Writing](src/Lokad.Qpdf/Writing) | Graph copying, identity remapping, serialization, object streams, QDF and linearization. |
| [Content](src/Lokad.Qpdf/Content), [Filters](src/Lokad.Qpdf/Filters), [Attachments](src/Lokad.Qpdf/Attachments) | Content/image transformations, stream decoding/encoding and embedded files. |
| [Json](src/Lokad.Qpdf/Json) | PDF inspection, import and update over the shared object model. |
| [Runtime](src/Lokad.Qpdf/Runtime), [Hosting](src/Lokad.Qpdf/Hosting) | Resource admission, diagnostics, cleanup and the built-in in-memory host. |

The reader handles classic, streamed, hybrid and incremental xrefs, including
compressed objects. Copies track identities per source so shared/cyclic graphs
remain coherent and object numbers from different inputs cannot collide. The
writer prepares lengths, offsets and identifiers before publication; linearization
uses a two-pass physical layout.

## Storage and ownership

[IQpdfHost](src/Lokad.Qpdf/Public/IQpdfHost.cs) supplies stable input leases and
staged output transactions. Paths identify host resources; the library has no
ambient filesystem, process or network access. The built-in host stores bytes in
memory, with separate registered-input and published-output stores. Publishing at
an input path does not change its registration; callers export and register the
output explicitly to read it as input. Physical adapters belong to applications
or development tools.

The memory host can borrow a durable memory budget for retained input, staging
and publication. Replaced input storage stays charged through its last lease;
export copies have a caller owner that can outlive host disposal. Borrowed private
input snapshots and host staging retain local policy estimates without duplicate
engine admission at the shared root.

Eligible page selections read bounded ranges from classic-xref inputs. If the
selected ranges cannot support the operation, the reader retries the complete
input on the same lease with the same recovery policy, before publishing output.
Other layouts and operations retain complete reads.
Eligibility reads the governor's existing counter values without allocating
public counter snapshots.

Documents own their input, resolution tables and object caches. Disposal drops
private references while public immutable values remain usable. Private copies can borrow immutable
stream payloads while their operation owns the backing input through publication.
Retaining/async sinks receive independent immutable packets. Exclusive multisource
inputs transfer payload/backing admission to the job before earlier documents
close; rewritten headers borrow those immutable bytes through publication.

Failed or canceled operations discard uncommitted staging and close their leases.
Host lease wrappers are allocated before acquisition. Completed host operations
return directly, so neither wrapper nor async completion metadata is allocated
after the host returns a raw lease. Pending operations retain the same cleanup.
A single-target failed commit preserves the prior output. Multiple outputs commit
sequentially, so a later failure can leave earlier outputs published; a close fault
after commit does not undo publication. The host defines its transaction semantics.

## Limits and cancellation

QDF repair keeps one immutable input and indexes object-stream lines by offsets
and lengths. Descriptor growth and numeric formatting receive coarse admission;
byte/record scans and output awaits check both cancellation routes. Outer syntax
requires LF endings while binary payloads and cross-reference bytes stay opaque.

[QpdfLimits](src/Lokad.Qpdf/Public/QpdfLimits.cs) bounds input/output bytes, decoded
data, estimated memory, host operations, handles, diagnostics and nesting. Invocation options
can tighten host policy but cannot raise explicit host ceilings. Nesting budgets,
including direct host settings, must remain within 1..499. Significant storage,
growth overlap and decompression receive admission before allocation.
Output bytes are charged cumulatively across the job's outputs, including
prepared output discarded on failure. Operations returning typed domain results
throw on cancellation; execution entry points return a canceled execution result.

Memory counters describe a charged working-set estimate, not CLR heap or process
RSS. Hosts must also account for their buffers, concurrent jobs and caller-retained
outputs. Unreleased charges alone do not prove retained memory. The library never
forces GC.

The optional `IQpdfMemoryHost` capability forwards the active caller-owned memory
budget to the resource governor. Shared admission batches the existing estimates
into coarse growth blocks while local limits stay binding. Caller scopes outlive
returned storage; host-private storage needs its own owner. See
[memory admission](docs/MEMORY.md) for the contract and current migration boundary.
Result wrappers, counter snapshots and versions use admitted capacity estimates
without finalizer receipts; returned metadata never roots its completed job.
Graph retention transfers construction estimates to coarse execution or caller
owners without walking object identities. Private cleanup refunds execution
storage; public document aliases remain charged to the caller's scope.

Cancellation is cooperative across bounded CPU loops, host calls and codec APIs;
there is no cumulative CPU fuel budget. Hosts use cancellation tokens for deadlines.
Structural bounds, cycle detection and decompression ceilings remain independent.
Cleanup can finish bounded retirement after cancellation. Diagnostics use bounded
stored records and actual UTF-8 message lengths. Overflow fails the job before
publication; accepted messages and a bounded terminal failure remain available.
Requested summaries are built once before publication and reused after commit.
Ordinary command results borrow the admitted diagnostic list without copying it
after publication; that list retains no execution owner.
Failure views cache visible counts and accepted-prefix overlap once. Reading
counts or terminal records does not repeat history searches; enumeration is linear.
Argv option recognition borrows spans. Command errors bound echoed argument text
before interpolation, including terminal parse failures outside ordinary quotas.

## Dependencies and development tools

The core has no runtime NuGet dependencies. Hosts optionally implement
`IQpdfJpegCodecHost` to supply an `IQpdfJpegCodec` for grayscale/RGB processing.
The core validates frame sizes and owns sample/output buffers; codecs borrow them
for one synchronous call. `QpdfJpegCodecContext` lends `CancellationToken` and
`MemoryBudget` for private codec storage. Create child scopes and use `TryReserve`
and reservation `TryResize` to admit growth and refund shrink without a second
allowance; buffer replacement must include old/new overlap. Codecs must release their actual
storage and finish all workers before returning; outstanding accounting charges
retire with the call. Worker admission and encoded-output growth share the job's
memory-accounting lock; the rest of the job retains one owner.
Without a codec, JPEG pass-through works, decoding checks report skipped, and
eligible image optimization fails explicitly. CMYK/YCCK remains opaque.

Native qpdf, PdfPig and renderers serve development tests, probes and benchmarks;
they are not shipped with the library. The nonpackable
[Lokad.Image 0.2.0 adapter](tools/Lokad.Qpdf.Image) exercises the public codec contract
in tests, probes and benchmarks. See [AGENTS.md](AGENTS.md) for checks and
[benchmarks](docs/BENCHMARKS.md) for performance measurements.

The reference helper launches native tools with argument vectors and a 30-second
deadline, preserving working directories and binary stdin/stdout/stderr. It closes
stdin after copying, terminates and awaits failed children, and settles transfers
before closing files. Both launch routes return only an exit-code record after
cleanup; no native Process escapes to reference callers.
Early input rejection retains the native nonzero exit and diagnostics; incomplete
stdin delivery to an otherwise successful command fails explicitly.

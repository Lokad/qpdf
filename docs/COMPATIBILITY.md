# Compatibility target and scope

Reference: qpdf **12.4.2**, commit
`4eba95899886e851cc41d76886483b347612f2a8`, from
https://github.com/qpdf/qpdf. This target must not move silently.

Document-open hint classification now handles direct containers, indirect roots,
page boundaries and generated object-stream containers. The original two-page
form invocations retain their fixtures/settings/routes. Non-name PageMode values
are preserved through catalog copying. Additional structural controls retain all
native hint fields; array-valued AcroForm still has eight explicit schema-rejection
differences. These controls establish no complete catalog-schema/default parity.
The preceding document-open integration passed 9767 Release cases; all 9597
preceding occurrences remained. The committed physical checker passes all 10833
final cases, retaining those 9767 occurrences and adding 1066 controls. Of 152
retained native document-open observations, 144
outputs pass native linearization and match assignments with zero warnings;
eight array-AcroForm schema rejections retain the native-success difference.
The committed physical checker rejects all eight preserved invalid form outputs
and accepts the corrected/native layouts; HINT-02 is retired by final source-bound
public, reference and package verification. See the finite public-route
checks and pass conditions in [AUDIT-CLOSURE](AUDIT-CLOSURE.md).
The committed DIAG-02C correction preserves final observed counters across argv/
response failures/cancellation, typed results and QDF/zlib companions. All 11421
Release cases pass, retaining 11008 preceding occurrences and adding 413 complete
public controls. Final pinned reference/parser, package and fresh consumer checks
pass; signatures and native 868/804/868 observations remain unchanged.
DIAG-02P1 corrects summary count admission across argv/job/mixed/response jobs.
Its 516 new public controls and seven reservation controls pass with 499 existing
related checks; exact limits preserve outputs, while short count limits prevent
publication. The original three one-record jobs now fail before publication and
successful jobs record six summaries. DIAG-02P2Q now rejects the original three
16-byte false publications before commit. Its 675 complete public controls and
60 sizing/admission controls pass with 1107 preceding related checks. Exact byte
limits retain output bytes, text, ordering and exits; short limits preserve all
prior split/sidecar targets and aliases. One-handle sidecars remain supported.
Identical 675 public cases on the count-only predecessor give 363 false completions
and 312 preserved results, with no loader failures or skips. The added split
counting pass does not close future work/memory admission. DIAG-02P remains open
for formatting work, storage ownership, accepted warning history, late multi-output
preparation and final combined reference/package verification.
DIAG-02P2S/P3S integrate owned summary formatting and prepared split publication:
2007 focused cases pass, including 120 public controls and ten storage/cancellation
controls. Same 120 public cases on the byte-only predecessor expose 44 failures,
including four lost histories on genuine memory failure, and preserve 76 results.
All four old split work ceilings now reject before commit. Prepared main/pass1
bytes match the shared writer across styles and segment boundaries; public fixed
memory matrices exercise failure after staging without replacing prior targets.
JSON warning-policy flags now reach the shared execution plan. Staged-warning
future memory admission, public accepted-warning cancellation, mixed suppression
retirement and future host/wrapper admission remain DIAG-02P2F/P3H; generic DTO
admission and JSON data ownership stay DIAG-01/JSON-01. Final combined checks remain
DIAG-02PV, and the parent/wider gates stay open.
The combined hint ownership correction passes 11008 Release cases, the full pinned
reference/parser lane and fresh package/consumer checks. Original 868 comparisons
and all 64 valid layout differences remain unchanged; HINT-03 is retired. Broader
checker/ownership/cold-state audits and the eight schema gaps remain open. The numerator
evidence below describes the preceding checkpoint and its then-preserved eight warnings.

Linearization shared numerators now use native width zero. The original 64
single-page hint differences retain complete payloads and fields: independent
physical checks validate both files' offsets, lengths and derived group widths,
and the pinned oracle accepts all 128 linearization checks. Raw hint byte equality
remains a separate observation because the emitted layouts differ. New offline
controls preserve the original fixtures, setters and routes. Hint ownership,
broader form/default classification and independent checker coverage remain open.
Twenty-four additional shared-page observations match native numerator width;
sixteen pass independent linearization checking. Eight forms/default observations
warn on both the preceding and corrected runtimes while our checker passes.
Their page-run/shared classification and checker coverage remain open.

**Implemented PDF operations: the audited non-cryptographic subset.** 152 inventory entries
are complete and 5 stay partial: `--job-json-file` still needs complete grammar,
defaults and interaction auditing after mixed-argv/multiple-file configuration;
`--check` still needs overlay/other transformation contexts and complete
context/default auditing after selection/rotation verification; the no-Zopfli
build profile and four-component CMYK image samples retain their accepted
compatibility exceptions with partial labeling;
and `--parser-max-errors`, whose invocation values, zero/default behavior, `--no-default-limits`
scope, host-ceiling enforcement and per-object repair are implemented for the verified
damage matrix, including full-range integers, long real tokens, numeric conversion
failures and the authored brace/NUL-name/literal-newline, dictionary-error guard,
malformed-name and binary-string/container cases. Broader nested-load evidence
and diagnostic ownership, cold/native state and broader loading/counters still keep that entry partial.
Successful recovery trials and unselected trailer subtrees now release their admission;
accepted trailer bytes/aliases survive. Strict/final xref acceptance and linearization
trials have ownership regressions, with unfiltered/Flate structural scratch cleanup.
Xref/revision/trailer and cache/loading/decoding collections now admit wrappers,
capacities and growth overlap before allocation. Temporary maps release after
scan/merge; cache metadata releases on owner disposal. Unread object-stream
replacement/disposal releases only its own subtree admission, preserving borrowed
values and nested loads. Independent backing allocation, finite typed/argv/job
traversal, prior targets, retries and observed-work cancellation have regressions.
Document input copies and exclusive xref/page-view containers now have owned
receipts and disposal/failure cleanup, with strict/recovered/stream/hybrid/
incremental aliases, finite/repeated documents, short reads, host-close faults
and cancellation after observed page visits. Caller-funded component engines
preserve borrowing. Independent forced-GC probes keep disposed handles and
returned aliases alive while exclusive storage dies. Accepted parser/cache values
now transfer their own receipts to independently escaping wrappers and backing
storage. Numeric text, dictionary key views, stream dictionaries/buffers and
copier aliases have lifetime regressions; collected values release admission
under repeated finite-budget jobs. Failed acceptance/growth preserves prior
aliases. Byte strings use an admitted fresh decoded buffer while public copies
remain immutable. JSON keeps internal values scoped until serialization, then
closes its reader before output creation. Typed/argv/job close faults preserve
prior main/sidecar targets; raw/decoded sidecars survive reader cleanup and fit
a one-handle ceiling. Tighter original memory ceilings and meaningful failed/
canceled counters have regressions. This unit passes 6,440 offline tests, 189
native comparisons, the full pinned differential/parser lane and 50 fresh packed
consumer smokes plus both specific rejection controls. These are local Windows
results bound to archived runtime/build evidence. Diagnostic record/list admission
and owned-message retirement, cold/native state, other codecs, all-route counters
and broader public repair/recovery nesting remain open.
Header-rejected and unmatched/trailing compressed-member values now have public ownership regressions,
including retained nested/cache aliases and cancellation; structural scratch evidence covers
unfiltered and Flate object streams, while other codec owners remain open. Excluded crypto
operations carry
negative rejection evidence. The scaffold, runner, and codec-probe tests establish
inventory consistency, fixture/independent-reader contracts, codec feasibility, and
package boundaries; operation coverage is carried by the per-entry unit, budget,
and oracle-differential evidence reconciled below.

Stream compression setters now share ordered state across argv, lexical job
keys, mixed argv and file chains. Repeated values use the final setter, and
invalid earlier layer values reject before PDF input. QDF defaults apply only
when fields are unset. Native writer defaults compress generalized streams;
typed selection remains opaque. Empty streams and catalog XML metadata retain
their native uncompressed rules, while page metadata follows writer settings.
Copied streams resolve Filter/DecodeParms in their destination pool, and newly
created images use the same owned filtering/encoding pipeline. Rewritten content
keeps each name slash and its original encoded bytes. Inline Flate delimiter
whitespace is accepted without removing checksum/truncation checks.
Qualified Release suites pass 9509 pending / 8786 isolated cases, including 541
new controls. The same 868 native observations now match all encoded headers
and exits and 804 decoded outputs; all 432 added name/candidate and 48 copied-
filter observations match, closing 36 unexpected warning exits. All 96 resource
and 20 XML/empty-stream comparisons remain matched. Compared stream headers
exclude Length/Resources/identities; deflater bitstreams are recorded separately.
The remaining 64 decoded differences are complete linearized hint payloads.
Full warning context, whole-job ownership and wider grammar/defaults remain
open; inventory labels are unchanged.

Bare argv input/output pairs now use the same whole-file rewrite plan as job
JSON, with an optional `--` output separator. Writer options apply without page
selection. Linearization/object-stream modes have public parity and source-bound
native/independent-reader evidence; malformed positional arguments reject before
input access. This does not close the remaining default/context audit.

Owned PDF name storage now has a verified integrated parser/recovery/copier/
remapper implementation with charged collision work and explicit immutable
backing ownership. Shared raw-name lookup now governs stream-length, object-stream,
xref and page-inheritance metadata, with independent component/reader controls;
broader owned-storage/dispatch auditing remains open. Predictor fields and LZW EarlyChange
now use governed borrowed metadata operations, with the predictor result funded
through codec use. Filter-stage metadata now uses an admitted disposable owner
across reader/checker/JSON/content/attachment/inspection/writer consumers; unknown
name aliases transfer to retained storage. Activated retained-value receipts now
share an admitted weak ledger link: active governors fund surviving aliases,
while completed jobs release ledger/index/retirement-queue metadata. Independent
component and compatible preceding-runtime controls cover this prerequisite;
broader codec and diagnostic ownership remain open.
Checker field/widget classification and parent-link metadata now use governed
borrowed raw-name operations, preserving field truth/short-circuit order and
parent warning behavior. Catalog/form registration, child arrays, annotations and
page contents now use borrowed raw-name lookup and bounded resolver boundaries.
Indexed traversal avoids array copies; admitted stream-reference/form-index
owners drop backing and execution state before retiring admission. Original form
failure/work/cancellation controls remain, including two diagnostic-dependent
growth controls now tested with the integrated diagnostic storage. Remaining resource,
inspection, JSON, content and writer name consumers/collections stay open.
The shared-resource decision now owns its indexes/pending nodes, borrows child
arrays and dictionary cursors, and governs raw metadata/form classification and
resolver boundaries. Explicit `auto` uses the same heuristic as the default;
three resource shapes have argv/job/mixed regressions and fresh native evidence.
Pages-block argv now preserves option context before main extraction, requires
named file/range values with equals signs and matches native replacement/trailing
block boundaries. Main labels share the job parser, including empty-block no-op
behavior. Legal argv/job/mixed positions and failure priority have 81 controls
and 73 fresh native comparisons; the full current reference lane passes.
Broader grammar/default/interaction, frontend ownership/counters, resource
pruning/storage and scheduling observations remain open.
Scoped tests, independent measurements and fresh focused
native/package evidence are recorded in docs/PROGRESS.md. Legacy unowned
construction and broader runtime lookup/collection audits remain open; this
correction does not close parser ownership or any broader compatibility gate.

Form resource traversal now admits its scopes, form references and visited-number
index through pruning and inline-image externalization. An iterative, admitted
value comparison preserves scope deduplication while bounding nested values,
raw names, numeric text and stream-byte work. Raw metadata lookup and bounded
indirect resolution borrow PDF values; concrete cursors and indexed content arrays
avoid key views and array copies. Fifty-two new controls, compatible preceding-DLL
proof and independent component/pending measurements verify this scope. Remaining
analysis name sets/strings, chunks, rebuilt graphs, broader lookup/storage and
diagnostic/default/interaction audits remain open; inventory status is unchanged.

## Pruning name-analysis checkpoint

Pruning uses admitted decoded-byte name storage and borrowed queries while
preserving single-pass escapes, binary/malformed bytes, resource operators,
operand history, form-to-page unresolved names, inline-image boundaries and
parse-failure warnings. Tokenizer scans observe finite work and both cancellation
routes; the original one-unit short-token control remains. Analysis storage drops
before admission retires; chunks, rebuilt graphs and other consumers remain open.

Native single-pass escaped-name evidence exposed inherited double decoding of
already-decoded dictionary keys. Known/resource membership now borrows those
key bytes directly; only raw content tokens decode escapes. Three new behavior
controls fail on the preceding runtime. The initial native mismatch, stopped
reference run and NUL/warning-adapter harness failures remain archived separately.
The component passes **7345 offline cases / 69 new controls**; wider pending
integration passes **8078 / 69**, preserving all preceding stable identities and
original test sources/fixtures/limits/assertions. Identical final tests on preceding
`B04540B7`: **22 fail / 22 preserve / 44 eligible**, zero loaders; 25 new helper/
owner/caller-token controls are excluded. Fresh focused native evidence passes **292 comparisons / 368 output rereads**,
including 21 decoded-name comparisons / 42 rereads. The full reference wrapper
passes its differential operation matrix and **1846 parser comparisons / 432
rewrite trios**. Package/symbol checks, **50 consumer smokes** and both specific
negatives pass on the same runtime. These are local Windows results.
Local Windows evidence on frozen pending `4D3887E7` / `4C3722AF`, build checkpoint
`666f28e`; package metadata exposes no source commit or URL. The initial tokenizer
budget correction and disposal-counter observation are archived; the unchanged
disposal control passes focused candidate/preceding runs but its scheduling audit
remains open. Broader defaults/interactions, visual/reference CI/release,
A01-A07/M11 and both completion verdicts remain open. Inventory is unchanged.

Joined content scratch is now scoped through page/form resource analysis. One
exact admitted buffer replaces unowned intermediate lists and newline arrays;
all sizing/copying work is bounded and cancelable. Existing LF/CR/empty/binary
boundary semantics remain. Construction, failure, cancellation and disposal
preserve prior receipts and borrowed input bytes. Decoded chunks, rebuilt graphs,
other content consumers and the broader allocation audit remain pending.

This component passes **7374 isolated / 29 new controls**; frozen pending runtime
`A88C7784` and tests `98C5FE5E` pass **8107 / 29**, preserving all 7345 isolated /
8078 root preceding identities and 206 original test sources. Identical final
tests on preceding pending `4D3887E7` fail eight / preserve thirteen of 21 eligible
cases, zero loaders; eight new owner controls are excluded. Original fixtures,
ceilings, repetitions, thresholds, deadlines and assertions remain unchanged.

Fresh focused native evidence passes **313 comparisons / 410 output rereads**,
including 21 content join comparisons / 42 rereads. The full reference wrapper
passes its differential operation matrix and **1846 parser comparisons / 432
rewrite trios**. Package/symbol checks, **50 consumer smokes** and both specific
negatives pass on the same runtime. These are local Windows results.

The first native run also exposed an inherited undefined form font mismatch:
with empty form Resources and a form content reference to F1, current and
preceding managed runtimes prune an unused page font while qpdf keeps both.
That context is archived as an open compatibility obligation. Final joined buffer
evidence uses declared form fonts; the runtime remained frozen and original
controls were preserved.

Independent managed observations fund the scoped buffer through last use and
show no execution/backing witnesses surviving 128 rooted disposed owners. These
local Windows results at build checkpoint `6d78f94` do not close cold/native/
pool/codec/whole-job/RSS/CPU/host measurement, the three preceding scheduling
observations, the complete diagnostic correction or either completion verdict.


## Decoded pruning chunk ownership checkpoint

Page/form analysis owns filtered chunks and list capacities through joining;
raw immutable stream payloads stay borrowed. Shared codec output storage and
LZW dictionary storage admit allocation and growth. Chains retire intermediate
arrays and preserve independent receipts on failure. Supported pruning filters
and predictors observe finite work and both cancellation routes. Existing filter
behavior remains; rebuilt graphs, broader codec aliases/JPEG and whole-job
ownership remain open.

The isolated component passes **7433 offline cases / 59 new controls**; frozen
pending integration passes **8166 / 59**, preserving all preceding stable IDs
and 209 original test sources. Identical final tests on preceding `A88C7784` fail
**37 / preserve 7 / 44 eligible**, zero loaders; 15 new owner/caller-route controls
are excluded. Original fixtures, budgets, assertions, repetitions and deadlines
remain. Independent allocation/retention probes agree; admission covers measured
managed storage, borrowed views allocate/retain zero, and all 1152 witnesses die
with 256 rooted disposed owners. Broader measurement obligations remain open.

Fresh focused native evidence passes **358 comparisons / 500 output rereads**,
including 45 filtered page/form chunk comparisons / 90 rereads. The complete
reference wrapper passes its differential operation matrix and **1846 parser
comparisons / 432 rewrite trios**. Package/symbol checks, **50 consumer smokes**
and both specific negatives pass on the same runtime. These are local Windows
results; the full compatibility and release audits remain open.

The archived MSBuild out-of-memory, missing probe dependency and malformed new
empty-Flate fixture observations are retained. Five new empty-result controls
and corrected native fixtures pass. Six trailing-whitespace lines were removed
before the final source freeze; both stopped candidate reference runs and prior
gates are archived. The inherited undefined form font context is confirmed on this
runtime. It and all three preceding
scheduling/counter observations remain open. This checkpoint preserves the
canonical inventory and leaves A01-A07/M11, combined correction and both verdicts
open. Runtime `8F278873`, tests `D764CF23`; local Windows build checkpoint
`885fdb5`, with no source commit or URL in package repository metadata.

## Frozen table-backed dictionary prerequisite

Internal PdfDictionary construction now accepts frozen PdfNameTable storage.
Public construction retains copying semantics; binary/empty names, replacement
order, values, equality and generic/nongeneric key enumeration have regressions.
Borrowed lookup/cursors retain explicit work and both cancellation routes.
Escaping key views keep backing admission alive independently of the wrapper;
completed execution state can collect. Isolated 7458 / pending 8181 offline
cases pass, including 15 new / ten promoted dictionary controls. The identical
final tests on preceding pending 8F278873 fail seven key-view behavior cases and
preserve 18; all 25 are eligible, zero loader failures. Original controls remain.
Affected native 358 comparisons / 500 rereads and package/symbol/50-smoke/two
specific-negative checks pass on pending 86794DBF, tests 1E5B59F5, build checkpoint
c5d9b00. Full reference evidence on preceding 8F278873 remains historical;
combined final correction requires a fresh full lane. The inherited undefined
form font mismatch, rebuilt resource graphs, broader ownership/diagnostic work,
A01-A07/M11 and both completion verdicts stay open. No inventory context closes.

## Resource-pruning graph ownership checkpoint

Pruning now owns its fresh resource/header tables and wrappers through atomic
acceptance, while borrowing original names, values and form payloads. Destination
validation precedes transfer; construction/work/cancellation/receipt failures
preserve the old pool and prior aliases. Escaping keys, names and buffers retain
independent funding without anchoring completed execution metadata.

Isolated **7508 / 50 new controls** and pending **8231 / 50** pass with locked
restore and Release builds. All preceding 7458 / 8181 stable identities and 214
original test sources remain. On preceding pending 86794DBF, identical final
tests fail 12 / preserve 3 of 15 eligible cases, with zero loaders; 35 new
batch/helper cases are excluded. Independent warmed probes cover construction,
acceptance, escaped aliases and retirement, with explicit measurement exclusions.

Fresh **358 focused native comparisons / 500 rereads**, the full differential
wrapper and **1846 parser comparisons / 432 rewrite trios**, package/symbol checks,
50 consumer smokes and both specific negatives pass on pending 72C30032 / tests
2D257C47 at build checkpoint 01ab178. These are local Windows results, and nuspec
exposes no source commit or URL. The known undefined form-font mismatch remains
open. No inventory context, broader audit, release or completion gate is closed.

## Shared content-header ownership checkpoint

Resource pruning and content-header replacement/encoding stripping share the
same admitted table construction and atomic fresh-carrier acceptance. Original
keys/values stay borrowed; inserted names and byte storage have independent
receipts, including empty names. Raw lookup/entry visits carry work/cancellation.
Ordering, actual-null entries, binary names and encoding removal remain.

Isolated **7547 / 39 new controls** and pending **8270 / 39** pass with locked
restores and Release builds. All preceding 7508 / 8231 stable identities and
217 original test sources remain. Identical final controls on preceding pending
72C30032 fail 18 / preserve 8 of 26 eligible cases, zero loaders; 13 new-kernel/
caller-token cases are excluded. The private adapter changes signature binding
only; fixtures, values, budgets and assertions stay identical.

Fresh **358 native comparisons / 500 rereads**, full differential/reference
(including normalization/coalescing), **1846 parser comparisons / 432 rewrite
trios**, package/symbol/50-consumer/both-specific-negative gates pass on pending
AC3D755A / tests 4D434434 at build checkpoint 49872d0. Independent warmed probes
cover escaped header/name funding and zero retirement/completed governors, with
explicit exclusions. These are local Windows results; no inventory context,
broader audit or completion gate closes. The known form-font mismatch, content
buffers/codec copies, other consumers and final combined correction remain open.

## Content traversal ownership checkpoint

Scoped content-entry plans now serve both coalescing and normalization, borrowing
streams while admitting metadata/list growth. Raw lookup and copied-pool
resolution carry work and both cancellation routes. Existing missing/null/skip
warnings, order, generations, IDs and orphan candidates are preserved; orphan-ID
lists use a caller scope. Isolated 7576 / pending 8299 offline cases, including
29 new controls, pass. Preceding AC3D755A preserves seven / fails 14 of 21 identical
eligible controls, zero loaders; eight new owner/pool/caller-token cases are
excluded. All preceding identities and 219 original test sources remain.

Independent warmed probes cover helper allocation, active heap/admission, zero
allocation borrowed queries, disposal and rooted-owner cleanup in both runtimes.
Fresh 358 native comparisons / 500 rereads, full differential/reference, 1846
parser comparisons / 432 rewrite trios and package/symbol/50-smoke/two-specific-
negative gates pass on D227DD6C / DF0B70A9 at build checkpoint 5b52720. These local
Windows results close this component only. Content buffers/arrays/codecs and
orphan-removal scratch, broader ownership/diagnostics, defaults/context auditing,
the known form-font mismatch, all five partial contexts, A01–A07/M11 and both
completion verdicts remain open.

## Transformed content ownership checkpoint

Coalescing/normalization own final buffers and fresh stream/header graphs;
replacement arrays borrow values and own exact backing storage. Scoped decode,
join, encode and map storage retires at last use. Public stream construction still
copies. Existing bytes, order, generations, actual-null handling, newline rules,
fatal decode/fallback policy and warnings remain covered. Both cancellation
routes, memory/work exhaustion, transfer and escaping aliases have controls.

Frozen pending 2164476D / tests 5CA2EC96 passes 8339 offline cases; the isolated
component passes 7616, including 40 new controls. All 8299/7576 preceding stable
identities and 221 original test sources remain. Preceding D227DD6C fails 17 /
preserves 10 of 27 eligible identical controls, with zero loaders; 13 new
builder/transfer/factory controls are excluded. Fresh 358 native comparisons /
500 rereads, full differential/reference, 1846 parser comparisons / 432 rewrite
trios and package/symbol/50-consumer/two-negative gates pass at build checkpoint
6205de0. Independent helper probes cover live managed admission and alias
retirement; broader measurements remain open. This local Windows component
checkpoint closes no inventory context or audit verdict. Orphan-removal scratch,
inline images, legacy inspection output, broader integration/defaults/contexts,
undefined form-font mismatch, M11/A01–A07 and both verdicts remain open.

## Orphan-removal ownership checkpoint

Orphan removal owns its sets, traversal backing and removal metadata while
borrowing copied values. It preserves the original one-scan inbound-reference
rule, including self/candidate references, generations, duplicate/nonpositive/
missing IDs and actual nulls. Mapping visits, growth overlap, copy/zero work and
both cancellation routes are bounded. Prepared mutation precharges rollback;
cancellation during pool or mapping removal restores aliases and iteration order.

Frozen pending 624EED12 / tests A604F964 passes 8365 offline cases; isolated passes
7642, including 26 new controls. All 8339/7616 preceding stable identities and
224 original test sources remain. Preceding 2164476D fails eight / preserves eight
of 16 eligible identical controls, with zero loaders; ten new plan/caller controls
are excluded. Fresh 358 native comparisons / 500 rereads, full differential/
reference, 1846 parser comparisons / 432 rewrite trios and package/symbol/50-smoke/
two-negative gates pass at build checkpoint 6b78d66. Helper probes cover managed
admission, zero-allocation application and scratch/context retirement. This local
Windows checkpoint closes no inventory context or audit verdict. Inline images,
legacy inspection output, broader integration/defaults/contexts, measurements,
undefined form-font mismatch, M11/A01–A07 and both verdicts remain open.

## Inline-image input storage

Page entry metadata, decoded chunks, scoped filter names and exact joined bytes
now have owners through their consumers; forms share the decode owner. Raw
fallbacks borrow immutable payloads. Validation precedes decoding, missing/null/
generation behavior remains, and page warning/form silence on supported decode
failure is preserved. Limits and both cancellation routes propagate.

Isolated 7679 and pending 8402 offline cases pass, including 37 new controls.
All preceding stable identities, 226 original test sources and five locks remain.
Identical compatible controls on preceding pending 624EED12 fail 15 / preserve
13 / 28 eligible, zero loaders; nine new owner controls are excluded. Fresh
358 native comparisons / 500 rereads, full reference with 1846 parser comparisons
and 432 rewrite trios, and package/symbol/50-smoke/two-negative checks pass for
pending 8FE9C3F9 / tests 6387A8E1 at build checkpoint e28e3fc, on local Windows.

Per-mode fixed-JIT probes retain their measured allocation and raw process-heap
differences; all 28 final comparisons fit admission and disposal releases it.
Earlier positive excesses and a final negative 8224-byte delta stay explicit;
exclusive helper/cold/default/grouped/runtime-state attribution remains open.
An initial unchanged QDF header weak-buffer control failed; final/preceding
focused 21-case runs and the unchanged full rerun pass. That observation remains
open. Inline reconstruction, legacy inspection output, wider diagnostic/ownership
integration, five partial contexts, M11/A01–A07 and both verdicts are still open.

## Inline-image dictionary conversion checkpoint

The shared converter preserves unknown binary keys and borrowed values, expands
the nine key, four colorspace and seven filter abbreviations, and gives non-null
canonical keys priority over abbreviations independently of insertion order.
Unchanged filter arrays remain borrowed; fresh dictionaries/names/arrays retain
their own admission across escaping aliases. Memory/work failures and both
cancellation routes preserve previously admitted storage. Isolated 7720 / pending
8443 offline cases pass, including 41 new controls. Identical final tests on the
preceding pending runtime fail 13 / preserve 28 / 41 eligible, without loaders.

Fresh local Windows reference/package evidence binds pending AA91374C / tests
06929009 at build checkpoint 2a90662: 358 focused native comparisons / 500 rereads,
full differential/reference, 1846 parser comparisons / 432 rewrite trios,
package/symbol checks, 50 consumer smokes and both specific negatives. Another
36 image-header comparisons match across argv/job/mixed routes, excluding Length;
18 public exits differ because native reports missing-resource repair warnings.
Those warning/default/context gaps and full image-payload parity remain open.
Paired warm process measurements do not resolve the older grouped 24-byte result
or establish exclusive helper/whole-job retention. No inventory label changes.

## Inline resource outcome qualification

Inline externalization prepares page Resources before selection, including
unselected pages, while retaining malformed leaves with valid dictionary
ancestors. Missing/malformed form Resources and invalid XObjects keep their
native shapes and failed-attachment naming behavior. Below-threshold content
still prepares valid XObject dictionaries. Later page parsing can retain earlier
resource mutations and original content; malformed form filtering can fail
without replacing a prior target. Unreferenced image/content/null preservation
uses the shared copier. Named colorspaces and explicit nulls remain distinct.

Pending/isolated offline suites pass 8687/7964 cases, including 171 public
and seven helper controls. All preceding case identities, 234 other test sources,
five locks and original acceptance controls stay. Sixteen cases in fourteen methods are
explicitly corrected against pinned native observations; fixtures stay unchanged.

The same 280 argv resource observations improve from 67 to 280 normalized
graph/exit matches. Another 516 observations cover inherited and indirect
malformed resources/colorspaces across argv/job/mixed routes. All preceding 96
decoded graphs and exits match; 86 exact outputs match. Ten job/mixed encoded
form headers still differ through streamData/compressStreams setter order.
Two already overlapped preceding missing-resource graph failures; eight were
previously header-only. These ten differences are retained, rather than closed.
Identities/Length, full diagnostic context/text and fatal warning retention are
outside normalization. Global resource defaults, wider interactions/publication
and invocation parity remain open. Inventory counts/labels are unchanged, and
the preceding undefined form-font finding persists.

Fresh local reference/parser/package checks and 50 consumer smokes plus both
specific negatives pass as qualified development evidence. Strict locked restore
and fresh artifact-consumer restore fail on five advisories for required
ImageSharp 3.1.12. Versions/locks/repository policy stay fixed; the original gate
is unmet. M11/A01–A07, original measurement attribution, visual/reference CI,
remaining contexts and both completion verdicts remain open.

## Inventory

The job `check` key now runs the shared checker with the native empty-string
value; booleans reject. Argv/job checking composes with count, linearization, xref,
object, page/image and attachment listings on one opened document in native
inspection order. Failed checking stops subsequent inspection. Serialization-only
QDF/linearize flags accept without rewriting inspected objects. Checking
traverses pages without warning about their declared count; page-count inspection
reports the declared `/Count`, matching the native behavior. Ordinary typed
document reading retains its count-mismatch warning. Checker/page-count execution
snapshots work, peaks and handles after input cleanup; broader diagnostic and
invocation/presentation counters remain under audit. Object inspection uses native
bounded integer-prefix semantics: nonpositive object numbers disable that listing,
negative/unmatched generations report null, and setter generations persist unless
a following setter supplies another generation. Duplicate keys in one JSON object
still use only their final value. Numeric overflow rejects before PDF access.

`jobJsonFile` loads partial configurations through the invoking host. Direct JSON
and mixed argv job-file routes share the loader and operation parser. Repeated
independent files are allowed; active include identity cycles reject. Argv setter
order follows argument position, and files do not advance the positional cursor.
Native sorted-key traversal determines order within each file; each JSON
object uses its last value per key. Repeated input/output/page declarations across
files reject. Cumulative configuration bytes, nesting, host operations/handles,
work and a conservative DOM envelope obey host ceilings. Loaded configuration
stays admitted through execution; cleanup precedes outcome snapshots. Pool/state
retention and exact allocation accounting remain under audit. The separate argv
job-file parameter is an existing managed syntax extension; native comparison
uses the documented joined form.

JSON inspection without an output file returns its content in
`QpdfExecutionResult.OutputText`, through the same inspector used by typed and
file-output routes. No ambient stdout authority is granted. The text obeys the
host output ceiling and remains usable after execution cleanup.

The target's 98 root keys now have parsing/loading or explicit crypto rejection
routes. Names alone do not establish complete grammar, defaults or context
coverage. Selection/rotation inspection now has source-bound effects and failure
ordering across JSON, argv and mixed routes: selected count/order, duplicate/
foreign sharing, original generations, inherited/null attributes, removed-page
nulls, physical xrefs and exact filtered bytes. The native lane has 189 comparisons;
public regressions cover finite ceilings, observed foreign-read cancellation and
prior-target preservation. The full isolated reference and fresh packed consumer
pass. Broader direct-root, unusual Rotate, compressed/linearized, unknown-key,
form/resource/metadata/attachment and overlay/underlay contexts remain required.
Job grammar/default/context auditing and checker obligations keep both entries partial.

`tests/Compatibility/options.json` records 140 distinct documented flags in
157 option/context entries. It includes public argument forms, contexts,
choices, JSON property mappings, scope, completion milestones, review status,
remaining gaps and evidence slots. Explicit `--json-key=encrypt` is excluded
without excluding other JSON keys.

The generator reads public metadata from both `job.yml` and `manual/cli.rst`.
Defaults, aliases, positional grammar, precedence, nested context transitions
and interaction behavior still need operation-specific review and fixtures.
An inventory row or an enumerated choice does not implement that behavior.
The `fix-qdf` and `zlib-flate` companion contracts are additional work, separate
from the main command's flag count.

Supported behavior should match documented qpdf semantics. Stable inspection
data, page sequence, sharing, lossless stream bytes and exit classes need
appropriate comparisons. Compressed output need not be byte-identical.
JPEG component/color invariants and narrowly defined sample tolerances are established
against the approved codec: governed 1- and 3-component DCT decoding plus shrink-only
optimization shipped in M8; four-component CMYK/YCCK samples stay opaque by decision
while codec allocation/cancellation auditing remains open (see below).

## Validation runners and fixtures

`tools/QpdfProbe` is the development-only differential runner. It invokes the
pinned qpdf 12.4.2 executable (`--version`, `--show-npages`, `--check`) and the
pinned independent reader (PdfPig `[0.1.16]`, never a runtime dependency).
`tests/Fixtures/manifest.json` records 47 synthetic fixtures (single- and multi-page
selections, outlines, thumbnails, images, overlays, labels, and orphans, plus truncated
negative fixtures) with SHA-256, byte counts and expected page counts. Ordinary tests use these
fixtures and a fake oracle; they never require `external/` or a real qpdf
binary. The explicit reference lane (`tools/bootstrap-qpdf.ps1`,
`tools/run-reference-checks.ps1`) requires `QPDF_EXE` and fails when it is
missing.

Pinned oracle (bootstrapped 2026-10-01): official `qpdf-12.4.2-msvc64.zip`
(SHA-256 `db87077e683630c1217e0e8f9a20a9749d952ab676e881c3689187763a5de25d`,
matching the release `qpdf-12.4.2.sha256`), extracted under the ignored
`external/oracle/` directory. `bin/qpdf.exe` (SHA-256
`1bdaf878f37eaffea1ea3c30591214c77636a0f2fc7432427516b6e22e36c0ff`)
reports `qpdf version 12.4.2`. Build features: libjpeg statically linked
(`libjpeg` present in `qpdf30.dll`, no separate JPEG DLL); Zopfli absent
(`--zopfli` reports `zopfli support is not enabled`, exit 2; the remaining
`zopfli` strings in the binary are the disabled-build messages). Set
`QPDF_EXE` to `external/oracle/msvc64/qpdf-12.4.2-msvc64/bin/qpdf.exe`
to run the reference and differential lanes.

`tools/run-differential-checks.ps1` (via `QpdfProbe emit`, public API only)
compares managed and oracle outputs operation by operation under ignored
a fresh `external/diff/` directory per run (or explicit fresh `-WorkRoot`).
Existing workspaces reject and earlier artifacts stay intact. Oracle `--check`
must pass on both outputs, oracle and
independent page counts must agree, plus per-case assertions. Current
coverage, all passing with no engine discrepancies (inventory diff labels may cite finer sub-assertions inside these coarser cases): select (first- and last-page trios), merge,
uncompress (decoded stream payloads identical), normalize (normalized
content payloads identical), coalesce (decoded payloads identical), compress (recompressed payloads identical), flatten-rotation
(swapped MediaBox, dropped /Rotate, byte-identical wrap streams), prune (unreferenced
/F2 dropped under page operations with decoded payloads identical), JSON structural
page-descriptor agreement (version, object and contents references, positions), json-modes (trailer filter, inline payloads, minimal-output, and prefix sidecars each agreeing with the oracle), json-import (shared oracle JSON built by both writers with trios green and sorted decoded-payload identity), json-update (shared mutated JSON applied by both writers with trios green and `/Custom` in both outputs), writer-controls (negotiated/forced/extension headers and catalogs, static test-constant trailers, deterministic/default byte identity, LF-before-endstream shapes), attachments (add/copy/remove trios with cross-extracted bytes, key-set agreement, empty sets),
job-file (shared subset file run by both sides with trio green), recovery (warning/failure exit parity with page-count agreement after repair), metadata (info/metadata removal trios with per-flag assertions), labels (identical labeled/unlabeled catalogs), catalog-extras (primary-merge, subset, whole-file and split catalogs agree), error exit-class parity (out-of-range page, missing input, and unknown flag all exit 2
on both sides), warning exit parity (3/3, 0/0, 3/3), show-npages page-count agreement, show-pages/show-object/show-xref line-for-line dumps, show-pages-with-images listings (clean and dimension-less fixtures, warning exits 3/3), ignore-xref-streams reading (hybrid/classic/stream-only exit parity plus a hybrid selection trio), remove-acroform/remove-structure invariants (explicit removal drops even when ordinary would preserve), forms-preserve (field registration, values, parent links and default resources agree on full selection, subset pruning and merge trios, plus JSON fullname/value/needAppearances agreement), outlines-preserve (ordinary single-page outline titles agree on both sides while contained preview drops them), structure-preserve (ordinary single-page structure keys agree on both sides while contained preview drops them; damaged structure drops leniently), inline (externalized /IIm1 content and kept-inline thresholds agree on both sides), forms (generated
appearance bytes and `/Fxo1` stamping match, including the `1 5.2 Td`
placement), generate-unreachable (selection drops the emptied form and skips silently with 0/0 exits while whole-file warns and regenerates with 3/3 exits), forms-check (--check exits warn 3/3 on unreachable widgets and direct field objects while clean forms stay 0/0), overlay, overlay-annots (copied subtypes, contents, transformed rectangles, appearance matrices and nulled page-boundary destinations agree on both sides), overlay-widgets (imported field registration, values, parent links and default resources agree), overlay-widgets-dr (collision renames and DR conflict rewrites agree), overlay-widgets-repeat (repeated placements rename agree), qdf (editable view with generated object streams: trio plus QDF/ObjStm/XRef/member markers on both outputs and an upstream fix-qdf round trip), qdf-noids (suppressed original-ID comments with the editable view intact on both sides), fixqdf (managed repair versus oracle fix-qdf byte-identical on edited classic and streamed files, trios green), zlibflate (both directions with corrupt-input parity and level ordering), linearized (oracle-linearized files plus page-count and hint-length mutations on both sides), linearize (managed writer versus oracle writer on one- and two-page outputs, both validating under oracle and managed checks with independent page-count agreement), linearize-streams (managed versus oracle writers with generated object streams on one-, two-page, and outlined selections, both outputs validating under both checkers), check-linearization (managed versus oracle inspection exits and messages on valid, plain, and hint-damaged files), show-linearization (managed versus oracle dumps compared line for line), linearize-pass1 (zeroed-entry shape and shorter-than-final on both sides), linearize-outlines (managed and oracle outputs validate with matching show dumps on both files), and linearize-thumbs (thumbnail part-9 placement with matching show dumps). Confirmed intentional differences: our default
output stays uncompressed where upstream compresses, and our `--overlay`
block requires a second `--` terminator before the output file while
upstream takes the output directly after the block terminator. Merged forms
concatenate per-source field registrations with per-source-snapshot `+N` collision renaming
for named field nodes (including direct-dictionary nodes renamed in place with upward rebuilds), keep the first default-resources
dictionary, and embed the combined form directly in the catalog where
upstream writes an indirect object. Direct-dictionary kids stay embedded where the reference promotes them to indirect objects (rename outcomes agree on surviving shapes), and field-like direct objects met while traversing now warn and stay ignored under `--check` on both sides (exit 3, forms-check lane case); page-unreachable form trees drop on explicit selection and merge paths exactly as the reference drops them, while whole-file rewrites keep them verbatim on both sides. `--check` additionally warns with exit 3 on page widgets no field registration reaches and on kids whose /Parent back-link does not name the traversed node, matching the reference; the copy path keeps preserving those shapes verbatim (tested difference). Damaged structure with unresolvable references drops leniently instead of failing, while the reference keeps the damaged tree; well-formed outlines/structure preserve with dangling destinations pruned to null where the reference keeps references to null objects. Catalog extras that reference page-tree or catalog nodes drop their key where the reference remaps them onto the rebuilt nodes.
The `progressive-jpeg` cases compare four authored grayscale/RGB image rewrites
with exact sample hashes, filter removal and oracle/independent-reader trios.
Binary payload/text helpers search PDF delimiters with ordinal comparisons;
culture-sensitive matching can stall on large control-byte sample payloads.

`tools/verify-package-consumer.ps1` runs the public consumer against exactly the
supplied packed artifact in a new consumer/feed/cache directory. Only the local
artifact's hash changes in a copied lock; all reviewed versions, constraints
and external hashes remain locked from the first restore. The consumed DLL
must match the packaged DLL. `tools/test-package-consumer.ps1` proves wrong
artifact versions and external hash mismatches reject before smoke execution;
the tracked consumer lock remains unchanged.

`tools/run-parser-checks.ps1` is included after the full differential suite in
the explicit reference runner. It creates unique per-run authored inputs and
records exact object values, warning exits and output validation. Job numeric
limits accept managed numeric values; oracle job files require decimal strings,
so reference job probes must use the pinned schema's representation.

Integer parsing accepts the complete signed 64-bit range and long leading-zero
tokens under governed scan work and memory admission. Original text remains
available for writing; inspection normalizes integer spelling like qpdf.
In repair mode, integer overflow or an indirect-reference component outside the
signed 32-bit range replaces the whole object with null and warns, independently
of the semantic error threshold. Invalid references within that range instead
consume one error and become a null value. Strict parsing retains its failure
policy. Real tokens retain their full source text while a governed scan produces
a bounded conversion input: 1,024 significant digits plus a nonzero-tail marker.
This preserves the side of every binary64 rounding midpoint; independent exact
rational tests cover underflow, normal/subnormal boundaries, ties and overflow.
Text is admitted before allocation and copied with work/cancellation checks;
the conversion's 8 KiB working allowance is released on success or failure.
Local component measurements observe cancellation after scan work starts and
zero warmed-up heap allocation in the bounded conversion unit. This does not
close the broader lifetime, stack-state or cancellation audit. Other
syntax/loading and temporary ownership audit remains open.

Each parser attempt now owns its reservations independently of nested resolver
loads. Aborted repairs, syntax/resource/diagnostic failures and cancellation
release that attempt's values and temporary containers while preserving cached
nested values, accumulated work, diagnostic counts and peaks. Container
capacities and immutable copies, references and stream-copy overlap are
admitted before allocation; working container storage releases after copying.
Recovery candidates start at the beginning of their numeric header, so digit
suffixes cannot create false xref entries for other object numbers. Public
regressions cover fifty null repairs under finite argv/job memory with
valid/rebuilt xrefs, fresh indirect-length cache retention, repeated failed loads and cancellation
after observed container work. Per-value receipts now distinguish a subtree's
own admission from nested resolver/cache loads. Successful duplicate assignment
releases the overwritten subtree and the second equal key; recovery sanity
checks release dropped orphans, while ordinary repair transfers them to fake
keys. Immutable dictionaries retain only surviving entries. Regressions cover
byte/name/numeric/reference/container values, missing duplicate values,
retained aliases and nested resolver caches, finite-memory argv/job bulk loads,
failure and cancellation after overwrite work. Adversarial nested-stream
cache tests establish ownership containment, without extending the oracle
grammar claim. Successful recovery trials and caller-rejected parsed values
still need ownership reconciliation. General
container/BCL cancellation, diagnostic memory and document/cache disposal
remain part of the broader audit; these corrections do not establish a
whole-job or process-memory guarantee.

Braces delimit adjacent numbers, words and names; repair replaces each brace
with null and counts a container error. A name containing an original `#00`
escape is invalid: strict parsing rejects it, while repair treats the token as
null with a budgeted error inside containers. Escapes are decoded once, so
`#2300` remains the valid bytes `#00` and escaped braces remain name bytes.
The dictionary-key case keeps the reference's fake-key ordering with valid
xrefs; reconstructed-xref sanity policy instead ignores orphan values.

Raw CR/CRLF in literal strings normalize to LF; LF followed by CR remains two
newlines. Backslash line continuations disappear, while escaped `\r` remains
CR. Name/literal unescaping charges work and checks cancellation by bytes
traversed, including escapes that cross an interval boundary. Scalar unescape
work failures release their admission. Inspection uses lowercase hexadecimal
spelling for names and hex strings; decoded bytes are unchanged. The authored
matrix has exact values/exits, public argv/job inspection and rewrite parity,
strict public rejection and fresh packed-consumer evidence.

The byte-preserving lexical/container corpus adds 56 independently authored
bodies through valid/rebuilt xrefs at six budgets (0, 1, 2, 3, 6, 15). It covers
literal nesting and octal widths, raw/escaped high bytes, odd/invalid hex,
string/container EOF, mismatched terminators, reserved/ambiguous words and
malformed name escapes. Exact stdout/stderr bytes are retained in the reference
lane; UTF-8 replacement characters cannot establish non-UTF8 PDF byte values.

Inspection chooses hex or literal strings from decoded contents rather than
their source `Hex` flag. Named control escapes remain literal; other low
controls force hex, as does a non-ASCII proportion above one fifth. Remaining
nonprintable literal bytes use three-digit octal escapes. The writer retains
the source literal/hex choice and decoded bytes. Classification and formatting
have byte-based work/cancellation checks, including cancellation after observed
buffer work, without a universal latency claim.

Stray name hashes retain their spelling with an unbudgeted warning once per
token. Raw names use a zero-byte repair marker, matching the reference's JSON
name identity and distinguishing `/A#2` from `/A#232`. The writer preserves the
malformed spelling: both outputs warn on oracle check/JSON/reinspection and
strict loading rejects it. Those cases require warning exit 3 explicitly in
the reference lane; ordinary repaired outputs require clean exit 0. Public
argv/job rewrites agree byte for byte, with independent page-count checks on
all outputs. Root unknown words remain strings even under rebuilt-xref sanity
checks. Canonical display now admits its bounded character chunks, capacity overlap,
dictionary sort scratch and immutable result before allocation. Sorting borrows
immutable keys instead of cloning raw bytes. Temporary owners release on
success/failure/cancellation; the exact result string transfers its reservation
to the diagnostic that retains it. Inspection preflights diagnostic count and
UTF-8 bytes while formatting, charges each final diagnostic once, and snapshots
work/peaks after disposing input handles during object-plan execution on success/failure/cancellation. Invocation/presentation counters and diagnostic entry/list capacities remain open.
Broader nested loads, document/cache ownership, other diagnostic paths and
temporary parsed-value ownership remain open. The preceding component finding
(33,637,808 cumulative bytes for a 4 MiB binary value with governor peak zero
under a 64 KiB ceiling) motivated this correction. Input/value allocation was
excluded; component allocation is not whole-job peak memory or an RSS bound.

## Codec feasibility probes (work item 03)

`QpdfProbe probe-jpeg` exercises the pinned ImageSharp `3.1.12` JPEG codec
through public APIs with per-call encoders, checked dimensions, bounded
streams and prompt disposal; global defaults are never mutated.

- Grayscale (`L8`) and RGB (`Rgb24`) 8x8 round-trips preserve dimensions;
  encoded sizes vary with quality as expected. Feasible for M8 DCT work.
- `JpegEncoder.Quality` accepts `1..100` (default `75`) and rejects `0` and
  `101` with `ArgumentException`. qpdf `--jpeg-quality` accepts `0..100`
  (`QPDFJob_config.cc`); the adapter mapping is `0 -> 1`, identity on
  `1..100`, rejection outside. This clamping is the documented intentional
  difference.
- `JpegEncodingColor.Cmyk`/`Ycck` labels exist, but decoding yields
  color-converted RGB pixels; raw four-component samples are not preserved
  through the public pixel API. Displaying a JPEG or selecting a CMYK enum
  does not prove PDF component preservation. This gap stays open for M8:
  DCT decode must preserve component count, color interpretation, dimensions
  and sample ordering, with explicit narrow per-sample tolerances only for
  rounding/upsampling.
- Allocation is bounded by checked `width * height` against a caller
  pixel budget before any image is allocated; codec-internal buffers remain
  ImageSharp-owned and must be budgeted conservatively in M8.
  Cancellation flows through `LoadAsync`/`SaveAsync` overloads.
Decision (work item 133): four-component opacity is final, not held open.
Probed against the pinned 3.1.12 assembly itself, the public API offers no
raw-sample path: no CMYK pixel type exists (`Cmyk32` does not compile), the
decoder exposes no component selector or no-conversion option, and the
`Cmyk`/`Ycck` encoder labels only tag RGB-derived output. A hand-built baseline
4-component JPEG with known DC-only samples pins the exact boundary from the
oracle side: plain input decodes to exactly 128 + DC per component (exit 0),
Adobe YCCK input converts to CMYK samples (exit 0), and both shapes fail
explicitly before any pixel work on the managed side. The alternatives all
violate standing rules: another JPEG package or native libjpeg binding (banned
dependencies), a hand-written DCT decoder (banned second codec), and lossy RGB
reduction (banned silent fidelity loss). Four-component DCT therefore stays opaque
with an explicit warning on every path: `--check` skips these streams with exit 3
where the reference exits 0, and `--optimize-images` keeps its partial entry for
the missing encode path. The boundary is pinned by the `cmyk` differential
(recorded oracle samples, opaque managed output, 0/3 exits) and unit tests over
genuine four-component inputs. JPEG decoding now uses a per-job managed,
non-pooling ImageSharp allocator: pixel, coefficient and stream buffer owners
reserve their typed capacities before allocation, including a conservative
array/owner allowance, and release them at disposal. Returned samples have a
separate reservation, so simultaneously live pixel and sample frames both count.
Metadata decoding is disabled. Tight-budget failures release codec owners and
preserve an existing output target. The fixed 64 KiB allowance covers other
codec/configuration state; it does not replace the allocator's buffer charges.
Encoding also uses the per-job allocator and a seekable governed output stream.
Every growing capacity and returned copy is admitted while its previous owner
is still charged; failures dispose the working owners. Encoded bytes are not
counted as decoded stream bytes. Both codec directions receive the caller's
cancellation token through the pinned token-bearing APIs, with row copying and
stream writes checking it as well. Aggregate decoded bytes are admitted from
the frame header before another pixel/sample frame is allocated.
Four authored progressive grayscale/RGB fixtures exercise separate DC/AC scans,
non-multiple-of-eight edges, and larger coefficient owners. All sample bytes
are exactly 128; the pinned oracle and managed public rewrites agree byte-for-
byte, and tight coefficient budgets dispose owners. These fixtures are rebuilt
by `tools/build-progressive-fixtures.py` and indexed in the fixture manifest;
they do not establish a general tolerance for textured/progressive images.
The remaining codec-state allowance, independent retention and measured
cancellation latency still need auditing.

`QpdfProbe probe-flate` exercises the governed BCL Flate pipeline.

- `ZLibStream` handles the zlib-wrapped (RFC1950) form used by PDF
  `FlateDecode`; `DeflateStream` handles raw deflate.
- qpdf `--compression-level` accepts `1..9`; BCL exposes `Fastest`,
  `Optimal`, `SmallestSize`, `NoCompression`. The mapping is `1 -> Fastest`,
  `2..8 -> Optimal`, `9 -> SmallestSize`, rejection outside. Numeric levels
  are buckets, not 1:1 zlib levels; record this intentional difference for
  M6 writer controls.
- Corrupted input is rejected with `InvalidDataException`. Half-truncated
  input may decode to short output without throwing (observed `20` vs `3072`
  bytes); callers must validate expected stream length rather than assuming
  the codec rejects every truncation. Expansion limits are enforced by a
  bounded copy loop (8 KiB buffer, checked total) because the output cap does
  not itself cap codec allocations. Codec input/output chunks check cancellation.

Flate encoding and inflation now reserve codec state before construction and
use the governed output stream for admitted capacities, growth overlap and
returned copies. Inflation charges its 8 KiB scratch array separately, validates
Adler-32 over the working span before allocating the returned copy, and releases
working owners on failure. A borrowed input stream caps each refill at 8 KiB
and charges work even for compressed blocks that produce no output; encoding
also supplies at most 8 KiB per codec call. Empty compression validates levels
and admits its eight-byte framing before allocation.

The state allowances are conservative admission models: 512 KiB for deflate
and 128 KiB for inflate, including the BCL pooled buffer and wrapper state.
[zlib's memory model](https://zlib.net/zlib_tech.html) gives approximately 268 KiB
for default compression and 40 KiB for inflation;
[the .NET 10 stream implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.Compression/src/System/IO/Compression/DeflateZLib/DeflateStream.cs)
uses an additional 8 KiB pooled buffer. The allowances add headroom; their
adequacy across supported runtime/platform implementations still needs
independent measurement. Returned pool buffers can remain retained by the BCL
after a job. Measured cancellation/retention and state-allowance adequacy remain
audit work; these changes do not establish a
process-memory ceiling.

ImageSharp stays behind the JPEG adapter and out of the public API;
no Drawing, SkiaSharp, native libjpeg or alternative codec is introduced.
The pin remains exactly `3.1.12` in central versions, lock files and the
package dependency group.

## Strict inspection (work item 05b)

`QpdfDocument` strictly inspects single-section classic-xref PDFs: `%PDF-` header
at offset 0 with versions 1.0-1.7 and 2.0, tail `startxref` that must match the
actual table position, mandatory `%%EOF`, fixed-width entries validated before
reading, exact `[0, /Size)` coverage with a free object 0, and header/xref
consistency per object. XRef streams, hybrid/incremental structures, and recovery
are explicit later work and fail as strict parse errors today.

Page counting traverses `/Kids` with a cycle set and the nesting budget;
traversal wins over `/Count`, which only warns. Absent `/Type` tolerates
`/Kids`-bearing nodes as page branches and others as leaves; present but
mismatched `/Type` fails. Trailer `/Encrypt` rejects structurally as
`EncryptedInput` (including empty-password forms), without password fields,
prompts, or value echo. The effective version is the header version overridden by a
valid catalog /Version name (direct or indirect); malformed, mistyped, unresolvable,
or unsupported catalog versions fail. Stream /Length resolves direct integers and
indirect integers with a load-cycle guard; missing, negative, non-integer,
unresolvable, and hugely over-declared lengths fail; stream bytes come from /Length,
so delimiter-like bytes inside the data are preserved.

## Governed Flate and predictors (work item 08)

The internal filter pipeline resolves `/Filter` names and arrays with
canonical abbreviations (`Fl`, `LZW`, `A85`, `AHx`, `RL`, `CCF`, `DCT`)
and aligns `/DecodeParms` as upstream does: a single dictionary applies
to every stage, an array must match the filter count, and empty chains
ignore parms. FlateDecode, LZWDecode (both EarlyChange values), ASCIIHexDecode,
ASCII85Decode, and RunLengthDecode execute; anything else fails explicitly as
unsupported, never silently. DCTDecode executes at decode level all (work item 32).

Flate uses BCL inflate inside strict zlib framing: method/window/header
checks, preset-dictionary rejection, per-chunk output budgets, and an
Adler-32 trailer verification that catches truncations BCL alone would
decode short without error. Corrupt bodies and checksum mismatches fail
as truncated input; decompression bombs fail on budgets.

Flate now owns the decoded-byte charge for direct, filter-chain, companion and
linearization-hint calls. Each bounded inflate chunk checks the aggregate
prospective sample count before output growth or copying; successful stages
commit their charge once. PNG working buffers admit the additional row
selectors, which do not count as returned sample bytes. Shared predictors also
check aggregate admission before allocating transformed output. Failed budget
checks release working owners while preserving prior returned results. Other
filters' capacity/ownership and failed/canceled public counters remain part of
the broader containment audit.

Shared TIFF/PNG predictors now admit returned output before allocation, charge
work before copying/transforming, and check cancellation within wide rows.
Previous samples come from the output itself, so empty inputs allocate no row
scratch even with huge declared columns. Failed transformations release their
output reservation; Flate releases its inflated intermediate after prediction.
Packed TIFF samples start each row at its byte boundary, including padding;
sixteen-bit samples retain their component and row boundaries.

Predictors mirror upstream parameters (`/Predictor` 1, 2, or 10-15 with
defaults 1/1/1/8 for predictor/columns/colors/bits, unknown keys
ignored): TIFF horizontal differencing per sample with packed rows, PNG
per-row Sub/Up/Average/Paeth at byte granularity. Strict deltas: partial
rows are rejected (upstream pads them), unknown PNG row bytes are
rejected (upstream ignores them), parms-length mismatches are rejected
(upstream warns and skips decoding), and preset dictionaries are
rejected. Structural decoding consumes this pipeline starting with xref
and object streams in work item 09.

## LZW, ASCIIHex, ASCII85, and RunLength (work item 11)

LZW mirrors the reference bit packing (MSB-first), table rules (clear 256, end 257,
entries 258-4095), both EarlyChange settings (default 1, explicit 0/1 only), KwKwK
handling, width growth at 511/1023/2047 with the EarlyChange delta, and table-full
rejection. Codes after end-of-data and trailing partial codes are ignored like
upstream. LZW predictors reuse the Flate predictor stage with the same strict deltas.

ASCIIHex ignores ASCII whitespace, ends at `>`, pads a trailing odd digit with zero,
tolerates a missing end marker, and ignores later bytes; bad digits fail. ASCII85
ignores ASCII whitespace, allows `z` only as a full group, ends at `~>`, pads partial
final groups with `u`, wraps group overflow like the reference, and ignores later
bytes; misplaced `z`, broken end sequences, and out-of-range bytes fail. RunLength
copies below 128, repeats above 128, continues past 128 markers like upstream, and
drops truncated tails instead of failing. All four stay budget-bounded per emitted
chunk. ASCIIHex, ASCII85, and RunLength reject DecodeParms; mixed chains decode in
filter order. Structural decoding accepts LZW xref streams through the same pipeline.

## Xref and object streams (work item 09)

`startxref` may point at a classic table or a Flate- or LZW-compressed xref
stream (`/Type /XRef`, `/Size`, `/W`, optional `/Index`, decoded under
output budgets with exact data sizing). Entry types are free,
uncompressed with pre-read offset validation, and compressed with
generation forced to zero; unknown types fail. Object-0 stream entries
are ignored like the reference reader while object 0 still resolves as
free. Coverage of `[1, /Size)` is exact; free-head rules match classic.
Object streams (`/Type /ObjStm`, `/N`, `/First`) decode once with
ascending, in-bounds offsets and per-entry extents, caching only what
the xref table points at the stream. Strict deltas versus warn-and-continue:
corrupt headers, self-containment,
low object numbers, non-ascending offsets, and trailing entry bytes all
fail, where the reference warns and continues. `/Prev` and `/XRefStm`
fail explicitly as work-item-10 scope, replacing silent staleness with
errors. `/Extends` on object streams is read per the reference (whose reader
carries no Extends handling): members resolve through the header pairs of the extending stream itself, so
even the self-extending objstm-extends.pdf fixture returns
its font object on both sides. Selection output normalizes to classic xref;
object-stream preservation arrives with writer controls (M6).

## Incremental and hybrid chains (work item 10)

`/Prev` chains across classic and stream sections resolve with
latest-wins precedence; hybrid `/XRefStm` links merge without
overriding their own revision and their own `/Prev` stays ignored like
upstream. Cycles terminate explicitly, section counts run against a
revision budget, and link targets must be integers inside the input.
Deleted entries (latest free) resolve as null; replaced entries and
generations resolve transparently, including compressed objects whose
streams were superseded. Union coverage of the effective `/Size` is
exact with absent-or-free object 0, and over-range entries fail rather
than being ignored. Every chained trailer is checked for encryption
declarations. Recovery of damaged structures stays M4 work and fails
explicitly here. `--ignore-xref-streams` (plus job key `ignoreXrefStreams`)
skips cross-reference streams and hybrid `/XRefStm` links, reading classic
tables only: hybrid files fall back to their classic revision, bogus stream
links never load, and stream-only files fail strictly into bounded recovery.
The flag rides every plan that opens a document (plus the host budgets) with
exit-for-exit oracle parity on hybrid, classic, and stream-only fixtures.

## Byte, version, and indirect-length semantics (work item 12)

`QpdfDocument.Version` is the effective version: the `%PDF-` header (offset 0,
1.0-1.7 and 2.0) overridden by a valid catalog `/Version` name, direct or
indirect. Null or absent `/Version` keeps the header; malformed, mistyped,
unresolvable, or unsupported values fail. Selection output echoes the effective
version in its header. Object loads carry a reentrancy guard so a
self-referential indirect `/Length` fails as a parse error instead of recursing.
Over-declared lengths fail before allocating; stream data is sliced strictly by
`/Length`, preserving delimiter-like bytes.

## Ordered multi-page selection (work item 13)

The typed selection accepts explicit ordered pages (duplicates and order
preserved), `FirstAndLast(N)` windows, and sorted/deduplicated `Normalized`
sets. `FirstAndLast(N)` returns the first N and last N pages in document order,
or every page once when the windows meet or overlap. Results carry the source
page count and the exact per-output-page source mapping. Each occurrence copies
to an independent page object while immutable resources stay shared; selecting
every page still rewrites the graph rather than echoing input bytes. The reference
instead shares content streams across duplicate occurrences (probed: a ranged collate
repeating pages yields shared streams there, independent copies here, with identical
page order on both sides). Oversized
selections fail on work budgets and out-of-range pages fail explicitly.

## Catalog, inheritance, and widget policy (work item 14)

Ordinary selection and rewrite paths carry a fresh catalog with the input form dictionary preserved through the shared copy engine (work item 80) and with outlines and structure preserved for ordinary paths while contained preview drops them: field registration, values, parent links and default resources round-trip across typed, argv and job paths, with top-level fields pruned when every widget in their subtree falls outside the kept pages. `--remove-acroform` is the explicit form prune switch threaded through every publication path; `--remove-structure` is the explicit structure prune switch (previously a no-op, now drops `/StructTreeRoot`/`/MarkInfo` even when ordinary would preserve); flatten-all drops the form with its annotations. Typed preview (`SelectPageAsync`/`SelectPagesAsync`) keeps the contained policy of dropping `/Outlines`/`/StructTreeRoot`/`/MarkInfo` while ordinary argv/job/whole-file/merge preserve them via the shared copier (dangling outline destinations prune to null; damaged structure drops leniently instead of failing). Page-tree (`/Pages`) and catalog references stay structural errors; references to already-copied objects remap, and references to any other page prune to null under the ordinary selection policy so omitted pages can never resurrect. Field and annotation `/Parent` links remap through the shared memo table while page-tree ancestry stays dropped and materialized. Parent-held field lookup and default-resource merging stay explicitly open. Unknown catalog keys (`/NeedsRendering`, `/PageLayout`, `/ViewerPreferences`, `/Lang`, `/OpenAction`) copy through the owning source engine on ordinary selection, whole-file rewrite, and primary-source merge (split groups and empty bases drop them); unresolvable values drop keys silently like the reference.
Widget annotations travel with the page with appearance bytes preserved. Only the four inheritable attributes materialize,
with explicit null treated as absent (ancestor fallback, else `/MediaBox` failure);
page-only boxes such as `/BleedBox` and `/UserUnit` never inherit.

## Inspection breadth (work item 16)

`qpdf input --show-npages` (plus job key `showNpages`) reports the traversed page count
as an info diagnostic with exit 0; valued forms, output files, page selections, and other
operations fail explicitly, and encrypted or exhausted-budget inputs fail without output.
The differential lane compares managed and oracle counts on one- and two-page fixtures.

`qpdf input --show-pages` (plus job key `showPages`) lists each page dictionary with its
indirect content references in oracle-identical line layout across argv and job paths, with
the same explicit combination failures; the differential lane compares dumps line for line.

`qpdf input --show-object={trailer|obj[,gen]}` (plus job key `showObject`) renders one object
definition in canonical single-line layout (sorted keys, oracle-identical spacing) across argv
and job paths, resolving compressed members and reporting `null` for missing objects like the
reference; streams show their dictionary only. Object-generation selectors resolve exact
pairs against the newest entry per number exactly like the reference: bare selectors
address generation 0, freed or never-defined pairs display `null` with a success exit,
and superseded same-generation revisions stay unaddressable on both sides.
`--show-object={trailer|obj[,gen]}` with `--raw-stream-data` (plus job key `rawStreamData`)
or `--filtered-stream-data` (plus job key `filteredStreamData`, decoded as `--decode-level=all`)
returns exact fileless stream bytes through `QpdfExecutionResult.OutputBytes`,
with filtered output winning when both flags are given. Explicit host output paths remain
an extension for stream extraction. Non-stream resolutions retain the diagnostic view;
their binary result is null. Empty stream payloads have a non-null value of length zero.
Repeated bare flags accept; flags unused by an inspection or writer are ignored.
Fileless filtered output honors `normalizeContent` and QDF defaults through the shared
normalizer, while raw output stays unchanged. The binary differential lane compares actual
bytes, disabled/absent/generation selectors, warnings and failure order across argv, JSON
and mixed configurations. The plain
show-object lane still compares definitions line for line on plain, stream, trailer, missing,
and compressed objects, plus same-generation, absent-generation, freed, and live-pair selectors over incremental fixtures.

Fileless attachment inspection uses the same binary result and embedded-file reader.
Checking and all following inspections share one document. The command checker decodes
the graph reachable by the writer; the typed checker retains its broader object inventory.
Native attachment output places check text on stderr and permits subsequent stream and
attachment payloads; binary stream output after prior informational output without that
attachment route fails after checking. Diagnostics and payloads remain separate in the
managed result. Failed or canceled execution returns no binary payload, preserving staged
publication: native stdout can already contain a prefix when a later attachment lookup
fails. Charged output counters may include those discarded staged bytes.

`qpdf input --show-xref` (plus job key `showXref`) lists every live cross-reference entry
(`n/gen: uncompressed; offset`, `n/0: compressed; stream, index`, free entries skipped)
in oracle-identical line layout across argv and job paths; the differential lane compares
dumps line for line on classic and object-stream files.

`Qpdf.CheckAsync` runs a declared partial check: header, cross-reference,
trailer, page tree, full object-graph traversal, decode-level-gated stream
decoding (`none`, `generalized`, `specialized`, `all`), page content, and
linearization. Skipped stages stay visible with explicit reasons and never
pass silently; failures carry sanitized per-object detail with caps. Streams
above the selected level are caller-scoped; streams with milestone-unavailable
filters skip with a warning. Content tokenizing (M7) and hint validation (M10)
report skipped; non-linearized files pass linearization trivially. `qpdf input
--check` maps to the same engine with exit 0/2/3 from the diagnostics; job-JSON
check has no upstream schema basis yet and fails explicitly.

## Cross-document import identity (work item 19)

`Qpdf.SelectPagesAsync` merges ordered pages from several inputs with per-source
object identity: separate copiers share one destination numbering sequence and one
governor, so equal object numbers across documents never collide while widgets
keep their within-source field links. Every page keeps its own resources, so
same-named resources and fields stay inert without content rewriting. Merged forms concatenate per-source field registrations with per-source-snapshot `+N` collision renaming for named field nodes, keep the first default-resources dictionary, OR the NeedAppearances flags, and embed the combined form directly in the catalog where upstream writes an indirect object. Output takes the highest source
version and carries per-page source mapping plus per-source page counts. Direct-node promotion and first-wins resources stay explicitly open; four-component CMYK/YCCK decoding stays opaque by decision (see below). Generation handling is complete: selectors and reference resolution match the reference pair-exact behavior above.

## Split publication and replacement (work item 21)

`qpdf --split-pages[=n]` partitions the selected pages into groups sharing one
object pool with per-group reachability pruning and renumbering, upstream `%d`
and `-NN[-MM]` naming, sliced page labels, and upfront collision validation;
partial outputs can remain if a later group fails. The job-JSON `splitPages` key
(string, `""` for bare) rides the same engine across merge, single-page and
whole-file plans with argv job-parity, and an empty valued form matches bare upstream.
`--replace-input` rewrites
the single distinct input in place with no backup files and no output operand,
through argv and job JSON (`replaceInput` as `""` or boolean, conflicting with
`outputFile`, `pages`-less plans, and `empty`); failed publications leave the
target untouched, matching the reference, which likewise creates no backups.
Both reject contradictory combinations explicitly; typed callers split through `PdfPageMergeOptions.SplitPages` (pages per group) on the shared merge engine, rejecting non-positive sizes with `ArgumentOutOfRangeException`. The differential lane replaces a shared two-page file
on both sides with trio validation and no backup files created.

## Collation and page labels (work item 20, part 2)

`PdfPageMergeOptions` carries collation (absent concatenates; one group size
applies uniformly, otherwise one per source with round-robin exhaustion),
rotations, and explicit labels. Source page-label trees merge through the
shared copy engine: per-output-page resolution over each source number tree in
output order concatenates per-source entries with page-index offsets, unlabeled
pages contribute fabricated continuation entries, covering starts are adjusted
with explicit starts on every computed entry, and runs compress against the
previous entry; indirect label dictionaries resolve through the owning source
engine. `--set-page-labels` (`first-page:[type][/start[/prefix]]` with `n`,
`rN`, `z`) and job-JSON `setPageLabels` replace merged labels with a fresh
`/PageLabels` number tree (first entry at page 1, strictly increasing), while
`--remove-page-labels` and job-JSON `removePageLabels: true` drop merged labels
everywhere including whole-file rewrites, and are contradicted only by
combining them with set. Bare whole-file rewrites carry the source tree
verbatim; split groups slice merged labels with restart behavior and drop
explicit specifications, matching the reference. Malformed trees fail
explicitly instead of warning. Prefixes encode as upstream does (ASCII literals,
PDFDocEncoding hex, otherwise UTF-16BE with byte-order mark); default start 1
stays omitted from explicit specifications.

## Multi-file merge (work item 20, part 3)

`qpdf --pages` takes repeated file/range groups (positional or `--file=`/`--range=`
forms, `.` for the primary input) with repeatable `--collate[=n[,m,...]]`
(uniform, per-source, or bare single-group); `--collate` is accepted at the main level
as upstream places it, while the inside-`--pages` form keeps working as leniency and both
forms share one grammar with identical bytes. Single groups without collation keep
the exact legacy single-file behavior. Job JSON accepts multi-entry `pages`
arrays concatenating in order plus a `collate` key (`""` for bare, otherwise `n[,m,...]`)
with argv job-parity. Group counts other than one value or one per source fail explicitly,
as do out-of-range pages, without publication. Ranged groups interleave in identical page
order on both sides (see the sharing note above).
Page selection carries complete option evidence (ordered/duplicate/first-last coverage,
explicit range failures, work-budget enforcement, select/merge differentials).

Bare `--empty` writes a zero-page document (fresh catalog with an empty page
tree) and every inspection accepts the empty input through argv and job JSON:
`--show-npages` reports 0, `--show-pages` lists nothing, `--show-object`,
`--show-xref`, `--json`, `--list-attachments`, and linearization inspections
read the synthetic catalog, while `--check` fails explicitly on the page-less
file, matching the reference exit classes throughout. Dot selections against
the empty input fail explicitly here while the reference crashes; no crash
parity is claimed. The differential lane covers bare rewrites (zero pages on
both sides with failing checks) plus show-npages/check exit parity.

## Ordinary rotation (work item 20, part 1)

`PdfPageRotation` (`Set`/`Add`/`Subtract` over output positions, any multiple of
90 normalized modulo 360) rides the multi-source selection and the single-page
typed selection (`SelectPageAsync` rotation overloads with default and explicit
budgets, proven by the package consumer); `qpdf --rotate` and
job-JSON `rotate` share the `[+|-]angle[:range]` specification with the full shared
page-range grammar (spans, descending ranges, `rN`/`z`, exclusions, `:odd`/`:even`,
duplicates preserved and applied in order, matching the probed reference). `--rotate`
is accepted at the main level as upstream places it, with the inside-`--pages` form kept
as leniency and byte-identical results; repeated specifications accumulate with later
specifications winning per page, and positions span merged outputs including
`--file=`/`--range=` groups. Typed single-page rotations are byte-identical with
the matching argv and job-JSON rotations, which transitively ties them to the
`rotate-ranges` oracle agreement. Out-of-range positions
fail explicitly. Rotation folds over the inherited
base (explicit page values beat ancestry, null counts as absent); pages without
applicable specifications keep their bytes untouched. Page labels
stay open behind source-tree merging.

## Invocation breadth (work item 18)

Response files expand once through the host with one argument per line, UTF-8
checked, budgeted per line, and leases closed; `@-` has no stdin descriptor and
fails explicitly, missing files stay literal like the reference parser, and
nested `@` tokens never re-expand. Response payloads are admitted against the memory
budget before any byte is fetched. `--job-json-file` reads the identical job
schema through the host with plan parity. Job-JSON parsing itself runs under the job
governor: input bytes are work-charged and the transient document model is
memory-admitted before construction and released with the plan. `--warning-exit-0` maps warnings to
exit 0, `--no-warn` drops warning messages while keeping exit 3 (both mirror the
reference manual), and both ride the shared plan across argv and job JSON (`warningExit0`,
`noWarn`, plus `suppressRecovery` mapping to strict recovery). The differential lane checks
warning exit parity with the oracle (plain 3/3, flagged 0/0, suppressed-message 3/3).
`--help` and `--version` return identity text as info diagnostics with exit 0, as does the
new `--copyright` notice (Lokad/MIT/12.4.2-target); valued identity forms fail explicitly, and
identity requests bypass exhausted output budgets. `--completion-bash` and `--completion-zsh` emit static subset completion scripts (no shell execution or environment lookup), `--job-json-help` prints the supported job-key subset, and `--json-help[=1|2|latest]` describes the matching inspection version; the differential lane checks exit-0 identity parity with shared markers on both sides. `--keep-files-open=y|n` and `--keep-files-open-threshold=n` are validated performance hints with identical outputs (host lease policy stays authoritative); the differential lane runs hinted merges with trios green on both sides. All other operations stay
explicitly unsupported. The differential lane checks error exit-class parity with the oracle: out-of-range page ranges, missing inputs, and unknown flags all exit 2 on both sides. `--global [options] --` limit blocks behave identically on both sides; bare limit flags outside the block are accepted here as a narrow-profile leniency while the reference rejects them, and the differential lane runs blocked selections (filters, nesting at 100 and at the upstream default 499, and loose container caps) with trios green on both sides. Over-limit filter chains stay opaque with a warning (exit 3) on both sides. Invocation parser knobs only tighten within embedding host hard ceilings: a value above an explicitly supplied host ceiling fails with BudgetExceeded, while default-constructed options leave invocation values (within their absolute bounds) in charge; value equality with a library default never counts as untouched (agreement at 499 for nesting under default options). `--no-default-limits` (bare argv, `--global` block, job `noDefaultLimits`) mirrors upstream `disable_defaults`, but only under default-constructed options: it lifts the error budget to 0 and the damaged container and filter caps to 4294967295. Explicit values, explicitly supplied host ceilings, parser nesting, and the undamaged container cap always stand, and valued, duplicate, and import combinations fail explicitly. The differential lane decodes a 30-filter chain identically on both sides under `--no-default-limits` (oracle inside its `--global` block, managed bare by narrow-profile leniency). `--parser-max-errors=n` has an independent error budget for each indirect object and compressed member (default 15; 0 removes the numeric threshold). Reaching a finite threshold returns that object as null with a warning (exit 3), rather than failing the whole job solely for the threshold. Six nearby errors still abort an object at zero; four intervening good tokens break the proximity cluster without resetting the total count. Actual loads use repair for valid xrefs as well as rebuilt xrefs: unknown words become byte strings with valid xrefs, but null elements after reconstruction, matching the reference's sanity policy. Duplicate keys consume one budgeted error and keep the last value when allowed; missing dictionary keys receive collision-free fake names after the dictionary is read with valid xrefs, while reconstructed-xref sanity policy ignores orphan values. A missing last value is null. In the authored matrix, nearby errors also abort when prior orphan values exceed the remaining allowable errors; zero still participates in that guard. A sufficient good-token gap bypasses it. The root opener does not count toward that gap, and duplicate integer values count the tokens used to disambiguate scalar/reference syntax once. Nested containers restore their own orphan context. Inspection omits dictionary references resolving to null; copying validates generations before using number-based memoization, so stale references cannot alias already-copied live objects. Warnings and budgeted errors are distinct. Strict typed opening and `--suppress-recovery` retain the documented strict profile. Invocation controls now propagate into argv and job inspection plans too. An explicit finite host error ceiling rejects zero or larger invocation values; an explicitly unlimited host allows finite thresholds. Host work/memory/diagnostic failures and cancellation propagate, and scan effort no longer shares the semantic error count. Recovery warnings summarize trial damage; actual object loads report their repairs. The explicit parser lane compares 1174 object values/exits against qpdf across finite/zero limits and valid/rebuilt xrefs, and 208 rewrites retain the repaired/null values with clean oracle checks and independent page counts. The additional 37 dictionary cases use eight budgets (0, 1, 2, 3, 4, 5, 6, 15); the source-page-tree-reference case has inspection coverage and offline rejection evidence under the documented contained-copy profile, rather than a rewrite trio. The large-storm differential now agrees under default and finite budgets as well as zero and `--no-default-limits`. The entry stays partial pending remaining lexical damage, complete damaged-container transitions, broader nested-load evidence and trial/discarded-value ownership review. The default nesting cap is tighter (256 vs 499) for stack safety with agreement at 499; tight container caps fail fast (exit 2) while the oracle warns and skips the oversized object during recovery (exit 3), with agreement at loose caps.

Output-neutral diagnostics (work item 81): `--progress` reports counted-byte
milestones (25/50/75/100%) while the reference streams object-based
percentages during the write; `--verbose` appends an operation summary;
`--report-memory-usage` reports the exact governor peak while the reference
reports a best-effort figure; `--test-json-schema` validates generated
top-level keys against the matching `--json-help` description and fails
explicitly on drift. Report text stays implementation-defined on both sides
with no stable cross-implementation contract. All four flags are exit-neutral
and output-neutral, and the differential lane checks exit-0 parity, green
trios, and byte-identical outputs with and without each flag (plus matching
top-level JSON keys for the schema check).

## Bounded repair (work item 17)

Strict parsing runs first; only its failure engages bounded repair, which scans
for object headers, trial-parses each candidate, keeps the last valid
occurrence, aborts after MaxParserErrors failed trial parses (default 15; 0 removes
the threshold), and fills
unscanned gaps as free. Streams with indirect lengths are
skipped by the scanner. The tail trailer wins when its root resolves, otherwise
the last scanned catalog wins; files without a catalog fail. Repaired output is
rewritten, never the original bytes. The CLI repairs by default and emits a
recovery warning (exit 3); `--suppress-recovery` keeps strict failure. Typed
selection and inspection stay strict. `--ignore-xref-streams` classic-table
reading is covered under incremental and hybrid chains above.

## Selector audit (work item 15)

Indirect `/Filter` and `/DecodeParms` resolve through the document engine,
including indirect predictor integers and `/EarlyChange`; unresolvable or
mistyped indirect specs fail explicitly. Cross-reference stream filter specs
stay direct-only. Empty documents resolve `FirstAndLast` to an empty selection
with a zero-page rewrite; cyclic resource graphs terminate through memoized
copies; escaped names and strings round-trip value-faithfully with canonical
re-escapes. The clean package consumer validates single- and multi-page typed,
argv, and job paths with source-mapping checks.

## Single-page selection (work item 06a)

Typed `Qpdf.SelectPageAsync` copies one page by 1-based index through the
same reader, graph copier, and classic-xref writer. Reachable shared objects
are copied once with destination identities registered before descending;
`/Parent` links are dropped and the four inheritable attributes are
materialized from ancestry (missing `/MediaBox` fails). Streams travel
opaque with recomputed `/Length`; output carries a fresh catalog, no `/ID`,
and generation 0 throughout. Every serialization runs a counting pass and
an emitting pass whose lengths must agree (the M1 replay canary); repeated
runs are byte-identical. Page-tree references fail explicitly; page references across
pages follow the item-13 multi-page and item-14 prune policies above: annotations,
forms, and multi-page surgery stay open for M3/M5.

## Command adapters (work item 06b)

`Qpdf.ExecuteAsync` (argv) and `Qpdf.ExecuteJobAsync` (job JSON) parse a
narrow single-page subset into one shared selection plan executed by the
typed path. The argv subset follows upstream spelling (`--pages`, `--file`,
`--range`, `.` for the primary input, the terminating `--`); the job subset
uses upstream field names (`inputFile`, `outputFile`, `pages[].file`,
`pages[].range`). An omitted range selects all pages; ranges,
collation, multiple sources, `--empty`, `--replace-input`, stdout `-`,
response files, flexible operand order, and unknown options fail
explicitly. All documented password/encryption flags reject with the
stable `UnsupportedCryptoOperation` diagnostic and exit 2 without retaining
or echoing values; encrypted inputs map to the same code. Adapter outcomes
reuse the execution-result mapping, including the warning exit. No inventory
entry changes: parsing a name never completes its behavior contract.

## Writer versions, IDs and stream endings (work item 22a)

`--min-version` raises the output header when the greatest input version is
lower; `--force-version` sets it exactly even when lower. Both accept
`major.minor[.extension-level]` for the supported headers 1.0-1.7 and 2.0;
other values fail as invalid arguments. A positive extension level emits a
direct catalog `/Extensions << /ADBE << /BaseVersion /M.m /ExtensionLevel N >> >>`.
Forced versions below 1.5 yield classic xref tables with object streams disabled first, per upstream (work item 22b);
no other feature downgrading is claimed.

Trailer `/ID` is always emitted. The first word preserves the source trailer
identifier when present, otherwise it repeats the generated second word. The
default second word is a deterministic MD5 content hash (placeholder zeros
hashed, then replaced by the digest, preserving the counting-pass length),
so repeated runs over the same inputs and options are byte-identical. This
differs intentionally from upstream time-based random IDs: the library has no
ambient clock, and the contained profile favors reproducibility. `--static-id`
selects the upstream test constant `<31415926535897932384626433832795>`
(static wins when both ID flags are given). `--deterministic-id` is accepted
and matches the default deterministic behavior.

Stream `endstream` handling follows upstream: by default the serializer
writes `data` + `endstream` with no extra newline; `--newline-before-endstream`
always writes `data` + `\\nendstream`, doubling a trailing newline when present.
`/Length` stays recomputed and direct. All paths (typed, argv, job JSON with
`minVersion`, `forceVersion`, `deterministicId`, `staticId`,
`newlineBeforeEndstream`) share one negotiation, identifier, and serializer
implementation with counting-pass replay checks and output budgets.

## Writer object-stream modes (work item 22b)

`--object-streams` accepts `disable`, `preserve` (the default, matching
upstream), and `generate` through argv (`--object-streams=mode` or
`--object-streams mode`) and job JSON (`objectStreams`), with invalid modes,
bare flags, and duplicates rejected as invalid arguments. `disable` always
writes classic xref tables; `preserve` re-compresses exactly the objects that
arrived compressed (inputs without object streams stay classic) without
reproducing exact original stream groupings; `generate` packs every eligible
non-stream object (catalog, page tree nodes, streams, and signed byte-range
dictionaries stay uncompressed) into new object streams of at most 100
members, distributed evenly. Any object streams imply a cross-reference
stream (`/Type /XRef`, `/Size`, `/Root`, `/ID`, `/W`, dense coverage, no
`/Index`) instead of a classic table, with `startxref` pointing at the xref
stream object. Generation raises the header to at least 1.5; a forced version
below 1.5 disables object streams first, per upstream. Payloads and xref
entries are emitted uncompressed with no `/Filter` or predictor: valid PDF
that our reader and the independent reader accept, but larger than upstream
Flate-packed output. Flate packing arrives with the compression controls
(work item 23). Typed callers default to `preserve` like the CLI.

## Preserve-unreferenced objects (work item 22c)

`--preserve-unreferenced` (bare; job JSON `preserveUnreferenced` as `""` or
boolean) keeps every input object the page selection did not reach, in
ascending original object-number order per source, appended after the
reachable graph with shared references remapped through the same memoized
copier. Preservation covers the primary input on merges (other sources keep
only their selected pages) and is suspended under `--empty`, matching the
reference on both counts. Detached pages and detached trees survive as loose
objects while rebuilt roots (the input catalog plus the selected page-tree
chain) are superseded, and preserved metadata stays linked from the rebuilt
catalog with its bytes intact. Fresh catalog/pages stay 1/2. Preserved
objects participate in `--object-streams` packing (compressed-ness or
generation eligibility apply to them like any selected object) and are
included in every split group via the reachability closure, so no dangling
references are emitted. Intentional differences from upstream: preserved
objects keep ascending copy order rather than original-ID order (identical on
single-source selections; multi-source pools renumber per source);
object/xref-stream containers are not carried over while their members are
preserved decoded as direct objects; objects that fail to load or copy are
skipped under one aggregated `UnsupportedFeature` warning instead of failing
the job. Follow-up closed: unknown catalog keys now ride the shared copy engine on ordinary paths (see the catalog policy above).

## Stream compression controls (work item 23a)

`--stream-data` (`compress`/`preserve`/`uncompress`), `--compress-streams`
(`y`/`n`, bare means `y`), `--decode-level` (`none`/`generalized`/
`specialized`/`all`), `--recompress-flate`, and `--compression-level`
(`1..9`) are accepted through argv (both `=` and space forms) and job JSON
(`streamData`, `compressStreams`, `decodeLevel`, `recompressFlate`,
`compressionLevel`), with invalid, bare-required, and duplicate forms
rejected. Resolution matches upstream mappings: `compress` implies Flate
output plus generalized decoding, `uncompress` implies generalized decoding
without output compression, `preserve` implies neither; explicit
`--compress-streams`/`--decode-level` override the `--stream-data` mapping
(upstream applies these in argument order instead). Already-Flate streams are
left untouched unless `--recompress-flate` is combined with a covering decode
level; empty results stay uncompressed; indirect filter specs resolve through
the document engine. Decode coverage mirrors inspection; `all` additionally decodes 1- and 3-component DCTDecode streams (work item 32) and warns per undecodable stream, while filters above the requested level stay opaque silently. Four-component CMYK/YCCK JPEGs stay opaque with a warning because the approved codec cannot preserve their raw samples. Corrupt streams and
bad specs fall back to opaque bytes under a warning; `/Crypt` is never
decoded or stripped. Levels bucket to BCL Flate effort (`1` fastest, `2..8`
optimal, `9` smallest size; default optimal) with governed bounded encoding
whose zlib output round-trips through the strict decoder. Intentional
difference: the default profile preserves stream encodings opaquely instead
of upstream compress defaults, so existing previews stay byte-stable without
flags. `--zopfli` reports that Zopfli is not enabled in this build and exits
with an error; `QPDF_ZOPFLI` has no host environment capability yet and is
treated as unset (matching upstream unset behavior). Generated object/xref
streams Flate-encode whenever the resolved compression request enables compression (work item 23b); otherwise they stay raw.

## Container Flate packing (work item 23b)

Generated object-stream payloads and cross-reference entries are
Flate-encoded with plain `/FlateDecode` (no PNG predictor) whenever the
resolved stream-compression request enables compression, honoring
`--compression-level` buckets through the same governed BCL pipeline as
regular streams (deterministic bytes, so counting and emitting passes still
agree). Otherwise containers stay raw. No predictor is emitted, unlike
upstream xref streams: readers must not require decode parameters, and the
strict decoder accepts plain Flate.

## Document info and metadata (work item 24a)

The output trailer carries the input `/Info` dictionary and the output
catalog carries the input `/Metadata` stream by default (copied through the
shared memoized copier, so streams honor the compression knobs and indirect
values remap; multi-source output takes the primary input, single-source and
split groups likewise). `--remove-metadata` omits the catalog stream while
keeping info; `--remove-info` reduces `/Info` to a lone `/ModDate` entry (or
omits `/Info` when absent) and also omits the metadata stream, matching
upstream. Both flags accept argv bare and job-JSON empty-string/boolean
forms. Unpreservable extras are skipped under one aggregated warning instead
of failing the job; old catalog structure is never relinked.

## Attachment list and extract (work item 24b)

`--list-attachments` prints one `key -> obj,gen` line per embedded file
plus filename, description, MIME type, dates, checksum, and size detail
lines; `--show-attachment=key` (repeatable, last wins) extracts fully
decoded bytes, falling back to raw bytes with a warning where decoding is
unavailable. Both share the typed `ListAttachmentsAsync` /
`ExtractAttachmentAsync` engine over name-tree traversal with cycle, depth,
and work budgets: malformed tree skeletons fail explicitly while bad entries
are skipped with warnings, and unknown keys fail without echoing key bytes.
Intentional differences: extraction publishes to the declared output file
instead of stdout (no stdout binary contract exists); listing detail lines
use this profile's stable diagnostic format rather than `--verbose` gating;
keys compare as decoded text with exact ordinal match. Selection output still
drops `/Names`; attachment add/copy/remove follow separately.

## Attachment add and remove (work item 25a)

`--add-attachment` blocks (file plus `= `/space nested options, repeatable,
`--`-terminated) and repeatable `--remove-attachment=key` combine with page
selection, replacement, and split groups (every group carries the full set)
through one shared engine: removals first (unknown keys fail), then sequential
adds with per-add `--replace` handling and collected collision errors.
Existing attachments are preserved from the primary input and merged with
additions into a rebuilt sorted tree; output uses `/PageMode
/UseAttachments` on insertions. Intentional differences: unspecified dates
are omitted rather than stamped (no ambient clock reads); keys and names
encode as ASCII literals or UTF-16 hex; trees use flat leaves chunked past
64 entries without balancing.

## Attachment copy and typed editing surface (work item 25b)

Repeatable `--copy-attachments-from FILE [--prefix=] --` blocks copy
entries from other files through the same engine after removals and
additions: copied filespecs are rechained per source with dense numbering,
prefixed keys colliding as a group per source file, empty `--prefix=` values failing explicitly where upstream copies unchanged, and unknown sources
failing without echoing key bytes. Nested `--password` (argv) and job
`password` keys reject as crypto operations. The typed surface
(`QpdfAttachmentSpec`, `QpdfAttachmentCopy`, `Add/Remove/CopyAttachmentsAsync`)
shares the engine and reports the final attachment count without reopening
the output. Intentional differences: no ambient clock reads; trees stay
flat-chunked past 64 entries.

## PDF JSON inspection (work item 26a)

`--json[=1|2|latest]` with repeatable `--json-key` (plus job `json`/`jsonKey`
and typed `GetDocumentJsonAsync`) emits the upstream top-level layout with
stream data omitted: canonical v1/v2 name and string encodings (u:/b:/n:
forms, arbitrary-precision numbers with upstream real normalization),
sorted dictionary keys, per-key selection with version rules, page entries
with contents/labels/per-page outlines, number-tree page labels, recursive
outlines with page positions, attachment details with ISO-8601 dates, and a
constant unencrypted report for the encrypt key. Page entries list direct image
XObjects with dimensions, bits, colorspace, filter names, aligned decode parms
and level-gated filterable flags, matching the reference on raw, dimension-less,
photo and filtered fixtures (both decode levels). Intentional differences:
JSON requires a declared output file (no stdout contract);
named outline destinations do not resolve. The differential lane compares the pages key structurally against the oracle (version, object and contents references, page positions).

## PDF JSON stream modes and serialization output (work item 26b)

Repeatable `--json-object` selectors (trailer or obj[,gen], canonicalized)
filter the objects, objectinfo, and qpdf dictionaries across versions.
`--json-stream-data=inline` embeds base64 payloads (decoded per the decode
level with raw fallback and slimmed dictionaries); `=file` publishes sidecar
outputs named after the output file or `--json-stream-prefix` and records
`datafile` paths. `--json-output[=2|latest]` implies JSON with inline
payloads, decode level none, the qpdf key by default, and no version or
parameters keys. The `acroform` key reports field type inheritance, qualified
names, values, choices, and widget annotations. Intentional differences: JSON
requires a declared output file (no stdout contract); version 1 ignores
stream payloads; `--json-output` with version 1 fails.

## PDF JSON import (work item 27a)

`--json-input` (plus job `jsonInput` and typed `CreatePdfFromJsonAsync`)
converts qpdf JSON version 2 back to PDF: the header version sets the output
version (with min/force overrides), stream dictionaries keep their semantics
with recomputed lengths, and objects are renumbered densely with dangling
references nulled. Import validates structure strictly (required qpdf pair,
versions, object keys, value/stream exclusivity, payload presence, base64 and
hex grammars, name escapes) while ignoring unknown keys for forward
compatibility. Inline base64 payloads are memory-admitted against the text-length
bound before decoding. Intentional differences: JSON input requires a declared output
file; object generations must be 0; stream and metadata writer options cannot
combine; PDF update from JSON is a supported companion path (work item 27b).

## PDF JSON update (work item 27b)

`--update-from-json` (plus job `updateFromJson` and typed
`UpdatePdfFromJsonAsync`) merges qpdf JSON into an existing PDF: every
existing object stays addressable while JSON entries replace or add objects
by number with the same value grammar as creation. Existing streams keep
their bytes when neither data nor datafile is given; new streams still
require exactly one. The trailer is replaced only when the JSON carries one,
the JSON pdfversion overrides the output version when present, and the input
document identifier is preserved. A `pushedinheritedpageresources` value of
true is rejected. Intentional differences mirror creation: generations must
be 0, stream and metadata writer options cannot combine, and outputs are
renumbered classic files.

## Content tokenizing (work item 28)

An internal content-stream tokenizer covers operators, numbers, strings,
names, arrays, dictionaries, and inline images with upstream-compatible
boundary rules: image data ends at a delimiter-surrounded EI validated by a
ten-token lookahead, falling back to the last candidate, with the separator
before EI included in the payload. No command surface changes yet; page
content checks, image reporting, and appearance work build on this next.

## Page content operations (work item 29)

`--coalesce-contents`, `--normalize-content=y/n`, and
`--remove-unreferenced-resources=auto/yes/no` (plus job keys) share one
engine over copied pages: coalescing joins decoded streams, normalization
rewrites newlines and recodes strings/names with warnings on damage, and
pruning drops unused font and XObject entries with form recursion and
unresolved-name guards. Intentional differences: content flags combine only
with page operations and whole-file output, never with inspection; damaged
content keeps original bytes where upstream writes partial output. The differential lane compares
coalesced bytes exactly and prunes an unreferenced /F2 under page operations with byte-identical
decoded payloads; pruning engages under page operations (including --split-pages) while whole-file
output without page selection keeps resources, matching the upstream page-spec gating. Bare `--preserve-unreferenced-resources` (plus job key) is a synonym for `--remove-unreferenced-resources=no` with last-wins precedence on conflict and no valued form.

## Inline image externalization (work item 30a)

--externalize-inline-images with --ii-min-bytes (default 1024, plus job keys) mirrors the upstream tracker: threshold on image bytes, abbreviation expansion, device color mapping with resource lookup and warn-and-keep on miss, unique /IIm naming, byte-identical kept images, and Flate-aware page and indirect-form rewriting before other content ops. Replaced content and resource identities drop from the pool when nothing references them anymore (shared streams and dictionaries stay), and the writers emit free cross-reference entries for the resulting holes. The differential lane externalizes a shared inline-image fixture on both sides with matching /IIm1 content. Intentional differences: direct form XObjects stay untouched, content flags combine only with page operations and whole-file output, and damaged content keeps original bytes with a warning.

## Overlay and underlay (work item 30b)

Repeatable --overlay and --underlay blocks (--file=file or positional file, --to=, --from=, --repeat= with empty allowed, plus job keys) mirror upstream stacking: underlays in order, then the original page as /Fx0, then overlays in order, each placed from TrimBox fallbacks with centered shrink-to-fit matrices that invert destination rotation and scale. Page ranges use the full shared grammar (spans, descending ranges, `rN`/`z`, exclusions, `:odd`/`:even`, duplicates preserved), with unused overlay pages left unembedded on both sides. Block options require the `=` form like the reference (space-separated values fail on both sides with exit 2), and empty ranges select nothing on both sides. Each applied overlay page also contributes fresh per-placement annotation clones appended to the destination `/Annots`: clone rectangles move with the same placement matrix (missing or malformed rectangles behave as all-zeros), appearance-stream matrices concatenate with it (missing matrices stay absent under identity placements; malformed matrices behave as all-zeros), page-boundary references prune to null, and non-dictionary entries are skipped with a warning, all matching the reference. Widget annotations additionally import their top-level fields through the shared form machinery (work item 121): parent-link top lookup with indirect field identity, one fresh field copy per placement shared across radio siblings, per-placement default-resource copying with destination-wins conflict renaming, document default-appearance and quadding overrides, field default-resource reset with conflict-map appearance rewriting, merge collision renaming, and NeedAppearances propagation, all matching the reference. Widgets without an importable field (direct annotations, formless sources, bare tops) fall back to plain clones, and lookup notes stay informational so exits stay 0, both matching the reference. Intentional differences: inherited /UserUnit on output pages is not inverted; damaged overlay content warns and skips instead of emitting partial forms; a direct-dictionary field parent that the reference fails on (exit 2) stays a plain clone with a note (exit 0); cyclic field parents import with a warning and a valid structure where the reference emits degenerate names; appearance-stream resource remapping stays deferred with rendering preserved through appearance-local resources. The differential lane overlays widget fixtures on both sides with trio validation, matching field registration, values, page placement, parent links, NeedAppearances, fallback notes, collision renames, and DR conflict rewrites, plus a repeated-placement rename case.

## Rotation flattening and page-content checks (work item 31a)

--flatten-rotation (plus job key) mirrors upstream page baking: boxes rotate about the media box per 90/180/270 degrees, a counter-transform wraps the content streams, /Rotate is dropped, and annotation rectangles move with the page. Appearance form XObjects referenced from annotation /AP dictionaries (including /N, /R, /D state dictionaries) gain the same rotation matrix composed over any existing matrix, while direct-stream appearances stay untouched as upstream leaves them. Direct /UserUnit entries survive on flattened pages while inherited units stay uninherited on both sides. Pages without actionable rotation stay byte-identical. The page-content check now tokenizes page content streams (honoring the check decode level) instead of reporting skipped. Intentional differences: damaged pages warn and stay as is, and form-field restructuring is left to later appearance work. The differential lane flattens a checked-in /Rotate 90 page on both sides with trio validation, a swapped MediaBox, a dropped /Rotate, and byte-identical decoded wrap streams, plus an appearance-bearing rotated page with matching appearance matrices and kept user units on both sides.

## Annotation flattening (work item 31b)

--flatten-annotations with ll, print, or screen (plus job key) mirrors upstream stamping: appearance streams become /Fxo form XObjects placed by annotation-derived matrices with flag filtering and page-rotation counter-rotation, and flattened annotations leave /Annots. Widgets defer to generated appearances when the document needs them, and appearance-less annotations drop only when they carry an appearance dictionary. Appearances referencing AcroForm default-resource fonts stamp verbatim with the DR font intact, matching the reference (flatten-dr lane case). Intentional differences: indirect damage keeps originals with a warning instead of failing.

## Appearance generation (work item 31c)

--generate-appearances (plus job key generateAppearances) mirrors upstream generation order (generate, then flatten-annotations, then flatten-rotation): when the catalog needs appearances it regenerates text/list appearance streams from field values and default appearance (ASCII fallback, WinAnsi/MacRoman encodings resolved through appearance resources, then AcroForm default resources (field-level /DR ignored like the reference; page /Resources never consulted; missing DR fonts merged into kept appearance resources), BMC/EMC value rewrite, list highlight) and synchronizes checkbox /V and /AS state (radio buttons keep both states with a warning, matching the reference exit 3; quadding is ignored in generated placement on both sides, matching the reference), then clears the condition so a later --flatten-annotations stamps fresh appearances. `--remove-acroform` (plus job key `removeAcroform`) is the explicit form prune switch threaded through every publication path; the differential lane checks
both sides drop the dictionary with trios green. `--remove-structure` (plus job key
`removeStructure`) is the explicit structure prune switch with the same coverage. Appearances are rewritten raw (no /Filter). Intentional differences: field context resolves through preserved /Parent links with subtree search as fallback and parent-held radios degrade to skip; widgets unreachable from the traversed form generate only when the output form keeps field registrations, with whole-file paths warning per unreached widget (exit 3) and selection paths skipping silently while dropping the emptied form entirely (generate-unreachable lane case); registration-free forms stay registration-free on every path, and the generated flag clears once generation has run; malformed parent links (a /Kids entry without a matching /Parent back-link) stay preserved silently on copy paths where the reference corrects and warns (exit 3, tested difference), while `--check` warns on both sides with exit 3 (unit evidence plus the forms-check lane case); wider resource lookup (page resources, conflicting dictionaries) stays explicitly open; signature fields are skipped and pushbuttons warn; damaged appearance streams warn and keep the field.

## Governed JPEG decoding (work item 32)

DCTDecode executes at decode level `all` through a governed ImageSharp 3.1.12 adapter: frame dimensions and component counts are read from the JPEG header before any pixel buffer is allocated, work and memory budgets are charged up front, and 1-component (grayscale) and 3-component images decode to raw sample bytes with the filter removed. Intentional differences: four-component CMYK/YCCK JPEGs stay opaque with an `UnsupportedFeature` warning (final decision recorded under codec feasibility above: the pinned assembly offers no CMYK pixel type or raw-component decoder surface; the `cmyk` differential pins oracle-0/managed-3 exits with recorded oracle samples); lower decode levels keep DCT streams opaque silently; corrupt JPEG data warns and stays opaque. Content and overlay paths still gate on `specialized` and never decode images implicitly.

## Image optimization (work item 33)

--optimize-images (plus job key optimizeImages) recompresses eligible image XObjects with DCT exactly when the JPEG is strictly smaller: 8 bits per component with /DeviceGray or /DeviceRGB, width, height, and area strictly above --oi-min-width/--oi-min-height/--oi-min-area (defaults 128/128/16384, 0 disables), and already-DCT images left alone unless --jpeg-quality (0..100, mapped 0 to 1, default 75) forces recompression. Replacement keeps every dictionary key except /Filter//DecodeParms, which become /DCTDecode, so /SMask references and /Mask arrays survive recompression. Recursion follows form XObjects with a cycle guard and shared images stay consistent. Intentional differences: four-component CMYK has no raw-sample encode path and stays as is; 1-bit image masks and colorspaces outside /DeviceGray//DeviceRGB (including /Indexed, /CalGray, and /Separation) are skipped; every skip is silent; large inline images externalize first (unless --keep-inline-images) and join the same eligibility rules, with the conventional pre-EI separator trimmed before sample validation. The differential lane optimizes a shared noisy photo on both sides (matching DCT dimensions and colorspaces at an explicit quality), proves byte-identical kept payloads under disabling thresholds, and keeps inline images inline with `--keep-inline-images` on both sides, plus a masked-image case proving DCT recompression with the /SMask reference intact and the mask stream left flate on both sides.

## Inline optimization and codec hardening (work item 34)

Image optimization covers externalized inline images under the same shrink-only rules, and the filter layer gained adversarial cases: truncated JPEGs and two-component frames stay opaque with a warning, chained Flate+DCT decodes with --recompress-flate, dimension mismatches skip silently, and encode budgets fail explicitly. Attachment extraction now decodes DCT streams, and the streams check passes valid JPEGs at decode level all. Authored progressive JPEG fixtures now verify decoded samples and allocation ownership; the pinned encoder still does not emit progressive JPEG. Four-component CMYK/YCCK stays opaque by the recorded decision (genuine four-component inputs covered by unit tests and the `cmyk` differential).

## QDF editable view (work item 35)

--qdf (plus job key qdf) with --no-original-object-ids (plus job key
noOriginalObjectIds) emits the editable QDF view: a binary marker plus
%QDF-1.0, per-object original-ID and page/contents comments, sorted keys with
indented values, uncompressed streams framed with indirect lengths in the
following object and ignore_newline tolerance, and a classic cross-reference
table. QDF implies generalized decoding with content normalization unless
explicitly overridden (--normalize-content=n keeps newlines,
--compress-streams=y restores Flate, --decode-level=none still normalizes).
--object-streams=generate packs eligible objects into unfiltered object
streams with per-member comments, upstream-style /First and offsets landing
on values, and an unfiltered cross-reference stream (/W [ 1 f1 f2 ]).
Intentional differences: generated grouping follows writer packing rules
(catalog/pages stay direct) rather than upstream packing; page /Parent links
stay dropped per catalog policy; objects without a source carry no original-ID
comment. The differential lane covers a two-page QDF object-stream case
(oracle trio, marker agreement, upstream fix-qdf round trip green). The
fix-qdf and zlib-flate companions remain later work items.

## Managed fix-qdf repair (work item 36)

The typed `FixQdfAsync` companion repairs edited QDF through the shared governor and staged host publication. Like upstream fix-qdf it recomputes object-stream `/N`, `/W` and `/First` with renumbered member tables, every stream length (honoring `ignore_newline`), classic cross-reference tables with trailer `/Size`, unfiltered cross-reference streams, and startxref offsets, while enforcing sequential object numbering. Classic QDF stream lengths now pair through renumbering so multi-stream files keep their length objects. Intentional differences: both paths are explicit host paths with no standard input or output default; carriage returns, empty inputs, truncated structures, and non-sequential objects fail explicitly instead of echoing degenerate output. The fix-qdf and zlib-flate companions stay outside the main flag inventory.

## Managed zlib-flate facade (work item 37)

The typed `RunZlibFlateAsync` companion compresses raw bytes or uncompresses zlib-wrapped bytes between explicit host paths through the governed Flate pipeline: strict header, framing, and Adler-32 checks on inflate with per-chunk memory budgets, and level buckets 1..9 (1 fastest, 9 smallest, default optimal) on deflate. Modes and out-of-range levels fail explicitly without publishing output. Intentional differences: truncated input fails instead of exiting 3 with partial output; compression level 0 and values above 9 fail instead of passing through to zlib; there is no Zopfli support and no host capability supplies `QPDF_ZOPFLI`, which stays treated as unset. The differential lane round-trips both directions against oracle zlib-flate, checks corrupt-input exit parity, and asserts level ordering. The companions stay outside the main flag inventory.

## Linearization checking (work item 38)

The linearization check detects the parameter dictionary in the first bytes, validates its key types/bounds and page count, resolves raw or Flate hint streams by actual cross-reference offsets, and parses the page-offset, shared-object and outline tables under file-bounded counts. Physical validation uses the shared reader's parsed object ends and shared graph classification with actual compressed-container owners: /O first-page identity, /E end interval, /T main-xref boundary, primary/overflow hint spans, page runs/counts/lengths, complete shared group members/identifiers, and outline identity/count/offset/length. Integer /P, deprecated content fields and unused first-shared metadata retain the pinned checker's ignored semantics. A mismatched /L reads as not linearized, matching upstream. Defects fail explicitly (exit 2) where upstream warns (exit 3); show-linearization retains parsed raw fields on physical failures. Frozen ordinary fixtures cover all eight old form false passes, corrected/native controls, independent targeted mutations, multi-object shared groups and alternate valid physical boundaries. All original 64 differing hint pairs remain byte-for-byte comparisons; both layouts pass public check/show routes. Hint-data allocation/alias ownership and broader checker combinations remain under audit.

## Linearized writing (work item 39)

`--linearize` (plus job key `linearize`) emits linearized web-optimized output through a two-pass writer over the shared selection engine: objects are classified into upstream-style parts (catalog in part 4, first page with its private then shared objects in part 6, later page-plus-private runs in part 7, remaining shared objects in part 8, pages tree plus unreached objects in part 9), renumbered so the second half sorts low, counted with placeholder dictionaries and zeroed cross-references, then emitted with generated page-offset/shared-object hint tables and padded reservation fields (200-space linearization dictionary, 21-wide `/Prev`) under a replay check that the emitting pass matches the counting pass plus the hint length. Hints Flate-encode only when the resolved stream-compression request enables compression (`--stream-data=compress`); `/ID` defaults to a deterministic content hash with `--static-id` selecting the upstream test constant. `--object-streams=generate` (and `preserve` over compressed inputs) publishes streamed linearized output: eligible objects pack into Flate object streams (pages and the catalog stay direct), members resolve to their container during part classification with page-shared containers winning placement, renumbering sorts uncompressed units low per half with container members numbered after their half, and raw cross-reference streams replace both classic tables under the same two-pass replay check. QDF is silently disabled, as do `--json`, JSON import, attachment inspection and `--check` combinations fail explicitly. Intentional differences from the oracle layout: cross-reference streams stay unfiltered (matching the QDF xref-stream profile) where upstream Flate-encodes with a PNG predictor; the outline hint table is omitted whenever outline objects compress (as observed upstream); member numbering follows container encounter order rather than upstream packing. Outlines ride the selection into the output catalog (first page when the catalog requests outline mode, otherwise closing part 9) with a generated outline hint table validated by offset cross-checks; outline destinations into non-primary merge inputs prune to null under the per-source copy policy. `--linearize-pass1` captures the counting pass, and `--check-linearization` and `--show-linearization` inspect through the shared engine. The differential lane compares managed and oracle linearized outputs on one- and two-page selections with oracle trios green on both sides plus independent page-count agreement. Parent-field/default-resource handling, CMYK/YCCK raw samples, and codec allocation/cancellation accounting stay explicitly open.

## Exclusions and differences

Passwords, encryption/decryption, encryption inspection, cryptographic options,
permission enforcement, signing and signature validation are out of scope.
Reject encrypted input structurally; do not prompt for or read passwords.
`--remove-restrictions` is excluded. Every password, permission, inspection, and
restriction flag fails explicitly with `UnsupportedCryptoOperation` (exit 2) across argv,
job JSON (top-level, pages, overlay/underlay, attachment-copy, and encrypt-block keys),
and encrypted file inputs, publishing no output; all 49 exclusion entries carry negative
evidence. Ordinary document IDs, deterministic IDs
and non-secret file checksums remain part of structural processing.

Rendering, OCR, text extraction/layout and general document authoring are not
project goals. Do not claim PDF/A, PDF/X or PDF/UA conformance from a rewrite.

There is no vendored code, qpdf/libqpdf binding or upstream runtime dependency.
The differential runner invokes the pinned qpdf executable as a development
oracle only. It remains separate from production and ordinary tests.

The target reference build has JPEG support and no optional Zopfli support.
`--zopfli` will follow that build profile's explicit unavailable behavior;
ordinary Flate compression remains required. Host limits remain enforceable
even when a caller disables qpdf-compatible default parser limits. A shared budget-enforcement suite proves every public entry path
(selection, inspection, content transforms, overlays, linearization, QDF, split, rotation,
appearances) fails explicitly with `BudgetExceeded` and publishes nothing under exhausted budgets.
Every public entry path constructs its governor with the caller's cancellation token,
so interval checks and codec entry/row checks observe cancellation instead of running blind.
Every input open and staged-output creation charges one host operation and one open handle
before touching the host, so per-job handle budgets bind concurrent leases (including
overlay second sources and linearize pass-one outputs) even against permissive hosts;
reads through a governed lease charge one host operation per underlying host call, so
one-byte short reads cannot bypass the budget; admission runs before acquisition and
ownership starts at acquisition, so budget failures never leak leases; response inputs
are admitted against the memory budget before any byte is fetched; duplicate opens of
one path keep the publication conflict until the last lease closes.

## Progression and dependency gates

| Phase | Intended result |
| --- | --- |
| M0 | Build/package baseline, inventory, reference runner and dependency probes. Scaffold, runners, fixtures and JPEG/Flate probes are present; a real-executable smoke remains. |
| M1 | Minimal strict classic-xref read/select/write path, shared typed/argv/job entry points and a real package consumer. |
| M2 | Strict reader breadth: structural filters, xref/object streams and incremental/hybrid inputs. |
| M3 | Complete bounded selector: first/last and explicit ranges, source mapping, duplicates, inheritance and retained page structures. |
| M4 | Inspection, bounded repair and invocation infrastructure. |
| M5 | Cross-document import, merge/collate/split, rotation/labels and output transactions. |
| M6 | Writer controls, metadata/attachments and PDF JSON inspection/import/update. |
| M7 | Content parsing, resource analysis, overlays and limited appearance/flattening operations. |
| M8 | Governed ImageSharp JPEG processing and image optimization. |
| M9 | QDF and managed companion facades. |
| M10 | Linearization inspection/checking, then two-pass writing. |
| M11 | Resolve remaining gaps and complete the compatibility/release audit. |

Early CLI page jobs use explicit strict input, supported writer settings and
disabled resource pruning. Defaults are not silently changed. General imports
depend on form/widget identity; default page-resource pruning depends on
content analysis. Full `--check` also depends on content, JPEG and linearization
checks. Keep those entries partial until their dependencies are validated.

Each implementation step includes limits, cancellation and failure behavior.
Milestones need reproducible evidence, not just accepted option names. A known
gap can permit independent later work, but cannot be declared complete.

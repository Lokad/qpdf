# Compatibility

Overlay and underlay widget imports now follow field parents up to the configured
`MaxNestingDepth`, counting parent edges from the widget. The former silent
32-parent cutoff could truncate qualified names and lose inherited field types.
Exceeding the configured limit now fails explicitly and preserves existing output;
cycle detection and malformed-parent fallbacks remain. Other graph/container
nesting checks still apply independently. Public signatures are unchanged.

Lokad.qpdf targets qpdf **12.4.2** for non-cryptographic PDF structural operations
through typed, argv and job-JSON entry points. Completion means delivering the
agreed [supported profile](SUPPORTED-PROFILE.md), including the limitations below.
Other required compatibility mismatches need a correction or an explicit scope decision.

## Limitations that affect callers

Attachment extraction searches for the requested key without building a full
listing or decoding presentation metadata. Unrelated damaged file specs no longer
consume diagnostic quotas, and shared descriptions no longer multiply extraction
memory. Selected descriptions, MIME/date/checksum parameters and filenames are
not needed to extract the embedded stream; malformed optional metadata does not
hide that payload. Relevant tree, file-spec and stream errors still fail, with
selected malformed specs now reporting their cause directly. Listing and JSON
metadata behavior, decode fallback, budgets and publication guarantees remain.
Public signatures are unchanged.

Attachment preservation and foreign copying roll back mappings, descendants and
allocator slots when an attachment is skipped. Earlier shared streams remain
available to later attachments. Previously a later alias could reuse an unfinished
mapping, publish a dangling name-tree reference and inflate the returned count;
linearization could fail on the missing object. Unsupported structural references
still skip the attachment with a warning, where native qpdf may preserve it.
This retains the contained-copy policy while correcting cleanup and counts.

JSON stream sidecar names now reject malformed UTF-16, NUL and paths longer
than the logical path limit, including the generated object-number suffix,
before copying the payload or creating output transactions. Valid Unicode names
retain their exact host identity. Previously typed/argv export could succeed
with a JSON filename that differed from the published sidecar, and overlong
generated names could escape command execution as `ArgumentException`.
Invalid generated names now raise `QpdfParseException` in typed export and yield
a structured failed command result. Mixed job-file argv also rejects malformed
UTF-16 before JSON serialization can replace it. Public signatures are unchanged.

Split outputs now advance the carried page-label start when a group begins
inside a numbering run. For example, a six-page run starting at 5, split in
pairs, starts at 5, 7 and 9 rather than restarting at 5 in every file. This
corrects typed, argv and job JSON output to agree with native qpdf splitting a
whole document. Selected and reordered pages retain their source labels; this
intentionally differs from native 12.4.2's combined selection-and-split command,
which applies the original label positions. Selecting and then splitting with
native qpdf supplies the reference control for these cases. Label styles,
prefixes and boundary-aligned entries retain their values; signatures are
unchanged. Split label lookup now searches the sorted breakpoints instead of
rescanning every preceding entry for every output group.
Continued labels receive memory admission before construction; tight jobs can
refuse earlier than when the incorrect original dictionary was reused.
Indirect label values retain their direct-equivalent semantics. The reference
lane uses a direct control where native 12.4.2 fails copying indirect labels.

Attachment listings and JSON now report semantic MIME subtype text: a literal
`#` remains `#`, so adding an attachment from listed metadata preserves its MIME
type. Names are decoded as UTF-8 without text-string BOM handling; malformed
sequences use replacement characters. MIME creation still requires ASCII.
AcroForm JSON field types and appearance states now use the same descriptor-name
writer as page-image resources, preserving hashes, spaces, delimiters and UTF-8
in both JSON versions. Missing or non-name descriptors remain empty strings.
Invalid UTF-8 descriptor names use the existing reversible escaped-name spelling
with an `n:` prefix, intentionally differing from native's invalid-UTF-8 JSON.
These are public value corrections; signatures and diagnostic display escaping
are unchanged.

Unreferenced-object preservation reuses objects already copied through an earlier
root, retaining shared references and cycles without duplicate stream payloads.
Failed candidates retire their new mappings and copied descendants before
continuing, so skipped objects no longer leave split output with unresolved IDs.
Candidate storage and copy-table growth receive coarse memory admission; tight
jobs can now refuse earlier. Ordinary output omits the unused preservation-ID
set; split output keeps its existing retained roots. Managed split preservation
intentionally keeps unreferenced objects in every group, while native qpdf drops
them. Contained-copy exclusions, aggregate warnings, cancellation and publication
rollback remain; public signatures are unchanged. Strict loading errors remain
fatal; repair mode and recoverable contained-copy failures keep their policies.

Overlay and underlay widget imports resolve indirect form `/DA`, `/Q` and
`/NeedAppearances` defaults, and compare imported defaults against resolved
destination values. Equivalent direct and indirect defaults now preserve the
same effective appearance, alignment and refresh flag. Field-local overrides
retain precedence. Previously indirect source defaults silently fell back to
empty appearance, left alignment and false. Malformed-value fallbacks, resource
renaming, independent placements and output rollback remain; public signatures
are unchanged. These corrected semantics agree with native qpdf.

Appearance generation and annotation flattening resolve indirect AcroForm
`/NeedAppearances` booleans before choosing their behavior. Indirect true now
refreshes appearances when requested and otherwise keeps widgets with the same
warning as direct true. Previously generation could silently skip, and flattening
could discard current field values while retaining stale appearances. Merged
forms also include indirect flags in their existing OR rule. Missing, null and
mistyped flags keep their fallback; reference bounds, cancellation, admission and
transactional output remain binding. Public signatures are unchanged.

Overlay and underlay copying validates source object-generation pairs before
reusing placement copies and retains the original field reference through parent
walks. Previously a live reference copied first could cause a later stale pair to
become live, and synthesizing a generation-zero field reference could lose valid
nonzero-generation AcroForm registration. These corrected outputs agree with
native qpdf. Sharing within a placement, separate placement identities, parent
bounds, cycle handling and output rollback remain; public signatures are unchanged.

Ordinary rotation, rotation flattening and annotation flattening resolve indirect
`/Rotate` integers, including inherited values, from the copied object pool.
Previously ordinary rotation rejected these valid inputs, rotation flattening
silently skipped them, and `NoRotate` annotations could be misplaced. Equivalent
direct and indirect values now use the same normalization and transforms. Null
and malformed-value policies, reference bounds, cancellation and publication
rollback remain; public signatures are unchanged. This intentionally corrects
input acceptance and affected output.

Argv option recognition borrows names instead of copying arbitrary caller text.
Unknown-option, numeric-value, rotation, page-label and unexpected-argument
syntax errors clip echoed text to at most 128 UTF-16 units plus `...`, without
splitting a surrogate pair. This intentionally
shortens large error messages before formatting; short errors, argument positions,
crypto rejection and error codes are unchanged. A useful terminal failure remains
available outside ordinary diagnostic quotas, including after memory refusal.
Public signatures and output publication guarantees are unchanged.

Host input/output acquisition creates its wrapper before calling the host and
returns completed host operations directly. This closes an allocation-failure
window that could leave a returned raw lease undisposed, including on first use.
Actual `OutOfMemoryException` propagation, publication/rollback, resource limits
and public signatures are unchanged.

Typed `Qpdf.CheckAsync` rejects undefined `QpdfCheckOptions.DecodeLevel` values
with `ArgumentOutOfRangeException` before accessing the host or its memory budget.
Previously those values could skip decoding corrupt streams and return a passing
partial check. The supported `None`, `Generalized`, `Specialized` and `All` levels,
partial-check reporting and public signatures are unchanged.

Attachment name-tree traversal resolves indirect `/Names` and `/Kids` arrays
and string keys before checking their types. Equivalent direct and indirect
trees now work in listing, extraction, both attachment JSON versions, copying
and whole-file rewriting. Previously the indirect forms failed as malformed
input. Missing references, malformed values, cycles, nesting and resource limits
still fail before publication. Public signatures and output rollback are unchanged.

`QpdfJpegCodecContext.ResizeMemory` lets caller-supplied codecs grow or shrink
their existing per-call reservations. Growth is admitted before allocation and
checks cancellation after budget callbacks; refusal or cancellation preserves the
prior estimate. Initial reservations also check cancellation after admission.
Shrink and disposal can finish after cancellation. Reservations still expire
with the codec call, and codecs remain responsible for actual storage cleanup.
Existing codec signatures and the dependency-free runtime contract are unchanged.

PDF JSON admission and DOM parsing share a finite syntax ceiling that covers the
supported PDF nesting ceiling plus the JSON envelopes. The former fixed 128
syntax cap rejected valid exports with 124 nested arrays in a catalog value.
Create/update now accept those round trips while the configured PDF model depth,
memory, byte and cancellation limits remain binding. Stream and trailer envelopes
are included; job-configuration depth and public signatures are unchanged.

PDF JSON creation and update reject invalid Unicode strings and property names
with parser diagnostics. Previously unpaired surrogate escapes in values, names
or metadata could escape as `InvalidOperationException` from typed and execution
entry points. Valid surrogate pairs, host faults, cancellation, admission/OOM
failures and output rollback retain their existing behavior. Public signatures
are unchanged; malformed text now follows ordinary input-error outcomes.

Linearization resolves an indirect `/PageMode /UseOutlines` before placing
outlines in the first-page section. Previously writing could report success with
incorrect section boundaries and hints; the shared checker accepted those files
and rejected valid native output. Checking now distinguishes those layouts.
Direct names, unrelated or malformed modes and public signatures are unchanged.

Linearization checking and reporting treat `/P null` as the absent optional
first-page field, using its zero default. Previously typed checking and argv/job
check/report requests rejected these valid files. Non-null type checks and
structural/hint validation remain; public signatures and integer handling are
unchanged. This intentionally corrects input acceptance and diagnostics.

Recoverably skipped catalog graphs discard new mappings, descendants and
allocator slots from the failed attempt. Earlier shared copies remain intact.
Previously skipped outlines, structure, extras or name categories could leave
references to missing objects and make later linearization fail. The documented
policy of dropping unsupported catalog graphs remains; cancellation, admission
refusal and host failures still propagate. Public signatures are unchanged.

Catalog-preserving rewrites and selections retain nonattachment `/Names`
categories, including named destinations, alongside rebuilt embedded-file names.
Previously the name root was dropped or replaced by `/EmbeddedFiles`, breaking
named links, bookmarks and open actions without a warning. Attachment edits now
keep unrelated name categories as well. Primary-source, omitted-page pruning,
contained-preview and split policies remain; ordinary page selection still omits
attachments unless edits request them. Output changes intentionally; public
signatures and named-destination JSON inspection limitations are unchanged.

DCT decoding supports absent or null `DecodeParms` only. Dictionaries, including
empty and indirect dictionaries, are unsupported. Rewriting and image optimization
preserve their encoded bytes and parameters; explicit filtered-data requests fail
without invoking the JPEG codec. Previously these parameters were silently ignored
and discarded, which could change image colors. Decode-all rewriting warns and
checking reports unavailable decoding as skipped. This intentionally changes
acceptance, diagnostics and affected output. JSON filterability and inline stream
data use the same boundary. Public codec signatures are unchanged.

Annotation flattening resolves indirect numeric appearance-matrix coefficients
before fitting the Form into its rectangle. These values no longer trigger a
silent identity fallback and incorrect placement. Malformed-matrix fallback and
non-finite rejection remain; argv and job JSON share the corrected output. Public
signatures are unchanged.

Annotation rectangles are normalized before flattening placement and `NoRotate`
arithmetic. Reversing either or both pairs of corners no longer mirrors or rotates
the appearance relative to the canonical rectangle. This intentionally changes
affected argv/job output; zero-extent and non-finite policies are unchanged.

The built-in memory host observes cancellation after publication admission,
including empty outputs. If a memory-budget callback cancels the token before
returning an accepted receipt, commit now throws instead of replacing the prior
target. Attachment extraction shares this behavior through typed, argv and job
entry points. The uncommitted transaction remains available for retry or discard;
public signatures and successful empty-output publication are unchanged.

Budgeted ranged reads observe cancellation after assembly admission and the final
reservation shrink, before returning caller-owned bytes. A callback that cancels
during the shrink now causes cancellation and refunds the read reservation instead
of returning a charged result. Assembly also checks the engine's independent
cancellation token before allocating its destination and after refunding overlap.
Input leases stay caller-owned; public signatures and successful result ownership
are unchanged.

Mixed argv with `--job-json-file` now shares ordinary managed parameter parsing
for JSON versions, split sizes, compression choices and keep-files-open hints.
Separate JSON/split values and bare compression retain their managed grammar;
explicitly empty JSON choices and separated keep-files-open values reject before
publication. Adding an empty configuration file no longer changes these forms.
Ordered file setters, collation/positional handling and layer rules remain.
The existing managed syntax extensions are not claims of native support. Public
signatures are unchanged; affected mixed invocations change acceptance/output.

Page-image JSON preserves raw `/Width`, `/Height` and `/BitsPerComponent` values,
including references and malformed values, instead of dereferencing integers or
replacing other types with null. Missing/null values remain null. Text image
listings retain numeric dimension lookup. Image lists now use native unsigned
raw-name byte order, rather than escaped display-name order. These are intentional
output changes in both JSON versions and all entry points; public signatures are
unchanged.
Text image listings retain their existing PDF display-name byte escapes (for
example `#20` for a space), while JSON names follow the policy below.

Page-image JSON preserves UTF-8 resource names in both versions, including
literal delimiters; it no longer replaces non-ASCII bytes with question marks.
Invalid UTF-8 names use the existing `n:` prefix followed by an escaped PDF name
in both versions. This intentionally differs from native's invalid-UTF-8 JSON
for those bytes and keeps managed JSON valid. Resource-name output changes; public
signatures are unchanged.

Page-image JSON and `--show-pages --with-images` resolve indirect `/Subtype`
names before identifying images. Valid indirect `/Image` names now appear in
both reports; missing, stale or unusable subtype values remain excluded. Lookup
uses the reader's resolution bounds and independent cancellation checks. Public
signatures are unchanged.

Outline JSON validates the effective generation of destination page references.
Missing, freed or stale references now produce a null first destination element
and no page association instead of targeting a live page with the same number.
Valid nonzero generations remain supported; live non-page targets retain their
reference without acquiring a page position. Normalization uses admitted temporary
storage and leaves source arrays unchanged. Public signatures are unchanged.

Outline JSON includes explicit local `/GoTo` destinations from `/A`, including
indirect action dictionaries, kinds and destination arrays. Non-null `/Dest`
values retain precedence; absent, null or unresolved `/Dest` can fall back to the
action. Destination positions and per-page bookmark lists now include these
links. URI and remote actions remain unchanged; named destinations remain
unresolved. Both JSON versions share this behavior; public signatures are unchanged.

Outline JSON resolves indirect `/Title` and `/Count` values before interpreting
them. Titles and closed-branch state now match direct values in both JSON versions
and all entry points. Missing or unusable optional values retain their defaults;
decoding admission and cancellation remain binding. Public signatures are unchanged.

Overlay and underlay placement normalize accepted signed 64-bit rotations before
inverting them. Large negative values equivalent to a cardinal angle now produce
the same transform as that angle instead of silently overflowing to identity.
This follows managed rotation normalization; native qpdf warns and clamps large
destination integers to its 32-bit range. Public signatures are unchanged.

PDF text construction uses UTF-16 when a PDFDocEncoding prefix would resemble
a Unicode byte-order mark. JSON imports, page-label prefixes and colliding
form-field renames now preserve leading `þÿ`, `ÿþ` and `ï»¿` text instead of
losing or reinterpreting it.
Other encodings and public signatures are unchanged; affected PDF bytes and
memory estimates change, with admission before allocation.

Page-label prefixes and attachment keys, filenames and descriptions preserve
Unicode controls whose ASCII byte positions are repurposed or undefined in
PDFDocEncoding. These values now use UTF-16 instead of becoming accents or
replacement characters. Ordinary ASCII, label PDFDocEncoding, attachment
non-ASCII UTF-16 and malformed-surrogate handling retain their encoding choices.
Affected bytes and memory estimates change; public signatures are unchanged.

Form checking uses the configured nesting ceiling instead of silently stopping
at depth 32. Deeper supported hierarchies now receive the same parent-link checks;
exceeding the ceiling produces an explicit failed budget outcome and stops later
inspections. Existing warn-only structural findings, cycle/alias guards and
unresolvable-branch handling remain. Public signatures are unchanged.

Page-content checking now fails malformed content collections and actual decode
errors instead of marking unexamined content passed. Unavailable or partially
inspected content reports a skipped stage; intentional decode-level exclusions
are informational, while unavailable decoding warns. Empty/null pages and undefined
outer `/Contents` references remain passing controls. These status/diagnostic and
exit changes can stop following inspections and withhold their binary payloads.
Memory refusal and cancellation still propagate; public signatures are unchanged.

Attachment JSON filename/stream maps now use PDF name keys (`/F`, `/UF`, `/Unix`,
`/DOS`, `/Mac`) in both versions and all entry points, matching qpdf. Previously
the leading slash was omitted. This intentionally changes the JSON shape; clients
must use the corrected keys. No duplicate aliases or public signatures are added.
Creation and modification dates remain independent.

Attachment metadata resolves indirect scalar values before checking their types,
including filename maps and alternate embedded streams in JSON. Previously valid
references silently lost filenames, descriptions, MIME types, dates, checksums and
declared sizes. Missing/null/wrong-type optional values remain omitted; resolver
failures retain existing attachment/inspection policies. Refusal and cancellation
propagate, and returned metadata keeps its caller ownership. Public signatures
and the existing date-format policy are unchanged.

Rotation flattening resolves indirect appearance-state dictionaries, matrices
and their numeric entries before transformation. Equivalent direct and indirect
values now produce the same appearance; public signatures are unchanged.
Rotation flattening copies appearance headers per transformed page, borrowing
immutable payload bytes and sharing each rotated stream within that page.
Appearances used by other pages remain unchanged. Annotation identities used
by form fields and related annotations remain intact. Output object numbers and
metadata memory charges can change; tight admission can refuse before publication.
Obsolete source AP/state containers and streams are removed when unreferenced;
live aliases and explicit `--preserve-unreferenced` retain them.

Annotation flattening normalizes page rotations modulo 360 before applying
`NoRotate` placement. Equivalent negative, full-turn and large 64-bit angles
now produce the same placement as their cardinal angle; public signatures
are unchanged.

Annotation and rotation flattening fail before publication if computed geometry
is non-finite. Previously, extreme finite coordinates could publish `NaN` or
`Infinity` operands while reporting success. Generated integral real coordinates
outside the 64-bit integer range retain a decimal point for strict readback.
Ordinary finite formatting and public signatures are unchanged.

Image optimization leaves dimensions outside its positive 32-bit codec domain
opaque. Eligible sample sizes are checked against decoded-byte/array ceilings
before decoding or codec calls; oversized declarations now fail explicitly as
`BudgetExceeded` rather than overflowing arithmetic or decoding first. Existing
unsupported filters and ordinary dimension-mismatch handling remain unchanged.

Form merging reuses admitted suffix-search progress for colliding qualified
names. The smallest free `+N` still depends only on earlier sources, so repeated
names within one source keep their shared rename. Naming, encoding and public
signatures are unchanged; repeated collisions no longer rescan all lower suffixes.

JSON text import and export share PDFDocEncoding conversion. Unicode controls
that occupy repurposed PDF byte slots now use UTF-16 rather than changing into
accent marks. Ordinary Latin-1 text can use PDFDocEncoding and export as `u:`
when the existing binary heuristic permits it; undefined bytes remain `b:`.
No public signatures change.

AcroForm JSON funds decoded names, choice text and ancestor scratch before
materialization, and refunds scratch after each field is copied into the JSON
buffer. Large metadata can therefore refuse earlier. A separate widget reports
its immediate terminal field even when `/FT` is inherited from a group; a merged
widget with field entries reports itself. Parent links, full/partial names,
inherited values and bounded lenient handling of broken parents remain visible.

Page JSON expands an indirect `/Contents` array into its entries, preserving
stream references and order like a direct array. Non-array and malformed entries
remain visible for inspection. Contents iteration borrows array storage. Image
inspection admits descriptor capacity and compares borrowed raw name bytes in
native order, without creating strings for each comparison.
Invalid image-resource overrides retain their existing inheritance policy.

| Boundary | Current behavior |
| --- | --- |
| JPEG processing | Grayscale/RGB decoding and recompression require a caller-supplied codec. Existing images can pass through unchanged without one. |
| Four-component JPEG | CMYK/YCCK stays opaque; decoding requests warn, and image optimization leaves it unchanged. This is an accepted limitation of the supported JPEG contract. |
| Zopfli | `--zopfli` reports unavailable and exits 2. Ordinary Flate compression is supported; `QPDF_ZOPFLI` is treated as unset. |
| Crypto | Passwords, encryption/decryption, permission/restriction operations, signing and signature validation are excluded. Crypto options and encrypted inputs, including empty-password inputs, reject without publishing output. |
| Host effects | Inputs, descriptors, environment and publication require host capabilities. Fileless text/binary results replace ambient stdout; companions use explicit host paths. |
| Resource limits | Host ceilings remain binding under `--no-default-limits`. `MaxMemoryBytes` bounds a charged working-set estimate; an optional shared host budget can also refuse admission. Neither guarantees CLR-heap or process-RSS bounds. See [memory ownership and migration scope](MEMORY.md). |
| PDF scope | Rendering, OCR, text extraction/layout and general authoring are outside the target. Rewriting does not establish PDF/A, PDF/X or PDF/UA conformance. |

Typed opening/selection is strict; argv/job loading repairs boundedly by default,
with `--suppress-recovery` selecting strict rejection. Argv/job rewriting defaults
to generalized decoding and compression; typed selection preserves opaque stream
encodings. Compression/decode setters apply in configuration order; QDF fills
unset defaults. These distinctions are part of the supported contract.

## Resource API and behavior changes

Appearance generation clears `/NeedAppearances` through the shared owned
dictionary rebuild, with admitted growth and both cancellation checks. It no
longer makes two unowned table copies under a flat 64-byte charge. Large form
dictionaries can now refuse tight budgets earlier; missing/null flags, borrowed
values and successful output semantics are unchanged. Public signatures are unchanged.

Public result, counter and version records use compiler-generated copying,
equality and hashing. Shallow aliases, constructor validation and admission are
unchanged. Numeric `GetHashCode` results may change; equal records still hash
equally. Public signatures and PDF output behavior are unchanged.

Zlib-flate mode and compression-level validation precedes cancellation and local
or shared memory admission. Invalid options return `InvalidArguments` with zero
counters and no host or budget callbacks. Shared refusal previously could hide
the argument error behind `BudgetExceeded`; otherwise admitted argument errors
could retain nonzero counters. Valid-option behavior and public signatures are
unchanged.

`InMemoryQpdfHost.AddInput` rejects undefined `QpdfInputOwnership` values with
`ArgumentOutOfRangeException` before admission or replacing an input. Previously
every value other than `Copy` silently transferred the array. Valid `Copy` and
`Transfer` behavior, existing leases and caller-owned reservations are unchanged.

Token encoding fills the already-measured result without recalculating unused
byte widths. The content tokenizer borrows its validated governor policy instead
of retaining a second limits reference or revalidating it at construction. Forwarded
limits arguments used only by that constructor are removed. Exact output sizing,
coarse owner allowances, cancellation and public signatures remain unchanged.

Page ranges append inclusions into one admitted list, remove excluded intervals
by testing selected members, and compact odd/even positions in place. Full-span
scratch lists, exclusion hash sets and a second filtered result are removed.
Excluded endpoints still validate, but exclusion width no longer consumes the
selected-entry ceiling; intermediate included selections remain bounded. Tight
jobs can now fit with less memory. Grammar, ordering/duplicates, cancellation,
public signatures and output bytes are unchanged.

QDF repair accepts carriage-return bytes in stream payloads and binary cross
references, preserving them through length/offset repair and repeated repair.
Previously a blanket scan rejected valid binary QDF, including the writer's own
image output. Outer QDF syntax still requires LF endings; unsupported CRLF syntax
rejects before publication. Public signatures and transactional guarantees remain.

QDF scans and table preparation check both cancellation tokens at bounded byte
and record intervals and around awaited writes. Oversized numeric headers stop
at the supported width. Cancellation at numeric-table admission now stops before
materialization; previously it could allocate the complete table before noticing.
There is no fuel accounting or extra per-byte memory admission.

QDF repair indexes object-stream lines by admitted offsets and lengths into its
owned immutable input instead of copying each line. Input ownership lasts through
awaited output and cleanup; hosts retaining backing after a write own its funding.
Unused member-number storage, line counters and the copy helper are removed;
sequential IDs, comment offsets and byte-offset failures remain. Working memory
estimates are lower; output bytes and public signatures are unchanged.

Binary QDF xref repair emits each row as one immutable packet using the shared
byte formatter. Previously three field writes per row exhausted the default host
operation ceiling on a 20,000-member fixture. The ceiling remains binding; byte
output is identical, with fewer allocations and host operations. Fixed formatting
storage stays inside the repair's coarse working allowance.

Optional predictor parameters use their defaults when their resolved PDF value
is null, just as when the dictionary key is absent. This applies to Predictor,
Columns, Colors and BitsPerComponent for Flate and LZW. Previously these valid
direct/indirect nulls failed decoding and checking. Non-null type/range validation,
cancellation and public signatures are unchanged.

LZW PNG predictors exclude row selectors from decoded-sample ceilings, matching
Flate. Selectors remain part of admitted raw intermediate storage; incomplete
rows and invalid selectors still reject. Tight jobs that previously refused only
because selectors counted as samples can now succeed.

LZW, ASCIIHex, ASCII85 and RunLength preflight remaining aggregate decoded bytes
before growing their output. Previously they could decode and allocate a complete
stage before refusing its final charge. Refusal now happens earlier and may take
precedence over malformed data later in that stage. A cached raw-byte ceiling
avoids per-byte governor calls or predictor divisions. Memory admission remains
coarse; final sample charges, cancellation, rollback and public signatures remain.

Compressed xref decoding borrows a complete owned encoded payload while its
parsed-stream owner stays live. Sliced backing still materializes under the
existing scratch scope. The redundant full-payload copy and its admission are
removed; decoded results keep their independent owner. Public copy APIs,
interpretation and failure/cleanup policies are unchanged. Tight readers may fit
with less working storage.

Unknown xref-stream entry types resolve to null and mask older entries for the
same slot. Their undefined fields are ignored, including unsigned-wide values.
Previously strict opening rejected these rows and recovery could restore stale
physical objects. The reader no longer repairs solely for an unknown row type;
affected results can change from warning to success and rewrites retain null
semantics. Native qpdf handles standalone future types but shares the incremental
and wide-type defects. Known-type validation, bounds and public signatures remain.

LZW keeps bounded prefix/suffix tables and one expansion buffer instead of copied
dictionary strings. One admitted batch holds about 16 KiB plus array overhead;
empty/single-literal streams avoid it. Tiny multi-code jobs can therefore refuse
earlier, while long dictionary chains need substantially less memory. Clear
resets the active entry boundary and EOD stops reading immediately. Output,
EarlyChange, malformed/full-table policies and public signatures are unchanged.

ASCIIHex and ASCII85 share the tokenizer's PDF whitespace rules. NUL, HT, LF,
FF, CR and space are ignored before EOD; vertical tab and other invalid bytes
reject. The former duplicate C-style predicates rejected NUL and accepted VT,
also defects in native qpdf. Public signatures, partial-group/odd-nibble handling
and marker policies are unchanged; affected inputs and diagnostics change.

RunLength decoding stops at the first 128 end marker. Previously trailing bytes
could become content or exhaust decoded-byte limits, a defect also present in
native qpdf. Post-marker bytes now neither emit nor consume decoded-byte budget.
Before EOD, truncated literal records now emit their available bytes, matching
native qpdf: encoded `00 41 02 42` yields AB rather than silently dropping B.
Missing repeat bytes still emit nothing, and missing EOD remains tolerated.
Recovered bytes receive ordinary cumulative decoded-byte and memory admission;
tight jobs can now refuse instead of returning an incomplete payload. Public
signatures are unchanged. The intentional post-EOD difference remains.

Argument filtering and collation retain admission scopes without privately rooting
every returned scratch collection. Callers and final plans hold the values they
use; accounting, growth overlap, cancellation and cleanup remain enforced.
Collection-root fields are removed; job JSON retires empty collation scopes using
their existing admission total. Estimates are lower; returned values, policy
snapshots and public signatures are unchanged.

Page-label number trees resolve indirect `/Nums`, `/Kids` and integer keys before
type checks. Equivalent direct/indirect trees now work in selection, merge and
inspection; malformed structures and cycles still fail before publication.
Both JSON label views resolve immediate label dictionaries and integer `/St`
values, retaining other value/reference presentation. The shared dictionary writer
uses its existing admitted sort scratch without cloning label graphs.

Merged page-label runs compare copied style/prefix references in the output pool.
Previously source object-number collisions could discard a later input's prefix,
a defect also present in native qpdf; renumbering could instead expand one run
into an entry per page. Both cases now preserve labels with compact runs.
Redundant source-reference carriers are removed. Indirect `/St` integers now
preserve selection/merge numbering instead of silently using the default start.
Physical output and memory estimates can change; public signatures are unchanged.

Attachment reading keeps existing reference identities and formats their text
only when JSON presentation requests it. Typed listing, extraction and rewrites
no longer allocate or reserve the unused reference labels. JSON reference numbers,
generations and direct-value nulls, listing metadata, escaped lifetimes and public
signatures are unchanged; attachment memory estimates are lower.

Attachment name-tree keys sort by emitted bytes, including mixed ASCII and
UTF-16 keys. Previously decoded ordinal order could publish an unsorted tree
whose existing entries a byte-order search missed; native qpdf shares this
mixed-encoding defect. Keys encode once and already ordered input avoids sorting.
Ordering scratch receives admission and retires after assembly; the redundant
decoded-pair list is removed. Decoded listing and request matching stay ordinal.
Physical bytes and estimates can change; public signatures are unchanged.

JSON export normalizes a PDF real's leading plus sign and integer zeros, while
preserving significant digits, fractional spelling and negative zero. Legal PDF
tokens such as `+.5` and `-00.5` now publish valid JSON through every entry point;
previously success could return invalid JSON, a defect also present in native
qpdf. Normalization uses bounded lexical scanning with cancellation and does not
round through floating point. Public signatures are unchanged.

Appearance generation and stamping share the existing bounded geometry formatter.
Large integral real coordinates retain a decimal point, so generated BBox/Matrix
values remain readable under the managed integer bounds. The former private
formatters could report success while writing oversized integer tokens; native
qpdf shares these extreme-coordinate defects. Non-finite fresh widget bounds and
stamp matrices now fail before publication. Existing malformed-appearance skipping
and ordinary rounding remain unchanged. Numeric strings receive admission before
creation, and content operands write bounded scratch directly into governed
buffers. Public signatures are unchanged.

Appearance generation resolves indirect named font encodings through the existing
bounded field resolver. WinAnsi and MacRoman characters now agree with direct
encoding declarations instead of silently becoming `?`. Local-font precedence,
ASCII fallback, supported encoding choices and public signatures are unchanged.

Overlay and underlay placement resolve indirect `/Rotate` and `/UserUnit` scalars
for source and destination pages. Previously these values silently used rotation
zero or unit one, changing orientation and scale. Direct/indirect values now agree;
inherited rotation, malformed-value defaults and the accepted inherited-UserUnit
limitation retain their policies. Public signatures are unchanged.

The reader uses the higher of the header and catalog `/Version`, including
indirect catalog values. A lower catalog declaration no longer silently lowers
the version of a rewritten PDF. Explicit `--force-version` downgrades still apply;
malformed or unsupported declarations still fail. Public signatures are unchanged.

Rewrites preserve existing extension vendors and negotiate Adobe extension levels
with minimum and forced versions. Previously ordinary rewrites erased declarations
and a minimum request could lower an existing level. Merge takes the greatest
level at the greatest input base version; a higher base resets the level. Explicit
force requests change only Adobe's declaration. Unchanged declarations retain
private fields; changed Adobe declarations rebuild their base/level. JSON creation
and update apply the same rules. Split and typed preview keep their existing
omission of other catalog extras. Corrected bytes and metadata admission estimates
can change; refusal, cancellation and failed publication still preserve prior output.
Public signatures and dependencies are unchanged.

`--show-pages` resolves an indirect contents array and lists its stream references
in order, like page JSON. Empty arrays list no content; object generations and
duplicates are preserved. The former helper listed the array object itself.
Inspection borrows array entries instead of copying a reference list and peeks at
uncompressed value tokens to avoid loading stream payloads. Referenced arrays use
the ordinary parser/cache bounds, recovery and cancellation. Public signatures and
existing non-reference filtering are unchanged.

PDF text metadata shares one decoder for UTF-8 and UTF-16 with a byte-order mark,
and PDFDocEncoding otherwise. UTF-8 BOM attachment keys, names and descriptions,
outline titles and form text now agree across typed, argv and job entry points;
attachment lookup uses the corrected Unicode key. Previously only selected JSON
form fields used the UTF-8 path. Malformed UTF-8 retains replacement decoding;
existing UTF-16/PDFDocEncoding, raw binary values, quotas and cancellation remain.
No public signatures change; metadata text and key matching intentionally change.

Whole-file rewriting and typed attachment edits accept valid zero-page inputs
while preserving their catalog metadata and embedded files. Synthetic empty
jobs use the same governed preparation, so accepted job-JSON attachment edits
now run, and missing sources fail before publication. Zero-page split jobs create
no output instead of overwriting the literal template with an empty PDF. Public
signatures are unchanged; physical bytes and memory estimates for synthetic empty
output can change. Explicit invalid page selections still reject.
Whole-file attachment preservation also applies to ordinary inputs without an edit
request; those jobs previously omitted the embedded files. Explicit selection and
merge catalog policies retain their existing behavior.
Argv accepts synthetic-empty attachment edits like job JSON, and bare `--empty`
accepts the output with or without `--`. Synthetic-empty overlays/underlays and
replacement still reject, including job requests without a pages block.

JSON sidecars must differ from the main JSON output, and an active linearization
first-pass capture must differ from every main PDF output, including later split
groups. Equal ordinal logical paths now fail before any output transaction is
created; previously the auxiliary bytes could overwrite the completed main file
while reporting success. Hosts remain responsible for aliases between different
logical names. Public signatures and sequential commit semantics are unchanged.

Without `--linearize`, `--linearize-pass1` is ignored for split output as well as
ordinary output. Split preparation no longer allocates or publishes an empty
capture that could overwrite an existing target. Host-operation reservations and
memory estimates omit that unused capture; active capture behavior is unchanged.

Typed attachment listing returns metadata without constructing discarded listing
messages. Clean metadata calls no longer spend diagnostic record/message quotas;
real warnings and all memory/I/O limits remain binding. Argv/job listing still
formats its existing diagnostic output under those quotas. The shared decoder,
returned metadata lifetimes and public signatures are unchanged.

Attachment editing emits one flat name array under memory admission and managed
array bounds. The former split above 64 entries produced direct child dictionaries
that native listing and extraction had to repair with warnings. Key order, text
encoding, payloads and file-spec sharing are preserved; physical output and memory
estimates can change. Public signatures are unchanged.

Command arguments and JSON key/object selectors expose read-only views of their
defensive copies. Casts to mutable collection interfaces now reject mutation,
so reusing an invocation or options value preserves its original settings.
Public signatures, ordering, duplicate behavior and caller-input independence
are unchanged.

JSON creation and update expand exponent notation into PDF decimal syntax without
rounding its significant digits through binary floating point. Ordinary integer
and decimal spellings remain unchanged. Exponent results within Int64 can become
integers; larger integral real results keep a decimal point for strict readback.
Plain integer tokens must fit signed 64-bit, real magnitudes must convert to a
finite binary64 value, and exponents must fit signed 32-bit. Underflow preserves
the exact decimal text even when the numeric approximation is zero. Expansion
receives memory admission before allocation; unsupported values or refused storage
fail before publication. Real conversion reuses the reader's admitted bounded
scratch allowance, so tight budgets can refuse earlier. Previously these cases could report success while
publishing invalid numeric tokens. Native also mishandles some extreme magnitudes;
managed rejection deliberately preserves readable output. No public signatures change.

`QpdfLimits.MaxNestingDepth` accepts 1..499 for direct and host-supplied budgets,
with the default remaining 256. Larger values now throw `ArgumentOutOfRangeException`
before host access. Previously they passed validation and could allow recursive
parsing to exhaust the CLR stack instead of returning a normal depth error.
This intentionally restricts accepted settings; public signatures are unchanged.
Zero-valued argv and job-JSON nesting overrides now return `InvalidArguments`
before PDF host access, instead of escaping as `ArgumentOutOfRangeException`.
Unlike native qpdf, which accepts zero as a depth cap, these overrides require 1..499.

Skipped stream checks describe unavailable filters and JPEG host capabilities
without promising future implementation. Disabled decoding remains an explicit
skip; grayscale/RGB decoding still requires a host codec, and four-component
JPEGs retain their accepted limitation. Public signatures and skip statuses
are unchanged.

Flate decoding requires a complete zlib frame, including the DEFLATE end marker,
even when the produced bytes match the Adler checksum. Premature codec-input EOF
now fails explicitly; the zlib companion preserves prior output and PDF checks
report a failed stream. Ordinary rewrite paths retain their existing warning and
opaque-preservation policy. Valid frames, inline-image delimiter whitespace,
resource limits and public signatures are unchanged.

Typed rotations carry numeric positions directly through the shared rotation
resolver. Explicit lists use `MaxContainerSize` and ordinary memory admission;
the former 4096-character limit on a generated range string no longer applies.
Null and empty lists still select every output page, and repeated positions
still apply the rotation repeatedly in order. Argv/job range grammar and its
4096-character ceiling are unchanged. No public signatures change.

Page selection, rotation and collation expose read-only views of their defensive
integer snapshots. Casts to mutable collection interfaces can no longer change
validated settings. Engine-created views receive memory admission; public
signatures, selection order and duplicate behavior are unchanged.

Name tables hash complete decoded name bytes, including suffixes after the first
16 bytes. Long-name hashing checks cancellation in bounded chunks; borrowed raw
and escaped lookups allocate no name copies. Name equality, duplicate-key order
and public signatures are unchanged. Hash codes remain process-local values.

Public API documentation describes the existing cumulative whole-job output
ceiling and independent output commits. A later output or post-commit close
failure does not roll back earlier publications. Typed domain-result calls throw
on cancellation; execution-result calls return a canceled status. These are documentation corrections,
with no runtime behavior or public signature changes.

The built-in memory host keeps registered inputs separate from published outputs,
including `--replace-input` and companion calls with equal input/output paths.
Reopening reads the existing registration until the caller explicitly exports and
registers the output. This clarifies existing behavior; public signatures and
publication/lease semantics are unchanged.

JSON object selectors use one admitted set of numeric object/generation identities
and trailer membership across typed, argv and job calls. Duplicate selectors no
longer allocate repeated canonical strings or execution snapshots. Tight memory
limits can refuse selection preparation before opening the PDF. Invalid-selector
errors clip the input excerpt to 128 UTF-16 units with `...`, preserving grammar
guidance and offsets. Short errors and JSON selection/output semantics are unchanged;
no public signatures change.

Page selection admits range expansion, page-sized working collections and graph
pool tables before allocation. First/last previews check their cardinality before
allocating; CPU loops cooperate with cancellation. Tight local or shared limits
can now refuse earlier, including before a later malformed-object error. Selection
order, duplicates, immutable result lifetimes and output transactions retain
their behavior. No public signatures change.

Form-resource traversal visits resource dictionaries by graph identity instead
of comparing their complete values. Shared scopes and indirect form cycles
still stop at their visit guards; distinct equal scopes are each traversed in
breadth-first order. Indirect form object numbers remain visited once. This
removes quadratic comparisons between equally sized resource dictionaries;
direct forms in distinct scopes retain their separate occurrences. Public
signatures and ordinary PDF output semantics are unchanged.

Typed attachment add/copy/remove calls admit their request projection under the
invocation governor before allocating lists or records and before opening inputs.
Cancellation interrupts projection. Null/empty containers still reject at entry;
entry validation now runs in the asynchronous operation, where cancellation or
admission refusal can precede an invalid-entry error. Public signatures are stable.

Attachment metadata receives admission before encoding and checks cancellation
while scanning/copying. Headers and name-tree arrays adopt admitted storage;
large metadata can now refuse before materialization. Existing ASCII/UTF-16
encoding choices remain unchanged; rebuilt name trees use one flat name array.

Attachment assembly adopts the owned read/compression payload instead of making
another defensive copy. Cancellation at attachment-input close is checked before
metadata construction. Duplicate-key conflicts retain their fixed error and
processing behavior without storing unused copies of the conflicting keys.
Public stream construction remains defensive; no public signatures change.

`--test-json-schema` scans admitted UTF-8 tokens for top-level help coverage,
without constructing a DOM for the complete generated output. UTF-8 capacity and
key/diagnostic storage receive admission before allocation, so tight limits can
refuse checks previously omitted from accounting. Version-specific coverage,
diagnostic overflow and acceptance before input close/publication are preserved.
No public signatures change.

Image optimization admits its form-visit set before growth and uses borrowed
name lookups. Wide form graphs can now refuse under local/shared memory limits.
Resource dictionaries retain their identities: optimization replaces indirect
stream entries in the copied pool. Unused direct-stream header rebuilding is
removed; PDF and JSON inputs and externalized inline images use indirect streams.
Eligibility, silent skips and the host JPEG contract retain their behavior.

Shared admission keeps less than two spare 16 KiB blocks between significant
releases, preventing repeated grow/shrink calls for tiny scratch at a block
boundary. Significant releases still return capacity promptly; zero-live cleanup
and final completion trim retain their behavior. Local/shared ceilings and
refused-growth policy remain binding. No public signatures change.

Split model-list capacity refunds after each PDF is captured into owned bytes,
instead of remaining charged through the whole batch. Group roots, ordering,
renumbering and rebuilt page children receive admission and cancellation. The
page node adopts its child array without another copy. All PDFs are still
prepared before the first publication; sequential commit and close-fault
semantics remain unchanged. No public signatures change.

QDF original-ID maps receive admission and cancellation during construction;
large provenance maps can refuse earlier. Suppressing original-ID comments skips
their preparation entirely. Ordinary, merged and split output retain the existing
source-ID comments and last-source precedence when comments are requested.
No public signatures change.

Split publication admits changed references, array backing, dictionary tables
and stream headers during identity remapping. Large remaps can refuse earlier
under local/shared memory limits. Rebuilt metadata stays funded through complete
byte capture; unchanged values and stream payloads remain borrowed. Reference
errors, generation normalization and staged output behavior are preserved.
No public signatures change.

Shared graph reachability borrows array and frozen dictionary storage instead
of queuing every child. Repeated references in wide containers can now fit
smaller memory allowances. Traversal scratch refunds before returning the reached
set; reverse visitation, generation-aware cycles and missing-reference handling
are preserved. No public signatures change.

Object-stream preparation admits candidate sets, remapping, sorting storage and
group capacity before allocation. Large generate/preserve/split jobs can refuse
earlier under local or shared memory limits, and cancellation interrupts planning.
Sorted balanced groups of at most 100 objects, signature exclusions and staged
output commit behavior remain unchanged. No public signatures change.

Trailer IDs are checked through borrowed byte storage before copying. Empty
and greater-than-256-byte first IDs retain their existing rejection policy;
rejected large strings no longer allocate payload copies. Accepted IDs still
receive a bounded independent copy. No public signatures change.

Every accepted positive split group size, including `2147483647`, can exceed
the page count and produces one complete group. Group arithmetic no longer
overflows into negative page ranges; planning receives admission and cancellation.
Ordinary page ordering uses the same admitted, cancellable collation path as
inspection. Rotation resolves only affected pages and no longer snapshots every
page dictionary. Large planning/header allocations can refuse earlier; rotation
composition and independent duplicate-page occurrences remain unchanged.
Catalog preservation borrows raw keys and admits its temporary metadata and
final dictionary before allocation. The final dictionary adopts its new storage
without another defensive copy; large catalogs can refuse earlier. Both caller
and governor cancellation interrupt construction. Opaque form arrays, primary
merge extras and split omission policy retain their existing behavior.
No public signatures change.

Page selection finishes copying catalog labels and form defaults before allocating
added attachments, and coordinates later copies with overlay/appearance objects.
Cold font and default-program references therefore keep their original meaning
when these operations are combined. Output object numbers can change.
QDF page/content comment preparation borrows array entries instead of copying
them; comment numbering and shared-stream first-use behavior are unchanged.
Ordinary form preservation now admits temporary registrations, metadata merges
and graph traversal, plus the final catalog dictionary and calculation-order
arrays. Large forms can refuse earlier under local or shared memory limits;
cancellation interrupts metadata assembly. Unchanged single-source metadata is
borrowed, and temporary storage refunds independently of escaping output values.
No public signatures change.

Overlay form reachability admits traversal and reached-set capacities before
allocation. Repeated field references no longer queue a full pending array;
large distinct field sets can refuse earlier. Cancellation interrupts traversal,
including field-array consumption. No public signatures change.
Imported fields retain unchanged headers. Resource unions, changed defaults,
parent repairs and direct-child arrays receive admission before allocation, so
large fixups can refuse earlier while preserving transactional output. Existing
parent/cycle repair and raw default-program behavior remain unchanged.

Appearance text replacement matches the first `/Tx` marked-content block with
bounded `BMC`/`BDC` nesting, preserving unrelated bytes and inline image data.
Malformed programs warn and keep their original appearances; nesting, memory,
decompression and cancellation limits remain fatal. A missing text block appends
a separated `/Tx` block after the unchanged original program.
Generation borrows the copied form's default `/DA` program when no field ancestor
supplies one, including indirect defaults. A selected font from AcroForm `/DR`
is added to absent, empty or indirect appearance resources; local bindings win.
Text without a usable default program warns and preserves its original appearance
instead of emitting text without a font selection. No public signatures change.

Appearance generation preserves skipped buttons in a page's annotation array
when other widgets on that page are updated. Radio buttons, pushbuttons and
buttons without a usable name value keep their existing state and warnings.
Separate and merged widgets update their own terminal field when `/FT` is
inherited, without changing a shared ancestor or unrelated sibling. Indirect
`/FT`, `/V`, `/DA` and `/Ff` values resolve consistently with direct values.
Parent traversal keeps bounded lenient `/Kids` fallback, using one admitted
reverse index per copied graph. Damaged multiple-parent graphs choose the first
indexed parent deterministically. Missing or broken `/Parent` links can use that
fallback; cycles and links without a usable fallback stop traversal.
Indirect widget updates preserve the original annotation array. Direct widgets
use one admitted replacement array, preserving skipped entries and order.
Annotation flattening shares content-entry collection with checking and rotation;
large metadata can refuse before allocation. Appearance-state lookup compares
raw name bytes without decoding all state names.
Overlay and underlay placements now use sequential `/Fx1`, `/Fx2`, ... names
in the fresh page resource dictionary. Names in the original page's resources
remain inside the `/Fx0` Form and do not reserve names in the outer namespace.
This can change generated resource names while preserving the original content
and its resource references. Placement resource storage receives admission.

JSON names use strict UTF-8 scalar validation. Surrogate encodings and values
above Unicode's range now export with `n:` byte escaping, preserving raw name
bytes through import/update instead of replacing them with replacement text.
Checkbox synchronization preserves the raw appearance-state name in `/AS` and
`/V`, including escaped and non-ASCII bytes. Merged widgets update both entries
together; replacement dictionaries receive admission before graph mutation.
Rotation and page-content checking share content-entry collection and accept
indirect arrays. Malformed instructions behind those arrays now fail content
checking just as direct arrays do. Rotation keeps existing unreadable-content
warnings; growing content/annotation containers and traversal sets receive
admission before allocation, so large rotations can refuse earlier.
Page JSON identifies an uncompressed content array from its first value token;
ordinary stream references no longer require materializing their payloads.
Array contents, compressed members, cached values and generation checks retain
the ordinary reader's parsing and ownership rules.

JSON outline inspection rejects cycles through sibling links as well as child
links, with cancellation and nesting bounds independent of cycle detection.
Decoded titles, outline collections and destination indexes receive memory
admission before allocation. Destination indexes are built only for a nonempty
requested outline tree; filtered JSON requests avoid this unused work.
Attachment keys, names, descriptions, dates, checksums and MIME names now receive
admission before decoding or copying. Large metadata may therefore refuse earlier.
Listing results retain independent decoded text after their input closes;
temporary metadata and traversal storage refund on failure or cancellation.
Attachment JSON reads modification dates from `/ModDate`, independently of
`/CreationDate`; a missing field remains null. Full PDF timestamps with signed
time-zone offsets now retain that offset. Date formatting checks bounded syntax
before allocating. Named streams share metadata reading with typed listings.
Page JSON includes nested bookmarks with explicit page destinations in tree
order. An admitted sparse lookup avoids traversing the outline tree per page.
Sequential JSON pages advance through sorted label breakpoints once, preserving
the last duplicate index and arbitrary label values without repeated prefix scans.

Coalescing, normalization and inline-image interpretation now require unfiltered
or successfully decoded content. Unsupported/damaged encodings and filter-count
ceilings preserve the affected content and headers; page operations warn rather
than interpreting opaque bytes as instructions. Memory/decompression limits and
cancellation remain fatal. No public signatures change.
These operations share content-entry collection and accept indirect content
arrays. Stream identities and shared array references survive normalization;
coalescing/externalization remove replaced streams only when no remaining copied
object refers to them. Unreferenced-object preservation remains explicit.
Normalization processes each shared indirect stream version once per phase,
including skipped streams, avoiding repeated buffers and repeated warnings.
Per-page resource operations still run; a replaced stream version is reconsidered.
Form merge name decoding, qualified names, collision storage and renamed bytes
receive admission before materialization. Earlier sources remain the collision
snapshot, including hierarchical and unnamed nodes. Large names may now refuse
before allocating text; PDFDocEncoding/UTF-16 and suffix rules are unchanged.
Resource-array merges borrow unchanged storage. Changed arrays admit their final
capacity before copying, retain existing order/duplicates and append only unseen
supported scalar values; cancellation also interrupts the inner equality search.
Merged page labels use admitted sorted lookup rows and predecessor search;
lookup no longer scans the whole prefix of the tree for each selected page.
Number-tree scratch is admitted and borrows parsed arrays. Selection order,
duplicate breakpoints, reference identity and run compression are unchanged.

Overlay catalog default appearances remain borrowed byte programs through import,
without eager PDF-text decoding or re-encoding. Imported defaults preserve their
original bytes, including non-ASCII names; source ownership transfers before close.
Page-label run comparisons borrow immutable values instead of cloning and
hex-encoding prefixes. Reference number/generation and real-number spelling keep
their existing comparison rules; byte-string syntax does not affect equality.
Overlay sources and destinations share content-entry collection and accept
references to content arrays. Damaged entries still warn and skip stamping;
filter/decompression policy and transactional output are unchanged.
Overlay planning admits sparse destination placements and prepares only the source
pages they use. Unplaced content is neither decoded nor warned about, including
no-op ranges. Source opens and explicit range validation still run; range order,
repeats and duplicate placements are preserved. Public signatures are unchanged.

Overlay annotation rectangles normalize reversed endpoints without losing their
extent. Shared appearance streams receive the placement matrix once per copied
stream; separate placements retain independent copies, including widget forms.
Ordinary indirect annotations share the placement's identity memo, so Popup,
Parent and reply links identify the actual transformed page annotations instead
of additional untransformed clones. This can change output object numbers.
Empty or entirely skipped overlay annotations leave the destination annotation
array unchanged, including an absent `/Annots` entry. Annotation metadata, append
arrays and page/rectangle header rebuilds receive admission before allocation;
large placements may refuse earlier without publishing output.
Unsupported or damaged content encodings now warn and skip the affected overlay;
an unreadable destination remains unchanged. Encoded bytes are never relabeled as
unfiltered Form instructions. Opaque stream copying and raw inspection remain available.
Overlay header changes and annotation clones borrow immutable payloads through
the existing source-to-job ownership transfer. Joining, slice adapters and decoded
scratch receive admission before allocation; temporary decode storage retires
after joining. Large jobs may therefore refuse earlier without publishing output.
Annotation flattening now builds each page's XObject resources once, with admitted
capacity growth and content buffers. Invalid page content is checked before
appearance promotion, so a skipped page keeps its appearances and emits no unused
wrapper streams. Indirect content arrays are accepted. Public signatures are unchanged.
Overlay ranges use the shared grammar, including relative indexes, odd/even
selection and exclusions. Duplicate job JSON keys retain their last value,
including nested overlay keys; invalid effective values reject before output.

Overlay font-conflict rewriting compares decoded name bytes and emits escaped
replacement names. Unchanged names, comments and whitespace retain their original
bytes; an escaped font spelling therefore follows the imported font after a
collision. Rewriting admits bounded storage before allocation and preserves a
malformed appearance unchanged when tokenization fails.

Missing appearances emit only the generated Form stream, without an unreachable
initial placeholder. Field values and choices now receive memory admission before
text/byte materialization; large jobs can refuse earlier while preserving the
same PDFDoc/UTF-16 and ASCII/WinAnsi/MacRoman conversion behavior.

Appearance generation resolves escaped font names by decoded bytes: `/F1` and
`/F#31` select and merge the same resource, including its WinAnsi/MacRoman
encoding. Original appearance spelling, comments and whitespace are preserved
apart from the existing font-size correction. Generated appearance stream bytes
are bounded by `MaxDecodedBytes`; large copies and JPEG candidates can now fail
memory admission before allocation. These corrections add no public signatures.

The resource simplification intentionally removes `QpdfLimits.MaxWork`,
`QpdfExecutionCounters.TotalWork` and `QpdfJpegCodecContext.ChargeWork` without
compatibility overloads. The counter constructor now takes seven values in this
order: live memory, peak memory, decoded bytes, output bytes, host operations,
open handles and diagnostic count. Codec providers use `CheckCancellation` and
`ReserveMemory`; hosts supply deadline cancellation rather than deterministic CPU
fuel. Nesting/container/revision bounds, cycle detection, decoded-byte limits,
memory admission and transactional output remain enforced. Expanded page and
object selections use `MaxContainerSize` for their cardinality bound.
Attachment-copy prefix expansion and assembly collections receive admission before
allocation, so tight jobs can refuse earlier. Copy-conflict errors identify the
first source path, clipped to 256 UTF-16 units with `...`; an explicit marker
reports omitted additional source paths. Short single-source errors are unchanged.
No public signatures or dependency versions change.

Diagnostic quotas cover actual stored records and UTF-8 message bytes, including
requested summaries. Overflow fails explicitly; it preserves the accepted prefix
and adds one bounded terminal failure outside that quota. Summary records already
accepted before a later failure remain in the cumulative diagnostic counter even
when the returned failure omits those success summaries. `--no-warn` avoids
storing or charging suppressed warnings, while their warning exit status remains
unless `--warning-exit-0` applies. Equal message strings have no identity guarantee.

Failure diagnostic views cache their visible counts and shared-prefix overlap.
Previously reading `Count`, enumerating the list or accessing its terminal record
could repeatedly search a duplicated history with quadratic cost. Counts now
require no traversal, and complete enumeration is linear. Diagnostic wording,
identity/order, warning policies, counter semantics and publication guarantees
are unchanged. The fixed failure-view memory estimate increases by 16 bytes;
no per-record index or failure-time set is allocated. Public signatures are unchanged.

Verbose/memory summaries capture the processing peak before presentation storage
is allocated. The final `PeakMemoryBytes` counter also includes that storage and
can therefore be higher. Summaries are formatted and admitted before output
publication; completion reuses them without a new allocation or cancellation
check that could turn a successful commit into failure. Each host transaction
retains its existing commit/discard guarantees; multiple targets are not a group
transaction.

## Known differences from native qpdf

These are documented behavior differences, not new waivers. The two accepted
codec/build limitations above remain visible in the inventory. The detailed
[supported profile](SUPPORTED-PROFILE.md) defines operation combinations and
failure behavior; the tables below highlight differences important to callers.

| Area | Difference |
| --- | --- |
| Invocation | Managed syntax also accepts a separate `--job-json-file file`, collation inside a pages block and bare `--no-default-limits`. Job numeric limits accept numbers; native job files use decimal strings. |
| Inspection/output | Diagnostics and payloads are separate results. Failed/canceled jobs discard staged binary output; native stdout may already contain a prefix. Progress uses counted-byte milestones, and diagnostic wording is implementation-defined. Keep-open options are validated hints; host lease policy governs actual handles. |
| Parser limits | Managed nesting settings accept 1..499 (default 256), within host ceilings. Native defaults to 499 and accepts zero as a depth cap; managed zero overrides reject as `InvalidArguments`. Tight container limits fail with exit 2 where native recovery may warn and skip an object. |
| Filters | Partial predictor rows, unknown PNG row selectors, mismatched decode-parameter arrays and preset zlib dictionaries reject instead of using native's lenient handling. Unknown/above-level encodings remain opaque according to the operation's decode policy. |
| Page graphs | Duplicate pages are independent page objects; content-stream sharing can differ. Only the four PDF inheritable page attributes materialize. Omitted-page references become null; page-tree/catalog references are rejected by contained copying. Typed preview drops outlines/structure; ordinary copies preserve them. |
| Catalog/structure | Unresolvable extras drop; extras referring to rebuilt catalog/page-tree nodes may drop instead of remapping. Damaged structure drops leniently. Dangling outline destinations become null; named destinations do not resolve in JSON inspection. |
| Forms | Combined forms can be direct dictionaries, with direct children remaining embedded rather than promoted to indirect objects. Merge default resources are first-source wins. Malformed parent back-links remain on copying paths where native may repair and warn; checking warns on both. General parent-field/default-resource parity is not claimed. |
| Overlay/widgets | Inherited `/UserUnit` is not inverted. Direct-dictionary field parents may remain plain clones where native fails; cyclic parents warn with a valid imported structure. Appearance-local resources preserve rendering while broader remapping remains limited. |
| Appearances/content | Parent-held radios may skip; signature fields skip and pushbuttons warn. Wider/conflicting resource lookup is limited. Damaged content, boxes, appearances or indirect annotations warn and preserve originals or skip transformations rather than emitting native's partial result. |
| Labels/attachments | Malformed label trees fail instead of warning. Attachment dates are omitted unless supplied; names use ASCII/UTF-16 encoding and rebuilt trees use one flat name array. Empty attachment-copy prefixes reject instead of copying unchanged. |
| PDF JSON | Import/update accepts generation 0 only, rejects pushed inherited attributes, and excludes stream/metadata writer combinations. Version 1 ignores stream payloads; `--json-output=1` rejects. Creation/update renumbers output to classic files. Unlike native qpdf 12.4.2, malformed, mistyped and unsupported catalog `/Version` values reject before output opens, including indirect values; this avoids publishing PDFs the managed reader cannot open. Dangling imported references still normalize to null. Valid catalog overrides remain intact even when `--force-version` lowers the JSON output header. |
| Unreferenced objects | Copy order and container handling can differ; object/xref-stream members are preserved through rebuilt containers. Unresolvable roots and contained-copy failures skip under one aggregated warning; strict load errors remain fatal. Managed split output retains preserved roots that native split omits. |
| Companions | `FixQdfAsync` requires LF outer syntax and rejects empty/truncated structures and nonsequential numbering; binary stream/xref carriage returns are preserved. `RunZlibFlateAsync` rejects truncation instead of returning partial output, and accepts compression levels 1–9 only. |
| Linearization checking | Physical defects fail with exit 2 where native may warn with exit 3. Show-linearization retains parsed raw fields on physical failure. |

Physical output can also differ while preserving PDF meaning:

- Default document IDs are deterministic content hashes, rather than native's
  time-based IDs; `--static-id` uses the native test constant. Unspecified
  attachment timestamps are omitted because runtime code has no ambient clock.
- BCL Flate levels map `1` to fastest, `2–8` to optimal and `9` to smallest size.
  JPEG providers receive quality `0–100`; the development Lokad.Image adapter maps
  `0` to `1`. Compressed bytes and lossy samples need their appropriate comparisons.
- Object numbering/grouping follows managed packing rules. Rebuilt catalogs and
  page-tree roots stay direct; QDF/linearized exclusions remain explicit. Ordinary
  compressed xrefs use plain Flate without native's PNG predictor; linearized
  xref streams stay unfiltered. Hint bytes describe each output's physical layout.

## Supported operations and evidence

| Family | Implemented scope | Detailed contract |
| --- | --- | --- |
| Reading | Classic, streamed, hybrid and incremental xrefs; object streams; strict parsing and bounded recovery | [Input profile](SUPPORTED-PROFILE.md#input-profile) |
| Pages | Ordered/duplicate selections, ranges, merge, collation, rotation, labels, split and replacement | [Selection](SUPPORTED-PROFILE.md#selection-semantics) |
| Inspection | Checking, object/xref/page/attachment listings, fileless payloads and PDF JSON inspection/import/update | [Inspection](SUPPORTED-PROFILE.md#inspection-profile) |
| Content/images | Normalization, coalescing, conservative pruning, inline externalization, overlays, appearances, flattening and grayscale/RGB JPEG processing | [Content](SUPPORTED-PROFILE.md#page-content-profile), [selection](SUPPORTED-PROFILE.md#selection-semantics) |
| Writing | Version/ID controls, stream compression, object streams and unreferenced-object policy | [Writer](SUPPORTED-PROFILE.md#writer-profile) |
| Attachments | List, extract, add, remove and copy | [Attachments](SUPPORTED-PROFILE.md#attachments-profile) |
| Companions/linearization | QDF, managed fix-qdf/zlib-flate and linearization check/show/two-pass writing | [QDF and linearization](SUPPORTED-PROFILE.md#qdf-and-linearization-profile) |

The canonical [option inventory](../tests/Compatibility/options.json) records
forms, contexts, choices, JSON mappings, scope, remaining gaps and executable
evidence. It contains 140 flags in 157 option/context entries: 106 required
entries complete, 49 crypto exclusions complete, and two explicit partial entries
(`--optimize-images` for CMYK and `--zopfli`). These counts describe the current
inventory, not test or release quotas. Completion of parsing alone is insufficient.
The fix-qdf/zlib-flate companions are additional contracts outside the flag count.

[CompatibilityInventoryTests](../tests/Lokad.Qpdf.Tests/CompatibilityInventoryTests.cs)
checks inventory identity, scope, evidence and visible partial-entry gaps.
`tools/update-compatibility.py` reads pinned `job.yml` and `manual/cli.rst` metadata
while preserving reviewed status/evidence and supplemental requirements. Its
offline regeneration tests use synthetic metadata and a mocked Git identity;
they preserve review annotations and exercise reference-version guards without
requiring an upstream checkout. Metadata does not prove defaults,
precedence, grammar or interactions; those require behavior fixtures.

## How compatibility is verified

The development validator accepts LF, CRLF and CR header endings. Its bounded
header inspection previously rejected CR-only headers before PdfPig opened the
input. Malformed/truncated rejection and the core input policy are unchanged.

The obsolete development-only `probe-flate` command has been removed. Codec and
companion tests exercise the shared production pipeline; the standalone BCL
feasibility implementation and smoke-only command test are no longer maintained.

Reference checks compare fresh managed/native outputs under the original input
and budget controls. Stable inspection text, decoded bytes, page order, sharing,
field relationships, error/warning exits and output integrity have mechanism-
appropriate assertions. Exact stream comparisons use pinned qpdf's structural
JSON and generalized decoding, retaining encoded bytes for opaque filters.
Signatures include each logical page's ordered content streams as well as the
stream-byte multiset; physical object order and numbering can differ. Matching
signatures also require matching ordered PdfPig page text. All-stream byte/text
views support separate image, font and appearance assertions without claiming
rendering equivalence. Negative controls cover payload words `stream`/`endstream`,
page/content-array swaps and changed font encoding with identical stream bytes.
Native syntax/linearization checks and visual checks supplement this evidence.
Skipped checker stages remain explicit; an unavailable check is not a pass.

The original 64 differing hint-payload pairs remain recorded. Their layouts are
validated semantically and against physical object boundaries; differing bytes
alone do not establish a defect. Historical checker false passes retain negative
regressions. Finite visual cases check placement, rotation, appearances and JPEG
optimization with fixed tolerances and deliberate output-defect controls.

Ordinary tests require no native tools. During implementation, run focused public
jobs and affected reference checks. Comprehensive reference, visual and fresh
package-consumer checks belong at integration/release checkpoints; repeat for
changed behavior, failures or a specific unresolved concern. Historical case
identities, counts, exact CLR receipts and prescribed GC rounds add no obligations.
Practical byte/structural/decompression limits, cancellation, cleanup and actual memory
growth remain protected by the small representative workload set.
See [contributor checks](../AGENTS.md) for verification commands and
[benchmarks](BENCHMARKS.md) for performance measurements and artifact identities.

## Codec implementation notes

The core has no runtime package dependencies. JPEG decoding/recompression requires
a caller-supplied `IQpdfJpegCodec`, exposed by the host's `IQpdfJpegCodecHost`
capability. Without it, opaque JPEG copying works, checks report decoding skipped,
and eligible image optimization fails before publication. Frame dimensions and
component counts are checked before sample allocation; the codec context admits
private memory and provides cancellation. Providers must observe the
[borrowed-buffer and cleanup contract](../src/Lokad.Qpdf/Public/IQpdfJpegCodec.cs).

The development-only [Lokad.Image 0.2.0 adapter](../tools/Lokad.Qpdf.Image)
uses packed Gray/RGB samples, disables orientation changes and forwards private
allocations to the active qpdf governor. Encoded input is borrowed when publicly
accessible or copied under admission. The ImageSharp adapter, dependency and six
temporary advisory exclusions are removed; ordinary NuGet auditing stays enabled.
CMYK/YCCK and parameterized-DCT images retain their PDF opacity policy, independent
of upstream codec capabilities. The explicit default quality remains 75, with
zero mapped to one; RGB encoding selects YCbCr 4:2:0. JPEG bytes, compression ratios
and decoded lossy samples can change between providers. Equal numeric quality
does not imply identical distortion, and optimization still requires a smaller
encoded packet. Caller codec signatures and the dependency-free core are retained.

Flate validates zlib framing and Adler-32 because BCL inflate can return short
output for truncated data. Codec calls/refills are bounded in 8 KiB chunks. The
current state allowances are 512 KiB for deflate and 128 KiB for inflate, with
scratch/output growth charged separately. Their rationale combines
[zlib's approximate state model](https://zlib.net/zlib_tech.html) (about 268 KiB
compression/40 KiB inflation) and the
[.NET 10 stream buffer](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.Compression/src/System/IO/Compression/DeflateZLib/DeflateStream.cs)
(8 KiB). These are coarse admission estimates; BCL pools can retain returned
buffers. Unreleased charges and pooled storage do not by themselves prove a leak
or a process-memory bound.

## Pinned reference provenance

The target is [qpdf 12.4.2](https://github.com/qpdf/qpdf), commit
`4eba95899886e851cc41d76886483b347612f2a8`; changing it requires explicit review.
The official Windows `qpdf-12.4.2-msvc64.zip` reports `qpdf version 12.4.2`, has
statically linked libjpeg support and no optional Zopfli support (requesting it
exits 2).

| Artifact | SHA-256 |
| --- | --- |
| `qpdf-12.4.2-msvc64.zip` | `db87077e683630c1217e0e8f9a20a9749d952ab676e881c3689187763a5de25d` |
| `bin/qpdf.exe` | `1bdaf878f37eaffea1ea3c30591214c77636a0f2fc7432427516b6e22e36c0ff` |

The development-only oracle can live under ignored `external/oracle/`.
[Installation](../tools/install-reference-qpdf.ps1) verifies the official archive
and executable hashes. [Bootstrap](../tools/bootstrap-qpdf.ps1) and QpdfProbe
require the exact first-line version banner and record the executable identity;
copyright lines are allowed, but lookalike versions and qualified builds reject.
Missing tools fail explicit reference jobs. Native qpdf, PdfPig and renderers
never become production dependencies.
Parser reference calls stop and await a timed-out oracle before closing capture
streams, retaining partial stdout/stderr for diagnosis. Their workspaces are fresh;
`-WorkRoot` selects an explicit destination. CI exercises this failure path with
a small controlled executable, without requiring native qpdf in ordinary tests.

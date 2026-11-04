# Compatibility

Lokad.qpdf targets qpdf **12.4.2** for non-cryptographic PDF structural operations
through typed, argv and job-JSON entry points. The [supported profile](SUPPORTED-PROFILE.md)
defines operation combinations and limitations; the [option inventory](../tests/Compatibility/options.json)
records their evidence. A roadmap entry or accepted option does not establish
implemented behavior. Completed correction history is in the [changelog](../CHANGELOG.md).

## Limitations that affect callers

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

Repeated page-source groups now use one source document per logical path and
register their selected page union before copying. Equivalent combined and
repeated groups retain the same selected-target links and metadata/attachment
aliases. Group mappings, collation and counts remain separate. Different logical
paths remain independent. Duplicate form pages keep independent widget and field
headers with collision-safe names; the previous combined-range path could share
one field where native qpdf creates distinct copies.
Distinct pages from one logical source retain their shared field branch, including
secondary sources. Native qpdf can split imported fields into separately named
copies. Group boundaries do not split that field identity here; genuinely repeated
widget pages still receive independent field headers.
Zero-sized collation groups contribute no copied page or field roots. Their ranges
and original source counts still validate; pages retained by another positive group
keep their shared identity. Previously, omitted groups could leave live page targets
and orphan Page dictionaries in nonsplit output.

Outline inspection treats indirect-null First/Next links as absent, matching direct
nulls. Missing targets and malformed non-null links still reject; cycle and nesting
bounds remain. This corrects otherwise valid outline/pages JSON projection failures.

Field selection validates child references before recording membership. A stale
child preceding a live generation no longer removes the live field registration
or calculation order. Existing malformed-value and memberless-field policies remain.

Object-stream header pairs and startxref now share the ordinary signed-integer
converter. Positive signs no longer discard optional compressed data or reject
valid page sources. Numeric/offset/member bounds and raw ordinary-object spelling
remain intact. Classic xref line boundaries accept CR, LF and CRLF, with space,
tab or form-feed padding after subsection counts. Signed classic subsection bounds
still reject. Subsection ranges check before addition, so oversized malformed
ranges report a parse failure instead of leaking an arithmetic exception.

Object-stream Type/N/First descriptors now resolve ordinary indirect values before
validation. Equivalent direct/indirect representations preserve their Info,
attachment and private graphs and remain usable as page sources. Previously, valid
indirection could warn and discard optional data or reject required pages. Stale,
malformed and cyclic descriptors retain rejection and rollback; xref-stream
bootstrap direct-value restrictions remain unchanged.

Field selection and checking resolve Widget subtypes before classifying membership.
Direct and indirect name values retain the same registered field/annotation and
calculation-order relationships. Form checking validates a child's Parent against
the actual live parent object, including its generation; a matching number with a
dead generation no longer suppresses the invalid-parent warning.

Fileless page/object inspection likewise prepares the complete selected-root union
per logical secondary source before copying. Forward selected-target links no
longer become null merely because their target is copied later. Secondary root
numbers reserve before dependent objects and may differ from native qpdf; primary
IDs/generations and physical xrefs stay intact. Completed secondary sources transfer
their copied-value admission to the existing inspection owner and close promptly,
avoiding repeated growing-pool transfers, cumulative open leases and public-version
funding for private readers. Zero collation still validates omitted ranges.

Split publication restricts retained page references to each file's own page tree.
Targets outside that group become null, including references through shared
destination arrays, Info, metadata, attachments and form graphs. Within-group
targets and non-page aliases remain connected. `--preserve-unreferenced` retains
other copied objects without restoring foreign pages. This corrects earlier
orphan Page retention; native qpdf likewise clears navigation outside a split group.

Explicit unreferenced-object preservation excludes only resolved page-tree
ancestors. Stale or private Parent links no longer discard unrelated original
objects. Loose trees retain their existing preservation policy; uncopyable roots
warn, and required malformed-ancestry lookups can still reject before publication.

Overlay/underlay destination fitting uses the preferred TrimBox/CropBox
intersection with MediaBox, normalizing reversed rectangles first. Original page
box values and independent aliases remain unchanged. Empty intersections warn and
skip before reading destination content. Native qpdf can fit to the raw preferred
box and clip the result afterward, changing the visible pattern; managed fitting
deliberately uses the effective bounds for both source Forms and destinations.

Overlay and underlay sources read UserUnit only from the leaf page. Ancestor
values no longer change placement; direct/indirect leaf values and null defaults
retain their existing meaning. Resources, MediaBox, CropBox and Rotate still inherit.

Page-copy operations, including overlay/underlay imports, reject reachable
signature dictionaries (`/Type /Sig`) and populated signature fields in widget
or retained AcroForm contexts. Field types may be inherited or indirect, and the
signature value need not have a type tag. Typed calls throw `QpdfCryptoException`
with `/Sig`; argv/job JSON report `UnsupportedCryptoOperation`. Blank fields
(absent/null values) and read-only inspection remain supported; no signature is
verified. Native qpdf 12.4.2 retains the reduced signed fixture's metadata; managed
copying deliberately rejects it. Private dictionaries using `V` and `Parent` do
not establish field context. Source catalog/page-tree containment and omitted-page
pruning apply independently.
The same exclusion propagates through retained Info, Metadata and attachment
graphs. Warning suppression cannot convert it into a successful rewrite that
drops those roots. Removed or replaced attachment-only graphs are not copied;
independent page or metadata aliases still retain their own graphs.

DCT decoding accepts absent/null `DecodeParms` only. Parameterized DCT, including
empty or indirect parameter dictionaries, stays opaque with warnings in rewriting
and optimization; direct decode requests fail explicitly. This PDF policy is
independent of the supplied codec's wider capabilities.

A stream's effective non-null `F` names external file data, which this profile
does not load. Raw inspection and opaque PDF/JSON copying retain its descriptor
and physical placeholder bytes; routine compression leaves them unchanged.
Filtered extraction, attachment extraction and transformations requiring effective
bytes fail explicitly, including required object/xref-stream decoding. Checking
reports unavailable coverage instead of counting placeholders as unfiltered data.
Null and stale-generation descriptors behave like absence. Resource pruning keeps
unavailable scopes. Appearance generation warns and retains unavailable programs
and field readiness; optional font inference skips unavailable data.
`FFilter`/`FDecodeParms` never become an embedded-data filter plan. The filename
`F` in a filespec and the inline-image Filter abbreviation have separate meanings.
Typed failures use `QpdfException`; argv/job failures report `UnsupportedFeature`.
Native qpdf 12.4.2 can expose these placeholders as payloads; this deliberate
divergence preserves the external-data boundary without adding external loading.
Linearization hint consumers follow the same boundary: unavailable hints report
skipped coverage, and `--check-linearization`/`--show-linearization` warn without
claiming valid tables or presenting their contents.

Hint decoding uses the shared filter resolver for equivalent names, one-element
arrays and indirect values, including resolved null parameters. The supported
hint domain remains raw data or one Flate stage without non-null decode parameters.
The stream-filter ceiling also binds hint decoding; disabled hint decoding reports
skipped coverage. General stream checking retains its existing filter-limit outcomes.

A single failed output commit preserves its prior target. Multiple outputs commit
sequentially; a later failure or post-commit close fault does not undo earlier
publications. JSON main output and stream sidecars follow the same rule. Prepared
PDF and JSON bytes pass their known lengths through an optional host capacity
hint. Hosts retain their own admission and publication authority.

JSON creation and updates now reject nonzero generations in reference values,
consistently with the existing declaration-key restriction. Previously, those
values could silently bind generation-zero objects by number. Updates also
validate existing PDF pairs before applying edits: live source generations stay
connected after normalization, while stale/free/missing references stay null,
including Info, attachments and stream headers. Empty updates no longer revive
dead references; this matches the native controls. Explicit new generation-zero
JSON edges can still target added or replaced objects. Public signatures are unchanged.

JSON creation/update also rejects unsupported page, content, image, metadata and
PDF layout transformations before opening document data or output. Previously,
some settings silently succeeded without executing, including form/structure
removal and job-JSON QDF/linearization. Argv, job JSON and mixed job files now
report InvalidArguments and preserve the prior target. Disabled booleans,
unchanged inline/image thresholds and disabled resource pruning remain harmless;
supported version, identifier, newline and reporting settings keep their behavior.
Document-JSON inspection likewise rejects the geometry/layout combinations already
excluded by argv. Its documented writer ignores, and the checking path's broader
inspection policy, remain separate. Native qpdf supports more JSON-import
transformations; these combinations stay outside this library's profile.
Update source documents and version carriers remain private through publication,
refunding their charges on failure and cancellation.

Appearance generation now isolates changed annotation arrays from independent
catalog and attachment aliases. It retains their original direct values while
affected pages share one replacement for each original indirect array. Native
qpdf changes the shared original in the qualified controls. Repeated pages no
longer regenerate those direct widgets or repeat their unreachable-widget
warnings; distinct warnings still obey the same quota and fail before commit.
Skipped arrays and indirect widget identities keep their existing behavior.

Appearance parent recovery now follows only registered field Kids branches.
Private dictionaries in catalog data, Info, attachments or pages cannot become
inferred field ancestors, even when their Kids arrays reference a widget. Valid
empty values and form defaults therefore stay independent of that private data.
Registered roots acquire no inferred parent; bounded explicit-parent inheritance
and supported Kids-only recovery within the form remain. Public signatures are
unchanged. This corrects generated values and removes spurious private-graph
nesting failures without increasing structural limits.

The built-in memory host stores registered inputs separately from published
outputs, including equal-path companion calls and `--replace-input`. Opening an
input reads its registration until the caller exports and registers the new bytes.

## Resource API and behavior changes

Inline-image externalization now requires a writable XObject dictionary and a
resolved named colorspace before replacing the inline bytes. Unavailable bindings
warn and keep the original image, including resource-less forms. Native qpdf can
instead emit an invocation without an attached image or keep an unresolved name;
managed preservation intentionally strengthens these cases. Explicit preservation
retains source objects without manufacturing unusable candidate images.

Externalization also uses declared unfiltered sample extents before accepting an
inline terminator. Row padding, image masks and resolved component counts prevent
an embedded `EI` from truncating the samples, even below an apparent byte threshold.
Native qpdf can truncate these inputs. Flate, LZW, RunLength, ASCIIHex, ASCII85 and
DCT packets now supply encoded extents too, using the outer filter in a chain.
Packet scans do not inflate samples, require a JPEG codec or charge decoded bytes;
actual decoding retains its validation and decompression limits. Known extents
take precedence over lexical lookahead. Header framing can require parsing and
admission before threshold eligibility is known; malformed headers can report
warnings at that point. Entire programs shorter than the externalization threshold
stay unchanged without that parsing.

Externalization, normalization, resource scanning, appearance scanning and page
content checking share this framing decision. Unknown filters or unresolved sample
extents preserve the entire program with bounded reporting; page-content checking
reports unavailable coverage instead of interpreting uncertain bytes. Malformed
known packets retain the operation's existing malformed-data warning/failure policy.
Abandoned externalization discards all staged candidates. Normalization reuses a
shared stream only under matching resources when named colorspace framing depends
on them; context-independent streams retain sharing. Standalone stream inspection
has only that stream's resource context and can reject requested normalization
before binary output when the extent cannot be established.

Resource pruning now keeps the enclosing scope when a Form's program cannot be
read or parsed, including Forms that borrow page resources. A Font spelling does
not establish an XObject binding, or vice versa; absent/null bindings preserve
uncertain scopes. This intentionally strengthens cases where native qpdf can also
prune after a category mismatch. The existing `ContentEntries` reader now serves
pruning as well: indirect content arrays behave like direct arrays without
requiring coalescing, and malformed non-null content keeps its resources.

Annotation appearances with absent or resolved-null Resources retain their page
scope during pruning and overlay/underlay wrapping. Such pages conservatively
keep their original resource categories and bindings; placement names avoid
existing XObjects. Imported appearances receive explicit source page resources
on their fresh placement copies, with local appearance resources taking precedence.
Only N/R/D appearance slots and their state dictionaries receive placement
transforms; private AP entries keep their values. Affected output bytes and object
numbering can change. Public signatures are unchanged.
Placement now copies changed appearance stream/state/container headers instead of
mutating their shared slots. Private aliases retain original matrices and resource
scopes, including when the same stream supplies several live appearance states.
Payload bytes keep their existing owner and sharing.

Final-root retirement now follows copied-object replacement rather than a list of
transform-specific signals. Resource pruning and annotation flattening consequently
retire obsolete graph members in ordinary output, while independent aliases and
explicit unreferenced preservation still retain them. Pure copying adds no walk.
Affected output bytes/object numbering change; public signatures are unchanged.

Annotation flattening resolves an indirect Form subtype without rewriting its
header. If a different or absent subtype needs normalization, it creates a fresh
Form header and borrows the payload, retaining original headers held by independent
roots. Rotation flattening likewise transforms only N/R/D and their one-level
state dictionaries; private AP streams and dictionaries retain their identities
and matrices. Affected output identities and bytes can change; public signatures
are unchanged.

Annotation flattening now retains annotations with non-null optional-content
conditions, including OCG/OCMD values, and reports one warning per operation.
Their flags, identities and appearances remain intact; unrelated annotations can
still flatten. Absent/direct-null/indirect-null conditions use the ordinary path.
This conservatively avoids native qpdf 12.4.2's loss of layer visibility. It does
not evaluate the viewer's current layer state or merge layer configurations from
different documents. Widget appearance-readiness preservation still takes priority.

Direct and indirect AcroForm calculation-order arrays now use the same reconstruction
path. Managed merging concatenates source orders, filters references outside the
surviving field closure and appends isolated rotation-field copies in the existing
order. Native qpdf 12.4.2 can keep only the primary order or retain references to
removed fields; those are intentional managed differences. Empty arrays stay empty
and opaque non-array values keep their preservation policy. No calculation engine
or public signature is added.

Form inspection now resolves optional entries before deciding field identity or
inheritance. Indirect null entries behave like direct null or absence, including
parent projection, flags, text values, choices and quadding. A catalog AcroForm
that resolves null is absent for inspection and copying. Existing malformed
non-null outcomes and bounded lenient field ancestry remain unchanged. Native
qpdf can retain the indirect spelling or widget identity in these cases; managed
inspection deliberately keeps its terminal-field and resolved-value contract.
Public signatures are unchanged.

JSON and show-pages image inspection now share ordinary page-resource inheritance.
Null and undefined resource references allow ancestor resources, matching the
reader and native qpdf; explicit non-null invalid overrides still hide images.
Resolved-null Contents is empty in both reports. The existing header probe still
leaves stream payloads unread and preserves raw references within content arrays,
including malformed entries. Non-null invalid Contents values remain visible;
required parent errors, cycles and nesting limits still reject.

Overlay/underlay placement now keeps a non-array appearance Matrix unchanged and
warns once per copied appearance identity. Native qpdf keeps these values silently;
managed placement no longer invents a zero transform that hides the appearance.
Malformed array matrices retain the existing zero-transform compatibility fallback,
including native's behavior. These damaged inputs do not establish general viewer
equivalence for unknown matrix values or unsupported placement semantics.

Attachment import conflicts now use requested keys even when an earlier graph
cannot be preserved. Reversing import order no longer changes whether a collision
rejects; prefixes apply before this decision, and warning controls cannot waive
it. Native also rejects both orders for contained catalog/page-tree graphs. Its
name-tree repair can drop missing specifications before conflict checking;
managed imports retain their raw key identity, consistently with primary entries.
Unrelated failed copies still warn/skip and disappear from the final collection.

Optional attachment name-tree and page-label number-tree arrays now classify
resolved values before testing presence. Indirect null Names/Nums/Kids behaves
like direct null or absence, including nonempty data in the other branch.
Malformed non-null arrays, required key/child errors, unresolved references,
cycles and structural limits keep their existing failures. Dead generation pairs
and free entries follow the reader's resolved-null policy for optional arrays;
required keys and children still fail. Selective attachment
extraction still stops after finding its requested entry. Empty trees retain
their existing operation-specific warning policy; public signatures are unchanged.

Private PDF-version carriers now retire with their writer/import job instead of
leaving caller charges after failure or cancellation. Public document versions
and typed check-result versions retain their durable ownership. Memory counters
and shared-budget retention can decrease; PDF bytes and public signatures keep
their existing behavior.

Typed explicit page-label prefixes now reject ill-formed UTF-16 with
`QpdfParseException` before publication instead of silently substituting U+FFFD.
Argv/job input validation retains its existing rejection. Valid surrogate pairs,
literal U+FFFD, BOM-looking text and PDFDocEncoding selection keep their values;
encoding and validation remain bounded and cancellable at chunk boundaries.

Content normalization now interprets an unescaped CR or CRLF inside a literal
string as one LF byte, matching the reader and PDF syntax rules. Explicit escaped
CR and octal bytes retain their values; line continuations still contribute no
byte. Output with those physical line endings can change under normalization
and QDF defaults.

Content normalization, including QDF defaults, now replaces page content edges
while preserving original streams and arrays retained by other roots. In
particular, an embedded file sharing a page stream keeps its exact decoded bytes.
Native qpdf can change that payload during normalization; managed preservation
intentionally strengthens this case. Output object numbering, sharing and derived
identifiers can change.

Image optimization and inline externalization inside Forms now replace resource
references while preserving original streams retained by attachments or other
independent roots. Repeated resource uses share replacements, including cyclic
Forms. Native qpdf preserves the image alias in the qualified cases but can change
the retained Form program; managed preservation intentionally strengthens that
case. Form conversion that makes no payload change keeps its original header,
including absent/null resource entries. Output identities and identifiers can change.

JPEG optimization now keeps images with effective color-key Mask arrays opaque,
preserving the exact samples used to determine transparency. Native qpdf can keep
the Mask array while changing those samples, making transparent pixels opaque;
this is an intentional managed strengthening. Absent/null masks, stream masks and
valid overriding SMask images retain their optimization policy. The color-key
skip happens before sample admission and does not require a host JPEG codec.

Overlay and underlay placement now interpret destination box corners in either
order, including indirect arrays and coordinates. Equivalent rectangles produce
the same placement and imported annotation geometry; original box values and
independent aliases retain their spelling and order.

Fresh text appearances likewise normalize local widget Rect corners before
deriving their dimensions. Reversed corners no longer create negative-width or
negative-height appearance boxes. Original Rect values and independent aliases
stay intact; existing appearance BBoxes retain their separate handling.

Generated text, list and combo appearances use an existing BBox's vertical origin
once for text and selected-row highlights. Translating the BBox therefore keeps
the visible positions within the widget. Native qpdf 12.4.2 can add that origin
twice; managed output intentionally corrects this displacement. Choice selection,
existing artwork, fonts and appearance matrices keep their existing policies.

Overlay/underlay source preparation resolves optional Contents and TrimBox nulls
before classifying them. Indirect-null Contents is empty; indirect-null TrimBox
uses the inherited CropBox/MediaBox fallback. Blank sources still import their
annotations. Malformed non-null boxes and content retain the warning/skip policy;
null entries inside content arrays remain damaged input.

Overlay/underlay preserves the destination's original content frame. Its background
Form uses MediaBox and an identity placement; the outer page retains rotation,
UserUnit and visible clipping. CropBox/TrimBox fitting applies to imported artwork.
Imported bounds use their effective intersection with MediaBox for both fitting
and the Form BBox; reversed corners normalize first. Native qpdf can expose
off-media artwork by using the raw preferred box. Original box aliases retain
their values. Missing, malformed or empty effective bounds warn and skip the
source before decoding content or copying resources; valid blank sources still
import their annotations.
This intentionally strengthens native behavior that can move or clip existing
marks, even when stamping an empty page. Source content bytes and resource scopes
remain unchanged.

Inline-image externalization now retains original content streams and arrays
referenced by independent catalog values. Previously those aliases could disappear,
or QDF output could fail on an unmapped object. Retirement now uses the complete
output roots, with explicit unreferenced-object preservation unchanged.

Resource pruning also leaves independently referenced Form headers intact.
Resource uses receive shared replacements through the same Form graph walk as
image processing; catalog and other data aliases retain their original resources.
Non-independent Form names are gathered before enclosing dictionaries are pruned.
Unknown dependencies conservatively retain the examined scopes for a copied
source, including incomplete nested-resource declarations that viewers tolerate.
This policy does not certify malformed declarations as standards-complete.

Regenerating appearance streams now retires superseded streams and headers only
after the final output roots are assembled. Catalog, Info, Metadata and surviving
rollover/down aliases retain their original graphs. Explicit
`--preserve-unreferenced` retains the superseded objects as well; native qpdf can
still omit some of those objects under that flag. Skipped generation does not
trigger an extra retirement pass.

Checkbox synchronization selects an existing stream-backed state by field value,
independently of dictionary order. Single-widget fields can still normalize to a
sole on-state; shared fields keep their value while nonmatching siblings are off.
Absent or ambiguous non-Off states warn and preserve the original field and
readiness flag instead of choosing a first key or fabricating `Yes`. This
intentionally strengthens native qpdf's first-state normalization.

Choice appearance generation now resolves selection by export value and paints
the corresponding display label. Lists retain valid multiselect values and sorted
indices, including repeated exports disambiguated by indices. Empty selections
paint no invented selected row. Malformed or ambiguous choice data warns and
retains the original appearance and readiness flag. Native qpdf 12.4.2 can instead
paint export identifiers or a selected empty row; this is an intentional strengthening.

Generated text now takes its encoding from the selected font. A local appearance
font with an absent/default encoding no longer borrows encoding from a shadowed
AcroForm default font of the same name. A genuinely missing local font still uses
the document default; unsupported encodings retain the bounded ASCII fallback.

Appearance generation clears `NeedAppearances` only when all retained widgets
were updated. Skipped generation preserves the flag and prevents subsequent
flattening from stamping stale widget appearances; eligible non-widget annotations
can still flatten. The completion decision is conservative across split groups,
and imported form readiness participates in the same guard. This intentionally
strengthens the previous request-based clearing policy.

Computed geometry now emits decimal PDF numbers that round-trip their finite
binary64 values. Tiny nonzero dimensions and scales are preserved instead of
being clamped to zero or rounded to five decimals. Numeric spelling and output
bytes can change; native qpdf can retain less precision. Non-finite results and
insufficient bounded formatting storage still reject before publication.

Text appearance regeneration now resolves an indirect appearance-state name
before selecting its normal program. Existing selected-state artwork and
resources survive generation and subsequent annotation flattening just as they
do with a direct name. Null/malformed state values retain their existing fallback.

Active linearization pass-one output now rejects logical paths used by primary,
page, overlay/underlay or attachment inputs before PDF access or any output
commit. This extends the existing main-output alias policy to the auxiliary
capture. An inactive capture remains ignored; host identity rules still govern
aliases between different logical names.

Main setters inside attachment/copy/overlay blocks now reject as invalid scoped
options. They previously could be lifted into the main context and publish a
different operation. Nested global markers remain with the owning block parser;
legal main-context settings and prior-output preservation are unchanged.

Attachment add/remove/copy arguments now work before input/page selection and
after the output filename in pure argv, matching equivalent mixed-job calls.
Existing block syntax, edit ordering and publication guarantees remain binding.

Argv/job rotations now follow native range-key semantics. Repeating `+90:1`
sets a single 90-degree addition; different textual keys compose in ordinal key
order, even when they select the same pages. Omitted/empty ranges share `1-z`.
Duplicate positions within one range still repeat the operation. Typed rotation
lists retain their explicit order and every entry; they have no textual keys.
This changes command calls that relied on folding repeated settings in input order.

Managed job JSON treats `empty:false` as a disabled setting, including partial
job files after an input. Enabled empty-input settings retain their ordering and
conflict checks. Native qpdf's job schema does not accept `false` for this flag.

Bounded xref recovery now preserves the generation of its fallback catalog,
accepts direct or indirect catalog type names and sizes the rebuilt table from
live scanned headers. A stale trailer `/Size` no longer discards the root or
its dependencies. A trailer root must match the scanned generation; fallback
chooses the last live catalog. Parsed string/stream payloads do not supply new
top-level headers. Strict opening, structural limits and crypto exclusions remain.
Native qpdf can retain an overwritten catalog or select header-looking stream
data in these damaged-input cases. Its fallback rewrites can also omit `/Size`;
managed rebuilt tables and output trailers include the computed size.

Invalid JSON-key diagnostics now use a bounded prefix ending in `...` for long
keys, including terminal failures after ordinary diagnostic storage is exhausted.
Short messages and error categories are unchanged. A truncated preview preserves
Unicode pairs at its boundary; it does not echo the entire request key.

Fileless JSON now includes the returned text's UTF-8 bytes in cumulative
`MaxOutputBytes` admission and `TotalOutputBytes`, alongside stream sidecars.
The combined limit is checked before any target commits. Calls that previously
omitted those bytes can now fail at that limit, preserving prior targets.
Progress and output summaries now include fileless JSON. Four progress records
use the existing diagnostic allowance; a smaller allowance fails before returning
the JSON or publishing sidecars.

Invocation nesting limits now govern later graph walks as well as PDF parsing,
matching equivalent explicit host options. The validated effective limit is
installed before PDF execution; explicit host ceilings still reject larger
requests. The managed bound also applies to field ancestry and graph traversal,
while native's parser nesting option need not reject the same indirect graphs.

Page-image JSON `filterable` now honors the accepted stream-filter ceiling and
is false when decoding is disabled, matching native eligibility. Raw image
descriptors remain unchanged. This projection does not decode the image or
predict memory/codec admission; native can also emit filter-limit warnings.

Field-parent inheritance for generated appearances and imported overlay defaults
now uses the configured nesting limit instead of separate 64/32-parent cutoffs.
Excess depth fails before publication rather than silently applying partial state.
Native qpdf can warn that a valid deeply nested widget is unreachable; managed
generation uses its configured structural bounds. Indirect widget subtypes,
appearance-state names and AcroForm inspection flags follow their direct values.

PDF JSON export now uses the existing PDF text-decoding policy for UTF-16-marked
strings. Malformed stored text no longer leaks unpaired surrogates into returned
JSON or changes during UTF-8 publication; valid text and binary strings are unchanged.
Truncated UTF-8-marked text uses managed replacement decoding; native qpdf can
emit invalid UTF-8 JSON for this existing malformed-input class.

PDF JSON creation/update now reject NUL in authored names, including `#00` in
`n:` name syntax. Native qpdf can publish a damaged name from literal NUL input.
Literal hash names and NUL text strings remain valid. The reader's existing
zero-byte marker for damaged hash escapes keeps its separate repair behavior.

Full attachment collection operations now reject duplicate logical keys before
publication, including collisions between different PDF text encodings. Native
qpdf repairs this ambiguous input and typically selects the later entry. Managed
selective extraction keeps its existing first-match behavior without a full-tree
scan; it can still extract from a source whose unrelated branches are malformed.

Existing attachment keys also remain reserved when their specification graph
cannot be preserved. A non-replacing add or import still reports a conflict;
explicit replacement/removal remains available. Unrelated edits retain the
recoverable skip policy, and skipped graphs never enter the final collection.

Extraction and removal now accept explicitly empty existing attachment identifiers,
with removal matching native qpdf. Native 12.4.2 treats an empty extraction selector
as a successful no-op; managed extraction returns the selected bytes or reports
not found. Missing/null parameters remain errors, and creation keys stay
nonempty. Typed lookup of an absent empty key throws `QpdfParseException` rather
than the former `ArgumentException` from empty-argument validation.

Typed and argv attachment authoring now reject malformed UTF-16 request text
instead of silently substituting U+FFFD, consistent with job JSON's rejection.
Authored MIME names also reject NUL. These intentional alpha behavior changes
preserve identifier identity and prior outputs on failure; valid Unicode and
lenient decoding of existing PDF text are unchanged.
Native qpdf 12.4.2 job parameters truncate authored attachment keys, filenames and
descriptions at NUL; managed text strings preserve the complete value. Native's
attachment-name lookup also truncates at NUL, while its object inspection reads
the managed file's complete strings and referenced payload correctly.

Ordinary page selection now retains the primary document's attachment collection
even when no primary pages are selected. This intentional alpha behavior change
replaces edit-dependent retention and aligns ordinary selection with native qpdf.
Explicit edits modify that collection; secondary collections require explicit import.
Managed split outputs continue retaining the final collection in every output.
For a new document assembled only from page graphs, use an explicit empty primary
where supported. Retained data can increase resource cost or expose profile errors;
limits still fail atomically instead of omitting attachments as a fallback.

The **0.1.0-alpha.2** host integration deliberately changes these alpha APIs:

- `IQpdfInput.ReadAsync` fills caller-supplied `Memory<byte>` and returns a byte
  count. Use nonallocating `ReadExactlyAsync` with already admitted storage, or
  explicitly budgeted `ReadExactlyToArrayAsync` when an owned array is needed.
  Keep the input identity and backing stable through disposal; finish each read
  before reusing its destination, starting another read or disposing the lease.
- `QpdfJpegCodecContext.MemoryBudget` replaces `ReserveMemory`/`ResizeMemory`.
  Codecs create child scopes and reserve/resize through the shared interfaces.
  Growth receives admission before allocation; refusal preserves the previous
  estimate. Shrink and disposal remain available after cancellation. All workers,
  private storage and borrowed aliases must finish before the codec returns.
- The read-only JPEG decode stream exposes its already admitted input segment
  through `MemoryStream.TryGetBuffer`. Borrow only the offset/count, keep those
  bytes immutable, and release aliases before returning. Other adapter input
  streams still use an admitted copy. Codec signatures are unchanged.
- `QpdfLimits.MaxWork`, `QpdfExecutionCounters.TotalWork` and
  `QpdfJpegCodecContext.ChargeWork` are removed without compatibility overloads.
  The counter constructor takes seven values: live memory, peak memory, decoded
  bytes, output bytes, host operations, open handles and diagnostic count.
  Use cooperative cancellation, including `CheckCancellation` inside codec work;
  hosts supply deadline tokens instead of cumulative CPU fuel.

Local `QpdfLimits` and an optional host shared allowance remain independent
admission controls. Significant storage and growth overlap receive admission;
refusal can precede malformed data discovered later in a decode stage. Estimates
are not measurements of the CLR heap or process RSS. Host buffers, concurrent
jobs and caller-retained values require host ownership policy. See [memory and
host ownership](MEMORY.md) for durable versus invocation owners and refunds.

`MaxNestingDepth` accepts **1..499**, default **256**, for public and invocation
settings. Parent-field checks, widget imports and parser containers retain their
structural checks and cycle guards; cached ancestors do not bypass the configured
ceiling. Expanded page/object selections use `MaxContainerSize` for cardinality.
Host ceilings remain binding when invocation defaults are lifted.

`MaxOutputBytes` is cumulative across a job's outputs, including prepared bytes
abandoned on failure. Typed domain-result operations throw on cancellation;
execution entry points return a canceled result. Cleanup completes pending I/O
before releasing backing or leases. Returned immutable values and independently
exported copies stay charged under their caller owners after document disposal.

Diagnostics store actual records and UTF-8 message bytes under their quotas,
including requested summaries. Overflow preserves the accepted prefix and adds
one bounded terminal failure outside the quota. `--no-warn` suppresses warning
storage while retaining warning exit status unless `--warning-exit-0` applies.
Message string identity is unspecified. Accepted success summaries remain in the
cumulative diagnostic counter even when a later failure omits them from its list.

Verbose/memory summaries capture the processing peak before presentation storage.
The final `PeakMemoryBytes` can include a higher presentation peak. Summaries and
result storage are admitted before publication and reused after commit, so result
construction cannot newly refuse a successfully committed output.

## Known differences from native qpdf

These are documented behavior differences, not new waivers. The two accepted
codec/build limitations above remain visible in the inventory. The detailed
[supported profile](SUPPORTED-PROFILE.md) defines operation combinations and
failure behavior; the tables below highlight differences important to callers.

| Area | Difference |
| --- | --- |
| Invocation | Managed syntax also accepts a separate `--job-json-file file` and bare `--no-default-limits`. Collation is a main option outside the pages block. Job numeric limits accept numbers; native job files use decimal strings. |
| Inspection/output | Diagnostics and payloads are separate results. Failed/canceled jobs discard staged binary output; native stdout may already contain a prefix. Progress uses counted-byte milestones, and diagnostic wording is implementation-defined. Keep-open options are validated hints; host lease policy governs actual handles. |
| Parser limits | Managed nesting settings accept 1..499 (default 256), within host ceilings. Native defaults to 499 and accepts zero as a depth cap; managed zero overrides reject as `InvalidArguments`. Tight container limits fail with exit 2 where native recovery may warn and skip an object. |
| Filters | Partial predictor rows, unknown PNG row selectors, mismatched decode-parameter arrays and preset zlib dictionaries reject instead of using native's lenient handling. Unknown/above-level encodings remain opaque according to the operation's decode policy. |
| Image identities | Managed optimization preserves shared image references and attempts each image once. Native qpdf may replace resource aliases with separate image copies. JPEG attempts and byte/memory accounting therefore follow managed object identities. |
| Split attachments | Managed split outputs retain the final document attachments; native qpdf split output omits document-level attachments. Shared objects and explicit unreferenced-object retention follow the managed output policy. |
| Page graphs | Duplicate pages are independent page objects; content-stream sharing can differ. Only the four PDF inheritable page attributes materialize. Omitted-page references become null; page-tree/catalog references are rejected by contained copying. Typed preview drops outlines/structure; ordinary copies preserve them. |
| Catalog/structure | Unresolvable extras drop; extras referring to rebuilt catalog/page-tree nodes may drop instead of remapping. Damaged structure drops leniently. Dangling outline destinations become null; named destinations do not resolve in JSON inspection. |
| Forms | Combined forms can be direct dictionaries, with direct children remaining embedded rather than promoted to indirect objects. Merge default resources are first-source wins. Malformed parent back-links remain on copying paths where native may repair and warn; checking warns on both. General parent-field/default-resource parity is not claimed. |
| Overlay/widgets | `/UserUnit` applies only to the leaf page. Direct-dictionary field parents may remain plain clones where native fails; cyclic parents warn with a valid imported structure. Appearance-local resources preserve rendering while broader remapping remains limited. |
| Appearances/content | Parent-held radios may skip; empty signature fields skip and pushbuttons warn. Populated signature copying rejects explicitly. Wider/conflicting resource lookup is limited. Many damaged content/box/appearance paths warn and preserve or skip. Placement keeps non-array appearance matrices opaque with a warning; malformed arrays retain the legacy zero-transform fallback. |
| Labels/attachments | Malformed label trees fail instead of warning. Attachment dates are omitted unless supplied; names use ASCII/UTF-16 encoding and rebuilt trees use one flat name array. Empty attachment-copy prefixes reject instead of copying unchanged. |
| PDF JSON | Import/update accepts generation 0 only, rejects pushed inherited attributes, and excludes stream/metadata writer combinations. Version 1 ignores stream payloads; `--json-output=1` rejects. Creation/update renumbers output to classic files. Unlike native qpdf 12.4.2, malformed, mistyped and unsupported catalog `/Version` values reject before output opens, including indirect values; this avoids publishing PDFs the managed reader cannot open. Dangling imported references still normalize to null. Valid catalog overrides remain intact even when `--force-version` lowers the JSON output header. |
| Unreferenced objects | Copy order and container handling can differ; object/xref-stream members are preserved through rebuilt containers. Unresolvable roots and contained-copy failures skip under one aggregated warning; strict load errors remain fatal. Managed split output retains preserved roots that native split omits. |
| Companions | `FixQdfAsync` requires LF outer syntax, consistent stream/object/length framing and sequential numbering; binary stream/xref carriage returns are preserved. Ambiguous marker payloads reject before commit; native fix-qdf can publish incorrect lengths. `RunZlibFlateAsync` rejects truncation instead of returning partial output, and accepts compression levels 1–9 only. |
| Linearization checking | Physical defects fail with exit 2 where native may warn with exit 3. Show-linearization retains parsed raw fields on physical failure. |

Additional intentional data differences are:

- AcroForm JSON identifies a separate widget by its terminal field; a merged
  field/widget keeps its own identity. Inherited `V`/`DV` report resolved values,
  rather than native's reference representation. Broken or over-depth ancestor
  walks stop boundedly and may omit inherited information; they do not turn
  read-only inspection into a strict form-tree validation pass.
- Attachment creation and modification dates are read independently from
  `CreationDate` and `ModDate`. Native qpdf 12.4.2 can repeat the creation date as
  `modificationdate`, including when `ModDate` differs or is absent. Managed
  inspection preserves the modification date or reports its absence.
- Inspection-only jobs accept writer settings without applying every rewrite
  operation. Managed form/structure removal flags leave inspected roots intact;
  native applies those removals before inspection. Page selection/collation,
  rotation and overlay/underlay inspection remain supported, and QDF can supply
  the existing content-normalization default. Rewriting still applies requested
  form/structure removals.

- Resource pruning keeps the effective scope for used Type 3 fonts and soft-mask
  groups, including inherited uses through forms. Type 3 protection applies to
  both Tf-selected fonts and ExtGState Font selections, including indirect arrays
  and subtypes. Native
  qpdf 12.4.2 can remove these dependencies and blank the reduced fixtures; managed
  deliberately preserves their rendering. These programs can retain unused page
  entries even with local resources; ordinary consumers still permit pruning.

- PDF JSON creation/update validates page-tree structure before publication through
  the shared bounded reader. Cycles, nesting-limit violations and malformed tree
  links reject with prior output preserved; native JSON import can pass these
  graphs through until a later read/check fails. Counts follow actual traversal,
  including indirect page types and repeated references.

- Split labels advance their source numbering runs. Selecting/reordering then
  splitting retains source labels; native's combined selection-and-split command
  instead applies original label positions. Native selection followed by a
  separate split supplies the managed behavior's reference control.
- Attachment name-tree keys sort by emitted bytes, including mixed ASCII/UTF-16
  encodings. Native qpdf 12.4.2 shares the former decoded-order defect. Listing
  and request matching continue to use decoded ordinal comparison.
- Integer scalars retain signed 64-bit values; PDF JSON real values expand into
  PDF decimal syntax, require finite binary64 magnitude and signed 32-bit
  exponents, and preserve decimal text on underflow. Unsupported magnitudes
  reject before publication rather than emitting unreadable numeric tokens.
  Obvious overflow now rejects before expansion admission, so a short invalid
  number can report `InvalidArguments` where temporary growth previously caused
  a memory-budget failure. Valid compensated exponents retain their behavior.
- RunLength decoding stops at its first end marker, ignoring trailing bytes.
  Native qpdf 12.4.2 also exhibits the former managed trailing-data defect.

Physical output can also differ while preserving PDF meaning:

- Rotation flattening keeps popup/reply links local to each shared annotation
  occurrence. Native qpdf 12.4.2 can leave these links pointing to detached or
  original annotations after duplicating pages; managed output deliberately keeps
  the relationships coherent. Appearance payload sharing remains independent of
  annotation and field header identity.

- Rebuilt page roots use a direct `/Type /Page`; native qpdf can retain indirect
  type names. Managed writer planning uses this canonical page type, while
  read-only inspection retains the source representation.

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

The packed-package consumer exercises content storage through argv/job page
selection with pruning, coalescing, inline-image externalization and overlays.
Typed inspection checks exact published bytes and their lifetime after document
and host disposal. Pruning qualification uses explicit page selection; the
whole-file rewrite path does not activate that policy.

The packed-package consumer also qualifies attachment removal, removal of an
unlistable filespec, and repeated replacement through typed, argv and job APIs.
Each edit must fit a 4096-byte output ceiling despite a discarded 64 KiB payload;
published keys and replacement bytes are inspected through the public API.

The packed-package consumer exercises argv/job image optimization with shared
aliases, indirect Image/Form subtype names and codec-free filter refusals. A fixed
valid JPEG provider checks samples and call counts; typed inspection verifies the
published sharing and filters. Codec fidelity remains covered by development
adapter tests.

The development validator accepts LF, CRLF and CR header endings. Its bounded
header inspection previously rejected CR-only headers before PdfPig opened the
input. Malformed/truncated rejection and the core input policy are unchanged.

The obsolete development-only `probe-flate` command has been removed. Codec and
companion tests exercise the shared production pipeline; the standalone BCL
feasibility implementation and smoke-only command test are no longer maintained.

Reference checks compare fresh managed/native outputs under the original input
and budget controls. Generated text placement is compared numerically at its
existing five-decimal acceptance; the decimal token spelling can differ.
Stable inspection text, decoded bytes, page order, sharing,
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
allocations to the active qpdf governor. Core decode input is borrowed through its visible read-only stream; other
input streams are copied under admission. The ImageSharp adapter, dependency and six
temporary advisory exclusions are removed; ordinary NuGet auditing stays enabled.
CMYK/YCCK and parameterized-DCT images retain their PDF opacity policy, independent
of upstream codec capabilities. The explicit default quality remains 75, with
zero mapped to one; RGB encoding selects YCbCr 4:2:0. JPEG bytes, compression ratios
and decoded lossy samples can change between providers. Equal numeric quality
does not imply identical distortion, and optimization still requires a smaller
encoded packet. Caller codec signatures and the dependency-free core are retained.

Flate uses the complete BCL zlib decoder to validate the frame and Adler-32 at
its actual endpoint. Its bounded input rejects exhaustion before frame completion,
so truncated input cannot return partial output. Bytes after the completed frame
are ignored, including non-whitespace tails; the former extra suffix-checksum scan
incorrectly rejected these inputs. Codec calls/refills are bounded in 8 KiB chunks. The
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
The option inventory's source hashes are SHA-256 over UTF-8 bytes with CRLF
normalized to LF; all other bytes are preserved. LF and CRLF checkouts therefore
have the same metadata identity. The updater still rejects a different commit
or edited reference files; ordinary inventory tests remain offline.
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

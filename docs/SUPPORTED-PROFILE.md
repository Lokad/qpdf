# Supported profile

This describes the current repository implementation. [Compatibility](COMPATIBILITY.md)
records the reference target, known differences and per-option evidence. See the
[changelog](../CHANGELOG.md) for unreleased changes and release history; a released
package's contract belongs to its source revision.

The original delivery profile was qualified at `219eca3` by local Windows
and Ubuntu Release/package/consumer checks plus pinned-reference and finite
visual checks. Subsequent corrections are recorded in the changelog. The accepted codec/build limitations
remain explicit: four-component CMYK/YCCK JPEGs stay opaque with warnings where
decoding is requested, and `--zopfli` fails explicitly with an unsupported-feature
diagnostic and exit 2. These limitations have positive/negative controls; they
do not waive another required compatibility mismatch. Approximate job-memory
estimates and practical safeguards are described under Governance below.

## Entry paths

The core package has no runtime NuGet dependencies. JPEG decoding and recompression
require an `IQpdfJpegCodec` supplied through the host's `IQpdfJpegCodecHost`
capability. This applies to every entry path. Without a provider, structural
operations and opaque JPEG copying work; decoding checks report skipped, and
eligible image optimization fails explicitly before publication. Tests and reference
tools opt into the development-only Lokad.Image 0.2.0 adapter. Other profile requirements
remain unchanged.

Three routes converge on one selection plan executed by the shared engine:

- Typed: `Qpdf.SelectPageAsync` with explicit, `FirstAndLast`, or `Normalized` page sets (plus per-page rotation overloads);
  `Qpdf.SelectPagesAsync` merges ordered `PdfPageSource` inputs with per-page source mapping
  and optional `PdfPageMergeOptions` collation, rotations, labels, and split group size;
  `Qpdf.CheckAsync` with `QpdfCheckOptions` (decode level) for partial inspection; attachment,
  JSON, and companion operations (`FixQdfAsync`, `RunZlibFlateAsync`) share the same engine
  and host contracts.
- Argv: `Qpdf.ExecuteAsync` accepts `input --pages [--file=]file [--range=]range
  -- output` (repeated file/range groups with the full range grammar, collation,
  rotation, labels, split, and empty-base forms), `input --check`, standalone
  `--job-json-file=file` mixed with ordinary argv settings and repeated files
  (also the existing separate `--job-json-file file` syntax extension),
  `@file` response files (one argument per line), `--help`/`--version`/`--job-json-help`/`--completion-bash`/`--completion-zsh`,
  content flags, overlay and underlay blocks, appearance flags, image optimization,
  QDF, linearization, JSON inspection and editing, attachment editing, writer controls,
  and the `--suppress-recovery`/`--warning-exit-0`/`--no-warn` modifiers.
- Job JSON: `Qpdf.ExecuteJobAsync` accepts `{inputFile, outputFile,
  pages:[{file, range}, ...]}` concatenating entries in order, plus matching keys
  for collation, rotation, labels, split, content transforms, overlays, appearances,
  optimization, QDF, linearization, attachments, writer controls, and inspection.

Bare argv input/output pairs rewrite the whole document through the same plan
as `{inputFile, outputFile}` job JSON. The output may follow `--`; writer options,
including linearization and object-stream modes, apply without `--pages`.

Job inspection accepts `{inputFile, check:""}` without an output file, optionally
with count, linearization, xref, object, page/image and attachment listings. The `check` value
must be an empty string; booleans fail. Failed checking stops following inspection.
Checking uses page traversal quietly when `/Count` disagrees, while page-count
inspection reports the declared count. Typed document reading still reports
that disagreement. Fileless inspection also accepts page sources, collation and
rotation, overlay and underlay, with or without checking, through JSON, argv and
mixed job-file routes. Job overlay/underlay values accept an object or the
existing array form.
Selection changes counts/order and materializes inheritance before inspection;
primary generations stay intact, duplicates/imports get fresh identities, and
physical xrefs remain unchanged. Rotation uses selected output-page positions.
Secondary selection groups share one copier per logical path. Their complete
selected-root union registers before copying; zero-sized collation groups still
validate their ranges but contribute no roots. Selected links stay connected,
while genuinely omitted page targets become null. Imported root numbers allocate
before their dependent objects and may differ from native qpdf; page/object
listings use the same consistent identities. Each completed secondary reader
closes before the next opens, with copied immutable values funded through the
inspection lifetime. Distinct logical paths remain independent.
Overlay/underlay uses the shared placement and widget-import pipeline before
checking/listing, preserves primary form/annotation identities and registers
imported fields with merged defaults/resources. Direct trailer catalogs remain
direct. New form/widget numbering may differ from the reference; their semantic
relationships and placement bytes are checked independently. Output files still
cannot accompany check/inspection jobs. Resource-pruning settings are
serialization controls and leave fileless inspection resources intact.

`{inputFile, outputFile}` rewrites the whole document through the shared writer.
`jobJsonFile` names a partial configuration in the invoking host's path context,
and nested files share native sorted-key setter order. JSON objects retain the
last value per key; repeated input/output/page declarations across files reject.
Malformed literal UTF-16 in inline job JSON returns `InvalidArguments`, as do
malformed surrogate escapes; transcoding failures do not escape execution results.
Configuration files together fit `MaxInputBytes`; host memory/nesting and
operation/handle ceilings apply during loading. File identity cycles reject,
crypto declarations reject before PDF/password access, and cancellation closes
job input leases. Models remain admitted through execution and are disposed
before result snapshots. Argv settings apply at their argument position; files
do not advance the positional input/output cursor. Active nested identities
form cycles; a completed independent file may be included again. A later input
supersedes an earlier empty configuration; empty after an input rejects.
Setter/duplicate/cycle/default behavior has bounded public/native qualification.
Mixed invocations retain managed separate JSON/split values and bare compression;
explicitly empty JSON choices reject, and keep-files-open requires equals syntax.
Practical resource checks use the approximate accounting target.

All routes return explicit outcomes (`PdfPageSelectionResult` or
`QpdfExecutionResult` with exit codes 0/2/3). Anything outside the profile —
ambient stdout output,
unknown options — fails explicitly. Password and encryption
flags fail with `UnsupportedCryptoOperation` and exit 2 without retaining or
echoing values.

## Input profile

- Chained cross references: classic tables and Flate- or LZW-compressed
  xref streams with exact entry sizing and coverage; `/Prev` chains and hybrid
  `/XRefStm` links merge latest-wins with cycle and revision budgets. Object streams
  decode once with validated headers and extents. Object-stream `/Type`, `/N`
  and `/First` may use ordinary indirect values; their generations, required
  types/ranges and loading cycles still validate. Xref-stream bootstrap entries
  retain their direct-value restrictions. `/Extends` stays opaque;
  members resolve through the current stream's header pairs, matching the
  reference reader. Writer controls can preserve or generate object streams;
  ordinary selection output normalizes to classic xrefs. Bounded CLI recovery
  and strict typed opening remain separate policies. Header pairs and startxref
  accept ordinary signed decimal integer spelling, including positive signs;
  object numbers stay positive and offsets nonnegative within their existing
  bounds. Classic xrefs accept CR, LF or CRLF line endings and space, tab or
  form-feed padding after subsection counts. Their subsection bounds remain unsigned
  decimal fields, and malformed/truncated entry fields still reject.
- Catalog/page-tree `/Type` names and page-tree `/Kids` and `/Count` values may
  be direct or indirect. Structural bounds, cycle detection, omitted-page pruning
  and the copier's catalog/page-tree containment rules still apply.
  Rebuilt page roots use a direct `/Type /Page`; read-only inspection retains
  the source representation.
- `%PDF-` header at offset 0; versions 1.0-1.7 and 2.0. The effective version
  is the higher of the header and catalog `/Version`.
- Unencrypted inputs; any effective `/Encrypt` declaration rejects
  structurally, including empty-password forms.
- Strict parsing: damaged inputs fail as parse errors. The CLI repairs boundedly by
  default (warning, exit 3); `--suppress-recovery` keeps strict failure. Typed
  selection and inspection stay strict.
  Configured nesting/container exhaustion is a resource failure, not malformed
  data: reader/copy/presentation paths use `QpdfLimitException`, and rewriting
  fails before publication instead of dropping retained optional roots. Checking
  can report a failed item. Bounded malformed-input salvage remains separate.
  Reconstructed xrefs retain live scanned generations and dependencies despite a
  stale trailer size. The active root must match its scanned generation; otherwise
  the last live catalog with a direct/indirect type name is used. Successfully
  parsed payload bodies are skipped; malformed candidates retain bounded salvage.
  Malformed, mistyped, unresolvable, or unsupported catalog `/Version` fails;
  null or absent `/Version` keeps the header.
- Integer scalars support the full signed 64-bit range and long leading-zero
  tokens within host memory limits; writing preserves their source spelling
  and inspection normalizes it. Repair replaces an object with null on integer
  conversion overflow; invalid in-range references instead consume one parser
  error and become null values. Real scalars retain their exact text, with bounded
  conversion and governed scanning/copying; long tokens, ties, underflow and
  overflow have public and exact-rational evidence.
- Braces delimit object tokens and repair to null, consuming an error inside
  containers. Original `#00` name escapes reject strictly and repair to null;
  escapes decode once. Invalid/truncated hash escapes instead retain their
  spelling with an unbudgeted warning and a zero-byte raw-name repair marker,
  distinct from an escaped hash. Rewrites preserve that damage and warn on
  reopening; strict loading continues to reject it.
- Literal CR/CRLF normalize to LF, escaped line continuations drop, and escaped
  `\r` remains CR. Octal escapes preserve their decoded bytes. Inspection chooses
  literal or lowercase hex strings from decoded contents, independently of
  source spelling, and uses octal escapes for nonprintable literal bytes.
  Name/literal unescaping and display scans check cancellation. Authored
  invalid-hex, EOF, mismatched-terminator and root-word cases agree through
  valid/rebuilt xrefs. Reconstructed xrefs ignore orphan dictionary values;
  valid xrefs insert fake keys. Display buffers and parser attempts admit their
  storage. Nested indirect loads and compressed members have independent error
  thresholds through public inspection/rewrite and strict-rejection controls.
- CLI stream-length repair scans for endstream/endobj within the current physical
  object, obeying memory limits and cancellation. Missing, incorrect,
  negative or unresolvable lengths recover with warnings; strict readers reject
  them. Copied repaired streams use the accepted payload length. Valid declared
  lengths preserve binary data without marker searching.
  Compressed members parse one value and do not interpret a following stream
  keyword as file data. CLI loading ignores trailing member bytes, matching
  native; strict typed loading retains its trailing-byte rejection.
- Typed page selection copies streams opaquely: direct and indirect
  `/Length` resolve with a load-cycle guard, unknown filter encodings pass through untouched, and
  copying a page needs no image support. Structural streams decode through Flate,
  LZW, ASCIIHex, ASCII85, and RunLength (direct or indirect filter specs), with
  shared predictors, plus DCTDecode with a supplied codec at `--decode-level=all` for
  1- and 3-component JPEGs with absent/null `DecodeParms`. DCT parameter dictionaries
  and four-component samples stay opaque in transformations; explicit filtered-data
  requests fail as unsupported.
  Flate/LZW row geometry is read only for active prediction. Absent/null Predictor
  or Predictor 1 ignores dormant geometry; LZW EarlyChange still controls code width.
- External-file streams (effective non-null `/F`) stay opaque. Raw inspection and
  PDF/JSON copying preserve their descriptors and physical placeholder bytes.
  Effective-data extraction, required structural decoding and transformations
  needing those bytes fail explicitly; checks report skipped coverage. No external
  file or URL is loaded. Ordinary embedded-data fallback cannot return a placeholder
  as attachment contents. Null descriptors retain ordinary embedded-data behavior.
- Argv/job rewriting defaults to compression with generalized decoding. Stream-data,
  compression and decode setters apply in configuration order; job keys use lexical
  order, including mixed/file-chain layers. QDF fills unset defaults. Catalog XML
  metadata is uncompressed, including indirect `/Type` and `/Subtype` names;
  page metadata follows ordinary stream settings. Empty streams retain zero-length
  payloads. Broader transformation/default/grammar differences remain in the compatibility audit.
- --optimize-images uses the supplied codec to recompress eligible image XObjects
  (8-bit DeviceGray/DeviceRGB above the oi-min thresholds) as JPEG only when strictly
  smaller. Each image identity is attempted once across aliases, forms and repeated
  pages, including skipped candidates. Resource aliases share replacements while
  independent attachment and catalog roots retain original payloads; affected
  ancestor Forms preserve sharing and cycles. Indirect Image/Form subtype names resolve
  through the copied object pool; stale or malformed values remain opaque.
  Processing uses the assembled graph after placements and appearance flattening,
  including imported Forms and flattened appearances reached through XObject
  resources. Previously missed eligible images now also require the host codec;
  an unavailable codec fails before publication.
  Providers receive --jpeg-quality in 0–100 (the development Lokad.Image adapter
  maps 0 to 1). Already-DCT images recompress only under an explicit quality; large
  inline images externalize first unless --keep-inline-images. Images exceeding
  MaxStreamFilters stay opaque with a warning before sample admission or codec
  checks. Unfiltered images remain eligible at a zero filter ceiling. Images
  with effective color-key Mask arrays remain opaque to preserve exact
  transparency keys. A valid SMask image takes precedence; stream masks and
  absent/null masks retain ordinary eligibility. Other ineligible images skip
  silently; an eligible operation without a codec fails
  explicitly. Warning suppression/exit policy applies as usual; diagnostic limits
  can still fail the operation before publication.
- One input file per selection job, several per merge, through host-mediated ranged reads.

## Selection semantics

Page-range `x` exclusions remove matches only from their preceding positive group;
consecutive exclusions stay in that group. Earlier groups and duplicate order remain.
For example, `1-3,1-3,x2` expands to `1,2,3,1,3`. A final `:odd`/`:even` filters
positions after all groups. The same grammar serves selection, rotation and
placement ranges; excluded endpoints still validate and all retained entries count
toward the configured selection ceiling.

- Repeated page-source groups with the same logical input path share a source
  document and the union of selected page identities. Links and retained metadata
  or attachment graphs can target pages selected in a later group. Group order,
  collation, per-group counts and source mappings are preserved. Different logical
  paths remain independent, including byte-identical inputs. Duplicate page objects
  remain independent; duplicated widgets and their registered field branches get
  separate headers and collision-safe names while sharing immutable payloads.
  Distinct pages from one logical document retain shared field branches, including
  imported sources where native qpdf can create separately named field copies.
  A zero collation group size omits that group's page roots while preserving range
  validation and its source-count/mapping position. It does not remove a page
  independently selected by another positive group.
- Ordinary selection preserves the primary document's attachment collection,
  including when none of its pages are selected. Typed multi-source selection
  uses its first source as primary; argv/job input identifies the primary separately
  from selected page providers. Edits modify this existing set. Secondary sources
  contribute page-reachable files without automatically importing their global
  collections. An explicit empty primary starts with no collection; a real
  zero-page input can own attachments. `FirstAndLast` chooses pages under the same
  retention rule. Use empty-base assembly where supported for a deliberately new
  document; the typed merge options currently have no explicit empty-base setting.
- Explicit ordered pages by 1-based index (order and duplicates preserved),
  `FirstAndLast(N)` windows, or sorted/deduplicated `Normalized` sets; out-of-range
  indexes fail explicitly and empty explicit selections are rejected.
- Ordinary rotation (`Set`/`Add`/`Subtract` multiples of 90 over output positions)
  folds over the inherited base; untouched pages keep their bytes. `--flatten-rotation`
  bakes rotation into content (see below).
  Argv/job settings use the exact range text as a key: the last setting for a key
  replaces earlier ones, and distinct keys apply in ordinal order. An absent or
  empty range has key `1-z`; equivalent texts such as `1` and `1-1` stay distinct.
  Duplicate positions within a range still apply repeatedly. Typed rotation lists
  instead apply every entry in supplied order, including repeated page lists.
  Whole-file argv accepts rotation without an explicit pages block, including
  attachment edits. Collation has no effect on a sole whole-file source. Adding
  an empty job file preserves these results.
- Bare `--empty` writes a zero-page PDF; it can also start a merge from a zero-page
  base whose catalog extras never carry over. Selecting from `.`, overlays and
  replacement with the synthetic base reject. Whole-file rewriting and typed
  attachment edits accept real zero-page inputs and preserve their catalog data.
  Empty split jobs have zero groups and create no output or first-pass capture.
  Argv and job JSON can add/copy attachments to the synthetic empty document.
  The bare argv output separator is optional.
- Merge options interleave per-source groups (collation), rotate, and emit explicit
  page labels or merge source label trees with page-index offsets (unlabeled pages
  carry no numeric portion, runs compress using copied identities). Absent/null
  numbering styles use only the prefix; their starts are not interpreted or
  incremented. Authored/derived prefix-only entries omit `St`, and equal prefix
  runs compact through selection and splitting. Numbered styles retain checked
  counter arithmetic. Indirect tree
  arrays, keys and starts retain their direct-value semantics. Optional Names/Nums/
  Kids arrays resolve explicit null references as absent; interpreted trees retain
  their failure policy for malformed required values and unresolved references.
  Unchanged whole-file label trees are copied opaquely without deriving counters
  or validating number-tree semantics; ordinary graph and parser guards still apply.
  An optional split group size partitions
  output through the same engine. Explicit labels override source labels in
  assembled output order before splitting, including reordered/duplicate pages.
  Each split file retains references only to its
  own pages; foreign page references become null in navigation and retained data
  graphs, including when unreferenced-object preservation is enabled. Shared
  non-page objects remain intact. Typed explicit prefixes reject invalid UTF-16
  before publication; valid surrogate pairs, literal U+FFFD and BOM-looking text
  retain their values with bounded validation and encoding.
- Output carries a fresh catalog with the copied pages only; each occurrence is an
  independent page object sharing immutable resources. Shared non-page objects
  are copied once. Source page ancestry is discarded; every output page receives
  an indirect `/Parent` link to the new page tree, including split, QDF and
  linearized output. Other field, annotation and private Parent roles retain
  their copying policy. Results carry the source page
  count and the exact per-output-page source mapping.
- Retained objects keep supported source-local meaning independently of generic
  catalog preservation. Static View optional-content groups/default OFF states
  combine by identity, including page/appearance resources and annotations;
  equal group names do not merge identities. A sole source keeps its complex
  configuration. Combining automatic states, alternate configurations or non-View
  intents fails before publication. Emitted optional-content registration requires
  PDF 1.5 unless an explicit forced version overrides negotiation.
  When source context would otherwise be lost, URI actions with an explicit source
  Base become absolute ASCII URIs, and source-local named destinations resolve
  through the source's legacy name dictionary or string name tree before existing
  page remapping. Preserved primary context keeps original values and shared
  identities. No ambient document path or network access supplies a missing base.
  Repeated pages retain the first registered
  target; omitted pages and placement-source page targets still prune to null.
  Binding follows retained page/annotation/field events, action chains, outlines and
  catalog actions. Independent private aliases and remote-action targets keep their
  original values. Unsupported required contexts fail explicitly. These rules do
  not import secondary metadata/attachment collections or enable generic typed
  catalog preservation; split retains its existing catalog-extra policy.
- The four inheritable attributes (`Resources`, `MediaBox`, `CropBox`,
  `/Rotate`) materialize from ancestry into each copied page; explicit null counts
  as absent. A missing `/MediaBox` after ancestry fails; missing `Resources` stays absent.
- Page-tree nodes require `/Type /Page` or `/Pages`. Traversal wins over
  `/Count`, which only warns. Page-tree (`/Pages`) and catalog references fail
  explicitly; references to already-copied objects remap while references to any
  other page prune to null, so omitted pages never resurrect. Ordinary selection
  preserves the form dictionary (fields, values, parent links, default resources),
  outlines, and structure through the shared copy engine while contained preview
  drops outlines and structure; explicit `--remove-acroform` and `--remove-structure`
  prune them. Direct/indirect calculation-order arrays share source-order concatenation,
  surviving-field filtering and isolated rotation-field registration. Empty
  orders stay empty; malformed non-array values remain opaque. This preserves
  field ordering without executing calculations. Widget annotations travel with
  appearances, while document info (`/Info`, ModDate-only under `--remove-info`) and the catalog
  metadata stream are preserved unless `--remove-info`/`--remove-metadata`
  drop them (`--remove-info` also drops metadata).
- Repeated runs over the same input and options are byte-identical.
- Publication is staged: validation, budgets, and serialization complete
  before commit, so failures leave existing targets untouched. Every lease
  closes on success, failure, and cancellation.
- `--split-pages` publishes one file per page group with pruned objects and
  sliced labels (typed callers use `PdfPageMergeOptions.SplitPages`); `--replace-input`
  publishes at the primary input's path with no backup, including selections from
  other files. The primary supplies document metadata and the original identifier
  even when none of its pages are selected; page ranges `1` and `1-1` accept the
  same external sources. Ordinary outputs must differ from primary and page-source
  paths. The host defines physical replacement semantics. `InMemoryQpdfHost` keeps
  registered inputs and published
  outputs separate: reopening that path reads the original registration until
  the caller exports the output with `TryGetOutput` and registers it with `AddInput`.
  Partial split outputs can remain if a later group fails.
- Replacing copied graph objects requests one retirement pass after final catalog,
  page-tree and Info assembly. Removed resource/annotation edges no longer leave
  obsolete payloads or headers in ordinary output. Independent catalog, metadata,
  attachment and page aliases retain their originals; `--preserve-unreferenced`
  keeps its explicit policy. Pure copying needs no additional retirement walk.
- --overlay and --underlay blocks (--file, --to, --from, --repeat, repeatable with order kept) stamp whole pages from other files as /Fx form XObjects below or above each output page, centered with shrink-to-fit placement that inverts destination rotation and scale. Ranges use the full shared grammar with an empty --from= letting --repeat supply every page. Missing files fail; damaged or unsupported content warns and skips. Applied pages contribute placement-transformed annotation copies, including imported widgets and form fields within the form limitations above. Shared appearance streams transform once within each placement; separate placements stay independent. Empty or entirely skipped annotation imports preserve the destination array, including an absent Annots entry.
  Supplied static annotation appearances also retain the source Form's clipping
  bounds. Fresh wrappers preserve the full annotation Rect and original artwork;
  only N/R/D slots and one-level state streams change. Private aliases retain
  original containers and bytes, while equivalent states within one annotation
  share their wrapper. Imported widget generation runs in source context first.
  When an annotation crosses the source bounds, an absent/unusable normal AP,
  non-stream state or unusable frame fails with UnsupportedFeature before any
  output commit. Valid zero-area appearances stay empty. Contained and hidden
  annotations retain existing handling. Popup windows, general viewer zoom,
  editing UI and interaction hit areas are outside this static-artwork contract;
  clipping does not generate missing annotation artwork.
  Source generations are validated before placement memo reuse; stale references
  stay null in either traversal order. Widget import retains actual field
  references through parent walks, including valid nonzero generations.
  Retained appearances with absent/null Resources keep their original page scope;
  pruning conservatively retains that page's resources and placement names avoid
  its existing XObjects. Imported appearances bind source page resources on fresh
  copies when no local scope exists. N/R/D and their one-level state dictionaries
  are appearance slots; private AP values remain opaque. Placement creates fresh
  stream/state/container headers for changed appearance roles, preserving original
  matrices and resource scopes held by private aliases. Shared appearances remain
  shared within one placement; payload bytes are borrowed without copying.
  Placement keeps a non-array appearance Matrix opaque and warns once per copied
  stream identity. Malformed array matrices retain the historical zero-transform
  fallback. Warning overflow rejects before commit; suppressed warnings consume
  no diagnostic quota under the existing warning policy.
- --flatten-rotation bakes page rotation into content: boxes rotate per the page angle, a counter-transform wraps the content streams, /Rotate is dropped, and annotation rectangles move with the page. Appearance headers are copied per transformed page while payload bytes remain shared; untouched pages retain their original appearances. Direct and indirect appearance-state dictionaries and matrices are equivalent.
  Only N/R/D appearance slots and their one-level states receive rotation matrices;
  private AP streams and dictionaries keep their identities and matrices.
  Shared annotation identities are isolated before rotation, including duplicated
  page selections. Page associations and popup/reply links follow their occurrence;
  linked popups need not also appear in the page's annotation array. A copied widget
  retains a coherent field ancestor branch; registered roots join the existing
  collision-renaming pass, while unregistered widgets remain unregistered. Other
  widgets on untouched pages keep their original field branches and geometry.
  Rotation baking and overlay/underlay placement also project stored Border
  corner radii, widths and dash lengths, and supported BS widths/active dashes.
  BS takes precedence over Border. Missing defaults become explicit when scaling
  requires it; zero widths and inactive dash data keep their original values.
  Direct/indirect values are equivalent, shared projections remain shared and
  independent originals stay intact. Required malformed or non-finite border
  projections fail before publication. These are stored-geometry corrections;
  supplied static appearances remain supported, while general border generation
  and exact viewer-generated dash origins after rotation are outside this contract.
- --flatten-annotations with all, print, or screen stamps annotation appearance streams as /Fxo form XObjects into page content and drops the flattened annotations. Print mode keeps only print-flagged annotations and screen mode drops no-view ones; widgets wait for generated appearances when the document needs them. Annotations without appearances stay unless they carry an appearance dictionary, and field default resources are not merged. Pages without actionable rotation keep their content and geometry; damaged content or boxes warn and keep the page.
  Indirect Form subtypes reuse their existing stream identity. Normalizing any
  other subtype creates a new header, preserving original headers retained by
  independent roots while borrowing payload bytes.
  Computed coordinates use bounded decimal formatting that preserves finite
  binary64 values, including nonzero dimensions/scales below five decimal places;
  exponent notation is expanded for valid PDF numeric tokens.
- Flattening retains annotations with effective non-null OC conditions, preserving
  their flags, identities and appearance edges, and warns once per operation.
  Absent/null OC remains eligible. It does not bake viewer layer state or combine
  layer configurations across documents. Widget readiness keeps its existing priority.
- Flattening eligible annotations removes Hidden/Invisible annotations without stamping
  their appearances in every mode; Print and NoView further govern print/screen
  stamping respectively.
- Form combination unions source default resources with collision-safe names and
  preserves each retained field's source DA/Q defaults, including ordinary page
  merges, overlays and underlays. Direct and indirect DA programs use the same
  font map; absent/null values still inherit through their field ancestry before
  source form defaults. Equal program text does not merge distinct font identities.
  Rewritten shared programs stay shared without changing independent original
  aliases. Ordinary copying retains its parent-link policy; appearance-local
  resources and primary document metadata/attachment ownership stay independent.
- `--generate-appearances` regenerates text/list widget appearances from field
  values and inherited or form-level default appearance programs. It clears
  NeedAppearances only after all retained widgets were updated. Skipped work
  preserves the flag and keeps widgets during later flattening; eligible
  non-widget annotations can still flatten. The decision applies conservatively
  across split groups and includes imported form readiness. It replaces a bounded,
  matching `/Tx` block and preserves unrelated content. Text and selected-row
  highlights count the existing BBox's vertical origin once, including negative
  origins. Parent-held fields retain
  their terminal identity; field-parent inheritance uses the configured nesting
  bound with cycle handling, including overlay default repair. Missing parents
  can be recovered from registered Kids branches, including direct child
  dictionaries; private Kids entries elsewhere supply no field ancestry. Top-level
  registrations have no inferred parent. Explicit parent links retain their
  bounded lenient handling. Indirect widget
  subtypes and appearance-state names behave like direct names. Checkboxes select
  stream-backed raw states by value; single-widget fields can normalize to a sole
  on-state, while shared fields keep their value and nonmatching siblings are off.
  Missing or ambiguous non-Off states warn and retain field/appearance readiness.
  Radios and
  pushbuttons warn and keep their state, and empty signature fields skip.
  Copying populated signature values, including overlay and underlay field
  imports, is excluded; typed calls reject `/Sig` and
  argv/job JSON report `UnsupportedCryptoOperation`. An absent or null `/V`
  remains copyable; inspection does not verify signatures. Local
  appearance fonts win over AcroForm default resources, including their absent or
  unsupported encoding. Encoding fallback never crosses selected font identities.
  A missing local font can use the document default; field default resources
  are not merged. A missing or non-dictionary selected font binding warns and
  preserves the appearance, readiness flag and widget during later flattening;
  ASCII encoding fallback does not substitute for a missing font resource.
  Unusable defaults/resources and malformed programs warn and
  preserve appearances. Structural, decompression, memory and cancellation limits
  remain binding.
  Replaced appearance streams and headers retire from final output roots after
  generation creates new streams. Independent catalog, Info, Metadata and other
  appearance aliases survive; `--preserve-unreferenced` keeps superseded objects.
  Unchanged or skipped generation needs no additional retirement traversal.
  Generating direct widgets replaces their page's annotation-array edge, keeping
  independently retained original arrays and their direct values intact. Pages
  sharing an indirect array reuse one generated replacement; unreachable-widget
  warnings in that array are reported once. Skipped arrays keep their identity.
  Fresh indirect arrays join the same final-root retirement as new appearances.
  Choice options keep export/display identity until selection is resolved. Lists
  support scalar and multiselect values with validated sorted indices; combo boxes
  paint the selected display label, and editable combos can paint an unmatched value.
  Empty selections have no fabricated selected row. Malformed indices, mismatched
  values and ambiguous exports warn and preserve the appearance/readiness state.

Imported overlay/underlay widgets generate in their source layout and resource
context before placement. This preserves the physical text scale and orientation
when an appearance must be created. Only successful field imports contribute
readiness; a skipped placement does not trigger generation on other pages.
Private graph references copied before a widget do not suppress its form
registration or duplicate the registered field identity.

Overlay form lookup treats an absent, direct-null or indirect-null Fields value
as absent. Null T, FT and Kids values likewise do not establish imported field
identity. Empty arrays and malformed non-null values keep their existing lenient
classification; this does not add general form validation.

Image optimization resolves indirect Image/Form subtype names before choosing
traversal or sample processing; stale and malformed subtype values stay opaque.

Stamp rotation/unit scalars and named appearance font encodings accept indirect
values with the same bounded resolution and interpretation as direct values.
Generated BBox/Matrix values keep large integral reals readable under the integer
bounds. Non-finite fresh widget bounds or stamp matrices fail before publication;
existing malformed-appearance skipping and ordinary numeric rounding remain.

## Inspection profile

- `--show-pages` lists content references from direct or indirect arrays, retaining
  order, duplicates and generations. Empty arrays list no content. Inspection
  resolves the outer array without loading stream payloads to print references.
- `Qpdf.CheckAsync` reports nine declared checks: header, cross-reference,
  trailer, page tree, object-graph traversal, decode-level-gated stream
  decoding, page content, form-tree validation, and linearization.
  Undefined typed decode levels throw `ArgumentOutOfRangeException` before host
  access; `None` intentionally disables stream decoding and reports skipped stages.
- Skipped stages stay visible with reasons and never pass silently; failures
  carry capped per-object detail. Page content tokenizes each complete logical
  page program across its physical streams, subject to the decode level. If a
  required fragment is unavailable, the whole program reports skipped coverage;
  readable fragments are not inspected as independent programs. Content-check
  detail counts page programs rather than physical streams. Linearization
  passes when absent and validates dictionaries, hint streams, and
  bit-level hint tables when present. Hint filters use the shared resolver for
  names, arrays and indirect values; the supported domain remains raw or a single
  Flate stage with absent/resolved-null parameters. External hints or a filter
  ceiling that prevents decoding report skipped linearization coverage. Standalone
  check/report requests warn without presenting unavailable hint contents.
  An absent or null optional `/P` uses its
  zero default; unavailable
  filters skip streams with a warning at level `all`. Form-tree validation warns without failing on page widgets no field registration reaches, kids whose /Parent back-link does not name the traversed node, and field-like direct objects met while traversing.
  Malformed content collections and decode errors fail page-content checking;
  unavailable/partially inspected content reports a skipped stage. Form traversal
  uses the configured nesting ceiling and fails explicitly when it is exceeded.
- `input --check` publishes no files and maps diagnostics to exit 0/2/3.
  Inspection never claims conformance, safe content, or rendering correctness.
  Checking can share its opened document with count, linearization, xref,
  object, page/image and attachment listings in native order. Failed checking
  stops them. QDF/linearize flags do not serialize an inspection-only job.
  Checking accepts page selection/collation, rotation and overlay/underlay
  inspections, plus attachment extraction. Other accepted writer settings do not
  generally transform the inspected graph: form/structure removals are ignored
  here, unlike native qpdf. QDF can supply the content-normalization default.
  Combined stream/attachment payloads retain stream-then-attachment order.
  Standalone raw/filtered stream output after checking or other informational
  output rejects under the existing ordering rules.
  Object selectors use bounded native signed-integer prefixes; nonpositive
  numbers disable object listing, while unmatched/negative generations report null.
  Generations persist across setters that omit one; overwritten JSON keys do not
  execute. The existing main-context global-option extension rejects duplicates;
  native `--global ... --` blocks retain repeated-setting semantics.
- JSON inspection may omit the output file, with or without a final `--`.
  `QpdfExecutionResult.OutputText` then carries the JSON content; no ambient
  stdout is used. File-output operations leave that property null. Returned
  text is immutable caller-owned data and obeys the job's output-byte ceiling.
- `input --json[=1|2|latest] [--json-key=]... -- output` writes document JSON
  (version plus parameters, then pages, pagelabels, acroform, attachments,
  encrypt, outlines, and objects/objectinfo for version 1 or qpdf for
  version 2). Version 1 object and objectinfo maps use complete reference keys
  such as `3 0 R`, matching page/content references. This corrects the former
  shortened `3 0` keys; clients must use the full spelling. The `trailer` key
  stays literal and version 2 retains its `obj:3 0 R` namespace.
  Pages carry object, contents, label, outlines,
  pageposfrom1, and direct image descriptors with raw dimension/colorspace values
  and filter data. Image lookup shares ordinary page-resource inheritance with
  show-pages; null/undefined overrides allow ancestors, while explicit invalid
  non-null overrides stop lookup. Resolved-null Contents is empty; array entries
  keep their raw identities and malformed non-null values remain visible.
  Image resources use unsigned raw-name byte order; their names
  preserve UTF-8 in both versions, with the `n:` escaped-name form for invalid UTF-8.
  Outlines
  resolve explicit destinations, including local GoTo actions; named destinations
  remain unresolved. Acroform reports fields with inherited
  types, names, values, and annotations. Optional field entries resolve before
  identity and inheritance decisions: indirect null behaves like direct null or
  absence. A resolved-null catalog AcroForm is absent for inspection and copying;
  malformed non-null values keep their existing outcomes.
  `--json-object={trailer|obj[,gen]}`
  limits the objects dictionaries. `--json-stream-data={none|inline|file}`
  adds base64 payloads inline or writes sidecar files (`--json-stream-prefix`
  overrides the output-derived prefix), decoding per the decode level with
  raw fallback; version 1 ignores stream payloads. `--json-output[=2|latest]`
  implies JSON with inline payloads, no decode, only the qpdf key by default,
  and no version or parameters keys. `--json-help[=1|2|latest]` (default 2) describes the matching version with same-keys/descriptive-values JSON and wins over `--json` combinations. The typed `GetDocumentJsonAsync` shares
  the engine. Sidecar paths must differ from the main JSON output; equal ordinal
  logical names reject before any output is created.
- `input.json --json-input -- output.pdf` builds a PDF from qpdf JSON version
  2 (only the top-level qpdf array is read): strict object keys, canonical
  value encodings with resolved names, inline base64 or host sidecar stream
  payloads, and renumbered classic output with the catalog first. Authored object
  declaration and reference generations other than 0, encrypted trailers, and
  stream/metadata writer options stay explicitly unsupported. Creation/update
  applies the ordinary signature-copy exclusion to all emitted mapped objects,
  including loose signature dictionaries and populated registered fields.
  Blank fields, private V/Parent dictionaries and read-only JSON stay supported.
  Excluded structures reject before opening output.
  Creation/update also rejects page, content, image and PDF layout transformations, including
  QDF/linearization and form/structure removal, across argv, job JSON and mixed
  files. Unsupported requests fail before document-data/output access; they do
  not silently publish an unchanged graph. Disabled booleans, unchanged inline/image
  thresholds and disabled pruning remain harmless. Version, static-ID, newline
  and reporting settings retain their supported behavior. The typed
  `CreatePdfFromJsonAsync` shares the
  engine. `input.pdf --update-from-json=update.json -- output.pdf` merges a
  qpdf JSON document into the PDF: objects are replaced or added by number
  (new streams still require payloads while existing streams keep their data
  when payloads are omitted; their supplied dictionary replaces the previous
  dictionary, including filter entries), the trailer is replaced when present, and the
  JSON pdfversion overrides the output version when present. Pushed inherited
  attributes stay explicitly unsupported. The typed
  `UpdatePdfFromJsonAsync` shares the engine.
  Existing PDF references are checked against the source cross-reference table
  before applying updates. Valid source generations normalize to zero; stale,
  free and missing source pairs become null, including retained trailer and
  stream-header values. Later declarations do not revive these dead source edges;
  newly authored generation-zero references can target added or replaced objects.
  Version-one object inspection and semantic text fields use the shared PDF text
  decoder, including malformed Unicode. Version-two object strings use `u:` only
  when import reproduces the original bytes; otherwise they use the existing `b:`
  binary encoding. This includes UTF-16 text representable in PDFDocEncoding,
  UTF-16LE/UTF-8 markers and damaged Unicode. Canonical UTF-16BE requiring Unicode
  stays readable. Raw name-tree keys, Limits, actions and opaque aliases therefore
  retain byte identity through export/create/update. Caller-authored `u:` values
  still select the existing canonical text encoding. This deliberately strengthens
  native qpdf round-trip fidelity; JSON encodings and output size can change.
  Returned JSON text and its UTF-8 publication agree.
  Authored name keys and values reject NUL, including `#00` in `n:` name syntax.
  Literal hashes (including the characters `#00`) and NUL text strings retain their
  identity. The reader's damaged-hash repair marker remains a separate input policy.
  Creation/update validates the normalized page tree with the shared reader before
  publication. Direct/indirect types and child arrays have identical counting;
  repeated references count each occurrence, while cycles, excessive depth and
  malformed page-tree links reject. Empty page trees remain supported. Declared
  count mismatches retain the reader's warning policy. Unlike native JSON
  pass-through, rejected trees leave the previous output untouched.
  After reference normalization, the root catalog `/Version` must be a supported
  PDF version name (1.0 through 1.7 or 2.0), directly or through one reference.
  Wrong types, malformed names, unsupported versions and reference cycles fail
  before output opens. This intentionally differs from native qpdf 12.4.2's
  pass-through behavior. Absent/null values and dangling references normalized
  to null keep the header version. Direct trailer `/Root` dictionaries are
  promoted to the first output object. Validation preserves JSON header
  negotiation: a forced header can remain below a valid catalog override, which
  the reader still uses for the effective version. The shared reader/import
  diagnostics echo malformed catalog type/version names only up to 64 raw bytes; longer names use
  a fixed description.
  Numeric exponent notation expands to exact decimal PDF text under memory
  admission. Plain integers require signed 64-bit values; real magnitudes must
  have a finite binary64 approximation, and exponents fit signed 32-bit.
  Unsupported values fail before output publication. Decimal underflow retains
  its text; integral exponent results outside Int64 retain a decimal point.
  A shared finite JSON syntax ceiling covers the supported PDF depth plus its
  envelopes; the configured PDF model nesting budget remains binding. Invalid
  Unicode text uses normal parser failures for creation and update.

JSON real output removes a PDF-only leading plus and redundant integer zeros,
while retaining significant digits, fractional spelling and negative zero.
Numbers are normalized lexically under the existing cancellation/output bounds.

## Page content profile

- `--coalesce-contents` joins each page content array into one stream with
  newline separators, decoding first and recompressing when compression is
  active. Shared original arrays and streams retain their values through other
  page/catalog references; unreferenced originals are retired after root assembly
  unless `--preserve-unreferenced` applies.
  `--normalize-content=y` rewrites newlines to LF and recodes strings
  and names canonically across the logical page program. It keeps the physical
  part count unless another requested transformation coalesces it. A complete
  token or inline-image packet crossing a boundary remains with its starting
  part; later parts can become empty. Any unreadable fragment/content error
  keeps the whole original program with a warning instead of partially normalizing it.
  Physical CR/CRLF within literal strings represent one LF byte; explicit escaped
  CR and octal values stay distinct, and escaped line continuations add no byte.
  It replaces page content edges while independent catalog, metadata and
  attachment roots retain the original stream/array values. Normalized page uses
  share replacements by immutable source identity. Independent fragments can
  share a stream replacement; context-dependent fragments are memoized with
  their complete array. Failed attempts are cached per logical source/context. Named
  inline colorspace framing also requires matching resources; independent framing
  continues to share across different page scopes.
- `--remove-unreferenced-resources` (auto by default, `yes`/`no`) drops
  unused `/Font` and `/XObject` entries using content analysis that recurses
  into form XObjects; pages with unreadable content or unresolvable names
  keep everything. Undefined usage under an explicit local form Resources
  dictionary also preserves the page scope conservatively; absent form Resources
  keeps inherited-name protection, and declared form fonts still allow pruning.
  Unreadable or unparseable Form programs preserve the enclosing scope as well.
  Non-independent Form names are gathered before pruning enclosing Forms. An
  undecidable Form dependency conservatively keeps the scopes examined for the
  copied source; complete ordinary scopes still permit pruning. Form resource
  replacements preserve original headers reached through independent page,
  catalog, metadata or attachment values, and retain sharing across selected pages.
  Font and XObject uses require bindings in their respective categories; null
  bindings do not establish a definition. Pruning uses the shared Contents reader,
  including indirect arrays and empty/null values. Malformed non-null content
  keeps its resources even without coalescing.
  Used Type 3 fonts and soft-mask groups keep the enclosing scope, including uses
  inherited through forms. Their glyph/mask programs are outside this scanner;
  a local dictionary alone does not establish dependency completeness. This can
  retain unused entries. Ordinary consumers still permit pruning.
  Auto mode prunes only when resource dictionaries are
  shared. Pruning applies to page selection, merge, and split outputs.
- --externalize-inline-images with --ii-min-bytes (default 1024) lifts inline images
  at or above the threshold into /IIm1, /IIm2 image XObjects referenced by Do.
  Abbreviated keys, filters and device color names expand; other color names resolve
  through resources with a warning on miss. Taken names are skipped and smaller
  images keep their bytes. Page conversion precedes page coalescing/normalization.
  Form conversion follows placement and appearance flattening over the assembled
  output graph, across selection, merge, split and whole-file output. It follows
  indirect Form bindings in page/Form Resources/XObject dictionaries, including
  imported page Forms and appearances made into live XObjects by flattening.
  Unflattened annotation appearances, tiling patterns, Type3 glyph programs and
  Forms reached only through ExtGState/SMask/G are outside this traversal. Binding
  the same Form as an XObject makes it eligible through that resource edge;
  independently retained originals keep their bytes. Page damaged-content recovery
  and existing Form error behavior remain.
  Original content streams and arrays retire only after final output roots exist.
  Independent catalog, page, Info, Metadata and attachment aliases keep their
  original values; `--preserve-unreferenced` retains otherwise obsolete objects.
  Eligible images need a writable XObject dictionary and resolvable named
  colorspaces before conversion. Otherwise a warning preserves the inline bytes;
  resource-less forms keep their original lookup context. Unfiltered image extents
  account for component counts and per-row packing before the terminator search,
  so sample bytes spelling EI do not end an image prematurely. Header framing can
  require parsing/admission before threshold eligibility, with ordinary malformed
  header reporting; programs shorter than the threshold skip it. Below-threshold
  images need no candidate binding. Flate, LZW, RunLength, ASCIIHex, ASCII85 and
  DCT outer packets supply encoded extents without inflating pixels or requiring
  a codec. Known extents outrank lexical lookahead. The same framing serves page
  normalization, resource/appearance scanning and checking. Unknown/unresolved
  extents preserve the program with a warning, or report unavailable checking;
  malformed known packets retain the operation's malformed-data outcomes. An
  abandoned rewrite discards earlier candidates. Rejected lookup does not
  materialize pixel data or create unattached objects under `--preserve-unreferenced`.

## Writer profile

- Classic xref tables with 20-byte entries, generation 0 throughout, and a
  trailer with `/Size`, `/Root`, `/ID`, `startxref`, and `%%EOF`.
- Version label negotiates the effective version: the greatest validated primary,
  page, overlay/underlay and attachment-import source version (the higher of each
  header and catalog `/Version`), raised by `--min-version` and
  overridden by `--force-version` (`major.minor[.extension]`; supported
  headers are 1.0-1.7 and 2.0). The managed writer uses a conservative basic
  profile floor of 1.2, covering its name escaping, Flate and linearization syntax;
  explicit force can lower the declaration. Existing positive 32-bit Adobe extension levels
  participate in minimum-version negotiation; merging uses the greatest level
  among inputs at the greatest base version. A higher base version resets the
  level. Forced versions set the exact base/level, removing `/ADBE` at level zero.
  Unchanged Adobe declarations retain their private fields; changed declarations
  use `/ADBE << /BaseVersion /M.m /ExtensionLevel N >>`.
  Ordinary catalog preservation retains other extension vendors, including
  through QDF, linearization, typed attachment edits and JSON import/update.
  Split and typed preview retain the negotiated Adobe level and omit other
  catalog extras under their existing policies. Invalid Adobe levels do not
  contribute to negotiation; indirect resolution remains bounded.
  Validated placement/import sources participate even when their selected target
  range or attachment collection is empty. Emitted page-label trees require 1.3,
  EmbeddedFiles collections 1.4, and newly authored UF filespecs 1.7; absent/null
  structures add no requirement. These known writer floors also apply when
  rebuilding retained trees. Explicit force still wins and does not transcode
  copied features. Opaque graphs are not scanned for general version conformance.
- Identifiers always emit `/ID [<id1><id2>]`: the first word preserves the
  source trailer identifier when present, otherwise it repeats the second.
  The default second word is a deterministic content hash (stable across runs
  within the contained profile, unlike upstream time-based random IDs);
  `--static-id` selects the upstream test constant
  `<31415926535897932384626433832795>`. `--deterministic-id` is accepted
  and matches the default deterministic behavior.
- Stream `/Length` is always recomputed from the emitted bytes and written
  directly, which can orphan an indirect length object as an unused entry.
  By default streams close as `data` + `endstream` with no extra newline;
  `--newline-before-endstream` always inserts `\n` before `endstream`
  (doubling a trailing newline when present), preserving PDF/A compliance.
- Object streams follow `--object-streams` (`disable`, `preserve` (the
  default), `generate`): `disable` writes only classic xref tables;
  `preserve` keeps compressed-ness from the input (no streams in, no streams
  out) without reproducing exact original groupings; `generate` packs every
  eligible non-stream object into new object streams. The rebuilt catalog and
  page-tree root stay direct; linearized page dictionaries also stay direct.
  A file with no other eligible objects therefore needs no object stream, even
  when native packs its page-tree root. This is a physical-layout difference.
  Any object streams imply
  a cross-reference stream instead of a classic table and raise the header to
  at least 1.5; a forced version below 1.5 disables object streams.
  Object-stream payloads and xref entries are Flate-packed with plain
  `/FlateDecode` (no predictor) whenever stream compression is active, and
  raw otherwise.
- Typed selection defaults to opaque preservation: page and resource streams
  keep their exact encoded bytes. Argv/job rewriting defaults to generalized
  decoding and compression, matching the reference. Control this
  with `--stream-data=compress|preserve|uncompress`, `--compress-streams=[y|n]`
  (bare means `y`), `--decode-level=none|generalized|specialized|all`, and
  `--recompress-flate`; `--compression-level=1..9` buckets Flate effort
  (`1` fastest, `2..8` optimal, `9` smallest size; default optimal). Compression
  and decode setters apply in configuration order; QDF fills unset defaults;
  `--recompress-flate` only takes effect with a decode level covering Flate.
  Decode coverage mirrors inspection (`generalized` adds ASCII/LZW/Flate,
  `specialized` adds RunLength); `all` additionally decodes 1- and 3-component
  DCT JPEGs when a codec is supplied (four-component CMYK/YCCK stays opaque), warning per undecodable stream. Corrupt streams and unknown
  specs are kept opaque under a warning; streams with more than `--max-stream-filters=n` filters (default 25, 0 keeps every filtered stream opaque) stay opaque under a warning on every decode path; `/Crypt` is never decoded. The
  typed default keeps preview stream bytes stable without flags.
- `--preserve-unreferenced` keeps every input object the selection did not
  reach, considering roots in ascending original object-number order per source after
  the reachable graph (fresh catalog/pages stay 1/2). The input catalog,
  page-tree, page, and object/xref-stream containers are excluded by
  contained-preview policy (structure is rebuilt; stream payloads travel
  through their members). Unresolvable roots and malformed objects rejected by
  contained copying are skipped under one aggregated warning. Excluded-profile,
  resource and strict load failures remain errors; explicit preservation cannot
  turn signature exclusion into a warning that drops its requested root.
  Preserved objects join object-stream packing and every
  split group under the same rules. Transitive copies retain sharing and cycles;
  a failed candidate drops its fresh copies before later roots continue. Native
  qpdf omits unreferenced objects from split output; managed split retention is
  intentional. Without the flag, unreferenced objects
  are discarded.

## Attachments profile

Optional catalog `/Names` and `/EmbeddedFiles` containers, stream `/Params` and
preferred embedded-stream candidates treat direct and indirect null equally.
Null preferred candidates allow the next existing stream key to supply the data.
Required file specifications, embedded-stream dictionaries and name-tree children
still reject unresolvable/null references; generation and cycle checks remain active.

Adding or importing attachments supplies `/PageMode /UseAttachments` when the
primary hint is absent or null, including indirect null. Explicit name hints retain
their original bytes after resolution; other copyable explicit values follow the
ordinary catalog-preservation policy. Split keeps name hints and drops opaque
extras; a new attachment then supplies the default. Removing files alone does not
create a viewing hint.

Authored keys, filenames, descriptions and copy prefixes require well-formed
UTF-16; malformed caller strings fail before publication instead of substituting
U+FFFD. Valid U+FFFD and surrogate pairs retain their identity. MIME names must be
ASCII without NUL. This PDF-name restriction does not prohibit NUL in text strings.
Existing PDF text keeps its separate lenient decoding policy.
Extraction and removal accept an explicitly empty existing name-tree key. Missing
or null parameters still reject; new attachment keys and filenames remain nonempty.
An absent empty identifier fails the same lookup as any other missing key.

Full-collection operations (listing, attachment JSON, rewriting and edits/import)
reject duplicate decoded keys, including equal text with different raw encodings.
Warning suppression does not permit choosing a different payload or publishing
duplicate JSON properties. Selective extraction retains its first-match lookup;
it does not scan unrelated branches to establish collection-wide uniqueness.

Named attachment extraction honors invocation parser controls and warning policy
through argv, mixed job-file arguments and job JSON. Explicit host ceilings stay
binding. Filter-limit refusals retain encoded bytes with a warning; `--no-warn`
suppresses warning storage while `--warning-exit-0` controls the warning exit code.

Removal matches name-tree keys before listing metadata or copying file
specifications, so malformed/null values can be removed without being listed.
Rewriting and attachment import preserve raw name bytes and safely copyable
specification graphs without requiring successful listing projection. Optional
descriptions, dates and checksums are decoded only for inspection. A preserved
entry can therefore remain unlistable; extraction still requires a valid file
specification and embedded stream. Edit counts describe retained name-tree entries.
Replacement excludes the superseded specification before copying it, while its
original key still governs conflicts. Independently retained aliases remain live.
Imported keys also retain their final prefixed identity after a recoverable
copy failure, so later collisions reject regardless of request order. Repeating
a failed source cannot waive a collision; distinct prefixes remain independent.
Missing keys and duplicate removal requests fail before publication.
Removed/replaced attachment-only graphs are excluded from output by default.
References from retained pages, catalog data or other attachments keep shared
objects alive. Explicit --preserve-unreferenced retains unreachable objects,
including removed or superseded attachments. Add requests still read their inputs
in order, even when a later request replaces the same key.

Managed split outputs retain the final document attachments; native qpdf split
outputs omit document-level attachments. Superseded additions do not become split
roots merely because they were allocated earlier in the job.

Emitted name-tree keys sort by their encoded bytes, including mixed ASCII/UTF-16
keys; listing order and request matching use decoded ordinal text.

- Embedded files live in the catalog `/Names` `/EmbeddedFiles` name tree
  (`/Names` pairs plus `/Kids` branches, within nesting/container bounds and cancellation).
  Pair/branch arrays and string keys can be direct or indirect objects; references
  resolve before type checks, with effective generations respected.
  Keys decode as PDF text (UTF-8 or UTF-16 with a byte-order mark, otherwise
  PDFDocEncoding) and match request keys by exact ordinal text.
- Attachment JSON versions 1 and 2 retain PDF name keys (`/F`, `/UF`, `/Unix`,
  `/DOS`, `/Mac`) in filename and stream maps. Optional metadata scalars resolve
  through the shared reader, including alternate embedded streams.
- `Qpdf.ListAttachmentsAsync` reports key, preferred file name, description,
  MIME type, PDF-syntax dates, stored checksum, stream number, and declared
  size without formatting listing diagnostics; genuine warnings still consume
  their quotas. `Qpdf.ExtractAttachmentAsync` finds the requested key and stages
  its decoded embedded stream to an explicit output path, using raw bytes plus
  a warning where decoding is unavailable. It does not build listing metadata
  for selected or unrelated entries. The same engine backs
  `--list-attachments` (info diagnostics, no output file) and
  `--show-attachment=key` (last key wins). Without a host output path, argv and
  job inspection return caller-owned `QpdfExecutionResult.OutputBytes`; empty
  payloads have a value of length zero, and operations without a binary payload
  return null. The library leaves returned bytes unchanged after cleanup.
  Raw/filtered object-stream inspection uses the same result contract; filtered
  bytes honor normalization and QDF defaults. Diagnostic text remains separate.
  Failed or canceled operations discard staged binary bytes; a native stdout
  prefix emitted before a later error is an explicit publication difference.
  Counters charge staged output even when it is subsequently discarded.
  Explicit output paths use the host's staged transaction instead of stdout.
- Catalog-preserving rewriting and selection retain nonattachment `/Names`
  categories through the graph copier. Embedded files use the shared attachment
  path; ordinary selection retains the primary collection independently of edits.
  Attachment blocks and removal flags belong to the main argument context:
  they can precede the input or pages block and follow the output filename.
  They remain invalid inside a pages block, and each add/copy block needs its
  own -- terminator. Placement does not reorder attachment input reads.
  `--add-attachment FILE [--key=] [--filename=] [--creationdate=] [--moddate=]
  [--mimetype=] [--description=] [--replace] --` (repeatable) embeds host
  files with key/filename defaulting to the path basename; unspecified dates
  are omitted (no ambient clock) while size and content checksums are always
  recorded. `--remove-attachment=key` (repeatable) drops entries; unknown keys
  fail without echoing bytes, and removals precede additions so re-adding wins
  ties. Collisions without `--replace` fail; existing attachments merge into
  the rebuilt tree (one admitted flat name array) with `/PageMode
  /UseAttachments` on insertions. `--copy-attachments-from FILE
  [--prefix=] --` (repeatable) copies entries from other files after
  removals and additions; prefixed keys collide as a group per source file
  and unknown or encrypted sources fail. The typed surface
  (`AddAttachmentsAsync`, `RemoveAttachmentsAsync`, `CopyAttachmentsAsync`)
  shares the same engine and reports the final attachment count.

## QDF and linearization profile

- `--qdf` emits the editable QDF view (per-object comments, uncompressed streams with indirect lengths, classic or object-stream layouts) with `--no-original-object-ids` suppressing original-ID comments;
  QDF implies generalized decoding with content normalization unless overridden.
  Normalization preserves opaque inline-image data and its delimiters, including
  filtered data; only surrounding content syntax is normalized. `FixQdfAsync`
  repairs edited QDF (stream lengths, object-stream tables, cross references,
  startxref offsets) and `RunZlibFlateAsync` compresses or uncompresses zlib bytes
  between host paths. Flate decoding, including the companion, ends at the first
  complete zlib frame and ignores later container bytes. Corrupt checksums and
  unfinished frames still reject; chunked input/output, admission and decoding
  ceilings remain binding.
  Repair requires consistent `endstream`/`endobj` and length-object framing;
  marker-shaped payloads that disrupt this sequence fail before commit. Native
  fix-qdf can instead publish an incorrect length for this ambiguous input.
  Both companions close their input before creating output staging; read or input-close failures publish nothing.
- `--linearize` emits web-optimized output through a two-pass writer with generated page-offset, shared-object, and outline hint tables (hints Flate-encode under stream compression) and optional generated object streams;
  `--linearize-pass1` captures the counting pass while `--check-linearization` and `--show-linearization` inspect through the shared engine. QDF stays disabled over linearization.
  An active first-pass target must differ from every primary/page/overlay/underlay/
  attachment input and every main PDF target, including later split groups;
  equal ordinal logical names reject before output creation.
  Without `--linearize`, the capture option creates no output, including in split
  jobs. Active split capture retains the last group's first pass at its target.

## Governance

Every path runs under one per-job governor: validated `QpdfLimits` budgets
for inputs, outputs, memory, decoded bytes, stream-filter counts, host operations, handles,
diagnostics, nesting depth, and repair error tolerance, plus cooperative cancellation.
Direct and host-supplied `MaxNestingDepth` settings must be within 1..499 (default 256);
unsupported settings reject before host access. Diagnostics use stable codes.
Messages may contain requested inspection data and bounded argument excerpts.
Host-failure diagnostics use logical paths and failure reasons; transport
exception text and stack traces are omitted.

`--parser-max-nesting=n` (1..499; zero rejects as `InvalidArguments`), `--max-stream-filters=n` (default 25), `--parser-max-container-size=n` (default 4294967295), and `--parser-max-container-size-damaged=n` (default 5000, governing repair trial-parses) tighten parser budgets per invocation through the nested job `global` object as well, within host ceilings an invocation value can never raise (exceeding one fails with BudgetExceeded); `--no-default-limits` lifts unspecified parser-error, damaged-container and filter defaults under default-constructed options; explicit invocation values and host ceilings remain binding. `--parser-max-errors=n` (default 15; zero removes the numeric threshold within host ceilings) governs errors per indirect object/member in repair mode: reaching the finite threshold replaces the object with null and warns; the proximity guard and the additional nearby orphan-value guard remain at zero; a sufficient good-token gap bypasses the orphan guard. Duplicate integer values account for disambiguating lookahead once, and nested containers restore their own orphan context. Valid-xref unknown words repair to strings; reconstructed-xref unknown words repair to null elements. Strict typed parsing and `--suppress-recovery` still fail on malformed syntax. Bounded nested/object-stream, stream-length and dictionary recovery jobs have public/native inspection and rewrite evidence; strict rejection and independent parser/memory ceilings remain protected. `--report-memory-usage` appends the processing peak memory estimate before presentation storage to successful results without affecting the exit code; `--test-json-schema` (with `--json`) validates generated top-level keys against the matching `--json-help` description and fails explicitly on drift. `--verbose` appends an operation summary; `--keep-files-open=y|n` and `--keep-files-open-threshold=n` are validated performance hints with no observable behavior (host lease policy stays authoritative).

The memory limit bounds an estimated per-job working set, not exact CLR object
sizes or process RSS. Significant buffers, decoded payloads, pixel planes and
growth overlap receive admission; coarse allowances cover other graph/codec work.
Live/peak snapshots remain immutable accounting estimates. Conservative charges
can outlast individual CLR values. Coarse execution, graph and result owners
refund storage at explicit lifetime boundaries without waiting for GC; callers
keep their supplied scopes alive through returned storage and async cleanup.
Exact small-object sizes and independent metadata-alias billing are not public
obligations. Validate the approximate target with the
small representative normal/adversarial workloads. Preserve meaningful size/structural
limits, cancellation, cleanup, immutable usable outputs and actual leak/growth
regressions. Concurrent jobs, host buffers and caller outputs need host policy.
Page/source result metadata keeps its material storage admission through the
operation's publication and cleanup, then under the caller's owner while returned
immutable collections remain usable. Individual metadata aliases need no separate
accounting receipt.

Canonical object inspection admits bounded character chunks, dictionary sort
scratch and its immutable result before allocation. It borrows immutable key
bytes, releases temporary owners on all outcomes and transfers the final string
reservation to the diagnostic that retains it. Diagnostic count and UTF-8 byte
ceilings are checked during formatting, with one final diagnostic charge.
Documents admit and transfer their input copy, xref containers and page-view
storage to explicit owners. Disposal drops implementation references and
releases exclusive storage on success/failure, including close faults; borrowed
component inputs/tables keep their caller funding. Returned values, trailer/key
aliases and page counts remain usable. Accepted parser/cache values transfer
significant storage admission through the applicable ownership scope. Immutable
numeric text and dictionary key views remain usable after owner cleanup;
process-lifetime atoms require no per-job receipts. Construction estimates transfer
to coarse graph owners without reference walks, weak tables or finalizer receipts.
Public graph admission remains with the caller's scope after document disposal;
private graph admission refunds with execution cleanup.
Failed acceptance preserves prior aliases. Byte-string parsing transfers its
fresh admitted buffer; public construction and returned copies still clone.
JSON uses an exclusive reader through serialization and closes it before output
creation. Input-close faults preserve prior main/sidecar targets on all three
routes; raw/decoded sidecar copies survive cleanup and fit one open handle.
Main/sidecar and active first-pass/input/main target collisions reject before any output
creation. Different logical names can still alias according to the host; multiple
outputs retain sequential commits rather than a group transaction.
Diagnostic records/text/list growth and failure-history export use the shared
governed machinery. Returned diagnostics form a coarse caller-owned batch, with no message memo.
Count/UTF-8 overflow fails explicitly while retaining the accepted prefix and a
bounded terminal error; suppressed warnings consume no diagnostic storage quota.
Summaries are admitted before publication and reused after commit. See
[resource API changes](COMPATIBILITY.md#resource-api-and-behavior-changes). Practical whole-job
estimates, meaningful limits and cleanup use representative workloads.

Object-plan execution snapshots counters/peaks after input cleanup, including failure
and cancellation. Whole-job memory validation uses the representative workloads; the bounded
public CPU/codec controls do not establish universal cancellation latency.

Parser attempts own their reservations separately from nested resolver/cache
loads. Failed, aborted and canceled attempts release abandoned values and
container storage; successful values retain admission under their graph owner's
lifetime. Lists/dictionaries,
immutable container copies, references and overlapping stream copies are
admitted before allocation. Working containers release after materialization.
Recovery uses complete numeric headers rather than digit suffixes. Finite-memory argv/job JSON inspections with valid/rebuilt xrefs can load repeated repaired nulls
without accumulating abandoned reservations; strict failure and cooperative
cancellation retain nested cached values and clean up attempt storage.
Successful parses release overwritten values and incoming equal keys; recovery
sanity checks release dropped orphans, while ordinary repair retains them under
generated keys. Separate parser scopes preserve independently loaded resolver/cache
values without an ownership graph walk. Finite-memory public bulk loads, retained aliases and post-overwrite
failure/cancellation have regressions; public dictionary construction still
copies caller storage. Concrete failures justify targeted corrections; there is
no additional exhaustive cache/alias/BCL-container audit.

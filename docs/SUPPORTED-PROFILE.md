# Supported profile

This describes shipped behavior. [Compatibility](COMPATIBILITY.md) records the
reference target, known differences and per-option evidence.

This is the agreed delivery profile, qualified on `219eca3` by local Windows
and Ubuntu Release/package/consumer checks plus pinned-reference and finite
visual checks. The accepted codec/build limitations
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
  decode once with validated headers and extents. `/Extends` stays opaque;
  members resolve through the current stream's header pairs, matching the
  reference reader. Writer controls can preserve or generate object streams;
  ordinary selection output normalizes to classic xrefs. Bounded CLI recovery
  and strict typed opening remain separate policies.
- `%PDF-` header at offset 0; versions 1.0-1.7 and 2.0. The effective version
  is the higher of the header and catalog `/Version`.
- Unencrypted inputs; any effective `/Encrypt` declaration rejects
  structurally, including empty-password forms.
- Strict parsing: damaged inputs fail as parse errors. The CLI repairs boundedly by
  default (warning, exit 3); `--suppress-recovery` keeps strict failure. Typed
  selection and inspection stay strict.
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
- Argv/job rewriting defaults to compression with generalized decoding. Stream-data,
  compression and decode setters apply in configuration order; job keys use lexical
  order, including mixed/file-chain layers. QDF fills unset defaults. Catalog XML
  metadata is uncompressed, and empty streams retain zero-length payloads. Broader
  transformation/default/grammar differences remain in the compatibility audit.
- --optimize-images uses the supplied codec to recompress eligible image XObjects (8-bit DeviceGray/DeviceRGB above the oi-min thresholds) as JPEG only when strictly smaller. Providers receive --jpeg-quality in 0–100 (the development Lokad.Image adapter maps 0 to 1). Already-DCT images recompress only under an explicit quality; large inline images externalize first unless --keep-inline-images. Ineligible images skip silently; an eligible operation without a codec fails explicitly.
- One input file per selection job, several per merge, through host-mediated ranged reads.

## Selection semantics

- Explicit ordered pages by 1-based index (order and duplicates preserved),
  `FirstAndLast(N)` windows, or sorted/deduplicated `Normalized` sets; out-of-range
  indexes fail explicitly and empty explicit selections are rejected.
- Ordinary rotation (`Set`/`Add`/`Subtract` multiples of 90 over output positions)
  folds over the inherited base; untouched pages keep their bytes. `--flatten-rotation`
  bakes rotation into content (see below).
- Bare `--empty` writes a zero-page PDF; it can also start a merge from a zero-page
  base whose catalog extras never carry over. Selecting from `.`, overlays and
  replacement with the synthetic base reject. Whole-file rewriting and typed
  attachment edits accept real zero-page inputs and preserve their catalog data.
  Empty split jobs have zero groups and create no output or first-pass capture.
  Argv and job JSON can add/copy attachments to the synthetic empty document.
  The bare argv output separator is optional.
- Merge options interleave per-source groups (collation), rotate, and emit explicit
  page labels or merge source label trees with page-index offsets (unlabeled pages
  fabricate continuations, runs compress using copied identities). Indirect tree
  arrays, keys and starts retain their direct-value semantics. An optional split group size partitions
  output through the same engine.
- Output carries a fresh catalog with the copied pages only; each occurrence is an
  independent page object sharing immutable resources. Shared non-page objects
  are copied once; `/Parent` links are dropped. Results carry the source page
  count and the exact per-output-page source mapping.
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
  prune them. Widget annotations travel with appearances, while
  document info (`/Info`, ModDate-only under `--remove-info`) and the catalog
  metadata stream are preserved unless `--remove-info`/`--remove-metadata`
  drop them (`--remove-info` also drops metadata).
- Repeated runs over the same input and options are byte-identical.
- Publication is staged: validation, budgets, and serialization complete
  before commit, so failures leave existing targets untouched. Every lease
  closes on success, failure, and cancellation.
- `--split-pages` publishes one file per page group with pruned objects and
  sliced labels (typed callers use `PdfPageMergeOptions.SplitPages`); `--replace-input`
  publishes at the single input's path with no backup. The host defines physical
  replacement semantics. `InMemoryQpdfHost` keeps registered inputs and published
  outputs separate: reopening that path reads the original registration until
  the caller exports the output with `TryGetOutput` and registers it with `AddInput`.
  Partial split outputs can remain if a later group fails.
- --overlay and --underlay blocks (--file, --to, --from, --repeat, repeatable with order kept) stamp whole pages from other files as /Fx form XObjects below or above each output page, centered with shrink-to-fit placement that inverts destination rotation and scale. Ranges use the full shared grammar with an empty --from= letting --repeat supply every page. Missing files fail; damaged or unsupported content warns and skips. Applied pages contribute placement-transformed annotation copies, including imported widgets and form fields within the form limitations above. Shared appearance streams transform once within each placement; separate placements stay independent. Empty or entirely skipped annotation imports preserve the destination array, including an absent Annots entry.
  Source generations are validated before placement memo reuse; stale references
  stay null in either traversal order. Widget import retains actual field
  references through parent walks, including valid nonzero generations.
- --flatten-rotation bakes page rotation into content: boxes rotate per the page angle, a counter-transform wraps the content streams, /Rotate is dropped, and annotation rectangles move with the page. Appearance headers are copied per transformed page while payload bytes remain shared; untouched pages retain their original appearances. Direct and indirect appearance-state dictionaries and matrices are equivalent.
- --flatten-annotations with all, print, or screen stamps annotation appearance streams as /Fxo form XObjects into page content and drops the flattened annotations. Print mode keeps only print-flagged annotations and screen mode drops no-view ones; widgets wait for generated appearances when the document needs them. Annotations without appearances stay unless they carry an appearance dictionary, and field default resources are not merged. Pages without actionable rotation stay byte-identical; damaged content or boxes warn and keep the page.
- `--generate-appearances` regenerates text/list widget appearances from field
  values and inherited or form-level default appearance programs, then clears
  the condition so later flattening can stamp the result. It replaces a bounded,
  matching `/Tx` block and preserves unrelated content. Parent-held fields retain
  their terminal identity; checkboxes synchronize raw state names. Radios and
  pushbuttons warn and keep their state, and empty signature fields skip.
  Copying populated signature values, including overlay and underlay field
  imports, is excluded; typed calls reject `/Sig` and
  argv/job JSON report `UnsupportedCryptoOperation`. An absent or null `/V`
  remains copyable; inspection does not verify signatures. Local
  appearance fonts win over AcroForm default resources; field default resources
  are not merged. Unusable defaults/resources and malformed programs warn and
  preserve appearances. Structural, decompression, memory and cancellation limits
  remain binding.

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
  carry capped per-object detail. Page content tokenizes content streams
  (decode-level gated) instead of skipping; linearization
  passes when absent and validates dictionaries, hint streams, and
  bit-level hint tables when present. An absent or null optional `/P` uses its
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
  Checking accepts selection/transformation inspections and attachment extraction.
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
  version 2). Pages carry object, contents, label, outlines,
  pageposfrom1, and direct image descriptors with raw dimension/colorspace values
  and filter data. Image resources use unsigned raw-name byte order; their names
  preserve UTF-8 in both versions, with the `n:` escaped-name form for invalid UTF-8.
  Outlines
  resolve explicit destinations, including local GoTo actions; named destinations
  remain unresolved. Acroform reports fields with inherited
  types, names, values, and annotations. `--json-object={trailer|obj[,gen]}`
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
  payloads, and renumbered classic output with the catalog first. Generations
  other than 0, encrypted trailers, and stream/metadata writer options stay
  explicitly unsupported. The typed `CreatePdfFromJsonAsync` shares the
  engine. `input.pdf --update-from-json=update.json -- output.pdf` merges a
  qpdf JSON document into the PDF: objects are replaced or added by number
  (new streams still require payloads while existing streams keep their data
  when payloads are omitted), the trailer is replaced when present, and the
  JSON pdfversion overrides the output version when present. Pushed inherited
  attributes stay explicitly unsupported. The typed
  `UpdatePdfFromJsonAsync` shares the engine.
  After reference normalization, the root catalog `/Version` must be a supported
  PDF version name (1.0 through 1.7 or 2.0), directly or through one reference.
  Wrong types, malformed names, unsupported versions and reference cycles fail
  before output opens. This intentionally differs from native qpdf 12.4.2's
  pass-through behavior. Absent/null values and dangling references normalized
  to null keep the header version. Direct trailer `/Root` dictionaries are
  promoted to the first output object. Validation preserves JSON header
  negotiation: a forced header can remain below a valid catalog override, which
  the reader still uses for the effective version. The shared reader/import
  diagnostic echoes malformed names only up to 64 raw bytes; longer names use
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
  active. `--normalize-content=y` rewrites newlines to LF and recodes strings
  and names canonically, keeping damaged streams with a warning.
- `--remove-unreferenced-resources` (auto by default, `yes`/`no`) drops
  unused `/Font` and `/XObject` entries using content analysis that recurses
  into form XObjects; pages with unreadable content or unresolvable names
  keep everything. Undefined usage under an explicit local form Resources
  dictionary also preserves the page scope conservatively; absent form Resources
  keeps inherited-name protection, and declared form fonts still allow pruning.
  Auto mode prunes only when resource dictionaries are
  shared. Pruning applies to page selection, merge, and split outputs.
- --externalize-inline-images with --ii-min-bytes (default 1024) lifts inline images at or above the threshold into /IIm1, /IIm2 image XObjects referenced by Do. Abbreviated keys, filters, and device color names expand, other color names resolve through page resources with a warning on miss, taken names are skipped, and smaller images keep their bytes. Runs before coalescing and normalization on pages and indirect forms across selection, merge, split, and whole-file output, keeping damaged content with a warning.

## Writer profile

- Classic xref tables with 20-byte entries, generation 0 throughout, and a
  trailer with `/Size`, `/Root`, `/ID`, `startxref`, and `%%EOF`.
- Version label negotiates the effective version: the greatest input version
  (the higher of each header and catalog `/Version`), raised by `--min-version` and
  overridden by `--force-version` (`major.minor[.extension]`; supported
  headers are 1.0-1.7 and 2.0). Existing positive 32-bit Adobe extension levels
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
  through their members). Unresolvable roots and objects rejected by contained
  copying are skipped under one aggregated warning; strict load failures remain
  errors. Preserved objects join object-stream packing and every
  split group under the same rules. Transitive copies retain sharing and cycles;
  a failed candidate drops its fresh copies before later roots continue. Native
  qpdf omits unreferenced objects from split output; managed split retention is
  intentional. Without the flag, unreferenced objects
  are discarded.

## Attachments profile

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
  categories through the graph copier. Embedded files use the attachment-edit
  path; ordinary page selection omits them unless attachment edits apply.
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
  QDF implies generalized decoding with content normalization unless overridden. `FixQdfAsync` repairs edited QDF (stream lengths, object-stream tables, cross references, startxref offsets) and `RunZlibFlateAsync` compresses or uncompresses zlib bytes between host paths.
  Both companions close their input before creating output staging; read or input-close failures publish nothing.
- `--linearize` emits web-optimized output through a two-pass writer with generated page-offset, shared-object, and outline hint tables (hints Flate-encode under stream compression) and optional generated object streams;
  `--linearize-pass1` captures the counting pass while `--check-linearization` and `--show-linearization` inspect through the shared engine. QDF stays disabled over linearization.
  An active first-pass target must differ from every main PDF target, including
  later split groups; equal ordinal logical names reject before output creation.
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
Main/sidecar and active first-pass/main target collisions reject before any output
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

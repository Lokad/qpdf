# Supported profile (M4)

This describes exactly what the library does today. Everything else in
`docs/COMPATIBILITY.md` is a roadmap, not a claim.

## Entry paths

Three routes converge on one selection plan executed by the shared engine:

- Typed: `Qpdf.SelectPageAsync` with explicit, `FirstAndLast`, or `Normalized` page sets;
  `Qpdf.SelectPagesAsync` merges ordered `PdfPageSource` inputs with per-page source
  mapping; `Qpdf.CheckAsync` with `QpdfCheckOptions` (decode level) for partial inspection.
- Argv: `Qpdf.ExecuteAsync` accepts `input --pages [--file=]file [--range=]n
  -- output` (repeated file/range groups with single-integer ranges), `input --check`, `--job-json-file`
  file`, `@file` response files (one argument per line), `--help`/`--version`/`--job-json-help`/`--completion-bash`/`--completion-zsh`,
  `--rotate=[+|-]angle[:range]`, `--split-pages[=n]`, `--replace-input`,
  and the `--suppress-recovery`/`--warning-exit-0`/`--no-warn` modifiers.
- Job JSON: `Qpdf.ExecuteJobAsync` accepts `{inputFile, outputFile,
  pages:[{file, range}, ...]}` concatenating entries in order, plus optional
  `rotate` and `setPageLabels` arrays; inspection and `collate` have no job
  key yet.

All routes return explicit outcomes (`PdfPageSelectionResult` or
`QpdfExecutionResult` with exit codes 0/2/3). Anything outside the subset —
ranges, `--empty`, `--replace-input`, stdout,
response files, unknown options — fails explicitly. Password and encryption
flags fail with `UnsupportedCryptoOperation` and exit 2 without retaining or
echoing values.

## Input profile

- Chained cross references: classic tables and Flate- or LZW-compressed
  xref streams with exact entry sizing and coverage; `/Prev` chains and hybrid
  `/XRefStm` links merge latest-wins with cycle and revision budgets. Object streams
  decode once with validated headers and extents. No `/Extends` links, recovery,
  or object-stream preservation in output.
- `%PDF-` header at offset 0; versions 1.0-1.7 and 2.0 with catalog `/Version` override.
- Unencrypted inputs; any effective `/Encrypt` declaration rejects
  structurally, including empty-password forms.
- Strict parsing: damaged inputs fail as parse errors. The CLI repairs boundedly by
  default (warning, exit 3); `--suppress-recovery` keeps strict failure. Typed
  selection and inspection stay strict.
  Malformed, mistyped, unresolvable, or unsupported catalog `/Version` fails;
  null or absent `/Version` keeps the header.
- Page streams travel opaquely and are never decoded: direct and indirect
  `/Length` resolve with a load-cycle guard, unknown filter encodings pass through untouched, and
  copying a page needs no image support. Structural streams decode through Flate,
  LZW, ASCIIHex, ASCII85, and RunLength (direct or indirect filter specs), with
  shared predictors, plus DCTDecode at `--decode-level=all` for 1- and 3-component JPEGs (four-component stays opaque); anything else fails explicitly as unsupported.
- --optimize-images recompresses eligible image XObjects (8-bit DeviceGray/DeviceRGB above the oi-min thresholds) as JPEG only when strictly smaller, with --jpeg-quality remapping 0 to 1; already-DCT images recompress only under an explicit quality, large inline images externalize first unless --keep-inline-images, and every skip is silent.
- One input file per selection job, several per merge, through host-mediated ranged reads.

## Selection semantics

- Explicit ordered pages by 1-based index (order and duplicates preserved),
  `FirstAndLast(N)` windows, or sorted/deduplicated `Normalized` sets; out-of-range
  indexes fail explicitly and empty explicit selections are rejected.
- Ordinary rotation (`Set`/`Add`/`Subtract` multiples of 90 over output positions)
  folds over the inherited base; untouched pages keep their bytes. Flattening
  stays unsupported.
- Merge options interleave per-source groups (collation), rotate, and emit
  explicit page labels; source label trees are dropped and never merged.
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
  other page prune to null, so omitted pages never resurrect. Widget annotations
  travel with appearances; `/AcroForm`, outlines, and names are dropped, while
  document info (`/Info`, ModDate-only under `--remove-info`) and the catalog
  metadata stream are preserved unless `--remove-info`/`--remove-metadata`
  drop them (`--remove-info` also drops metadata).
- Repeated runs over the same input and options are byte-identical.
- Publication is staged: validation, budgets, and serialization complete
  before commit, so failures leave existing targets untouched. Every lease
  closes on success, failure, and cancellation.
- `--split-pages` publishes one file per page group with pruned objects and
  sliced labels; `--replace-input` overwrites the single input with no backup.
  Partial split outputs can remain if a later group fails.
- --overlay and --underlay blocks (--file, --to, --from, --repeat, repeatable with order kept) stamp whole pages from other files as /Fx form XObjects below or above each output page, centered with shrink-to-fit placement that inverts destination rotation and scale. Ranges accept plain numbers, dash ranges, and comma lists, and an empty --from= lets --repeat supply every page. Missing files fail, damaged overlay content warns and skips, and overlay annotations are not copied.
- --flatten-rotation bakes page rotation into content: boxes rotate per the page angle, a counter-transform wraps the content streams, /Rotate is dropped, and annotation rectangles move with the page.
- --flatten-annotations with all, print, or screen stamps annotation appearance streams as /Fxo form XObjects into page content and drops the flattened annotations. Print mode keeps only print-flagged annotations and screen mode drops no-view ones; widgets wait for generated appearances when the document needs them. Annotations without appearances stay unless they carry an appearance dictionary, and field default resources are not merged. Pages without actionable rotation stay byte-identical; damaged content or boxes warn and keep the page.
- --generate-appearances regenerates widget appearance streams when the catalog needs them (text/list with field values and default appearance, checkbox/radio state sync), then clears the condition so a later --flatten-annotations stamps fresh appearances. Parent-held field nodes stay out of reach, field default resources are not merged, and damaged appearances warn and keep the field.

## Inspection profile

- `Qpdf.CheckAsync` reports eight declared checks: header, cross-reference,
  trailer, page tree, object-graph traversal, decode-level-gated stream
  decoding, page content, and linearization.
- Skipped stages stay visible with reasons and never pass silently; failures
  carry capped per-object detail. Content (M7) always skips; linearization
  passes when absent and validates dictionaries, hint streams, and
  bit-level hint tables when present; unavailable
  filters skip streams with a warning at level `all`.
- `input --check` publishes no files and maps diagnostics to exit 0/2/3.
  Inspection never claims conformance, safe content, or rendering correctness.
- `input --json[=1|2|latest] [--json-key=]... -- output` writes document JSON
  (version plus parameters, then pages, pagelabels, acroform, attachments,
  encrypt, outlines, and objects/objectinfo for version 1 or qpdf for
  version 2). Pages carry object, contents, label, outlines, and
  pageposfrom1 with empty images until content parsing lands (M7); outlines
  resolve explicit destinations only; acroform reports fields with inherited
  types, names, values, and annotations. `--json-object={trailer|obj[,gen]}`
  limits the objects dictionaries. `--json-stream-data={none|inline|file}`
  adds base64 payloads inline or writes sidecar files (`--json-stream-prefix`
  overrides the output-derived prefix), decoding per the decode level with
  raw fallback; version 1 ignores stream payloads. `--json-output[=2|latest]`
  implies JSON with inline payloads, no decode, only the qpdf key by default,
  and no version or parameters keys. `--json-help[=1|2|latest]` (default 2) describes the matching version with same-keys/descriptive-values JSON and wins over `--json` combinations. The typed `GetDocumentJsonAsync` shares
  the engine.
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

## Page content profile

- `--coalesce-contents` joins each page content array into one stream with
  newline separators, decoding first and recompressing when compression is
  active. `--normalize-content=y` rewrites newlines to LF and recodes strings
  and names canonically, keeping damaged streams with a warning.
- `--remove-unreferenced-resources` (auto by default, `yes`/`no`) drops
  unused `/Font` and `/XObject` entries using content analysis that recurses
  into form XObjects; pages with unreadable content or unresolvable names
  keep everything. Auto mode prunes only when resource dictionaries are
  shared. Pruning applies to page selection, merge, and split outputs.
- --externalize-inline-images with --ii-min-bytes (default 1024) lifts inline images at or above the threshold into /IIm1, /IIm2 image XObjects referenced by Do. Abbreviated keys, filters, and device color names expand, other color names resolve through page resources with a warning on miss, taken names are skipped, and smaller images keep their bytes. Runs before coalescing and normalization on pages and indirect forms across selection, merge, split, and whole-file output, keeping damaged content with a warning.

## Writer profile

- Classic xref tables with 20-byte entries, generation 0 throughout, and a
  trailer with `/Size`, `/Root`, `/ID`, `startxref`, and `%%EOF`.
- Version label negotiates the effective version: the greatest input version
  (header with catalog `/Version` override), raised by `--min-version` and
  overridden by `--force-version` (`major.minor[.extension]`; supported
  headers are 1.0-1.7 and 2.0). A positive extension level adds a direct
  catalog `/Extensions << /ADBE << /BaseVersion /M.m /ExtensionLevel N >> >>`.
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
  eligible non-stream object into new object streams. Any object streams imply
  a cross-reference stream instead of a classic table and raise the header to
  at least 1.5; a forced version below 1.5 disables object streams.
  Object-stream payloads and xref entries are Flate-packed with plain
  `/FlateDecode` (no predictor) whenever stream compression is active, and
  raw otherwise.
- Stream data defaults to opaque preservation: page and resource streams
  keep their exact encoded bytes (upstream defaults to compress). Opt in
  with `--stream-data=compress|preserve|uncompress`, `--compress-streams=[y|n]`
  (bare means `y`), `--decode-level=none|generalized|specialized|all`, and
  `--recompress-flate`; `--compression-level=1..9` buckets Flate effort
  (`1` fastest, `2..8` optimal, `9` smallest size; default optimal). Explicit
  `--compress-streams`/`--decode-level` override the `--stream-data` mapping;
  `--recompress-flate` only takes effect with a decode level covering Flate.
  Decode coverage mirrors inspection (`generalized` adds ASCII/LZW/Flate,
  `specialized` adds RunLength); `all` behaves as `specialized` until JPEG
  support (M8), warning per undecodable stream. Corrupt streams and unknown
  specs are kept opaque under a warning; streams with more than `--max-stream-filters=n` filters (default 25, 0 keeps every filtered stream opaque) stay opaque under a warning on every decode path; `/Crypt` is never decoded. The
  default profile differs intentionally from upstream compress defaults so
  typed previews stay byte-stable without flags.
- `--preserve-unreferenced` keeps every input object the selection did not
  reach, appended in ascending original object-number order per source after
  the reachable graph (fresh catalog/pages stay 1/2). The input catalog,
  page-tree, page, and object/xref-stream containers are excluded by
  contained-preview policy (structure is rebuilt; stream payloads travel
  through their members), and unloadable objects are skipped under one
  aggregated warning. Preserved objects join object-stream packing and every
  split group under the same rules. Without the flag, unreferenced objects
  are discarded.

## Attachments profile

- Embedded files live in the catalog `/Names` `/EmbeddedFiles` name tree
  (`/Names` pairs plus `/Kids` branches, within nesting and work budgets).
  Keys decode as PDF text (UTF-16 with a byte-order mark, otherwise
  PDFDocEncoding) and match request keys by exact ordinal text.
- `Qpdf.ListAttachmentsAsync` reports key, preferred file name, description,
  MIME type, PDF-syntax dates, stored checksum, stream number, and declared
  size; `Qpdf.ExtractAttachmentAsync` stages fully decoded bytes to an
  explicit output path (raw bytes plus a warning where decoding is
  unavailable, e.g. JPEG until M8). The same engine backs
  `--list-attachments` (info diagnostics, no output file) and
  `--show-attachment=key` (last key wins; output file required). Upstream
  writes extracts to stdout; without a stdout binary contract this profile
  publishes them to the declared output instead.
- Page selection still drops `/Names` unless attachment edits apply.
  `--add-attachment FILE [--key=] [--filename=] [--creationdate=] [--moddate=]
  [--mimetype=] [--description=] [--replace] --` (repeatable) embeds host
  files with key/filename defaulting to the path basename; unspecified dates
  are omitted (no ambient clock) while size and content checksums are always
  recorded. `--remove-attachment=key` (repeatable) drops entries; unknown keys
  fail without echoing bytes, and removals precede additions so re-adding wins
  ties. Collisions without `--replace` fail; existing attachments merge into
  the rebuilt tree (flat leaf, chunked past 64 entries) with `/PageMode
  /UseAttachments` on insertions. `--copy-attachments-from FILE
  [--prefix=] --` (repeatable) copies entries from other files after
  removals and additions; prefixed keys collide as a group per source file
  and unknown or encrypted sources fail. The typed surface
  (`AddAttachmentsAsync`, `RemoveAttachmentsAsync`, `CopyAttachmentsAsync`)
  shares the same engine and reports the final attachment count.

## Governance

Every path runs under one per-job governor: validated `QpdfLimits` budgets
for inputs, outputs, memory, work, decoded bytes, host operations, handles,
diagnostics, and nesting depth, plus cooperative cancellation. Diagnostics
carry stable codes and never embed input content, paths beyond logical
names, host exception text, or stack traces.

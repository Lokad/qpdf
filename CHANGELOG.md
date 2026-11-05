# Changelog

## Unreleased

- Generate imported widget appearances with source defaults/resources and layout
  before placement. Ignore skipped placements when establishing readiness, and
  retain field registrations even when private references copied their graphs first.

- Treat direct and indirect nulls alike when classifying overlay form presence
  and field identity. Keep null Fields absent and ignore null T/FT/Kids identity
  entries, while retaining existing empty and malformed non-null handling.

- Project stored annotation Border radii/widths/dashes and supported BS widths
  and active dashes into transformed page units. Materialize required defaults,
  preserve zero/unused fields and private originals, and share projected values
  across equivalent uses. Refuse unusable required projections before publication;
  retain supplied appearances without adding general border generation.

- Treat absent/null page-label numbering styles as prefix-only. Skip inactive
  counters, omit them from authored/derived labels, and compact equal prefix runs
  through selection and splitting while retaining private original values.

- Copy preserved whole-file page-label trees without building a discarded
  per-page model. Keep raw values, aliases and integer spelling, including large
  starts; retain graph guards and semantic validation when labels are interpreted.

- Apply explicit output page labels before splitting, consistently across typed,
  argv and job routes. Preserve output-order continuations and private source
  aliases without interpreting the source labels they replace.

- Preserve NoRotate annotation anchors and orientation during rotation baking
  and overlay/underlay placement. Keep equivalent appearance frames shared and
  bind inherited source resources without rotating the preserved appearance.
  Private aliases and appearance payloads remain intact.

- Scale Line leader lengths, extensions and offsets with imported page units.
  Scale used caption offsets along/across the line, retaining signs and local
  axes. Preserve pure rotation, zero/null values and unused/private fields.

- Rotate and scale `RD` inner-rectangle insets for FreeText, Square, Circle and
  Caret annotations. Preserve original private values and translation-only
  identities; reject unusable non-null insets when their linear frame changes.

- Preserve null optional FreeText callouts when baking page rotation. Treat direct
  and indirect nulls as absent without allocating coordinates; keep malformed
  non-null rejection, private aliases and transactional failure handling.

- Compact equivalent selected/merged page-label runs across direct and indirect
  style/prefix values. Compare resolved name and string bytes through the copied
  pool, retaining emitted references, private aliases and whole-file raw trees.

- Use existing byte-key lookup for attachment and appearance queries, avoiding
  temporary PDF names. Keep filename precedence, optional metadata, reference
  handling and diagnostic text unchanged.

- Classify page-label container types directly from name bytes, avoiding display
  strings for long unknown names while preserving containment and opaque values.

- Keep temporary filter names scoped to private image, attachment, appearance and
  JSON descriptor consumers. Retain full names when results or errors can escape;
  preserve output bytes while refunding names after private plans close.

- Release raw object-stream assembly storage after successful compression, keeping
  the stored buffer funded through writing. Preserve ordinary, QDF and linearized
  output bytes and cleanup on cancellation or allocation refusal.

- Make automatic resource pruning recognize indirect Kids arrays and Form Subtype
  names, matching equivalent direct values. Preserve independently rooted original
  resources, shared image identity and explicit pruning choices.

- Borrow immutable input slices during unfiltered compression, opaque JSON stream
  projection and JPEG separator trimming. Remove intermediate payload copies while
  retaining durable sidecar copies and escaping stream ownership.

- Compute attachment checksums incrementally with cancellation checks between
  bounded chunks. Admit one temporary hash context and the small retained digest;
  preserve checksum and payload bytes while refunding temporary storage on failure.

- Check cancellation during object-number sorting in structural inspection and
  unreferenced-object preservation. Reuse the existing in-place governed sorter
  and numeric order without additional collection or comparer storage.

- Reject bare indirect references used as object definitions in strict binary
  loading, including object-stream members. Bounded recovery substitutes null
  with a warning. Keep legal nested references and align PDF/JSON publication
  with the existing JSON-import constraint. Optional attachment scalar inspection
  omits malformed referenced values without dropping a usable attachment or its
  payload; resource, cancellation and host failures propagate. Public signatures
  are unchanged.

- Expand inline-only Indexed family/device-base aliases when externalizing images,
  including QDF. Preserve palette and sample bytes, canonical array identities
  and unrelated color-space values. This corrects output vocabulary that native
  qpdf and renderers can tolerate despite its restriction to inline dictionaries.

- Apply source page rotation and UserUnit to separately imported overlay/underlay
  annotation rectangles and appearance headers, matching the source Form frame.
  Keep private appearance aliases and shared payloads intact. This deliberately
  corrects native qpdf's missing source transform; public signatures are unchanged.

- Compose existing appearance matrices before changing page frame during rotation
  baking and overlay/underlay placement. Noncommuting matrices now retain the
  correct artwork orientation; private aliases and malformed-matrix policy stay
  intact. Finite geometry is judged using the corrected composition order.

- Transform imported popup geometry when the popup is reachable only from its
  owning annotation. Listed and linked-only popups receive the same frame once
  per placement, keeping Parent links and the original Annots membership.

- Transform recognized absolute annotation coordinates during rotation baking and
  placement: line endpoints, FreeText callouts, link/text-markup quadrilaterals,
  polygon/polyline vertices and ink paths. Resolve indirect values and retain
  private aliases and point order. Non-null malformed arrays reject when their
  frame changes; null/empty handling and unrelated private fields stay intact.

- Preserve PDF byte-string identity through JSON v2 export and import. Use the
  existing binary encoding whenever text would re-encode differently, retaining
  distinct destination keys/actions, Limits, opaque values and malformed Unicode.
  Canonical Unicode stays readable; v1 inspection and authored text semantics
  remain unchanged. This intentionally strengthens native qpdf round-trip fidelity.

- Author required page Parent backlinks to the new output tree while copying
  page headers. Keep those edges through split remapping, QDF and linearization;
  preserve other Parent roles and read-only source ancestry. This corrects an
  incomplete output graph that native syntax checking could accept.

- Preserve supported source catalog meaning for retained pages and actions.
  Combine static View optional-content groups/OFF states by identity, retain
  unchanged catalog-root aliases, and bind explicit URI bases and source-local
  named destinations when their context would otherwise be lost. Preserved primary
  context keeps its original values and identities. Keep field registration,
  action cycles, private aliases and primary metadata/attachment ownership.
  Repeated pages keep the first registered destination. Single complex layer
  configurations remain intact; unsupported composition or required source
  contexts fail before publication. This deliberately strengthens native qpdf
  context preservation. No public signatures or generic typed-catalog defaults change.

- Apply page-range exclusions only to their preceding positive group, preserving
  earlier groups and duplicate order. Selection, relative rotation and overlay/
  underlay to/from/repeat ranges share this correction. Consecutive exclusions
  stay in that group; final odd/even filtering and entry budgets retain their rules.

- Finish Form inline conversion, selected resource pruning and image optimization
  after placements and appearance flattening over the assembled output graph.
  Process supported imported and flattened XObjects while preserving independent
  original aliases and early page conversion/coalescing/normalization order.
  This deliberately strengthens coverage where native qpdf misses flattened
  appearances or late unused resources. Newly reached eligible optimization also
  requires the host JPEG codec and fails before publication when it is absent.
  Clarify XObject-resource traversal scope; unflattened AP, pattern/glyph programs
  and soft-mask-only Forms retain their existing scope. No public signature changes.

- Treat configured nesting/container exhaustion as a budget failure throughout
  reading, copying and presentation. Rewriting fails before publication instead
  of dropping retained catalog data, Info, XML metadata or attachments. Typed
  reader/copy paths now throw `QpdfLimitException` rather than `QpdfParseException`;
  execution reports `BudgetExceeded`, while checking can report failed items.
  Keep malformed-input salvage, structural ceilings, cancellation and cleanup.

- Preserve each retained source field's appearance resources, DA and Q defaults
  in page merges and overlay/underlay imports. Resolve indirect programs before
  renaming fonts and rewrite source fallbacks before comparing inherited defaults.
  Keep original value aliases and shared rewritten programs; ordinary field
  ancestry and appearance-local resources retain their existing policy.

- Correct JSON v1 object and objectinfo keys to include the reference suffix,
  such as `3 0 R`, so emitted page references address those maps directly.
  Clients using the former shortened keys must update their lookups. Trailer
  spelling and JSON v2 object keys keep their existing contract.

- Apply the existing signature-copy exclusion to JSON creation/update, including
  loose objects and populated registered fields. Explicit unreferenced preservation
  now fails on excluded structures instead of warning and dropping requested roots.
  Retain malformed-data recovery, blank fields and read-only inspection; share
  the current policy and cache without compatibility forwarding paths.

- Include effective overlay/underlay and attachment-import versions and Adobe
  levels in PDF output negotiation, including validated empty selections. Apply
  a conservative basic writer floor of 1.2 and known floors to emitted
  label/attachment structures; explicit force still wins.
  Preserve raw graphs and document the stronger defaults where native qpdf also
  retains a lower declaration.

- Check and normalize one logical page program across physical content streams,
  preserving split inline-image context and opaque samples. Normalization retains
  part count and independent original aliases; unreadable programs keep all their
  parts. Check details now count page programs rather than individual streams.
  Borrow immutable raw slices until joining, avoiding a payload copy for each part;
  retain decoded ownership transfers and independent final buffers.

- Ignore row geometry when `Predictor` is absent or 1, restoring usable Flate/LZW
  data in metadata, attachments and pages. Keep LZW `EarlyChange` independent;
  active predictor metadata now validates in fixed parameter order.

- Keep external-file streams opaque, refuse effective-data extraction and required
  structural decoding, and report unavailable check coverage. Preserve raw
  descriptors/placeholders instead of interpreting or recompressing them as data.
  Linearization checks/reports also skip unavailable external hint data.

- Resolve supported Flate hint filters through the shared pipeline, including
  arrays, indirect values and null parameters. Honor the stream-filter ceiling
  without claiming that unavailable hint tables were validated.

- Observe cooperative cancellation while scanning classic-xref section tokens,
  including long malformed tokens, comments and whitespace.

- Accept equivalent signed integer spelling in object-stream headers/startxref
  and CR/LF/CRLF classic xrefs with count padding. Share the existing converter;
  preserve structural bounds and reject overflowing subsection ranges explicitly.

- Accept bytes after completed Flate frames, including in object-stream inputs
  and the zlib-flate companion. Remove the redundant checksum/suffix scan;
  the complete zlib decoder and bounded input still reject corruption/truncation.

- Resolve indirect object-stream Type/N/First descriptors through the existing
  source resolver, preserving valid metadata, attachments and page members while
  retaining type/range, generation, cycle and decompression checks.

- Keep writer syntax caches retryable after cold allocation failure. The capped
  package consumer also verifies a successful engine run after releasing heap
  pressure and closing the original host lease.

- Preserve private appearance aliases during overlay/underlay placement. Compose
  placement matrices and bind source resources through fresh appearance headers,
  keeping stream payloads and sharing within a placement.
- Request final-root retirement when copied objects are replaced, removing obsolete
  resource/annotation graphs without losing independent aliases or explicit
  unreferenced preservation. Remove transform-specific retirement bookkeeping.

- Fit overlays/underlays to the destination TrimBox/CropBox intersection with
  MediaBox, preserving original box values and aliases. Share the bounded
  intersection calculation with source Forms; skip empty destination bounds
  before decoding their content, with a warning.

- Reject unsupported transformations and PDF layout settings in JSON creation
  and updates instead of silently discarding them. Align job/mixed JSON inspection
  rejections with argv while preserving documented inspection writer ignores,
  disabled settings and supported JSON controls.

- Intersect imported overlay/underlay TrimBox and CropBox bounds with MediaBox
  for clipping and fitting, including reversed and indirect rectangles. Preserve
  independently referenced original boxes; skip empty or unusable bounds before
  decoding content or copying unused resources, with a warning.

- Protect Type 3 glyph resources when ExtGState Font selects the font, including
  indirect arrays and subtype names. Keep ordinary font pruning and soft-mask
  guards; native qpdf can lose glyph artwork through this graphics-state path.

- Read overlay/underlay UserUnit from the source leaf page, keeping private
  ancestor values out of placement while preserving actual page inheritance.

- Validate page-tree ancestry before excluding original objects from explicit
  unreferenced preservation. Keep private targets and report uncopyable roots
  under the existing warning policy; retain loose trees and genuine exclusions.

- Validate source child identities before recording field membership, preserving
  live registrations and calculation order after stale-generation links. Check
  cancellation on both sides of child resolution.

- Resolve Widget subtypes consistently in source/pool field membership and form
  checking; share the membership walk. Check Parent backlinks against the live
  resolved parent identity, preserving valid nonzero generations and reporting
  stale-generation links.

- Resolve optional outline First/Next values before traversing them. Indirect
  nulls match direct-null/absent links; missing references and malformed non-null
  entries still reject before publishing JSON.

- Prepare complete secondary page-root unions for fileless inspection, preserving
  selected links across repeated source groups. Transfer each completed pool once
  and fund its immutable values through inspection before closing the private
  reader. Imported root numbering may change; primary identities and physical
  xrefs remain stable. Zero collation validates ranges without retaining roots.

- Restrict split publications to their own page identities. Clear foreign page
  references through navigation, Info, metadata, attachments and form graphs,
  preserving within-group links, shared non-page values and transactional output.
  Unreferenced-object preservation no longer restores pages outside the group.

- Share source document identity across repeated page groups, preserving links and
  metadata/attachment targets selected in later groups. Keep original group
  mappings, counts and order, with one stable input lease per logical document.
  Reuse annotation header copying for duplicate form pages so widgets, field
  branches and popup/reply links stay independent without copying payloads.
  Exclude zero-sized collation groups from copied roots while retaining request
  validation and original group mappings, preventing omitted pages from resurfacing.

- Constrain appearance parent recovery to registered form branches. Private Kids
  dictionaries in catalog, Info, attachments or pages no longer supply field text,
  default appearances or flags, or cause unrelated field-depth failures. Preserve
  bounded explicit-parent inheritance and Kids-only recovery inside the form.

- Preserve independent annotation-array aliases during appearance generation.
  Share changed indirect arrays across affected pages, avoiding repeated direct
  widget generation and duplicate warnings; retain skipped arrays unchanged.

- Reject nonzero JSON reference generations before publication. Preserve valid
  source aliases during updates and normalize stale, free and missing source
  pairs to null before applying edits, including no-op updates. Keep update
  source documents and version carriers private so failure/cancellation refunds
  their charges.

- Keep non-array appearance matrices opaque during overlay/underlay placement,
  with a bounded warning, instead of replacing them with invisible zero transforms.
  Preserve sharing and the separate legacy malformed-array fallback.

- Share page-resource inheritance across JSON and show-pages inspection, including
  null and undefined overrides. Report resolved-null Contents as empty while
  preserving stream identities, malformed non-null values and bounded ancestry.
  Remove both duplicate resource walks without eagerly reading stream payloads.

- Preserve imported attachment key identity when graph copying fails. Later
  imports with the same final prefixed key now reject in either request order,
  while unrelated imports and distinct prefixes retain their recovery behavior.

- Treat resolved-null AcroForm roots and optional field entries like direct null
  when copying or inspecting forms. Preserve terminal field identity and inherited
  values, including flags, names, choices and quadding; retain existing malformed
  non-null handling and bounded ancestry.

- Reject invalid UTF-16 in typed page-label prefixes before publication, retaining
  valid surrogate pairs and literal replacement/BOM-looking text. Remove the
  stale duplicate "work" iteration from the prefix memory-refusal test.

- Refund private writer/import version carriers on completion, failure and
  cancellation. Keep public document versions and typed check-result versions
  funded under their existing caller/result owners.

- Treat resolved null optional Names/Nums/Kids arrays like direct null in
  attachment and page-label walkers. Keep malformed required entries, missing
  references, cycles and bounds explicit, including selective extraction.

- Reconstruct indirect calculation-order arrays through the same form path as
  direct arrays. Merge source orders, filter removed fields and include isolated
  rotation copies consistently, while retaining opaque non-array values.

- Retain annotations with non-null optional-content conditions during flattening,
  with one bounded warning. Preserve their flags and condition instead of turning
  layer-dependent artwork into unconditional page content; null conditions remain
  eligible for ordinary flattening.

- Preserve independently retained appearance headers when flattening requires
  Form normalization; reuse indirect Form subtypes like direct ones. Rotation
  flattening keeps private AP values opaque instead of adding placement matrices.

- Count a generated text/list appearance's BBox origin once when placing text
  and selected-row highlights. This intentionally corrects nonzero vertical
  origins that native qpdf can also offset twice.

- Keep page resources used by retained annotation appearances during pruning and
  page stamping. Avoid existing XObject names and give imported appearances
  explicit source resources when they have no local scope. Transform only the
  normal, rollover and down appearance slots, preserving private AP values.

- Preserve independently retained Form resource headers during pruning and share
  replacements across selected pages. Analyze inherited uses before pruning
  enclosing Forms, retaining scopes whose dependencies remain unknown.

- Remove the unused checker stream-list owner and its type-specific tests;
  checking and transformations use the shared content-entry reader.

- Preserve enclosing resources after failed Form analysis and check Font/XObject
  uses in the correct category. Remove pruning's duplicate Contents reader and
  declared-name index, keeping indirect arrays consistent with other transforms.

- Share inline-image packet framing across externalization, normalization,
  resource/appearance scanning and checking. Keep unknown extents opaque,
  respect named colorspace contexts in shared streams and discard candidates
  from abandoned rewrites. Remove obsolete lexical boundary guessing; packet
  scans require no pixel decoding or JPEG codec.

- Bound unfiltered inline-image terminator searches by complete sample extents,
  retaining packed rows, masks and named colors while removing the old candidate
  builder that accepted already truncated data.

- Retain attachment key conflicts after recoverable specification-copy failures,
  while excluding skipped graphs from the final collection.

- Normalize local widget rectangles before deriving fresh appearance dimensions,
  preserving source coordinates and aliases through generation and flattening.

- Preserve existing page artwork under overlay/underlay, keeping background
  coordinates and clipping independent of the imported artwork's placement box.

- Resolve optional overlay/underlay source Contents and TrimBox nulls before
  selecting empty content or a fallback box, retaining annotation imports.

- Normalize local destination-box coordinates for overlay/underlay placement,
  preserving original rectangle values and independent aliases.

- Keep effective color-key image masks opaque during JPEG optimization, preserving
  exact transparency keys before sample admission while retaining soft-mask precedence.

- Preserve independently retained image and Form payloads while redirecting
  transformed resource references, including shared and cyclic Forms. Leave
  unchanged Form headers intact during inline-image externalization.

- Keep inline images when resource or colorspace binding fails, preserving form
  artwork and rejecting unresolved conversion before pixel allocation. Remove the
  obsolete candidate-preservation parameter and intermediate pixel owner.

- Normalize physical literal-string EOLs to one LF byte, preserving explicit CR
  escapes and line-continuation semantics.

- Preserve independently rooted streams and arrays during page-content
  normalization, including exact attachment payloads shared with page content.

- Preserve catalog aliases to externalized content through final-root retirement;
  remove the obsolete per-page orphan-removal plan and its test scaffolding.

- Retire superseded appearance streams after final-root assembly, retaining
  independent aliases and explicit unreferenced-object preservation.

- Select checkbox states by value and stream-backed appearances, preserving shared
  field values and warning on missing/ambiguous states instead of guessing from order.

- Preserve choice export/display and selection identities in generated appearances,
  including valid multiselect indices. Keep malformed or ambiguous choices with
  a warning instead of painting a fabricated selected row.

- Keep generated text encoding with the selected appearance font, including its
  default encoding, while retaining document-font fallback for missing local fonts.

- Preserve appearance readiness after skipped regeneration, including imported
  forms and split output. Later flattening keeps affected widgets while eligible
  non-widget annotations can still flatten.

- Preserve finite geometry values with bounded round-trip decimal formatting,
  including tiny page dimensions and placement scales that previously became zero.

- Preserve existing text-field artwork when generating an appearance selected
  by an indirect state name, including generation followed by flattening.

- Reject active linearization pass-one captures at primary, page, overlay/underlay
  or attachment input paths before PDF access or publication. Preserve ignored
  capture settings when linearization is disabled.

- Reject main-context setters nested inside attachment/copy/overlay blocks,
  including nested global markers, before PDF access or output publication.
  Main-option extraction preserves each block for its existing parser.

- Borrow funded unfiltered image samples through JPEG encoding instead of
  materializing stream slices, retaining decoded-byte charging and needed
  filtered/trimmed copies.

- Skip inline-image payload copying and admission when the borrowed payload is
  below the externalization threshold, preserving cancellation and parsing order.

- Reject obvious JSON numeric overflow before admitting expanded temporary
  storage, retaining compensated exponents, signed zeros and exact underflow text.

- Accept attachment add/remove/copy blocks throughout the main argv context,
  including before page selection or input and after the output filename.
  Reuse the existing block parsers and preserve input order and atomic failures.

- Apply native exact-range-key replacement and ordering to argv/job rotations,
  retaining typed list order and repeated positions within a range. Allocate
  per-page rotation folds only for affected pages, including inspection.

- Treat `empty:false` consistently as disabled in partial job files before or
  after an input, preserving enabled empty-input ordering and conflict checks.

- Reconstruct live catalog generations and indirect type names during bounded
  recovery. Keep scanned objects beyond stale trailer sizes, and skip parsed
  payload bodies so embedded header text cannot replace the catalog. Retire
  superseded identity facts without retaining trial objects or header history.

- Bound invalid JSON-key excerpts before exception formatting, reusing the
  existing preview policy for typed selectors and root/global/pages job keys.
  Preserve short messages, error categories and prior outputs.

- Count returned JSON UTF-8 bytes together with stream sidecars under the
  cumulative output limit, before any publication. Fileless JSON now reports
  the same logical output count as equivalent named JSON, including progress
  summaries under the existing diagnostic quota.

- Apply validated invocation nesting limits to the shared governor's graph walks,
  keeping reader/writer traversal consistent with equivalent explicit host options.
  Explicit host ceilings remain binding.

- Report page-image JSON filterability using the accepted stream-filter ceiling
  and decode level, including unfiltered images when decoding is disabled.

- Honor configured field-parent depth in appearance generation and overlay default
  repair, preserving inherited values beyond the former 64/32-parent cutoffs.
  Resolve indirect widget subtypes, appearance states and inspection flags through
  existing bounded lookups; retain cycle handling and atomic limit failures.

- Use the shared PDF UTF-16 decoder for JSON strings, preserving valid pairs and
  applying the established stored-text policy to malformed units. Returned JSON
  now agrees with UTF-8 publication without intermediate decoded-string copies.

- Reject NUL in PDF JSON name keys and values before publication, including
  escaped name syntax. Preserve literal hashes, NUL text strings and the existing
  damaged-name repair behavior.

- Reject inconsistent QDF stream/object/length framing before publication,
  including marker lines inside binary streams and object-stream members.
  Preserve ordinary edited lengths and other marker-shaped payload bytes.

- Preserve effective resources used by Type 3 glyphs and soft-mask groups during
  pruning, including uses inherited through forms. Conservatively keep these
  scopes while ordinary consumers still prune; native qpdf's destructive behavior
  is not reproduced.

- Resolve primary PageMode before applying attachment defaults, treating indirect
  null as absent and retaining name bytes without a text round trip. Keep opaque
  hints under ordinary catalog preservation and existing split policy.

- Reject duplicate decoded attachment keys in full-collection operations before
  publication, preventing silent payload changes during rewriting and duplicate
  JSON properties. Selective extraction retains its first-match behavior.

- Accept explicitly empty existing attachment identifiers for extraction and
  removal through typed, argv and job routes. Missing/null parameters and empty
  creation keys still reject. A missing empty key now fails lookup instead of
  argument validation, changing that typed exception to `QpdfParseException`.

- Reject malformed UTF-16 in authored attachment keys, filenames, descriptions
  and copy prefixes, and reject NUL in authored MIME names before publication.
  Existing PDF text decoding stays lenient; valid replacement characters,
  surrogate pairs and NUL in PDF text strings preserve their identity.

- Treat direct and indirect null consistently for optional attachment containers,
  stream parameters and preferred stream candidates. A null preferred candidate
  permits the existing fallback; required file and stream references remain strict.

- Retain the primary document's attachment collection during ordinary page
  selection, including selections with no primary pages. Attachment edits change
  that retained set; they no longer enable retention. Equivalent typed, argv and
  job operations use the same rule. Secondary collections require explicit import;
  use an explicit empty primary where supported when assembling a new document.

- Preserve raw attachment names and file-specification graphs without decoding
  unused listing metadata. Replacements discard superseded graphs before copying,
  retaining independent aliases and original key-presence conflict checks. Retained
  profile errors in Info, Metadata and attachments now fail publication even when
  warnings are suppressed. Safely copyable entries can survive despite unlistable
  optional metadata; extraction still requires a valid embedded stream.

- Isolate shared annotations when flattening rotations on repeated or shared
  pages. Each occurrence keeps its own rectangles, appearance headers, popup and
  reply links, and page association. Copy affected widget field branches through
  existing form registration and collision renaming; retain shared payload bytes.

- Coalesce page contents through a fresh stream reference, preserving shared
  content-array values in page and catalog data. Retire replaced-only objects
  after final output roots are assembled, honoring unreferenced-object retention.

- Preserve inline-image sample bytes during content normalization, including QDF
  defaults and images left inline by the externalization threshold. Binary samples
  are no longer parsed as names, strings or comments.

- Honor Hidden and Invisible annotation flags in every flattening mode, including
  indirect flag values. Flattened hidden annotations are removed without adding
  their appearances to page content.

- Accept rotation and collation in plain whole-file argv, matching the existing
  job-file route and native behavior. Rotations use the existing whole-file plan;
  collation leaves a sole source unchanged.

- Remove retired argv inspection parsing, four unused plan variants and their
  execution branches; public inspection commands retain the shared job pipeline.

- Honor parser limits, `--no-default-limits`, `--no-warn` and `--warning-exit-0`
  during named attachment extraction through argv and job JSON. Accepted settings
  previously disappeared from the extraction plan; explicit host ceilings remain
  binding, and rejected inputs preserve prior output.

- Validate imported and updated JSON page trees through the shared reader before
  publication. Indirect page types report correct counts; excessive depth, cycles
  and malformed child links reject without replacing prior output. This is stricter
  than native JSON pass-through. Shared page references count each occurrence.
  Catalog-type diagnostics bound name display to 64 bytes without materializing
  oversized names.

- Resolve indirect catalog and page-tree type names, page-tree children and page
  counts. Equivalent indirect layouts now open, select and report counts through
  the existing bounded reader; copying retains omitted-page and containment guards.
  Rebuilt page roots use a direct type name for ordinary and linearized writing.

- Keep the primary document distinct from selected page providers. Replacement
  targets the primary path, including selections from multiple files. Unselected
  primary inputs are validated and retain their catalog metadata and original ID;
  external single-page ranges use the same selection path as equivalent ranges.

- Resolve indirect catalog XML metadata type and subtype names before stream
  transformation, keeping metadata uncompressed as for direct names. Page
  metadata continues to follow ordinary stream settings.

- Transfer a sole materialized or decoded content chunk into its joined owner,
  avoiding a second full-size buffer in content analysis and overlays. Borrowed
  buffers still copy and multiple streams retain their exact concatenation rules;
  admission, cancellation and output ownership remain explicit.

- Read overlay rectangle coordinates through the existing array indexer, avoiding
  four defensive array copies per rectangle without changing geometry handling.

- Exclude unreachable attachment graphs after removal or replacement, including
  superseded additions in split outputs. Shared page/catalog references remain
  intact; explicit unreferenced-object preservation still retains such objects.

- Match attachment removals by name-tree key before interpreting or copying their
  values. Entries with missing embedded streams or null values can now be removed;
  missing and duplicate removal requests still fail before publication.

- Honor image filter ceilings before decoded-sample admission or codec checks.
  Preserve over-limit images opaquely with a warning, including codec-free hosts;
  diagnostic limits and output-commit guarantees remain binding.

- Resolve indirect image and Form subtype names during image optimization.
  Valid indirect forms now expose their images to the same bounded traversal;
  stale or malformed subtype values remain opaque.

- Attempt each shared image identity once during optimization, including skipped
  candidates. Preserve shared references while avoiding repeated JPEG generations,
  codec work and decoded-byte charges through resource aliases and repeated pages.

- Map missing and failed file-backed probe inputs to host outcomes, aligning
  structured command failures with the memory-backed probe. Preserve cancellation,
  unexpected exceptions, acquired-handle cleanup and existing output files.

- Fill admitted job-file buffers directly through bounded reads, removing
  temporary chunk arrays and copies while preserving short-read and cancellation
  behavior, host-operation counts and cleanup.

- Return `InvalidArguments` for literal unpaired UTF-16 in inline job JSON,
  matching malformed surrogate escapes instead of leaking a transcoding exception.
  Preserve prior output, cancellation, admission and cleanup.

- Expose read-only JPEG input backing for immutable borrowing during codec calls,
  removing the development adapter's redundant encoded-input copy. Supply known
  JSON publication lengths through the existing host capacity hint, avoiding
  staging growth copies and unnecessary shared-memory refusals.
- Remove the unused memory-resize helper and consolidate compatibility guidance;
  retain current contracts, alpha API migrations and intentional native differences.

- Reject populated signature values during page copying with an explicit
  excluded-signature outcome instead of copying stale metadata or failing later
  at a signature's source-catalog reference. Preserve blank signature fields,
  read-only inspection and transactional failure behavior.

- Refuse oversized memory estimates without arithmetic overflow, preserving the
  memory-failure category through nested codecs. Unrepresentable combined
  estimates saturate the reported requested amount at Int64.MaxValue.

- Keep shared memory owners usable after a refused codec probe. Affordable
  fallback reservations can proceed without changing the failed request's
  accounting or automatically retrying that request.

- Serialize JPEG worker admission with encoded-output buffer accounting so
  concurrent codec work cannot lose charges or strand shared reservations.

- Version 0.1.0-alpha.2 deliberately breaks the alpha host input contract:
  `IQpdfInput.ReadAsync` now accepts `Memory<byte>` and returns a byte count.
  Replace allocating `ReadExactlyAsync` calls with explicitly governed
  `ReadExactlyToArrayAsync`, or fill an already admitted buffer with the
  nonallocating `ReadExactlyAsync`. Short reads need no assembly allocation.
- Replace `QpdfJpegCodecContext.ReserveMemory`/`ResizeMemory` with borrowed
  `MemoryBudget` and standard scopes/reservations in the same alpha.2 refresh.
  Codec failures after admission refusal retain their budget category. Remove
  the development Image adapter's owner registry and blanket workspace estimate.

- Preserve source object generations in overlay/underlay annotation and widget
  imports. Stale references stay null regardless of traversal order, and valid
  nonzero-generation fields retain their AcroForm registration.
- Replace handwritten reference-lane stream scans with pinned native structural
  inspection. Exact content comparisons now retain logical page order and check
  independent page text, closing changed-payload and page-swap false passes.
- Reject malformed, mistyped and unsupported catalog `/Version` values before
  JSON creation/update opens output, intentionally stricter than native qpdf.
  Publish direct JSON trailer `/Root` dictionaries as the first output object.
  Bound malformed version-name diagnostics in the shared reader/import guard.
- Cache failure diagnostic counts and prefix overlap, removing repeated quadratic
  searches and per-record enumeration allocations while preserving failure history.
- Resolve indirect page rotation in ordinary rotation and both flattening paths,
  including inherited values; correct skipped flattening and NoRotate placement.
- Refresh job JSON help for check/stream inspections and parser error/default-limit
  controls already supported by the shared pipeline.
- Avoid input-sized argv option-name copies and bound echoed command error text
  before formatting, retaining useful terminal failures under tight allowances.
- Prevent input/output host lease leaks when allocation fails during acquisition,
  including the first completed call; retain actual OOM propagation and cleanup.
- Reject undefined typed inspection decode levels before host access rather than
  treating corrupt filtered streams as outside the requested decode level.
- Reuse admitted diagnostics for ordinary command results, removing the legacy
  presentation fallback and its extra allocation after publication. Preserve
  warning policies and terminal-failure accounting; clarify result documentation.
- Accept indirect attachment name-tree arrays and string keys across listing,
  extraction, JSON inspection, attachment copying and whole-file rewriting.

- Replace the development JPEG adapter and fixture dependency with Lokad.Image
  0.2.0; remove ImageSharp and its temporary audit exclusions. The core remains
  dependency-free. JPEG output and lossy samples can change between providers.
- Add resizable per-call codec memory reservations, retaining atomic refusal,
  cooperative cancellation and cleanup. Correct PDF JSON Unicode error handling
  and bounded round trips through the supported PDF nesting ceiling.

### Earlier corrections in this development cycle

These entries record completed fixes. Current scope, caller contracts and alpha
API migrations are defined in [compatibility](https://github.com/Lokad/qpdf/blob/master/docs/COMPATIBILITY.md).

- Failed document metadata and info copies discard their newly copied descendants and
  source mappings before recovery continues. Earlier shared objects remain available,
  and later roots can copy those descendants afresh.

- Attachment extraction searches for the requested key without building a full listing
  or decoding presentation metadata. Unrelated damaged file specs no longer consume
  diagnostic quotas, and shared descriptions no longer multiply extraction memory.

- Attachment preservation and foreign copying roll back mappings, descendants and
  allocator slots when an attachment is skipped. Earlier shared streams remain available
  to later attachments. Unsupported structural references still skip the attachment with
  a warning, where native qpdf may preserve it.

- JSON stream sidecar names now reject malformed UTF-16, NUL and paths longer than the
  logical path limit, including the generated object-number suffix, before copying the
  payload or creating output transactions. Valid Unicode names retain their exact host
  identity.

- Split outputs now advance the carried page-label start when a group begins inside a
  numbering run. For example, a six-page run starting at 5, split in pairs, starts at 5,
  7 and 9 rather than restarting at 5 in every file. This corrects typed, argv and job
  JSON output to agree with native qpdf splitting a whole document. Selected and
  reordered pages retain their source labels; this intentionally differs from native
  12.4.2's combined selection-and-split command, which applies the original label
  positions. Selecting and then splitting with native qpdf supplies the reference
  control for these cases. The reference lane uses a direct control where native 12.4.2
  fails copying indirect labels.

- Attachment listings and JSON now report semantic MIME subtype text: a literal `#`
  remains `#`, so adding an attachment from listed metadata preserves its MIME type.
  Names are decoded as UTF-8 without text-string BOM handling; malformed sequences use
  replacement characters. Invalid UTF-8 descriptor names use the existing reversible
  escaped-name spelling with an `n:` prefix, intentionally differing from native's
  invalid-UTF-8 JSON.

- Unreferenced-object preservation reuses objects already copied through an earlier
  root, retaining shared references and cycles without duplicate stream payloads. Failed
  candidates retire their new mappings and copied descendants before continuing, so
  skipped objects no longer leave split output with unresolved IDs. Managed split
  preservation intentionally keeps unreferenced objects in every group, while native
  qpdf drops them.

- Overlay and underlay widget imports resolve indirect form `/DA`, `/Q` and
  `/NeedAppearances` defaults, and compare imported defaults against resolved
  destination values. Equivalent direct and indirect defaults now preserve the same
  effective appearance, alignment and refresh flag. These corrected semantics agree with
  native qpdf.

- Appearance generation and annotation flattening resolve indirect AcroForm
  `/NeedAppearances` booleans before choosing their behavior. Indirect true now
  refreshes appearances when requested and otherwise keeps widgets with the same warning
  as direct true.

- Overlay and underlay copying validates source object-generation pairs before reusing
  placement copies and retains the original field reference through parent walks.
  Previously a live reference copied first could cause a later stale pair to become
  live, and synthesizing a generation-zero field reference could lose valid nonzero-
  generation AcroForm registration. These corrected outputs agree with native qpdf.

- Ordinary rotation, rotation flattening and annotation flattening resolve indirect
  `/Rotate` integers, including inherited values, from the copied object pool.
  Previously ordinary rotation rejected these valid inputs, rotation flattening silently
  skipped them, and `NoRotate` annotations could be misplaced. This intentionally
  corrects input acceptance and affected output.

- Argv option recognition borrows names instead of copying arbitrary caller text.
  Unknown-option, numeric-value, rotation, page-label and unexpected-argument syntax
  errors clip echoed text to at most 128 UTF-16 units plus `...`, without splitting a
  surrogate pair. This intentionally shortens large error messages before formatting;
  short errors, argument positions, crypto rejection and error codes are unchanged.

- Host input/output acquisition creates its wrapper before calling the host and returns
  completed host operations directly. This closes an allocation-failure window that
  could leave a returned raw lease undisposed, including on first use.

- Typed `Qpdf.CheckAsync` rejects undefined `QpdfCheckOptions.DecodeLevel` values with
  `ArgumentOutOfRangeException` before accessing the host or its memory budget.
  Previously those values could skip decoding corrupt streams and return a passing
  partial check.

- Attachment name-tree traversal resolves indirect `/Names` and `/Kids` arrays and
  string keys before checking their types. Equivalent direct and indirect trees now work
  in listing, extraction, both attachment JSON versions, copying and whole-file
  rewriting.

- PDF JSON admission and DOM parsing share a finite syntax ceiling that covers the
  supported PDF nesting ceiling plus the JSON envelopes. The former fixed 128 syntax cap
  rejected valid exports with 124 nested arrays in a catalog value.

- PDF JSON creation and update reject invalid Unicode strings and property names with
  parser diagnostics. Previously unpaired surrogate escapes in values, names or metadata
  could escape as `InvalidOperationException` from typed and execution entry points.

- Linearization resolves an indirect `/PageMode /UseOutlines` before placing outlines in
  the first-page section. Previously writing could report success with incorrect section
  boundaries and hints; the shared checker accepted those files and rejected valid
  native output.

- Linearization checking and reporting treat `/P null` as the absent optional first-page
  field, using its zero default. Previously typed checking and argv/job check/report
  requests rejected these valid files. This intentionally corrects input acceptance and
  diagnostics.

- Recoverably skipped catalog graphs discard new mappings, descendants and allocator
  slots from the failed attempt. Earlier shared copies remain intact.

- Catalog-preserving rewrites and selections retain nonattachment `/Names` categories,
  including named destinations, alongside rebuilt embedded-file names. Previously the
  name root was dropped or replaced by `/EmbeddedFiles`, breaking named links, bookmarks
  and open actions without a warning. Output changes intentionally; public signatures
  and named-destination JSON inspection limitations are unchanged.

- DCT decoding supports absent or null `DecodeParms` only. Dictionaries, including empty
  and indirect dictionaries, are unsupported. This intentionally changes acceptance,
  diagnostics and affected output.

- Annotation flattening resolves indirect numeric appearance-matrix coefficients before
  fitting the Form into its rectangle. These values no longer trigger a silent identity
  fallback and incorrect placement.

- Annotation rectangles are normalized before flattening placement and `NoRotate`
  arithmetic. Reversing either or both pairs of corners no longer mirrors or rotates the
  appearance relative to the canonical rectangle. This intentionally changes affected
  argv/job output; zero-extent and non-finite policies are unchanged.

- The built-in memory host observes cancellation after publication admission, including
  empty outputs. If a memory-budget callback cancels the token before returning an
  accepted receipt, commit now throws instead of replacing the prior target.

- Budgeted ranged reads observe cancellation after assembly admission and the final
  reservation shrink, before returning caller-owned bytes. A callback that cancels
  during the shrink now causes cancellation and refunds the read reservation instead of
  returning a charged result.

- Mixed argv with `--job-json-file` now shares ordinary managed parameter parsing for
  JSON versions, split sizes, compression choices and keep-files-open hints. Separate
  JSON/split values and bare compression retain their managed grammar; explicitly empty
  JSON choices and separated keep-files-open values reject before publication. The
  existing managed syntax extensions are not claims of native support.

- Page-image JSON preserves raw `/Width`, `/Height` and `/BitsPerComponent` values,
  including references and malformed values, instead of dereferencing integers or
  replacing other types with null. Missing/null values remain null. Image lists now use
  native unsigned raw-name byte order, rather than escaped display-name order. These are
  intentional output changes in both JSON versions and all entry points; public
  signatures are unchanged.

- Page-image JSON preserves UTF-8 resource names in both versions, including literal
  delimiters; it no longer replaces non-ASCII bytes with question marks. Invalid UTF-8
  names use the existing `n:` prefix followed by an escaped PDF name in both versions.
  This intentionally differs from native's invalid-UTF-8 JSON for those bytes and keeps
  managed JSON valid.

- Page-image JSON and `--show-pages --with-images` resolve indirect `/Subtype` names
  before identifying images. Valid indirect `/Image` names now appear in both reports;
  missing, stale or unusable subtype values remain excluded.

- Outline JSON validates the effective generation of destination page references.
  Missing, freed or stale references now produce a null first destination element and no
  page association instead of targeting a live page with the same number.

- Outline JSON includes explicit local `/GoTo` destinations from `/A`, including
  indirect action dictionaries, kinds and destination arrays. Non-null `/Dest` values
  retain precedence; absent, null or unresolved `/Dest` can fall back to the action.

- Outline JSON resolves indirect `/Title` and `/Count` values before interpreting them.
  Titles and closed-branch state now match direct values in both JSON versions and all
  entry points.

- Overlay and underlay placement normalize accepted signed 64-bit rotations before
  inverting them. Large negative values equivalent to a cardinal angle now produce the
  same transform as that angle instead of silently overflowing to identity. This follows
  managed rotation normalization; native qpdf warns and clamps large destination
  integers to its 32-bit range.

- PDF text construction uses UTF-16 when a PDFDocEncoding prefix would resemble a
  Unicode byte-order mark. JSON imports, page-label prefixes and colliding form-field
  renames now preserve leading `þÿ`, `ÿþ` and `ï»¿` text instead of losing or
  reinterpreting it.

- Page-label prefixes and attachment keys, filenames and descriptions preserve Unicode
  controls whose ASCII byte positions are repurposed or undefined in PDFDocEncoding.
  These values now use UTF-16 instead of becoming accents or replacement characters.

- Form checking uses the configured nesting ceiling instead of silently stopping at
  depth 32. Deeper supported hierarchies now receive the same parent-link checks;
  exceeding the ceiling produces an explicit failed budget outcome and stops later
  inspections.

- Page-content checking now fails malformed content collections and actual decode errors
  instead of marking unexamined content passed. Unavailable or partially inspected
  content reports a skipped stage; intentional decode-level exclusions are
  informational, while unavailable decoding warns.

- Attachment JSON filename/stream maps now use PDF name keys (`/F`, `/UF`, `/Unix`,
  `/DOS`, `/Mac`) in both versions and all entry points, matching qpdf. Previously the
  leading slash was omitted. This intentionally changes the JSON shape; clients must use
  the corrected keys.

- Attachment metadata resolves indirect scalar values before checking their types,
  including filename maps and alternate embedded streams in JSON. Previously valid
  references silently lost filenames, descriptions, MIME types, dates, checksums and
  declared sizes.

- Rotation flattening resolves indirect appearance-state dictionaries, matrices and
  their numeric entries before transformation. Equivalent direct and indirect values now
  produce the same appearance; public signatures are unchanged.

- Annotation flattening normalizes page rotations modulo 360 before applying `NoRotate`
  placement. Equivalent negative, full-turn and large 64-bit angles now produce the same
  placement as their cardinal angle; public signatures are unchanged.

- Annotation and rotation flattening fail before publication if computed geometry is
  non-finite. Previously, extreme finite coordinates could publish `NaN` or `Infinity`
  operands while reporting success.

- Image optimization leaves dimensions outside its positive 32-bit codec domain opaque.
  Eligible sample sizes are checked against decoded-byte/array ceilings before decoding
  or codec calls; oversized declarations now fail explicitly as `BudgetExceeded` rather
  than overflowing arithmetic or decoding first.

- Form merging reuses admitted suffix-search progress for colliding qualified names. The
  smallest free `+N` still depends only on earlier sources, so repeated names within one
  source keep their shared rename.

- JSON text import and export share PDFDocEncoding conversion. Unicode controls that
  occupy repurposed PDF byte slots now use UTF-16 rather than changing into accent
  marks.

- AcroForm JSON funds decoded names, choice text and ancestor scratch before
  materialization, and refunds scratch after each field is copied into the JSON buffer.
  Large metadata can therefore refuse earlier.

- Page JSON expands an indirect `/Contents` array into its entries, preserving stream
  references and order like a direct array. Non-array and malformed entries remain
  visible for inspection. Image inspection admits descriptor capacity and compares
  borrowed raw name bytes in native order, without creating strings for each comparison.

- Writer-version parsing bounds the complete scalar before splitting it into components.
  Oversized malformed job-JSON values no longer allocate token arrays and strings
  proportional to their input length.

- Appearance generation clears `/NeedAppearances` through the shared owned dictionary
  rebuild, with admitted growth and both cancellation checks. It no longer makes two
  unowned table copies under a flat 64-byte charge.

- Public result, counter and version records use compiler-generated copying, equality
  and hashing. Shallow aliases, constructor validation and admission are unchanged.

- Zlib-flate mode and compression-level validation precedes cancellation and local or
  shared memory admission. Invalid options return `InvalidArguments` with zero counters
  and no host or budget callbacks.

- `InMemoryQpdfHost.AddInput` rejects undefined `QpdfInputOwnership` values with
  `ArgumentOutOfRangeException` before admission or replacing an input. Previously every
  value other than `Copy` silently transferred the array.

- Token encoding fills the already-measured result without recalculating unused byte
  widths. The content tokenizer borrows its validated governor policy instead of
  retaining a second limits reference or revalidating it at construction.

- Page ranges append inclusions into one admitted list, remove excluded intervals by
  testing selected members, and compact odd/even positions in place. Full-span scratch
  lists, exclusion hash sets and a second filtered result are removed.

- QDF repair accepts carriage-return bytes in stream payloads and binary cross
  references, preserving them through length/offset repair and repeated repair.
  Previously a blanket scan rejected valid binary QDF, including the writer's own image
  output.

- QDF scans and table preparation check both cancellation tokens at bounded byte and
  record intervals and around awaited writes. Oversized numeric headers stop at the
  supported width.

- QDF repair indexes object-stream lines by admitted offsets and lengths into its owned
  immutable input instead of copying each line. Input ownership lasts through awaited
  output and cleanup; hosts retaining backing after a write own its funding.

- Binary QDF xref repair emits each row as one immutable packet using the shared byte
  formatter. Previously three field writes per row exhausted the default host operation
  ceiling on a 20,000-member fixture.

- Optional predictor parameters use their defaults when their resolved PDF value is
  null, just as when the dictionary key is absent. This applies to Predictor, Columns,
  Colors and BitsPerComponent for Flate and LZW.

- LZW PNG predictors exclude row selectors from decoded-sample ceilings, matching Flate.
  Selectors remain part of admitted raw intermediate storage; incomplete rows and
  invalid selectors still reject.

- LZW, ASCIIHex, ASCII85 and RunLength preflight remaining aggregate decoded bytes
  before growing their output. Previously they could decode and allocate a complete
  stage before refusing its final charge.

- Compressed xref decoding borrows a complete owned encoded payload while its parsed-
  stream owner stays live. Sliced backing still materializes under the existing scratch
  scope.

- Unknown xref-stream entry types resolve to null and mask older entries for the same
  slot. Their undefined fields are ignored, including unsigned-wide values. Native qpdf
  handles standalone future types but shares the incremental and wide-type defects.

- LZW keeps bounded prefix/suffix tables and one expansion buffer instead of copied
  dictionary strings. One admitted batch holds about 16 KiB plus array overhead;
  empty/single-literal streams avoid it.

- ASCIIHex and ASCII85 share the tokenizer's PDF whitespace rules. NUL, HT, LF, FF, CR
  and space are ignored before EOD; vertical tab and other invalid bytes reject. The
  former duplicate C-style predicates rejected NUL and accepted VT, also defects in
  native qpdf.

- RunLength decoding stops at the first 128 end marker. Previously trailing bytes could
  become content or exhaust decoded-byte limits, a defect also present in native qpdf.
  Before EOD, truncated literal records now emit their available bytes, matching native
  qpdf: encoded `00 41 02 42` yields AB rather than silently dropping B. The intentional
  post-EOD difference remains.

- Argument filtering and collation retain admission scopes without privately rooting
  every returned scratch collection. Callers and final plans hold the values they use;
  accounting, growth overlap, cancellation and cleanup remain enforced.

- Page-label number trees resolve indirect `/Nums`, `/Kids` and integer keys before type
  checks. Equivalent direct/indirect trees now work in selection, merge and inspection;
  malformed structures and cycles still fail before publication.

- Merged page-label runs compare copied style/prefix references in the output pool.
  Previously source object-number collisions could discard a later input's prefix, a
  defect also present in native qpdf; renumbering could instead expand one run into an
  entry per page.

- Attachment reading keeps existing reference identities and formats their text only
  when JSON presentation requests it. Typed listing, extraction and rewrites no longer
  allocate or reserve the unused reference labels.

- Attachment name-tree keys sort by emitted bytes, including mixed ASCII and UTF-16
  keys. Previously decoded ordinal order could publish an unsorted tree whose existing
  entries a byte-order search missed; native qpdf shares this mixed-encoding defect.

- JSON export normalizes a PDF real's leading plus sign and integer zeros, while
  preserving significant digits, fractional spelling and negative zero. Legal PDF tokens
  such as `+.5` and `-00.5` now publish valid JSON through every entry point; previously
  success could return invalid JSON, a defect also present in native qpdf.

- Appearance generation and stamping share the existing bounded geometry formatter.
  Large integral real coordinates retain a decimal point, so generated BBox/Matrix
  values remain readable under the managed integer bounds. The former private formatters
  could report success while writing oversized integer tokens; native qpdf shares these
  extreme-coordinate defects.

- Appearance generation resolves indirect named font encodings through the existing
  bounded field resolver. WinAnsi and MacRoman characters now agree with direct encoding
  declarations instead of silently becoming `?`.

- Overlay and underlay placement resolve indirect `/Rotate` and `/UserUnit` scalars for
  source and destination pages. Previously these values silently used rotation zero or
  unit one, changing orientation and scale.

- The reader uses the higher of the header and catalog `/Version`, including indirect
  catalog values. A lower catalog declaration no longer silently lowers the version of a
  rewritten PDF.

- Rewrites preserve existing extension vendors and negotiate Adobe extension levels with
  minimum and forced versions. Previously ordinary rewrites erased declarations and a
  minimum request could lower an existing level.

- `--show-pages` resolves an indirect contents array and lists its stream references in
  order, like page JSON. Empty arrays list no content; object generations and duplicates
  are preserved.

- PDF text metadata shares one decoder for UTF-8 and UTF-16 with a byte-order mark, and
  PDFDocEncoding otherwise. UTF-8 BOM attachment keys, names and descriptions, outline
  titles and form text now agree across typed, argv and job entry points; attachment
  lookup uses the corrected Unicode key. ; metadata text and key matching intentionally
  change.

- Whole-file rewriting and typed attachment edits accept valid zero-page inputs while
  preserving their catalog metadata and embedded files. Synthetic empty jobs use the
  same governed preparation, so accepted job-JSON attachment edits now run, and missing
  sources fail before publication.

- JSON sidecars must differ from the main JSON output, and an active linearization
  first-pass capture must differ from every main PDF output, including later split
  groups. Equal ordinal logical paths now fail before any output transaction is created;
  previously the auxiliary bytes could overwrite the completed main file while reporting
  success.

- Without `--linearize`, `--linearize-pass1` is ignored for split output as well as
  ordinary output. Split preparation no longer allocates or publishes an empty capture
  that could overwrite an existing target.

- Typed attachment listing returns metadata without constructing discarded listing
  messages. Clean metadata calls no longer spend diagnostic record/message quotas; real
  warnings and all memory/I/O limits remain binding.

- Attachment editing emits one flat name array under memory admission and managed array
  bounds. The former split above 64 entries produced direct child dictionaries that
  native listing and extraction had to repair with warnings.

- Command arguments and JSON key/object selectors expose read-only views of their
  defensive copies. Casts to mutable collection interfaces now reject mutation, so
  reusing an invocation or options value preserves its original settings.

- JSON creation and update expand exponent notation into PDF decimal syntax without
  rounding its significant digits through binary floating point. Ordinary integer and
  decimal spellings remain unchanged. Native also mishandles some extreme magnitudes;
  managed rejection deliberately preserves readable output.

- `QpdfLimits.MaxNestingDepth` accepts 1..499 for direct and host-supplied budgets, with
  the default remaining 256. Larger values now throw `ArgumentOutOfRangeException`
  before host access. This intentionally restricts accepted settings; public signatures
  are unchanged. Unlike native qpdf, which accepts zero as a depth cap, these overrides
  require 1..499.

- Skipped stream checks describe unavailable filters and JPEG host capabilities without
  promising future implementation. Disabled decoding remains an explicit skip;
  grayscale/RGB decoding still requires a host codec, and four-component JPEGs retain
  their accepted limitation.

- Flate decoding requires a complete zlib frame, including the DEFLATE end marker, even
  when the produced bytes match the Adler checksum. Premature codec-input EOF now fails
  explicitly; the zlib companion preserves prior output and PDF checks report a failed
  stream.

- Typed rotations carry numeric positions directly through the shared rotation resolver.
  Explicit lists use `MaxContainerSize` and ordinary memory admission; the former
  4096-character limit on a generated range string no longer applies.

- Page selection, rotation and collation expose read-only views of their defensive
  integer snapshots. Casts to mutable collection interfaces can no longer change
  validated settings.

- Name tables hash complete decoded name bytes, including suffixes after the first 16
  bytes. Long-name hashing checks cancellation in bounded chunks; borrowed raw and
  escaped lookups allocate no name copies.

- Public API documentation describes the existing cumulative whole-job output ceiling
  and independent output commits. A later output or post-commit close failure does not
  roll back earlier publications.

- The built-in memory host keeps registered inputs separate from published outputs,
  including `--replace-input` and companion calls with equal input/output paths.
  Reopening reads the existing registration until the caller explicitly exports and
  registers the output.

- JSON object selectors use one admitted set of numeric object/generation identities and
  trailer membership across typed, argv and job calls. Duplicate selectors no longer
  allocate repeated canonical strings or execution snapshots.

- Page selection admits range expansion, page-sized working collections and graph pool
  tables before allocation. First/last previews check their cardinality before
  allocating; CPU loops cooperate with cancellation.

- Form-resource traversal visits resource dictionaries by graph identity instead of
  comparing their complete values. Shared scopes and indirect form cycles still stop at
  their visit guards; distinct equal scopes are each traversed in breadth-first order.

- Typed attachment add/copy/remove calls admit their request projection under the
  invocation governor before allocating lists or records and before opening inputs.
  Cancellation interrupts projection.

- Attachment metadata receives admission before encoding and checks cancellation while
  scanning/copying. Headers and name-tree arrays adopt admitted storage; large metadata
  can now refuse before materialization.

- Attachment assembly adopts the owned read/compression payload instead of making
  another defensive copy. Cancellation at attachment-input close is checked before
  metadata construction.

- `--test-json-schema` scans admitted UTF-8 tokens for top-level help coverage, without
  constructing a DOM for the complete generated output. UTF-8 capacity and
  key/diagnostic storage receive admission before allocation, so tight limits can refuse
  checks previously omitted from accounting.

- Image optimization admits its form-visit set before growth and uses borrowed name
  lookups. Wide form graphs can now refuse under local/shared memory limits.

- Shared admission keeps less than two spare 16 KiB blocks between significant releases,
  preventing repeated grow/shrink calls for tiny scratch at a block boundary.
  Significant releases still return capacity promptly; zero-live cleanup and final
  completion trim retain their behavior.

- Split model-list capacity refunds after each PDF is captured into owned bytes, instead
  of remaining charged through the whole batch. Group roots, ordering, renumbering and
  rebuilt page children receive admission and cancellation.

- QDF original-ID maps receive admission and cancellation during construction; large
  provenance maps can refuse earlier. Suppressing original-ID comments skips their
  preparation entirely.

- Split publication admits changed references, array backing, dictionary tables and
  stream headers during identity remapping. Large remaps can refuse earlier under
  local/shared memory limits.

- Shared graph reachability borrows array and frozen dictionary storage instead of
  queuing every child. Repeated references in wide containers can now fit smaller memory
  allowances.

- Object-stream preparation admits candidate sets, remapping, sorting storage and group
  capacity before allocation. Large generate/preserve/split jobs can refuse earlier
  under local or shared memory limits, and cancellation interrupts planning.

- Trailer IDs are checked through borrowed byte storage before copying. Empty and
  greater-than-256-byte first IDs retain their existing rejection policy; rejected large
  strings no longer allocate payload copies.

- Every accepted positive split group size, including `2147483647`, can exceed the page
  count and produces one complete group. Group arithmetic no longer overflows into
  negative page ranges; planning receives admission and cancellation.

- Page selection finishes copying catalog labels and form defaults before allocating
  added attachments, and coordinates later copies with overlay/appearance objects. Cold
  font and default-program references therefore keep their original meaning when these
  operations are combined.

- Overlay form reachability admits traversal and reached-set capacities before
  allocation. Repeated field references no longer queue a full pending array; large
  distinct field sets can refuse earlier.

- Appearance text replacement matches the first `/Tx` marked-content block with bounded
  `BMC`/`BDC` nesting, preserving unrelated bytes and inline image data. Malformed
  programs warn and keep their original appearances; nesting, memory, decompression and
  cancellation limits remain fatal.

- Appearance generation preserves skipped buttons in a page's annotation array when
  other widgets on that page are updated. Radio buttons, pushbuttons and buttons without
  a usable name value keep their existing state and warnings.

- JSON names use strict UTF-8 scalar validation. Surrogate encodings and values above
  Unicode's range now export with `n:` byte escaping, preserving raw name bytes through
  import/update instead of replacing them with replacement text.

- JSON outline inspection rejects cycles through sibling links as well as child links,
  with cancellation and nesting bounds independent of cycle detection. Decoded titles,
  outline collections and destination indexes receive memory admission before
  allocation.

- Coalescing, normalization and inline-image interpretation now require unfiltered or
  successfully decoded content. Unsupported/damaged encodings and filter-count ceilings
  preserve the affected content and headers; page operations warn rather than
  interpreting opaque bytes as instructions.

- Overlay catalog default appearances remain borrowed byte programs through import,
  without eager PDF-text decoding or re-encoding. Imported defaults preserve their
  original bytes, including non-ASCII names; source ownership transfers before close.

- Overlay annotation rectangles normalize reversed endpoints without losing their
  extent. Shared appearance streams receive the placement matrix once per copied stream;
  separate placements retain independent copies, including widget forms.

- Overlay font-conflict rewriting compares decoded name bytes and emits escaped
  replacement names. Unchanged names, comments and whitespace retain their original
  bytes; an escaped font spelling therefore follows the imported font after a collision.

- Missing appearances emit only the generated Form stream, without an unreachable
  initial placeholder. Field values and choices now receive memory admission before
  text/byte materialization; large jobs can refuse earlier while preserving the same
  PDFDoc/UTF-16 and ASCII/WinAnsi/MacRoman conversion behavior.

- Appearance generation resolves escaped font names by decoded bytes: `/F1` and `/F#31`
  select and merge the same resource, including its WinAnsi/MacRoman encoding. Original
  appearance spelling, comments and whitespace are preserved apart from the existing
  font-size correction.

- Failure diagnostic views cache their visible counts and shared-prefix overlap.
  Previously reading `Count`, enumerating the list or accessing its terminal record
  could repeatedly search a duplicated history with quadratic cost.

## 0.1.0-alpha.1 - qualified non-cryptographic supported profile

- Make JPEG processing pluggable through the host; remove ImageSharp from the core
  package's runtime dependencies. Keep the pinned adapter in development tools.
  Codec-free hosts preserve opaque images and report unavailable decoding or
  eligible optimization explicitly.

- Implement qpdf 12.4.2 structural operations through shared typed, argv and job-JSON routes: reading/recovery, page selection/merge/split, inspection, attachments, writer controls, content transforms, overlays, appearances, grayscale/RGB JPEG processing, QDF and linearization.
- Preserve the accepted limitations: four-component CMYK/YCCK images remain opaque with warnings when decoding is requested; Zopfli is unavailable. Crypto operations reject explicitly. Other native differences remain documented.
- Resolve compression/decode setters in configuration order, apply native argv/job defaults and preserve typed opaque copying. Compose repeated/nested job files through the shared loader.
- Repair bounded stream lengths consistently through parsing and copying. Preserve AcroForm relationships, forward page links and overlay inspection; conservatively retain unresolved resources.
- Fund attachment extraction and image candidates through consumption, avoid
  redundant payload copies, and scan default appearances without token lists.
  Resolve escaped appearance font names by decoded bytes and bound generated
  appearance streams by `MaxDecodedBytes`.
- Preserve untouched and escaped font names during overlay conflict rewriting.
  Borrow appearance payloads for header-only changes, admit inspection/value
  conversion buffers before allocation, and omit unused appearance placeholders.
- Validate linearization hints against physical output boundaries and graph membership. Preserve valid layout differences and regressions for checker false passes. Repeated hint encoding completes within the existing 4 KiB control.
- Enforce practical allocation, decompression and work limits with approximate memory estimates; observe cancellation during numeric and JPEG work. Clean up leases and temporary storage while preserving committed outputs and surviving immutable aliases. Runtime code never forces GC.
- Qualify Windows/Ubuntu ordinary tests, pinned-native reference comparisons, seven finite visual cases and fresh locked package consumers, including rejection controls. Historical audit counts and exact CLR accounting are not release requirements.

See [supported behavior](https://github.com/Lokad/qpdf/blob/master/docs/SUPPORTED-PROFILE.md) and
[native differences and option evidence](https://github.com/Lokad/qpdf/blob/master/docs/COMPATIBILITY.md).

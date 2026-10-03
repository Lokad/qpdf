# Progress

## Current checkpoint
Work item 96: page-label prefix encoding (M5). Label prefixes now encode exactly as the probed reference: ASCII stays literal, PDFDocEncoding-representable text goes hex, and anything else goes UTF-16BE with byte-order mark hex (including surrogate pairs); default start 1 stays omitted from the number tree. Six new encoding unit cases plus a two-form differential (PDFDoc and UTF-16 prefixes with identical labeled catalogs on both sides) carry the evidence. `--set-page-labels` stays partial behind source-tree merging. Merge collision renaming, first-wins default resources, nonzero-generation inspection, CMYK/YCCK decoding, unknown catalog keys and typed split output stay explicitly open.

Prior gate

## Current checkpoint
Work item 95: collate completion (M5/M11). `--collate` moves to complete with ranged-merge proof: groups carrying full ranges interleave in identical page order on both sides (new unit parity plus a `collate-ranges` differential asserting our marker order and the oracle sharing pattern), count mismatches fail explicitly, and collation yields to exhausted budgets. This also records a genuine intentional difference the proof exposed: duplicate page occurrences are independent copies here while the reference shares content streams. Merge collision renaming, first-wins default resources, nonzero-generation inspection, CMYK/YCCK decoding, unknown catalog keys and typed split output stay explicitly open.

Prior gate

## Current checkpoint
Work item 94: overlay range wiring (M7). Overlay `--to`/`--from`/`--repeat` now use the full shared grammar across argv, job JSON and underlays: descending ranges, `rN`/`z`, exclusions, `:odd`/`:even`, and duplicates preserved, with empty `--from` still selecting nothing and unused overlay pages left unembedded exactly as the reference. Per-page placed-marker unit tests prove descending/relative/exclusion/odd/repeat/underlay/job-parity mappings plus malformed failures, and a new `overlay-ranges` differential case (including underlay) shows placed-marker agreement on both sides. All five overlay entries stay partial behind uncopied annotations. Merge collision renaming, first-wins default resources, nonzero-generation inspection, CMYK/YCCK decoding, unknown catalog keys and typed split output stay explicitly open.

Prior gate

## Current checkpoint
Work item 93: rotation range wiring (M5). `--rotate` ranges now use the full shared grammar across argv, job JSON and typed rotations: descending ranges, `rN`/`z`, exclusions, `:odd`/`:even`, and duplicates preserved and applied in order (the reference double-applies `+90:1,1` to 270, replacing the old dedup). `--rotate` is accepted at the main level as upstream places it, with the inside-`--pages` form kept as leniency and byte-identical results; repeats accumulate with later specs winning per page and positions spanning merged outputs. Bounds failures keep the typed `ArgumentOutOfRange` contract while argv/job map them to explicit `InvalidArguments`; mismatched counts and pages-less combinations fail explicitly, and expansion runs under the job work budget. A new `rotate-ranges` differential case (including a two-spec mapping proof) shows per-page rotations agreeing on both sides. `--rotate` stays partial behind multi-file `--pages` input forms and typed single-page overloads. Overlay ranges, omitted ranges, merge collision renaming, first-wins default resources, nonzero-generation inspection, CMYK/YCCK decoding, unknown catalog keys and typed split output stay explicitly open.

Prior gate

## Current checkpoint
Work item 92: page-range wiring for page selection (M5). `--pages` groups now accept the full shared grammar (spans, descending ranges, `rN`/`z`, `x` exclusions, `:odd`/`:even`, duplicates) across argv, job JSON and a new internal typed `FromRange` form: parse-time syntax validation, per-source execution expansion against its own page count with the job work budget, and explicit failures without publication for malformed or out-of-range ranges. Plain single numbers keep the exact legacy single-page plan; anything richer routes through the shared merge engine (spec counting unchanged, so collation semantics hold). A new `ranges` differential case proves span/reverse/exclude trios green with identical marker order on both sides. `--rotate` ranges and overlay `--to`/`--from`/`--repeat` keep their partials until their own wiring lands; omitted ranges stay explicitly required. Merge collision renaming, first-wins default resources, nonzero-generation inspection, CMYK/YCCK decoding, unknown catalog keys and typed split output stay explicitly open.

Prior gate

## Current checkpoint
Work item 91: shared page-range grammar (M5 foundation). New `Pages/PageRanges` parser/expander implements the upstream range syntax probed on a 6-page scratch file against the pinned oracle: `rN`/`z` endpoints, descending ranges, `x` exclusions removing accumulated members, comma lists with duplicates/order preserved, a single trailing `:odd`/`:even` position filter applied last, empty range selecting all pages, `:odd`/`:even` alone selecting nothing, leading zeros accepted, whitespace rejected, 64-bit overflow and out-of-range endpoints (including excluded ones) failing explicitly, and a first-group exclusion rejected. Expansion carries an explicit entry budget plus a range-text cap. Covered by 48 `PageRangesTests` cases including every manual example. Not yet wired: `--pages` groups, `--rotate` ranges and overlay `--to`/`--from`/`--repeat` keep their partial entries and single-integer behavior until the wiring lands. Merge collision renaming, first-wins default resources, nonzero-generation inspection, CMYK/YCCK decoding, unknown catalog keys and typed split output stay explicitly open.

Prior gate

## Current checkpoint
Work item 90: collate job-key and placement completion (M5/M11). The job-JSON `collate` key (`""` for bare, otherwise `n[,m,...]`) now rides the shared merge plan with argv job-parity (byte-identical outputs plus marker order), group application, single-entry merge routing, and explicit malformed/duplicate/count-mismatch/pages-less failures. Argv also accepts the upstream main-level `--collate` placement (bare and `=` forms) with byte-identical results to the inside-`--pages` leniency, and mismatched group counts now fail explicitly instead of escaping as an unhandled error. A new `collate` differential case runs the upstream form on both sides with trios green. `--collate` stays partial behind single-integer page ranges and full range syntax. Merge collision renaming, first-wins default resources, nonzero-generation inspection, CMYK/YCCK decoding, unknown catalog keys and typed split output stay explicitly open.

Prior gate

## Current checkpoint
Work item 89: parser-limits completion (M11). `--max-stream-filters`, `--parser-max-nesting`, `--parser-max-container-size` and `--parser-max-container-size-damaged` move to complete with agreement-at-loose-caps evidence and documented strictness differences: over-limit filter chains stay opaque with a warning (exit 3) on both sides; the default nesting cap stays tighter (256 vs upstream 499) with agreement at 499; tight container caps fail fast (exit 2) while the oracle warns and skips the oversized object during recovery (exit 3), with agreement at loose caps. The extended `global` differential case runs blocked selections (filters, nesting at 100 and 499, loose container caps) with trios green on both sides. `--no-default-limits` and `--parser-max-errors` stay partial as rejection-only containment. Merge collision renaming, first-wins default resources, nonzero-generation inspection, CMYK/YCCK decoding, unknown catalog keys and typed split output stay explicitly open.

Prior gate

## Current checkpoint
Work item 88: image-threshold completion (M11). `--jpeg-quality`, `--keep-inline-images`, `--oi-min-width`, `--oi-min-height` and `--oi-min-area` move to complete with a shared exhausted-budget theory, a threshold job-parity theory, a new checked-in `photo-1page.pdf` fixture (manifest/csproj/manifest-tests wired), and a new `optimize` differential case proving oracle agreement (matching DCT dimensions at an explicit quality, byte-identical kept payloads under disabling thresholds, inline kept inline on both sides). JPEG bytes stay encoder-defined; only structural and payload agreement is claimed. The `--optimize-images` entry itself stays partial behind its CMYK/mask gaps. Merge collision renaming, first-wins default resources, nonzero-generation inspection, unknown catalog keys and typed split output stay explicitly open.

Prior gate

## Current checkpoint
Work item 87: global-block completion (M11). `--global` moves to complete with a new `global` differential case proving block parity (both sides run `--global --max-stream-filters` and `--global --parser-max-nesting` selections with green trios). Bare limit flags outside the block stay accepted as documented narrow-profile leniency (the reference rejects them). Merge collision renaming, first-wins default resources, nonzero-generation inspection, CMYK/YCCK decoding, unknown catalog keys and typed split output stay explicitly open.

Prior gate

## Current checkpoint
Work item 86: empty-input completion (M11). `--empty` moves to complete: bare `--empty -- out` writes a zero-page document through the normal writer, and every inspection (check, show-npages/pages/object/xref, json, list-attachments, linearization) accepts the empty input through argv and job JSON via a synthetic zero-page document parsed by the same strict reader pipeline with no host lease. `--check` fails explicitly on page-less files, matching the reference exit classes; dot selections keep failing explicitly where the reference crashes (documented hardening, no crash parity claimed). New empty-document unit theory plus extended empty differential assertions (zero-page agreement, show-npages/check exit parity) prove oracle agreement. Merge collision renaming, first-wins default resources, nonzero-generation inspection, CMYK/YCCK decoding, unknown catalog keys and typed split output stay explicitly open.

Prior gate

## Current checkpoint
Work item 85: replace-input completion (M11). `--replace-input` moves to complete with a new job-JSON `replaceInput` key (bare `""`/boolean, conflicting with `outputFile`, `pages`-less plans and `empty`, mirroring the argv rules through the shared plan) plus job parity, malformed-combination, rotation-interaction and exhausted-budget evidence proving failed publications leave the input untouched. A new `replace` differential case replaces a shared file on both sides (managed job vs oracle argv) with trio validation and no backup files created either side, documenting the no-backup behavior as probed parity. Merge collision renaming, first-wins default resources, nonzero-generation inspection, CMYK/YCCK decoding, unknown catalog keys and typed split output stay explicitly open.

Prior gate

## Current checkpoint
Work item 84: unreferenced-object preservation completion (M11). `--preserve-unreferenced` moves to complete: detached pages and detached trees are now preserved as loose objects (identity-based exclusion of rebuilt roots only), preservation runs for the primary merge input and suspends under `--empty`, and preserved metadata stays linked with intact bytes, all matching the pinned oracle. The old contained-preview exclusions and the stale unlinked-metadata claim are replaced by probed parity; remaining intentional differences (ascending copy order, decoded stream members instead of containers) are documented. A shared orphan/metadata/dangling-page fixture plus a `preserve-unreferenced` differential case (single-source loose-object agreement plus empty-base agreement) and the previously unwired exhausted-budget theory fill the evidence. Open follow-up recorded: unknown catalog keys ride along on every reference path while this profile drops them (separate catalog-extras item); merge collision renaming, first-wins default resources, nonzero-generation inspection and CMYK/YCCK decoding stay explicitly open.

Prior gate

## Current checkpoint
Work item 83: rotation-flattening completion (M11). `--flatten-rotation` moves to complete: appearance form XObjects referenced from annotation /AP dictionaries (including /N, /R, /D state dictionaries) now gain the same rotation matrix the content wrap uses, composed over any existing matrix exactly as the reference does (probed across 90/180/270 degrees, nonzero origins, composed priors, markup annotations and direct-stream appearances, which upstream leaves untouched). Direct /UserUnit entries survive and inherited units stay uninherited on both sides with no code change. New appearance-matrix unit theory plus a `rotated-ap-1page.pdf` fixture (manifest/csproj/manifest-tests wired) and an extended flatten-rotation differential sub-case prove oracle agreement. Parent-held field lookup, default-resource merging, nonzero-generation inspection and CMYK/YCCK decoding stay explicitly open.

Prior gate

## Current checkpoint
Work item 82: inline-image externalization completion (M11). `--externalize-inline-images` and `--ii-min-bytes` move to complete with a shared exhausted-budget theory, a new checked-in `inline-1page.pdf` fixture (manifest/csproj/manifest-tests wired), and a new `inline` differential case proving oracle agreement (externalized /IIm1 content with no inline image left on both sides at the default threshold, verbatim kept-inline content above a high threshold). Fixing the comparison exposed real residue: page rewrites orphaned the replaced content streams in the pool, so single-reference contents/resources now rewrite in place, array leftovers drop through a shared unreferenced-sweep, and both xref writers emit free entries for the resulting holes. Merge collision renaming, first-wins default resources, nonzero-generation inspection and CMYK/YCCK decoding stay explicitly open.

Prior gate

## Current checkpoint
Work item 81: diagnostics completion (M11). Four output-neutral diagnostics entries (`--progress`, `--verbose`, `--report-memory-usage`, `--test-json-schema`) move to complete with a new `diagnostics` differential case proving oracle agreement (exit-0 parity on both sides with green trios, byte-identical managed outputs with and without each write flag, matching top-level JSON keys for the schema check). Report text stays implementation-defined on both sides with no stable cross-implementation contract (counted-byte milestones vs object-based percentages, exact governor peak vs best-effort figure); the difference is now documented under Invocation breadth instead of lingering as open gaps.

Prior gate

## Current checkpoint
Work item 80: catalog/form preservation through the shared copy engine (M7). Ordinary selection and rewrite paths now preserve the input catalog's interactive form dictionary instead of dropping it under the contained-preview policy: field registration, values, parent links and default resources round-trip through the shared graph copier, with top-level fields pruned when every widget in their subtree falls outside the kept pages (matching the pinned oracle on full, subset and merge trios). `--remove-acroform` (argv/job) is now the explicit prune switch actually threaded through every publication path; flatten-all drops the form with its annotations. Field/annotation `/Parent` links remap through the memo table while page-tree ancestry stays dropped. Radio buttons are never synchronized (new oracle probes: the reference syncs checkboxes but leaves radios untouched, parent-held or not), restoring the parent-held skip as genuine parity. New `forms-2page.pdf` fixture (manifest/csproj/manifest-tests wired), `AcroFormPreservationTests` (typed/argv/job parity, subset pruning, merge, flatten interplay, budgets), a `forms-preserve` differential case, and reconciled `--remove-acroform`/`--generate-appearances`/`--flatten-annotations` evidence. Merge collision renaming, first-wins default resources, nonzero-generation inspection and CMYK/YCCK decoding stay explicitly open.

Prior gate

## Current checkpoint
Work item 79: identity and hints completion (M11). Seven identity entries (`--version`, `--copyright`, `--help`, `--job-json-help`, `--json-help`, `--completion-bash`, `--completion-zsh`) plus both file-handle hints move to complete with a new `identity` differential case (exit-0 parity with shared markers on both sides) and a `hints` case (hinted merge trios green plus hinted/unhinted byte identity), refreshing four stale "explicitly unsupported" passages in COMPATIBILITY along the way.

Prior gate

Work item 78: page-label completion (M11). `--remove-page-labels` moves to complete with existing job-parity evidence, a new shared labels budget theory, and a new `labels` differential case proving oracle agreement on setting (identical labeled catalogs) and removal (identical unlabeled catalogs); `--set-page-labels` keeps its genuine tree-merging/encoding gaps but gains the same budget and differential evidence.

Prior gate

Work item 77: QDF completion (M11). `--qdf` moves to complete on its existing full evidence (packing and catalog differences stay documented in COMPATIBILITY), and `--no-original-object-ids` moves to complete with new valued/duplicate guard tests, job parity, the shared QDF budget case, and a new `qdf-noids` differential case proving both sides suppress original-ID comments while keeping the editable view.

Prior gate

Work item 76: resource-preservation completion (M11). `--preserve-unreferenced-resources` was rejected while upstream treats bare `--preserve-unreferenced-resources` as a synonym for `--remove-unreferenced-resources=no`, so it is now implemented across argv, job JSON, help, and completion with last-wins precedence on conflict (probed both orders on the oracle) and no valued form. New guard/parity/last-wins/budget unit evidence plus a `preserve` differential case move it to complete alongside `--json-key` (stale deferred-help/input gap) and `--object-streams` (new generate-budget case), with the previous wrong-family evidence replaced throughout.

Prior gate

Work item 75: metadata-removal completion (M11). `--remove-info` and `--remove-metadata` move to complete: a new budget theory covers both publishing paths, and a new `metadata` differential case on a new checked-in `meta-1page.pdf` fixture (trailer /Info plus catalog /Metadata, manifest/csproj/manifest-tests wired) proves oracle agreement (trios green with /Metadata dropped on both sides, ModDate-only info vs full info preserved per flag).

Prior gate

Work item 74: job-file and recovery completion (M11). `--job-json-file` and `--suppress-recovery` move to complete: the job file executes the identical plan through the host (plus an exhausted-budget case), recovery paths fail cleanly without output or leases under budgets, and two new differential cases prove oracle agreement (job file: shared subset file run by both sides with trio green; recovery: warning/failure exit parity 3/3 and 2/2 with nothing published on strict failure and page-count agreement after repair).

Prior gate

Work item 73: linearization-inspection completion (M11). `--check-linearization`, `--show-linearization`, and `--linearize-pass1` move to complete: the existing exit/message/combination/job evidence plus differential trios already covered every other slot, so a shared input-budget theory (both inspections) and a pass1 output-budget case fill the only missing resource slots. Dropped the stale show-unsupported pointer from the check entry; the shared exit-2 difference now lives in COMPATIBILITY.

Prior gate

Work item 72: attachment-operations completion (M11). The five main attachment entries plus seven nested add-attachment keys plus `--prefix` move to complete (13 flips): a metadata job-parity test and shared block-grammar guards fill the nested slots, budget theories cover every publishing path (plus an input-budget case for the output-less listing), and a new `attachments` differential case proves two-sided oracle agreement (add/copy/remove trios green with cross-extracted bytes matching the source, key sets agreeing on both sides including prefixed copies, empty sets after removal). Found and recorded one genuine difference: empty `--prefix=` fails explicitly here while upstream copies unchanged.

Prior gate

Work item 71: writer-controls completion (M11). `--min-version`, `--force-version`, `--deterministic-id`, `--static-id`, and `--newline-before-endstream` move to complete: existing malformed/duplicate guard tests fill the negative slots, a new `WriterControlsYieldToExhaustedBudget` theory fills the resource slots, and a new `writer-controls` differential case proves oracle agreement (negotiated/forced/extension headers and extension catalogs identical, static trailers byte-identical on the shared test constant, deterministic output matching default bytes and validating, LF-before-endstream shapes agreeing). The stale below-1.5-awaits-22b sentence now states the implemented rule.

Prior gate

Work item 70: compression-controls completion (M11). `--stream-data`, `--compress-streams`, `--decode-level`, `--recompress-flate`, and `--compression-level` move to complete: existing malformed/duplicate/valued guard tests fill the negative slots, a new shared-pipeline `CompressYieldsToExhaustedBudget` theory fills the resource slots, and a new `compress` differential case (oracle-compressed input rebuilt by both writers with recompress plus level 9, oracle trios green with sorted decoded-payload identity) fills the oracle slots alongside the existing uncompress case. Intentional differences stay documented in COMPATIBILITY (explicit-override precedence, opaque-preserving defaults, DCT/CMYK opacity, BCL level buckets); the stale "containers stay uncompressed until 23b" sentence now states the implemented packing rule.

Prior gate

Work item 69: JSON inspection-modifier completion (M11). `--json-object`, `--json-output`, `--json-stream-data`, and `--json-stream-prefix` move to complete: the shared inspection budget case covers the resource slot (same `JsonCoreAsync` pipeline, consistent with `--json`/`--json-key` precedent) plus a new `json-modes` differential case proving per-modifier oracle agreement on one fixture (trailer filter: same keys/trailer/pages; inline: byte-identical stream payloads; json-output: identical headers and objects; prefix: identical datafile references and byte-identical sidecars).

Prior gate

Work item 68: JSON import/update completion (M11). `--json-input` and `--update-from-json` move to complete: exhausted-budget resource evidence on both paths plus two-sided differential cases against the pinned oracle (shared oracle JSON imported by both writers with oracle trios green and sorted decoded-payload identity; shared mutated JSON updated by both writers with oracle trios green and `/Custom` present in both outputs). Cross-implementation object numbering and deterministic-vs-random IDs stay documented non-goals.

Prior gate

Work item 67: no-external audit and evidence reconciliation (M11). Proved the ordinary suite passes 1482/1482 with `external/oracle`, `external/qpdf`, `external/diff`, and `external/probestruct` renamed away (plus static proof: no `external/` or `QPDF_EXE` references in `src/` or ordinary tests, checked-in fixtures only) and that the reference lane fails explicitly without `QPDF_EXE`. Reconciled five stale evidence references to real tests (`AdapterTests.ArgvNamedFormsSucceed`, dropped two never-existing normalize names and one obsolete linearize name, dropped one never-existing coalesce name) plus a new `NormalizeRewritesNewlines` case, so all 531 non-oracle evidence references resolve. Package/docs checks clean: `verify-package` pins ImageSharp 3.1.12 with expected contents only, README/CHANGELOG/COMPATIBILITY carry no stale counts.

Prior gate

Work item 66: write-progress reporting (M11). `--progress` (plus job key `progress`) appends four exit-neutral `ProgressReport` diagnostics (25/50/75/100% of counted output bytes) to every successful write through the shared presentation point, with byte-identical outputs, silence on inspection paths without an output file, valued/duplicate/job-key failures, job parity, verbose combination, and budget evidence. Upstream streams object-based percentages during the write; diagnostics-only milestones stay the explicit intentional difference with oracle output parity pending.

Prior gate

Work item 65: global option block (M11). `--global [options] --` delimits the six upstream global limit flags through the shared plan (bare limit flags keep working outside the block as a narrow-profile leniency); non-global, nested, unterminated, and duplicate-across-delimiter blocks fail explicitly. With block/bare parity, nesting-limit, refusal, guard, and budget evidence; bare-acceptance versus upstream block-only stays an explicit gap with oracle parity pending.

Prior gate

Work item 64: stream payload inspection (M11). `--show-object` with `--raw-stream-data` or `--filtered-stream-data` (plus job keys, filtered decoded as `--decode-level=all` and winning when both are given) publishes exact stream bytes to a required output file through staged host publication, with non-stream resolutions keeping the diagnostics view, undecodable streams failing explicitly, and valued/duplicate/misplaced/normalize combinations failing explicitly. With publication, parity, guard, and budget evidence plus a dedicated byte-identity differential case.

Prior gate

Work item 63: verbosity and file-handle hints (M11). `--verbose` (plus job key) appends an operation summary diagnostic to every successful result without affecting the exit code; `--keep-files-open=y|n` and `--keep-files-open-threshold=n` (plus job keys) are validated performance hints with identical outputs (host lease policy stays authoritative). With summary, parity, guard, and budget evidence on all three flags.

Prior gate

Work item 62: testing and debugging options (M11). `--report-memory-usage` (plus job key) appends the exact governor peak to every successful result without affecting the exit code; `--test-json-schema` (plus job key, requires `--json`) validates generated top-level keys against the matching `--json-help` description and fails explicitly on drift. With reporting, validation, parity, guard, and budget evidence on both flags.

Prior gate

Work item 61: empty-base merges (M11). `--empty` (plus job key `empty`) stands in for the input file so `--pages` merges start from a zero-page base with no catalog extras carried over, through the shared merge engine with an explicit empty-primary flag; `.` selections fail explicitly where the reference crashes, and bare, replacement, overlay, and inspection uses fail explicitly. With merge, parity, guard, and budget evidence plus a dedicated differential trio.

Prior gate

Work item 60: parser container budgets (M11). `--parser-max-container-size=n` (plus nested job key, default 4294967295) caps top-level array/dictionary elements during strict opens while `--parser-max-container-size-damaged=n` (default 5000) governs repair trial-parses via derived limits; `--parser-max-errors` is refused on every surface because the fail-fast parser satisfies no finite tolerance observably and unlimited tolerance contradicts containment. With evidence across strict, boundary, repair, parity, guard, and host-budget cases; oracle parity stays an explicit gap.

Prior gate

Work item 59: classic-table reading mode (M11). `--ignore-xref-streams` (plus job key `ignoreXrefStreams`) skips cross-reference streams and hybrid `/XRefStm` links through the governed reader on every plan that opens a document (plus host budgets): hybrid files fall back to classic revisions, bogus stream links never load, stream-only files fail strictly into bounded recovery, with exit-for-exit oracle parity and a hybrid selection trio in a dedicated differential case.

Prior gate

Work item 58: page-image listing (M11). `--show-pages --with-images` (plus job key `withImages`) lists direct image XObjects from effective inherited resources with oracle-identical lines, backed by image fixtures, unit evidence (sorting, form skipping, dimension warnings, combinations, job parity, budgets) and a dedicated differential case (clean and dimension-less listings, warning exits 3/3). `--with-images` without `--show-pages` fails explicitly where upstream ignores the modifier.

Prior gate

Work item 57: parser nesting budget and global job object (M11). `--parser-max-nesting=n` (plus nested job key, within upstream default 499 for stack safety) overrides the nesting budget per invocation through the same plan-composition point as the stream-filter cap; `--no-default-limits` is refused on containment grounds across argv and nested job keys. Job JSON gains the upstream nested `global` object (`maxStreamFilters`, `parserMaxNesting` now; other/unknown nested keys fail), correcting the flat key to the upstream schema and inventory convention, with nesting, guard, parity, and host-budget evidence; oracle parity stays an explicit gap.

Prior gate

Work item 56: stream-filter budget (M11). `--max-stream-filters=n` (plus job key, default 25, nested under the `global` object since work item 57) caps decoded filter chains on every decode path through one governed choke point: over-limit streams stay opaque under a warning (exit 3) instead of failing, with `>` boundary parity, argv/job parity, JSON-import and inspection-combination guards, host-budget and validation evidence; oracle parity stays an explicit gap.

Prior gate

Work item 55: shell completion identity (M11). `--completion-bash` and `--completion-zsh` are identity requests emitting static subset completion scripts (eval-able `complete -F` / `compdef` over the narrow-profile flags, with no argv[0] or environment lookup per containment), with bare/with-input/combination, valued-form, and budget evidence; oracle script parity stays an explicit gap.

Prior gate

Work item 54: JSON help identity (M11). `--json-help[=1|2|latest]` (default 2) is an identity request emitting same-keys/descriptive-values JSON for the matching inspection version (v1 object tables versus v2 qpdf key), with version-selection, unknown-version, wins-over---json, and exhausted-budget evidence; oracle output parity stays an explicit gap.

Prior gate

Work item 53: job-JSON help identity (M11). `--job-json-help` is an identity request printing the supported job-key subset (exit 0 with no output file, valued forms fail, exhausted output budgets ignored), with bare/with-input/combination, valued-form, and budget evidence; upstream prints the full schema so oracle output parity stays an explicit gap.

Work item 52: release-gate honesty audit (M11). Verified the section 13 structural gates
with evidence (no unsafe/interop/process/network code in src, ImageSharp pinned centrally
and in locks and the package group, deterministic locked builds), refreshed stale
public metadata (package description/release notes, README status) and class docs (document
leases, job-parser scope, per-call governor with threading/cancellation/commit contract).

Prior gate

Work item 51: structure-tree removal (M11). `--remove-structure` (plus job key
`removeStructure`) is accepted as a no-op backed by the fresh-catalog invariant, with a
checked-in structure fixture and a dedicated differential case.

Prior gate

Work item 50: shared budget enforcement (M11). One theory proves a dozen public entry
paths fail explicitly with `BudgetExceeded` and publish nothing under exhausted budgets,
filling resource evidence across content, inspection, merge, and writer entries (4 flips).

Prior gate

Work item 49: form-dictionary removal (M11). `--remove-acroform` (plus job key
`removeAcroform`) is accepted as a no-op backed by the fresh-catalog invariant (outputs never
carry `/AcroForm`); a dedicated differential case checks both sides.

Prior gate

Work item 48: selection evidence (M11). Top-level `--pages` is complete with ordered,
range-failure, budget, and differential evidence; `--preserve-unreferenced-resources` moves
to partial with a budget case, oracle preservation parity still open.

Prior gate

Work item 47: cross-reference inspection (M11). `--show-xref` (plus job key `showXref`)
lists live entries with oracle-identical lines (compressed entries included, free entries
skipped), backed by a line-for-line differential case on classic and streamed files.

Prior gate

Work item 46: object inspection (M11). `--show-object` (plus job key `showObject`) renders
canonical single-line object definitions with line-for-line oracle parity (including streams,
trailer, missing-as-null, and compressed members); non-zero generations and binary stream-data
modes stay explicitly out of scope.

Prior gate

Work item 45: page-dictionary inspection (M11). `--show-pages` (plus job key `showPages`)
lists page dictionaries with indirect content references in oracle-identical line layout,
backed by a line-for-line differential case.

Prior gate

Work item 44: page-count inspection (M11). `--show-npages` (plus job key `showNpages`)
reports traversed page counts across argv and job paths with explicit combination failures,
encrypted/budget failures without output, and a managed-versus-oracle count-agreement lane case.

Prior gate

Work item 43: identity outputs and the no-external audit (M11). `--copyright` returns honest
implementation text; valued identity forms fail explicitly; identity requests bypass exhausted
output budgets; the ordinary suite passes 1150/1150 with `external/` absent while the reference
lane fails explicitly without `QPDF_EXE`. Help/version/copyright/json-help move to partial with
evidence.

Prior gate

Work item 42: warning/exit-code contract with job parity (M11). `warningExit0`, `noWarn`,
and `suppressRecovery` job keys ride the shared plan; `--no-warn` keeps exit 3 while dropping
warning messages (matching the reference manual); the differential lane checks warning exit
parity (3/3, 0/0, 3/3); invocation, check, and page-selection entries gain complete
evidence (5 flips) with suppress-recovery and job-json-file moving to partial.

Prior gate

Work item 41: crypto-exclusion evidence (M11). All 49 password/permission/inspection/restriction
entries now carry negative evidence: every argv flag, top-level and nested job JSON crypto key,
and encrypted file input fails explicitly with `UnsupportedCryptoOperation` (exit 2) publishing
no output, backed by 58 exclusion cases (top-level job crypto keys newly rejected to match the
argv surface).

Prior gate

Work item 40: linearized object-stream modes (M10). `--linearize` with `--object-streams=generate` (or `preserve` over compressed inputs) publishes streamed linearized output through the shared selection engine: members resolve to their container during part classification with page-shared containers winning placement, uncompressed units sort low per half with container members numbered after their half, and raw cross-reference streams replace both classic tables under the same two-pass replay check. Outline hint tables are omitted whenever outline objects compress (as observed upstream); xref streams stay unfiltered and member numbering follows container encounter order as documented intentional differences. `preserve`/`disable` over classic inputs still publish the classic layout; QDF stays silently disabled and `--json`, JSON import, attachment inspection and `--check` combinations still fail explicitly. Eleven new classification/emission/mode cases plus streamed oracle fixtures (check/show) plus the `linearize-streams` differential case (managed versus oracle writers on one-, two-page, and outlined selections, both outputs validating under both checkers) pass with no engine discrepancies.

Prior gate

Work item 38: linearization checking (M10). The check detects the parameter dictionary, validates its keys against traversed pages, resolves hint streams by cross-reference offset, and parses page-offset, shared-object, and outline tables with a governed bit reader. Committed oracle-linearized one- and two-page fixtures back unit and differential coverage, including malformed dictionary, offset, encoding, and bit-level cases.

Prior gate

Work item 37: managed zlib-flate facade (M9). The typed RunZlibFlateAsync companion compresses (levels 1..9) or uncompresses zlib streams between explicit host paths through the governed Flate pipeline with staged publication and explicit failure on bad modes, levels, corrupt input, and budgets. The differential lane gains a zlibflate case round-tripping both directions against oracle zlib-flate with corrupt-input parity and level ordering.

Prior gate

Work item 36: managed fix-qdf repair (M9). The typed FixQdfAsync companion recomputes object-stream tables, stream lengths, cross-references, and startxref offsets over edited QDF with sequential numbering enforcement, bounded governor budgets, staged host publication, and explicit failure on carriage returns, truncation, and empty input. Classic QDF stream lengths now pair through renumbering (multi-stream fix with a two-page regression). The differential lane gains a fixqdf case comparing managed repair against oracle fix-qdf byte for byte on edited classic and streamed files.

Prior gate

Work item 35: interoperable QDF editable view (M9). Classic layout (binary marker, %QDF-1.0, original-ID/page comments, indented values, indirect lengths, ignore_newline tolerance) plus object-stream layout (unfiltered containers with member comments, upstream-style /First and value offsets, unfiltered xref stream with /W [ 1 f1 f2 ]) across argv and job paths with QDF-implied decode/normalize defaults. Differential lane gains a two-page QDF object-stream case (oracle trio, marker agreement, upstream fix-qdf round trip green).

Prior gate

Work item 34 extended: external validation lane. Bootstrapped the official qpdf 12.4.2 portable build (checksums recorded in docs/COMPATIBILITY.md), fixed the reference script exit-code capture, and added tools/run-differential-checks.ps1 comparing managed vs oracle outputs (select, merge, uncompress/normalize payload identity, JSON pages, forms appearances, overlay) with oracle trios green and no engine discrepancies.
Work item 34: inline optimization and codec hardening. Externalized inline images join shrink-only DCT optimization, plus adversarial JPEG cases (truncation, components, chains, budgets), DCT attachment extraction, and valid-JPEG check coverage.

Prior gate

Work item 31a: rotation flattening and page-content checks. Counter-transform baking with box and annotation geometry across argv and job paths, plus tokenizer-backed page-content inspection.

Prior gate

Work item 30b: overlay and underlay transforms. Repeatable stamping blocks with to/from/repeat mapping, form XObject placement with centered shrink-to-fit matrices, and stacking across argv and job paths.

Prior gate

Work item 30a: inline-image externalization. Threshold-based lifting to image XObjects with abbreviation expansion, color lookup, unique naming, and form recursion across argv and job paths.

Prior gate

Work item 29: page content operations. Coalescing, newline normalization, and unreferenced-resource pruning with shared-resource heuristics across argv and job paths.

Prior gate

Work item 28: content tokenizer and inline-image boundary tests. Strict operand tokenizing with governed inline-image scanning backs upcoming content transforms.

Prior gate

Work item 27b: PDF update from JSON. Merged object identity with kept stream bytes, optional trailer replacement, and pdfversion override across argv, job, and typed paths.

Prior gate

Work item 27a: JSON import from qpdf JSON. Strict structural validation with canonical value decoding, inline and sidecar payloads, and renumbered classic publication across argv, job, and typed paths.

Prior gate

Work item 26b: JSON stream modes, object filtering, acroform details, and minimal serialization output. Inline/file payloads with decode-or-raw fallback, canonical object selectors, inherited form-field reporting, and json-output defaults across argv, job, and typed paths.

Prior gate

Work item 26a: PDF JSON inspection. Canonical v1/v2 object encoding with omitted stream data backs selected top-level keys (pages, labels, outlines, attachments, encrypt, objects) across argv, job, and typed paths.

Prior gate

Work item 25b: attachment copy plus the typed editing surface. Repeatable copy blocks rechains per-source filespecs after removals and additions with grouped collision errors, and the typed add/remove/copy surface shares the engine while reporting final attachment counts.

Prior gate

Work item 25a: attachment add and remove. Repeatable add blocks plus repeatable removals build on the EmbeddedFiles reader with staged host file reads, sorted rebuilt trees, and per-group split coverage.

Prior gate

Work item 24b: attachment list and extract. EmbeddedFiles name-tree traversal with text-key decoding backs shared typed/argv/job paths: listing diagnostics plus staged byte extraction with decode-or-raw fallback warnings.

Prior gate

Work item 24a: document info and metadata. Trailer `/Info` and catalog `/Metadata` are preserved by default with `--remove-info` (ModDate-only info, also drops metadata) and `--remove-metadata` semantics matching upstream, across typed/argv/job paths with replay-checked serialization.

Prior gate

Work item 23b: container Flate packing. Generated object/xref streams Flate-encode (plain `/FlateDecode`, no predictor) when compression is active, honoring `--compression-level` through the governed pipeline with replay-checked serialization; otherwise they stay raw.

Prior gate

Work item 23a: stream compression controls. `--stream-data`, `--compress-streams`, `--decode-level`, `--recompress-flate`, and `--compression-level` decode (generalized/specialized sets) and Flate-encode regular streams through one governed pipeline with opaque fallback warnings; `--zopfli` reports the no-Zopfli build profile. Defaults stay opaque-preserving (intentional difference from upstream compress defaults). One implementation across typed/argv/job paths with replay-checked serialization.

Prior gate

Work item 22c: preserve-unreferenced objects. `--preserve-unreferenced` keeps unreached input objects (ascending original order per source) through the shared copier with omitted-page pruning intact, joins object-stream packing and split-group closure, and skips unloadable objects under one warning. One implementation across typed/argv/job paths with replay-checked serialization.

Prior gate

Work item 22b: writer object-stream modes. `--object-streams` (`disable`/`preserve` default/`generate`) packs eligible non-stream objects into uncompressed object streams with a cross-reference stream, raising the header to at least 1.5 unless a forced version below 1.5 disables them; `preserve` keeps input compressed-ness without exact grouping. One shared implementation across typed/argv/job paths with replay-checked serialization.

Prior gate

Work item 22a: writer versions, IDs and stream endings. `--min-version`/`--force-version` negotiate the header (`major.minor[.extension]`, 1.0-1.7 and 2.0) with catalog `/Extensions` for positive levels; trailer `/ID` always emits (original first word preserved, default second word is a deterministic content hash, `--static-id` selects the upstream test constant, `--deterministic-id` matches the default); stream `endstream` defaults to no extra newline with `--newline-before-endstream` forcing `\\n`. All entry paths share one implementation with replay-checked serialization.

Prior gate

Work item 21: split and replacement publication. `qpdf --split-pages[=n]`
partitions output into upstream-named groups sharing one object pool with
per-group reachability pruning, renumbering, and sliced labels; `--replace-input`
rewrites the single distinct input with no backup. Contradictions, collisions,
and oversized names fail before publication; typed callers split by looping.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 1527 tests (844
  prior plus 24 annotation cases (stamping, flag filtering, widgets, merge coverage, job round trips) plus 19 appearance cases (text/list synthesis, checkbox/radio sync, encodings, whole-file/merge/generate-then-flatten, job round trips) plus 23 rotation cases (checked-in /Rotate fixture flattening) plus 32 overlay cases plus 26 inline-image cases: thresholds, abbreviations, color lookup, naming, forms, damaged content, job round trips, plus 7 JPEG decode cases (1-/3-component decode, 4-component and corrupt opacity, level gating, job parity, header-first budgets) plus 24 image optimization cases (eligibility, thresholds, quality, shrink-only replacement, merge/job coverage) plus 11 hardening cases (inline externalize-and-optimize, keep-inline, truncation, components, chains, budgets, extraction, check) plus 27 content transform cases (checked-in unreferenced-font fixture pruning, newline rewriting): coalescing, normalization,
  resource pruning with shared heuristics and form recursion, damaged
  content warnings, merge/split coverage, and job round trips, plus 16 QDF editable-view cases plus 1 stream-data regression plus 4 streamed-QDF cases replacing the interim rejection, plus 13 fix-qdf repair cases plus 1 two-page QDF length regression, plus 19 zlib-flate cases, plus 2 JSON budget cases (import/update exhausted budgets), plus 12 linearization cases plus 2 manifest page-count rows, plus 15 linearized-writing cases (with pass1 budget evidence), plus 18 linearization-inspection cases (with input-budget evidence), plus 13 linearization-display cases, plus 8 first-pass capture cases, plus 6 outline cases and 2 outline manifest rows, plus 2 thumbnail cases and 1 thumbnail manifest row, retiring the thumbnail rejection case,
  plus 2 streamed-manifest rows (oracle object-stream fixtures), plus 5 streamed-inspection cases
  (check/show over object-stream files), plus 3 streamed-parts cases (container placement and numbering),
  plus 3 streamed-emission cases (managed check, determinism, classic fallback), plus 17 page-count cases (counts, combinations, job parity, budgets), plus 18 page-dictionary cases (exact layouts, combinations, job parity, budgets), plus 27 object cases (canonical definitions, streams, trailer, missing-as-null, compressed members, combinations, job parity, budgets), plus 21 xref cases (classic/compressed entries, combinations, job parity, budgets), plus 10 form-dictionary cases (invariant, merge, check-combination, job parity, budgets), plus 10 structure-tree cases (invariant, merge, check-combination, job parity, budgets) plus 1 structure manifest row, plus 12 budget-enforcement cases (entry-path BudgetExceeded with no output) plus 5 compression budget cases (shared-pipeline exhausted budgets) plus 5 streamed-mode cases
  (generate/preserve/disable, outlines, job parity) plus 3 job-JSON-help identity cases (bare/with-input/combination wins, valued-form failure, exhausted output budget) plus 8 JSON-help identity cases (version selection, unknown versions, wins-over---json, exhausted output budget) plus 5 shell-completion identity cases (bash/zsh scripts, valued-form failure, exhausted output budget) plus 12 stream-filter budget cases (cap/boundary, argv/job parity, combination guards, host budget, validation, malformed values) plus 24 global-limits cases (deep-nesting default/raise/boundary/ceiling, nested global parity/guards, limits refusal, host-budget interplay) plus 17 page-image cases (sorted direct listing, form skipping, dimension warnings, combinations, job parity, budgets) plus 11 classic-table reading cases (hybrid fallback, damaged-link recovery skip, stream-only recovery, inert classic, malformed forms, job parity, host budgets) plus 21 parser container-budget cases (strict/damaged caps, boundary, repair split, parity, guards, host budgets, malformed values, refusal) plus 16 empty-base cases (merges, parity, guards, budgets) plus 22 testing-option cases (memory reporting, schema validation, parity, guards, budgets) plus 27 verbosity and file-handle cases (summaries, hints, parity, guards, budgets) plus 23 stream-payload cases (raw/filtered publication, precedence, guards, parity, budgets) plus 9 global-block cases (block/bare parity, nesting limit, refusal, guards, budgets) plus 10 progress cases (milestones with identical bytes, inspection silence, valued/duplicate/job-key failures, job parity, verbose combination, budgets)).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Pinned oracle bootstrapped: official qpdf-12.4.2-msvc64.zip (zip SHA-256 db87077e…, exe SHA-256 1bdaf878…), `qpdf version 12.4.2`, libjpeg linked, Zopfli absent. Reference lane passes; differential lane (select with first-page coverage, merge, uncompress, normalize, coalesce, flatten-rotation,
  prune with an unreferenced-font fixture, structural JSON page descriptors, error exit-class parity, warning exit parity, show-npages page-count agreement, show-pages/show-object/show-xref line-for-line agreement, remove-acroform/remove-structure invariants, forms, overlay, qdf with fix-qdf round trip, fixqdf byte-identity, zlibflate round trips, linearized check parity, linearize writer parity, linearize-streams parity (generated object streams on one-, two-page, and outlined selections), check-linearization inspection parity, show-linearization dump parity, linearize-pass1 shape parity, linearize-outlines parity, linearize-thumbs parity) passes with oracle trios green and no engine discrepancies (see docs/COMPATIBILITY.md).

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. M11 audit (in progress): crypto, warning/exit-code, identity, page-count, page-dictionary, object, xref, selection, form-dictionary, structure-tree, budget, and release-gate entries evidenced
   (work items 41-42, 53-79).
   Next slices: remaining oracle-parity gaps across the 87 partials,
   then final package/docs/release gates.
   Re-run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.


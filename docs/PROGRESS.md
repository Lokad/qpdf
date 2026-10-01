# Progress

## Current checkpoint

Work item 20 (part 2: collation and labels). `PdfPageMergeOptions` carries
collation (uniform or per-source groups, upstream round-robin), rotations, and
explicit labels over the multi-source engine; source label trees are dropped,
never merged. `qpdf --set-page-labels` and job-JSON `setPageLabels` share the
`first-page:[type][/start[/prefix]]` specification; `--remove-page-labels` is an
accepted no-op. Multi-file `--pages` forms and `--collate` stay open.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 370 tests (355
  prior plus 15 label/collation cases: number-tree emission, prefix/start styles,
  order rules, argv/job parity, remove no-op, collation orders, and option
  validation).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 20 (part 3): multi-file `--pages` forms with `--collate` (M5).
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.

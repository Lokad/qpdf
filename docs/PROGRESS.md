# Progress

## Current checkpoint

Work item 20 (part 1: rotation). `PdfPageRotation` folds over output positions on
the multi-source engine; `qpdf --rotate` and job-JSON `rotate` share the
`[+|-]angle[:range]` specification (`n`, `n-m`, comma lists, `z`). Later specs
win per page, out-of-range positions fail explicitly, and untouched pages keep
their bytes. Collation, labels, and multi-file `--pages` stay open.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 355 tests (339
  prior plus 16 rotation cases: inherited/explicit bases, set/add/subtract folds,
  out-of-range and invalid specs, duplicates, garbage bases, and argv/job paths).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 20 (part 2): collation, page labels, and multi-file `--pages` (M5).
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.

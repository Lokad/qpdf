# Progress

## Current checkpoint

Work item 19: cross-document import identity. `Qpdf.SelectPagesAsync` merges
ordered pages from several inputs with per-source object identity (separate
copiers, one numbering sequence, one governor); per-page resources keep
same-named cross-document entries inert, widgets keep within-source links, the
output catalog stays fresh, and the version is the highest source.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 339 tests (333
  prior plus 6 merge cases: cross-document identity, inert field names, duplicate
  imports, highest-version wins, clean range failure, and fast validation).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 20: multi-source merge, collate, labels, and rotation (M5).
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.

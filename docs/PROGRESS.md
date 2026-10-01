# Progress

## Current checkpoint

Work item 17: bounded repair with strict/recovery comparisons. Strict parsing runs
first; its failure engages a bounded rescan (trial-parsed headers, last wins,
gaps as free, tail-trailer or scanned-catalog root). The CLI repairs by default
with a warning; `--suppress-recovery` and the typed paths stay strict.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 323 tests (316
  prior plus 7 repair cases: strict/repair comparison, suppress parity,
  byte-identical repair, stale-catalog rejection, last-wins duplicates,
  bogus-storm bounds, and budget failure).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 18: response-file/job parsing, per-job limits, warning/exit behavior (M4).
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.

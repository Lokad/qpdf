# Progress

## Current checkpoint

Work item 20 (part 3: multi-file merge). `qpdf --pages` takes repeated
file/range groups with repeatable `--collate[=n[,m,...]]` (bare, uniform, or
per-source sizes); single groups without collation keep exact legacy behavior.
Job JSON accepts multi-entry `pages` arrays concatenating in order. Group size 0
matches upstream (spec contributes nothing); job `collate` stays unsupported.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 375 tests (370
  prior plus 6 merge cases net of 1 retired single-form case: multi-file order,
  collate interleaving and groups, dangling/out-of-range/collate errors, job
  multi-entry parity, and 2 legacy tests updated for the new grammar).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 21: split/replacement publication, backup rules, output collisions (M5).
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.

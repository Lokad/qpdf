# Progress

## Current checkpoint

Work item 15: selector audit and M3 close-out. Indirect `/Filter`/`/DecodeParms`
(plus predictor integers and `/EarlyChange`) resolve via the document engine;
xref-stream specs stay direct-only. Empty-document `FirstAndLast` yields an empty
rewrite, cyclic resources terminate, and escaped names/strings round-trip. The
consumer validates multi-page mapping; the supported profile records the M3 subset.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 307 tests (294
  prior plus 13 audit cases: indirect filter/parms/predictor/EarlyChange specs
  with an object-stream end-to-end, empty-document selection, shared-once
  resources, escaped name/string fidelity, and cyclic-resource termination).
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 16: inspection breadth and partial-check orchestration (M4).
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.

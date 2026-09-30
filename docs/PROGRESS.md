# Progress

## Current checkpoint

Work item 08: governed Flate and predictors. The internal `Filters/`
pipeline resolves `/Filter` and `/DecodeParms` (abbreviated names honored,
single-dict and aligned-array parms) and decodes FlateDecode chains with
strict zlib framing, Adler-32 verification, and TIFF/PNG predictor
transforms under output budgets. All other filters fail explicitly as
unsupported. No engine consumer is wired yet; structural decoding lands
with xref/object streams in work item 09.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 199 tests (166
  prior plus 33 filter cases: round-trips, header/adler/truncation/
  corruption rejection, expansion budgets, chain order and parms alignment,
  TIFF 8-bit/RGB/packed/1-bit and PNG None/Sub/Up/Average/Paeth round-trips
  including mixed row types, predictor defaults, dimension/value rejection,
  and row-mismatch cases).
- Strictness deltas against the pinned reference are recorded in
  `docs/COMPATIBILITY.md` (partial rows, unknown PNG row bytes, parms
  length mismatch, preset dictionaries); mirrored behaviors are noted too
  (single-dict alignment, upstream parms defaults, ignored unknown keys).
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency.
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 09: resolve xref streams and object streams, wiring the governed
   pipeline into structural decoding.
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.

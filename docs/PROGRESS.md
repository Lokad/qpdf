# Progress

## Current checkpoint

Work item 11: LZW, ASCIIHex, ASCII85, and RunLength filters. `FilterChain` decodes
Flate, LZW (EarlyChange 0/1, default 1), ASCIIHex, ASCII85, and RunLength in filter
order with predictors shared between Flate and LZW; DCT and other filters still fail
explicitly as unsupported. LZW mirrors upstream table, width-growth, KwKwK, EOD, and
table-full rules; ASCIIHex/ASCII85/RunLength mirror upstream state machines with
budget-bounded output. LZW xref streams open through the same pipeline.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 261 tests (216
  prior plus 45 filter cases: LZW round-trips across width growth for both
  EarlyChange values, hand vectors, growth boundaries, predictors after LZW,
  ASCIIHex/ASCII85/RunLength round-trips and edge cases, mixed-chain order, and
  an LZW xref-stream open).
- Regenerated `tests/Fixtures/xstream-lzw.pdf` with genuine LZW entry data;
  prior and chain behavior is unchanged and still green.
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency, plus a clean
  package-consumer smoke (`CONSUMER SMOKE PASSED`).
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 12: byte/version/indirect-length semantics with bounded reader cases.
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.

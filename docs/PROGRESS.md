# Progress

## Current checkpoint

Work item 03: bounded JPEG/Flate feasibility probes. `QpdfProbe probe-jpeg`
and `probe-flate` plus `CodecProbeTests` establish the M0 codec decisions in
`docs/COMPATIBILITY.md`. Work items 01-03 are complete; M0 still needs a real
qpdf 12.4.2 executable smoke. No PDF operation or public execution API exists.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 39 tests (13
  scaffold, 12 runner/fixture, 14 codec-probe cases).
- Executed `probe-jpeg`: grayscale/RGB 8x8 round-trips preserve dimensions;
  quality endpoints `1..100` enforced with qpdf `0 -> 1`; CMYK/YCCK gap
  confirmed (color-converted RGB, raw samples not preserved); dimension
  bounds and cancellation honored.
- Executed `probe-flate`: zlib round-trip exact; qpdf `1..9` to BCL
  Fastest/Optimal/SmallestSize buckets; corruption throws
  `InvalidDataException` while half-truncation yields short output (callers
  must validate length); expansion limit and cancellation enforced.
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency. PdfPig and probe
  ImageSharp use are development-only and are not packaged.
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

The Windows/Linux CI workflow builds the probe via the solution; the
reference lane remains an explicit local job requiring `QPDF_EXE`. Ordinary
`dotnet test` stays offline and never needs `external/` or a qpdf binary.

## Next work

1. M1: implement minimal owned input/output, governor and diagnostics, then a
   strict classic-xref reader and one complete read/select/write operation.
   Exercise it through narrow argv/job routes and a real package consumer.
   Reuse the codec bounds above; do not build the M8 JPEG adapter yet.
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.

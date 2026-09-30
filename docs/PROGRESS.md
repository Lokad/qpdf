# Progress

## Current checkpoint

Work item 10: incremental and hybrid precedence. `XRefTable` walks `/Prev`
chains and hybrid `/XRefStm` links with latest-wins merging (classic entries
win ties within a revision, mirroring upstream), cycle termination, a
revision-count budget, and exact union coverage of the effective `/Size`.
Deleted entries resolve as null; replaced entries, generations, and
compressed objects resolve transparently. Every trailer in the chain is
checked for encryption declarations.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 216 tests (209
  prior plus 7 chain cases: incremental replacement and deletion, hybrid
  stream merge, stream-led chains, `/Prev` cycle termination with closed
  leases, revision-cap enforcement, and selection from an update).
- Five new synthetic fixtures (incremental replacement/deletion, hybrid
  merge, stream-led chain, self-looping `/Prev`); classic and stream
  behavior is unchanged and still green.
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency.
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 11: LZW (both EarlyChange values), ASCIIHex, ASCII85, and
   RunLength filters with chain edge cases.
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.

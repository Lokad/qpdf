# Progress

## Current checkpoint

Work item 09: xref streams and object streams. The unified `XRefTable`
parses classic tables and Flate-compressed xref streams (`/W`, `/Index`,
exact data sizing) with trailer duties taken by the stream dictionary;
`/Prev`, `/XRefStm`, and `/Extends` fail explicitly as work-item-10 scope.
The engine decodes object streams once through the governed filter pipeline
(`N`/`First` validation, ascending offsets, per-entry extents, xref-backed
caching) with cycle guards. Compressed references resolve transparently;
selection output normalizes to classic xref.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 209 tests (199
  prior plus 10 xref-stream cases: stream opens with catalog/spot checks,
  typeless `/W` entries, compressed dict/int resolution, selection from
  both new forms with reopened outputs, LZW-labeled stream rejection with
  `UnsupportedFilter`, unknown entry types, `/Prev`, `/XRefStm`, `/Extends`,
  and entry offsets outside the input).
- Eight new synthetic fixtures (four xref-stream variants, two object-stream
  files, two chained-structure negatives); classic coverage and the 05b
  strict rules are unchanged and still green.
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency.
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary.

## Next work

1. Work item 10: incremental/hybrid precedence with deleted and replaced
   references (`/Prev` chains, `/XRefStm`, `/Extends`).
2. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result, including
   upstream acceptance of M1 outputs.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.

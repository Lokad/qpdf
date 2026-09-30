# Progress

## Current checkpoint

Work item 05a: strict PDF syntax layer in the shipped library. The public
object model (`PdfNull`, `PdfBoolean`, `PdfInteger`/`PdfReal` with preserved
token text, `PdfName`, `PdfByteString`, `PdfArray`, `PdfDictionary`,
`PdfReference`, `PdfStream`), the byte-oriented tokenizer, and the strict
governed parser (depth budgets, work/memory charging, duplicate-key
last-wins warnings, direct and indirect stream lengths) are implemented with
`QpdfParseException` positions. No xref, document, or operation exists yet.

## Verification

Verified locally on Windows with SDK 10.0.401:

- Locked restore, Release build with zero warnings, and all 99 tests (79
  prior plus 20 syntax cases: token shapes/offsets/malformed input, scalar
  fidelity, references, strings/names/hex escapes, duplicate keys, direct
  and indirect stream lengths, stream truncation/EOL rules, nesting and
  work budgets, indirect objects, structural equality).
- Duplicate-key last-wins matches the pinned upstream `insert_or_assign`
  behavior; odd hex nibbles pad per the PDF specification.
- Release `.nupkg`/`.snupkg` creation and inspection: expected contents only,
  with exactly ImageSharp 3.1.12 as the runtime dependency.
- Real qpdf 12.4.2 executable smoke has still not run; no exe checksum or
  JPEG/Zopfli build features are recorded.

Ordinary `dotnet test` stays offline and never needs `external/` or a qpdf
binary; internals are white-box tested via `InternalsVisibleTo`.

## Next work

1. Work item 05b: classic xref tables, trailer/`startxref`, `QpdfDocument`
   with bounded page-count/object inspection over this parser, encrypted-input
   structural rejection, and fixture-driven document tests.
2. Work item 06: copy and serialize a selected page, add narrow argv/job
   adapters, verify independent output plus replay counts.
3. Bootstrap a real qpdf 12.4.2 executable and record its checksum/features;
   run `tools/run-reference-checks.ps1` and reconcile the result.

Continue in small tested local commits. Update the compatibility entries and
this record with concrete evidence. Do not report full completion until all
required operations and dependency gates have been satisfied.

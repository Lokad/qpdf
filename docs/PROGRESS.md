# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The goal remains open: the clean
`e69cf22` panel meets parity in three cells, with ratios 0.81–3.33×.
Sparse selection is 10.72 / 3.22 ms; its managed transport reads the entire
16.8 MB input. [Results](BENCHMARK-RESULTS.md) and
[protocol](BENCHMARKS.md) separate complete jobs, memory diagnostics and historical
observations. Performance commits need reduced time or allocation, with no
minimum percentage gain. Noisy overlapping timings do not establish a time win.

Pushed `e69cf22` passed [hosted CI run 38320576709](https://github.com/Lokad/qpdf/actions/runs/38320576709):
Windows 7m00s, Ubuntu 5m37s, pinned reference 4m51s. Both platforms pass 17,825
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 object comparisons
and 442 rewrite trios. Seven visuals and both controls pass on the same writer
source. One push-triggered run qualified dictionary finalization; no manual rerun.
Batch useful source changes before another checkpoint.

The agreed [supported profile](SUPPORTED-PROFILE.md) and completed features remain
closed. CMYK/YCCK JPEG decoding remains opaque with warnings; Zopfli is
unavailable. Both accepted limitations are tested. Crypto, including empty-password
inputs, remains excluded. Other requirements and documented differences remain
in [compatibility](COMPATIBILITY.md); performance work does not waive them.

## Current correction and next work

Object-stream decoding reuses one parser across members. The existing entry
point resets error/abort/proximity/orphan/token state and restores its private
owner. Nested decoders remain independent; member bounds, per-member admission
and cache acceptance/discard scopes are unchanged. New argv/job controls preserve
a valid string after an aborted member, its disposal-surviving alias and cleanup.

All 1,657 affected reader/object-stream checks and nineteen final recovery
controls pass. Identical-driver streamed allocation falls 941.8 → 927.7 KB/job,
with unchanged hashes. Times 3.91 → 3.85 ms overlap; claim allocation only.
Release build/pack/inspection, fresh locked consumer/rejections, 26 fresh
managed/native outcomes, three negative controls, 1,858 parser comparisons,
442 rewrite trios and seven visuals/two controls pass. Visual wall is 25.96s.
The source unit is ready for integrated hosted ordinary/reference qualification.

## Recent integrated corrections

Private dictionary finalization now reuses established buckets and hash chains
when their capacity matches the compact final table. Larger-capacity sources and
repair-receipt tables keep the existing copy path. Conversion finishes before
ownership moves; memory rejection/cancellation leave the source usable. The same
helper preserves ordering, collisions, null values and immutable publication.

All 2,198 affected parser/name-table checks and 153 final ownership/recovery
controls pass. A same-driver object-heavy pair removes about 119 KB/job
(9.129 → 9.010 MB), with unchanged output hashes. Times overlap broadly
(74.94 → 49.41 ms); claim allocation only. Release build/pack/inspection and the
fresh locked consumer pass, including unchanged cache and 2 MiB multisource
limits. All 26 fresh managed/native outcomes and three negative controls pass,
with original inputs unchanged. The pinned parser lane passes 1,858 comparisons
and 442 rewrite trios. Seven visuals/two controls pass in 25.92 seconds.
The full local ordinary suite passes 17,825 tests, zero failures/skips, in
13m33s. Its unrelated slowest tests differ from the prior run; retain that
contended wall time without attributing it to this correction. The hosted
ordinary/reference/package jobs pass on integrated `e69cf22`.

Numeric/reference lookahead now classifies the following prefix before scanning
a whole token, using the existing whitespace and numeric lexer. Repair offsets,
grammar, error ordering and cancellation remain covered. A public reader with
two integers before a 1 MiB string now fits a representative work ceiling that
the baseline exhausted; a lower ceiling still rejects, both outcomes close
leases and the returned string survives disposal. All three new controls fail
on the baseline and pass the correction.

Canonical single-digit numbers also reuse immutable literal text, preserving
signed/zero-padded spellings and the same work observations. The same-driver
object-heavy pair removes about 54 KB/job (9.184 → 9.130 MB); output hashes match.
Times overlap (44.54 → 44.97 ms), so claim allocation only. A separate generated
large-string diagnostic has medians 38.34 → 30.29 ms with overlapping IQRs;
it adds no parity cell and establishes no decisive time win. A retirement-counter
read trial showed no useful reduction and was reverted.

Affected syntax/recovery/cancellation and reader controls pass, as do all 26
fresh managed/native outcomes, three negative controls, 1,858 pinned parser
comparisons and 442 rewrite trios. Release build/pack, package inspection, fresh
locked consumer and rejection controls pass. Seven visuals and both controls
also pass in 17.32 seconds. This source unit is integrated in the green
`431870c` checkpoint; earlier clean-panel evidence remains identified.

The numeric commit's hosted native reference passed, but ordinary jobs exposed
twelve exact-zero retirement assertions. Shared digit literals had per-job text
receipts despite their process lifetime. The correction omits those receipts
only for identical known literals; fresh text and scalar wrappers retain their
owners. All 340 affected checks pass without changed tests or limits, including
the twelve failures. Fresh 26-output/three-control validation and the locked
consumer also pass. The full local ordinary suite passes 17,823 tests with no
failures/skips in 5m25s. Both hosted ordinary platforms and native reference pass.

Page-attribute lookup now avoids traversal scratch for leaf hits and one
indirect parent. Missing optional attributes normally visit the page-tree root
and stop; callers already bypass most direct leaf hits. The first parent is
checked against the complete source identity. Longer chains allocate the same
governed visited set, seed prior identities, and preserve cycle/depth/work limits,
cancellation, raw-byte lookup and cleanup.

The correction passes 111 affected checks and eleven final controls, including
complete typed/argv/job cycle failures preserving prior output/closed leases,
generation-sensitive lookup and work failure after deeper traversal. Identical-
driver M500 selection allocation falls 19.843 → 19.520 MB/job (323 KB), with
unchanged output hashes. Times overlap (32.09 → 31.71 ms); claim allocation only.
The existing thirteen-cell profile and independent negative controls remain
binding. Logs/TRX, patches and raw observations are in the ignored
`external/reviews/2026-10-12-structural-review/` checkpoint.
All 26 fresh managed/native outcomes and three negative controls pass; input
hashes are unchanged. Explicit Release pack, inspection and a fresh locked
consumer pass, including the unchanged 2 MiB multisource case. The correction
is integrated in the green `be70d4c` checkpoint.

A short allocation trace identified repeated generic collection resizing.
Governed dictionaries/sets now request geometric growth. If its conservative
admission exceeds the memory ceiling, they use the prior smaller step before
allocation. This preserves the unchanged cache-consumer budget that rejected
the initial prototype. The existing admission, reconciliation and failure cleanup
remain shared. All 271 affected growth/parser/copier/writer checks pass.
The prototype's same-driver object-heavy allocation falls 10.223 → 9.183 MB/job
(1.041 MB); output hashes match and timings overlap. Final budget fallback is
separately checked; the paired prototype contains no time claim. Release build,
pack/inspection and the fresh locked consumer pass on the final source.
Seventeen cache-budget/alias controls also pass, along with all 26 final fresh
managed/native outcomes and three negative controls. Seven visuals/two controls
pass. This completes the local correction; the combined source checkpoint uses
the existing full CI/reference/package checks.

Continue with one measured parser/writer or reader/host cost at a time. Preserve
the thirteen cells, original inputs, meaningful ceilings and negative controls;
do not bypass public copying contracts to improve a ratio. Use focused checks
and narrow probes, normally under one minute, during implementation. Repeat
comprehensive reference, visual and consumer qualification at source integration
checkpoints when changes or unresolved concerns justify it.

## Useful completed work

- Names, dictionary enumeration, known arrays and parser scratch avoid redundant
  copies. Split uses known reachable IDs; fully overwritten buffers avoid clearing.
- Private copier/checker graphs use scoped ownership. Exclusive single-source
  memory-host jobs borrow immutable stream slices and fund the pinned input.
  Public readers, retaining custom hosts and multisource copying keep independent
  payloads. Defensive copies and surviving aliases remain covered.
- Uncompressed object-stream counting avoids member/payload captures; its paired
  allocation falls by 80.2 KB/job. Unescaped names reuse immutable backing bytes,
  saving 1.066 MB/job in object-heavy rewriting. Emitted hashes remain unchanged.
- Shared fixed tokens, pure sizing, incremental IDs and admitted output staging
  preserve offsets, IDs and publication. Underestimated hints grow safely; only
  written bytes publish and settled storage drops promptly.
- Batching/direct formatting covers ordinary/QDF/split, ID hashing and linearized
  publication. Retaining hosts receive immutable packets; staging hints, offsets,
  golden IDs and practical host caps survive. Paired allocations fall by 1.465
  MB/job for object-heavy output, 84.7 KB for streamed rewriting, and another
  415/425 KB for default IDs/linearization. Timings overlap. Public retention and
  publication controls use representative workloads and practical backing bounds.
- Developer emitters transfer privately read buffers through the existing API.
  The paired sparse file-job median falls 11.68 → 6.63 ms (43%); this is a transport
  correction. Separate memory-only Copy/Transfer medians are 2.68 / 1.14 ms, with
  16.26 MiB / 247 KiB allocation; these supply no native ratio.
- Benchmark acceptance rejects a no-op checker and marker-only object-stream
  claim. Four additional cells isolate IDs, object-heavy rewriting, sparse
  selection and single-page splitting. Original seven fixture hashes are unchanged.

Earlier `219eca3` qualification remains useful: both ordinary suites passed
17,846 checks in 6m11s, pinned reference in 5m57s, seven visuals/two controls in
20s, package/consumer controls in 19s / 26s. Its nine-cell 2× target is historical
evidence. Counts describe runs, rather than individual test or release quotas.

The approximate memory contract admits significant buffers, material growth and
copy overlap, and protects against allocation/decompression amplification. Keep
work limits, cancellation, host boundaries, cleanup and valid surviving outputs.
Unreleased charges do not prove retained memory. Exact CLR sizes, individual
small-object or metadata-alias receipts, fixed GC-round retirement and precise
bookkeeping charges remain withdrawn; see [governance](SUPPORTED-PROFILE.md#governance).
No forced production GC, parsed-input cache, runtime native bridge or new
accounting/benchmark framework was added.

Runtime pins and the five reviewed temporary ImageSharp 3.1.12 advisory exceptions
remain unchanged. No release-version change, tagging or package publication.

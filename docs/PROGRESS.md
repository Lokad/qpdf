# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The goal remains open: the clean
`11fb330` panel meets parity in three cells, with ratios 0.78–3.61×. Sparse
selection is the largest complete file-job gap (8.17 / 2.26 ms); its managed
transport reads the entire 16.8 MB input. [Results](BENCHMARK-RESULTS.md) and
[protocol](BENCHMARKS.md) separate complete jobs, memory diagnostics and historical
observations. Performance commits need reduced time or allocation, with no
minimum percentage gain. Noisy overlapping timings do not establish a time win.

Pushed `c6eca2e` passed [hosted CI run 38303565149](https://github.com/Lokad/qpdf/actions/runs/38303565149):
Windows 6m23s, Ubuntu 5m55s, pinned reference 3m55s. Both platforms pass 17,811
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 object comparisons
and 442 rewrite trios. Seven visuals and both controls pass on the same writer
source. Two push-triggered runs in that batch corrected one chunk-boundary test;
no manual reruns. Batch further source changes before another hosted checkpoint.

The agreed [supported profile](SUPPORTED-PROFILE.md) and completed features remain
closed. CMYK/YCCK JPEG decoding remains opaque with warnings; Zopfli is
unavailable. Both accepted limitations are tested. Crypto, including empty-password
inputs, remains excluded. Other requirements and documented differences remain
in [compatibility](COMPATIBILITY.md); performance work does not waive them.

## Current correction and next work

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
is a local source unit for the next combined integration checkpoint.

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

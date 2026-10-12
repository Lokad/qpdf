# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The goal remains open: the clean
`11fb330` panel meets parity in three cells, with ratios 0.78–3.61×. Sparse
selection is the largest complete file-job gap (8.17 / 2.26 ms); its managed
transport reads the entire 16.8 MB input. [Results](BENCHMARK-RESULTS.md) and
[protocol](BENCHMARKS.md) separate complete jobs, memory diagnostics and historical
observations. Performance commits need reduced time or allocation, with no
minimum percentage gain. Noisy overlapping timings do not establish a time win.

Pushed `8c99543` passed [hosted CI run 38289384516](https://github.com/Lokad/qpdf/actions/runs/38289384516):
Windows 7m05s, Ubuntu 5m30s, pinned reference 5m55s. Each platform includes locked
restore, Release build/tests, explicit pack, inspection, fresh consumer and
rejection checks. Local locked Release build had zero warnings/errors; seven
visual cases and two controls passed in 19s. One push-triggered run, no reruns.
Batch further coherent changes before another hosted checkpoint.

The agreed [supported profile](SUPPORTED-PROFILE.md) and completed features remain
closed. CMYK/YCCK JPEG decoding remains opaque with warnings; Zopfli is
unavailable. Both accepted limitations are tested. Crypto, including empty-password
inputs, remains excluded. Other requirements and documented differences remain
in [compatibility](COMPATIBILITY.md); performance work does not waive them.

## Current correction and next work

Ordinary/QDF/split publication now batches small writes in bounded scratch and
formats short tokens there using existing encoders. Exact packet copies remain
immutable for retaining hosts; logical offsets include pending bytes and flush
precedes commit. A public 200-copy page-selection job that exhausted a 500-host-
operation cap now completes under that cap. A two-operation control still fails,
preserves prior output and closes leases. Long tokens bypass bounded probes.

The local correction passes 2,237 affected checks, seven final controls, all 26
fresh extended managed/native outcomes and three historical negative controls.
Release pack, inspection and a fresh locked consumer also pass, including the
unchanged 2 MiB multisource case. Same-driver probes preserve output hashes and
reduce object-heavy allocation 11.754 → 10.289 MB/job (1.465 MB) and streamed
rewrite 1.189 → 1.105 MB/job (84.7 KB). Timings overlap; claim allocation only.
The initial batching-only prototype had no decisive performance win and remains
recorded. Source/patches, logs/TRX and raw observations are in the ignored
`external/reviews/2026-10-12-structural-review/` checkpoint.

The same batching now covers placeholder-ID hashing and classic/streamed
linearized publication, including optional first-pass output. Capacity hints
still reach admitted staging; pending bytes count toward offsets, and flushing
precedes digest/commit. Both linearized 200-copy jobs now complete under the
unchanged 500-host-operation cap. Golden IDs remain unchanged. The affected
suite passes 1,529 checks. Eight representative public publication cases replace
the private exact-receipt/chunk-boundary matrix, preserving both routes, decoded
inputs, output integrity, retained chunks, failure, cancellation and cleanup.

Same-driver pairs remove another 415 KB/job from default-ID rewriting and
425 KB/job from linearization; output hashes match. Times overlap under load,
so allocation alone qualifies these changes. The public raw archive now retains
ten runs, including both clean thirteen-cell panels and both batching prototypes.
Final-source semantic preflight passes all 26 fresh outcomes and three negative
controls. Locked Release restore/build pass with zero warnings/errors; explicit
pack, inspection and the fresh locked consumer pass, including the unchanged
2 MiB multisource case. Hosted integration qualification follows this combined
writer correction.

The `11fb330` hosted reference job passed in 5m39s. Each ordinary job passed
17,809 checks and failed one retained-output control that assumed the tiny stream
was a separate host write. Its correction checks every retained backing against
the existing 64 KiB bound, reassembles immutable chunks against published output,
and covers both batched and direct payload writes. It preserves the actual
whole-input retention regression. The following corrective hosted checkpoint
qualifies tests/packages; production code and benchmark coverage remain unchanged.

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

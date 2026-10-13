# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The goal remains open: the clean
`e2451c5` panel meets parity in one cell, with ratios 0.79–3.40×. M500 selection
and checking are near parity (1.06× / 1.05×); differing host load prevents
attributing separate-panel times to source changes. Sparse selection remains the
largest gap at 8.36 / 2.46 ms: managed transport reads the entire 16.8 MB input.
[Results](BENCHMARK-RESULTS.md) preserve all observations and corrections;
[the protocol](BENCHMARKS.md) separates complete jobs, memory diagnostics and
startup-inclusive measurements. Performance commits require reduced time or
allocation, without a minimum percentage gain.

Pushed `b70b9f0` passed [hosted CI run 38328414537](https://github.com/Lokad/qpdf/actions/runs/38328414537):
Windows 6m30s, Ubuntu 5m33s, pinned reference 6m01s. Both platforms pass 17,827
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 object comparisons
and 442 rewrite trios. Seven visuals and both controls pass on the same writer
source. The corrective push preserved actual CPU cancellation after stream admission; no manual rerun.

## Current correction and next work

Classic object loads reuse one idle parser and tokenizer per document. Active
nested stream-length loads keep separate instances. The existing object entry
resets repair state and borrowed-stream observations; tokenizer reuse resets
position, governor and scan work. Disposal drops both cached instances and their
input references. Per-active admission, cache ownership, limits and cancellation
remain unchanged; no parsed-document reuse or global pool was added.

All 3,906 affected syntax/reader/recovery/ownership checks pass, including new
argv/job cases with an aborted classic object, following nested stream load and
surviving stream/string aliases under valid/reconstructed xrefs. An identical-
driver object-heavy pair removes about 423 KB/job (8.902 → 8.479 MB), preserving
hashes. Medians 47.18 → 52.90 ms overlap; claim allocation only. The intermediate
parser-only pair (302 KB less) remains identified in the raw archive.

Release build/pack/inspection and the fresh locked consumer pass, including the
unchanged 2 MiB multisource ceiling. All 26 fresh managed/native outcomes and
three negative controls pass; original inputs are unchanged. Final integrated
ordinary/reference/package qualification uses one batched hosted checkpoint.
Unaffected visuals retain the identified `97c38d5` writer evidence.

Continue with the largest demonstrated reader/writer or transport cost. Sparse
selection still includes the full physical input read. Preserve all thirteen
cells, immutable public copying, meaningful limits and independent controls.
Use narrow probes and focused checks during implementation, then qualify accepted
source batches with the existing comprehensive machinery. Earlier dictionary
key-view and test-controller corrections are integrated in green `b70b9f0`; their
observations and original failed checkpoint remain in the benchmark/raw records.

## Useful completed work

- Names, concrete dictionary enumeration, known arrays and parser scratch avoid
  redundant allocations. Private dictionary finalization reuses established
  bucket/hash chains at unchanged capacity; conversion failures retain ownership.
- Scoped copier/checker ownership and exclusive memory-host stream borrowing
  preserve public/custom/multisource payload independence, funded pinned inputs,
  surviving immutable aliases and retained-host chunks.
- Bounded writer batching, direct formatting, pure sizing, uncompressed counting,
  incremental IDs and output staging preserve offsets, IDs and publication.
- Geometric collection growth keeps tight-budget fallback. Deferred ancestry
  scratch retains complete source identity, cycles, cancellation and work limits.
- Numeric/reference lookahead avoids scanning large following strings, with
  grammar/repair ordering preserved. Known global digit literals have no job-owned
  text receipt. Decoder-local parser reuse resets each compressed member's errors.
- Developer input transfer removes a private redundant copy. Benchmark validation
  rejects no-op checking, marker-only object streams and wrong content/order.

Earlier corrections, rejected trials and source/artifact identities remain in
[benchmark results](BENCHMARK-RESULTS.md) and its raw archives. Historical counts
and individual test identities are not release quotas. Closed supported features
remain closed; further work must name a concrete defect or bounded verification.

The [supported profile](SUPPORTED-PROFILE.md) stays delivered. CMYK/YCCK JPEG
remains opaque with warnings, Zopfli is unavailable, and crypto including
empty-password inputs is excluded. These limitations are explicit and tested;
other [compatibility requirements](COMPATIBILITY.md) are not waived.

`MaxMemoryBytes` bounds approximate charged working storage. Admit significant
buffers, material growth/copy overlap and decompression/allocation amplification;
retain work limits, cancellation, host boundaries, cleanup and output integrity.
Unreleased charges do not prove retained memory. Exact CLR sizes, individual
small-object/metadata-alias receipts, fixed GC rounds and precise bookkeeping
charges remain withdrawn; see [governance](SUPPORTED-PROFILE.md#governance).
No CPU parallelism, forced production GC, parsed-job cache, runtime native bridge
or replacement accounting/benchmark framework was introduced.

Runtime pins and the five reviewed temporary ImageSharp 3.1.12 advisory exceptions
remain unchanged. No release-version change, tagging or package publication.

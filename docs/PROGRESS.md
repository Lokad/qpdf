# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The goal remains open: clean
`d4032b3` meets parity in three cells, with ratios 0.67–3.61×. Structural checking
is near parity at 1.0088×; ordinary/streamed rewriting are 1.44× / 1.48×. Sparse
selection is the largest gap (6.17 / 1.71 ms), including the full 16.8 MB input
read. [Benchmarks](BENCHMARKS.md) gives the current figures, protocol and raw
results. Performance commits need reduced time or allocation, with no minimum
percentage gain.

Pushed `d4032b3` passed [hosted CI run 38342431222](https://github.com/Lokad/qpdf/actions/runs/38342431222):
Windows 6m44s, Ubuntu 5m48s, pinned reference 5m35s. Both platforms pass 17,850
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 object comparisons
and 442 rewrite trios. The clean final-source benchmark panel retains all
22,820 observations, fresh semantics and original input hashes.

## Current correction and next work

Counted output now sizes bounded writer batches while preserving the host's
preallocation hint. Outputs below 16 KiB and tight jobs keep the 256-byte batch;
larger jobs use 2–4 KiB within current memory/work headroom, allocated once through
the existing governor. Synchronous destinations borrow scratch; retaining hosts
receive immutable packets. A public 200-page selection now completes within
32 host operations; the two-operation negative control still rejects and keeps
previous output. Cancellation, cleanup and byte limits remain enforced.

The identical-driver pair reduces allocation by about 11.8 KB per M500 rewrite
(18.3874 → 18.3756 MB) and 1.1 KB per X100 rewrite (828.5 → 827.3 KB).
Hashes match. Medians 20.470 → 20.190 ms and 2.832 → 2.725 ms have overlapping
IQRs; claim allocation only. Earlier trials, including the accidental omission
of host preallocation and its large allocation regression, remain in the raw
archive. None supplies a speed claim.

All 3,625 affected writer/publication/practical-memory checks pass (54s).
Release Windows/Linux builds, explicit pack/inspection, fresh locked consumer
and rejection controls pass (package checks 45s). All 26 fresh managed/native
outcomes and three negative controls pass; original input hashes and the 2 MiB
multisource ceiling remain unchanged. Comprehensive ordinary/reference/package
qualification uses the integrated push; the run above identifies the preceding
qualified source.

Next, measure the largest remaining reader/writer/transport costs. Object-stream
capture consolidation is deferred: current X100 rewriting emits no object streams,
and the generation cell's 103 members occupy only 8.7 KB of combined payloads
against 922 KB allocated per job. Sparse selection remains the largest gap and
includes full-input transport. Keep all thirteen cells, public immutability and
independent negative controls. Sampled GC suspension does not establish a leak.

## Useful completed work

- Names, concrete dictionary enumeration, known arrays and parser scratch avoid
  redundant allocations. Private dictionary finalization reuses established
  bucket/hash chains at unchanged capacity; conversion failures retain ownership.
- Scoped copier/checker ownership and exclusive memory-host stream borrowing
  preserve public/custom/multisource payload independence, funded pinned inputs,
  surviving immutable aliases and retained-host chunks.
- Copying reuses unchanged immutable containers while retaining remapping, cycles,
  omitted/repaired entries, order and original source ownership.
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
[benchmarks](BENCHMARKS.md) and its raw archives. Historical counts
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

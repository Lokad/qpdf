# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The goal remains open: clean
`fe2dd1d` meets parity in one cell, with ratios 0.74–3.40×; M500 selection and
single splitting remain near parity at 1.0121× / 1.0178×. Ordinary/streamed
rewriting are 1.45× / 1.51×. Sparse selection is the largest gap (5.69 / 1.67 ms),
including the required full 16.8 MB input read. [Results](BENCHMARK-RESULTS.md)
preserve all observations; [the protocol](BENCHMARKS.md) separates complete jobs,
memory diagnostics and startup-inclusive measurements. Performance commits need reduced time or
allocation, without a minimum percentage gain.

Pushed `a8a08cc` passed [hosted CI run 38340347635](https://github.com/Lokad/qpdf/actions/runs/38340347635):
Windows 6m45s, Ubuntu 4m09s, pinned reference 6m01s. Both platforms pass 17,845
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 object comparisons
and 442 rewrite trios. Production is unchanged from the clean `fe2dd1d` panel;
its 24,480 observations/fresh semantics remain valid without retiming.

## Current correction and next work

Graph copying now reuses unchanged immutable arrays/dictionaries and stream
wrappers. The existing owned builders allocate only after the first changed or
removed child; earlier children are copied without another recursive visit.
Reference identities/cycles, Parent/null omission, repaired stream Length,
ordering, depth/work/cancellation and original source ownership remain.

The identical-driver pair removes about 934 KB/job from object-heavy rewriting
(8.0791 → 7.1448 MB) and 190 KB/job from M500 rewriting (18.5766 → 18.3870 MB).
Hashes match. Object-heavy medians 89.942 → 68.822 ms narrowly separate in IQR
under variable load; M500 medians 25.968 → 23.074 ms overlap. Allocation is the
primary gain. The prior canonical-digit correction removes 67.6 KB/job / 6.6 KB/job
from object-heavy/streamed rewriting; no measured time claim. Fully overwritten
buffers avoid redundant clearing; the rejected async-wrapper trial remains recorded.

All 2,137 affected writer/document-value/practical-memory checks pass (54s),
including first/middle/last remapping, cycles, omitted entries, repaired lengths
and valid bytes after source closure. Release build/pack/inspection, fresh locked
consumer, 26 fresh managed/native outcomes and three negative controls pass.
The unchanged 2 MiB multisource ceiling and every original input hash remain.
Comprehensive ordinary/reference/package qualification uses the integrated push.
The raw archive preserves thirty-nine runs, including failed CI checkpoints,
accepted/rejected candidates and contended observations without dropping samples.

Continue with measured reader/writer/transport costs. Object-stream members still
become separate byte arrays before payload assembly; consolidation must preserve
bounded captures, offsets, async retention, practical limits and cleanup. Sparse
selection includes full-input transport and remains the largest gap. Keep all
thirteen cells, public immutability and independent negative controls. Existing
reader lifetime scopes and output capacity hints already avoid suspected costs;
reuse them. Sampled GC suspension does not establish retained-memory leaks.

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

# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The goal remains open: the clean
`8f69bb7` panel meets parity in three cells, with ratios 0.76–3.23×. Content-ID
and streamed rewriting remain 1.68× / 1.62×; differing host load prevents
attributing separate-panel times to source changes. Sparse selection remains the
largest gap at 7.17 / 2.22 ms: managed transport reads the entire 16.8 MB input.
[Results](BENCHMARK-RESULTS.md) preserve all observations and corrections;
[the protocol](BENCHMARKS.md) separates complete jobs, memory diagnostics and
startup-inclusive measurements. Performance commits require reduced time or
allocation, without a minimum percentage gain.

Pushed `8f69bb7` passed [hosted CI run 38332182116](https://github.com/Lokad/qpdf/actions/runs/38332182116):
Windows 6m28s, Ubuntu 5m23s, pinned reference 3m32s. Both platforms pass 17,833
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 object comparisons
and 442 rewrite trios. Seven visuals and both controls pass on the same writer
source. One push qualified reader and adapter corrections; no manual rerun.

## Current correction and next work

Batching borrows bounded scratch for explicitly synchronous hash and built-in
memory-host destinations. The capability passes through governed transactions;
custom/retaining hosts keep immutable packets and the original async path.
Shared admission preserves host-operation counts, output-byte charging, limits,
cancellation and publication. Large payloads avoid async dispatch where supported.
No public API, digest algorithm, global pool or CPU parallelism was added.

All 3,377 affected writer/host/ID/QDF/linearization/retention/publication checks
pass, including true/false capability, scratch reuse and failure/cancellation.
The identical-driver content-ID pair removes about 238 KB/job (18.906 → 18.667 MB)
and reduces medians 36.324 → 32.694 ms, with separated IQRs and matching hashes.
Native is 26.667 ms (1.23×): parity remains open. Streamed allocation falls
898.0 → 873.7 KB/job; timings overlap, so claim allocation only. The intermediate
hash-only pair (119 KB less) remains identified in the raw archive.

Release build/pack/inspection, fresh locked consumer and final 26 fresh managed/
native outcomes plus three negative controls pass. The unchanged 2 MiB multisource
ceiling and original input hashes remain. Comprehensive ordinary/reference/package
qualification uses one integrated hosted run; unaffected visuals retain identified
writer evidence. The raw archive includes thirty-three runs and the clean prior
panel, without dropping candidates or contended samples.

Continue with the largest demonstrated reader/writer or transport cost. Current
profiles place content-ID hashing and writer dispatch among contributors; sampled
GC suspension does not prove retained-memory leaks. Preserve all thirteen cells,
original inputs, public immutability, practical limits and independent controls.
Use narrow probes and focused checks, then qualify coherent accepted source batches.

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

# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The goal remains open: clean
`fc5a304` meets parity in two cells, with ratios 0.70–3.60×; M500 selection is
near parity at 1.0088×. Ordinary/streamed rewriting remain 1.60× / 1.50×.
Sparse selection is the largest gap (6.43 / 1.78 ms), including the required
full 16.8 MB input read. [Results](BENCHMARK-RESULTS.md) preserve all observations;
[the protocol](BENCHMARKS.md) separates complete jobs, memory diagnostics and
startup-inclusive measurements. Performance commits need reduced time or
allocation, without a minimum percentage gain.

Pushed `fc5a304` passed [hosted CI run 38335308941](https://github.com/Lokad/qpdf/actions/runs/38335308941):
Windows 6m29s, Ubuntu 4m46s, pinned reference 4m33s. Both platforms pass 17,837
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 object comparisons
and 442 rewrite trios. Unaffected visuals retain identified writer evidence.
The clean final panel records all 21,903 observations and fresh semantic checks.

## Current correction and next work

Canonical one-digit integers reuse ten immutable process-lifetime values,
removing parsed scalar allocation and individual ownership receipts. Original
numeric validation/work/cancellation still runs. Signed/padded spellings, references
and public authored instances retain their original behavior and ownership.
No unbounded interning, parsed-job cache, public API or CPU parallelism was added.

The identical-driver pair removes about 67.6 KB/job from object-heavy rewriting
(8.1466 → 8.0790 MB) and 6.6 KB/job from streamed rewriting (873.6 → 867.0 KB).
Hashes match. Medians 54.238 → 48.257 ms and 3.499 → 3.195 ms have overlapping
IQRs; claim allocation only. Fully overwritten parser arrays and governed-buffer
snapshots/growth also avoid redundant clearing, retaining explicit zeroed gaps.
Their separate pair provides no measured gain. An async-wrapper prototype lacked
a measured gain and was restored; every observation remains in the raw archive.

All 11,788 affected parser/foundation/writer/ownership checks pass (4m43s),
including original limits, cancellation, cleanup and new spelling/gap regressions.
Release build/pack/inspection, fresh locked consumer and final 26 fresh managed/
native outcomes plus three negative controls pass. The unchanged 2 MiB multisource
ceiling and original input hashes remain. Comprehensive ordinary/reference/package
qualification uses the integrated push-triggered hosted run. The raw archive now
preserves thirty-seven runs without dropping candidates or contended samples.

Continue with a concrete measured reader/writer or transport cost. One short
ordinary-rewrite profile attributes most sampled copying to required host staging,
transport and public defensive copies; preserve those boundaries. Sampled GC
suspension does not establish retained-memory leaks. Use narrow allocation probes
and focused checks, then qualify coherent accepted batches. Keep all thirteen
cells, original inputs, public immutability and independent negative controls.

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

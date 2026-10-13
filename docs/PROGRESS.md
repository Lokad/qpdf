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

Pushed `97c38d5` passed [hosted CI run 38325794775](https://github.com/Lokad/qpdf/actions/runs/38325794775):
Windows 6m25s, Ubuntu 4m30s, pinned reference 5m02s. Both platforms pass 17,827
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 object comparisons
and 442 rewrite trios. Seven visuals and both controls pass on the same writer
source. One push-triggered run qualified decoder-local parser reuse; no manual rerun.

## Current correction and next work

The [e2451c5 hosted run](https://github.com/Lokad/qpdf/actions/runs/38327442961)
passes Ubuntu and pinned reference, but Windows exposed a short cancellation-test
window: all eight stream copies completed before the sleeping observer canceled.
The test controller now polls without sleeping, retaining the original payload,
limits, timeout/retries and required canceled outcome, nested reservation and
cleanup assertions. Production sources are unchanged. All 41 parser-ownership
checks and five independent runs of the exact failure pass. Full qualification
uses one corrective push, with no manual rerun or repeated production benchmark.

Owned PDF dictionaries defer their cached key-view wrapper until `Keys` is
requested. Internal entry-only consumers avoid that allocation. Generic and
nongeneric key order, lookup, immutable publication and surviving aliases retain
their existing behavior. The identical-driver object-heavy pair removes about
106 KB/job (9.009 → 8.902 MB), with unchanged hashes. Times 56.20 → 52.11 ms
overlap; claim allocation only.

All 9,342 affected foundation/parser/copier/serializer checks pass, with zero
failures/skips. Release build/pack, inspection and the fresh locked consumer pass,
including the unchanged 2 MiB multisource limit. All 26 fresh managed/native
outcomes and three negative controls pass; original inputs are unchanged. The
clean final panel preserves 12,719 observations. Full ordinary/reference/package
qualification uses the corrective push-triggered CI run.
Unaffected visual behavior retains the identified `97c38d5` evidence.

Continue with one measured parser/writer or reader/host cost at a time. Parser
construction/validation per classic indirect object is a candidate; reuse must
preserve nested loads, repair state, borrowed payload policy and scoped ownership.
Sparse file transport remains explicit; changing input boundaries or dropping
public defensive copies cannot establish parity. Keep all thirteen cells and
important interactions. Use narrow probes and focused checks while debugging,
then qualify accepted source batches with the existing comprehensive machinery.

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

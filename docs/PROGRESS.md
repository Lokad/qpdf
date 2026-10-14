# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The target remains open. The clean
`4d7e683` panel records five cells at or below 1.0×, with ratios 0.70–3.94×.
[Benchmarks](BENCHMARKS.md) gives comparative figures and the reproducible protocol.
Performance commits need reduced time or allocation, with no minimum percentage.

Pushed `d06beef` passed [hosted CI run 38486253203](https://github.com/Lokad/qpdf/actions/runs/38486253203):
Windows 6m22s, Ubuntu 5m54s, pinned reference 5m54s. Both platforms pass 17,864
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 exact object
comparisons and 442 rewrite trios. The current correction passed local
qualification; its integration runs the comprehensive gates on push.

## Current correction and next work

Classic emission stores offsets in the existing admitted WriterOrder<long> when
model numbers are validated dense/increasing and memory/work headroom permits.
Sparse, duplicate, reordered and low-headroom layouts keep the current dictionary
path. Counting remains allocation-free for dense offsets. The existing sequence
owner clears storage and controller references on success, failure and cancellation;
offset storage retires before unrelated trailer output. No new index or cache.

Identical-driver allocation falls about 2.6 KB/job for X100 (718.4 → 715.8 KB)
and 25.7 KB for object-heavy rewriting (6.2177 → 6.1920 MB). Every output hash
matches. Medians 2.401 → 2.465 ms and 30.504 → 30.516 ms have overlapping
IQRs; claim allocation only.

All 2,096 affected writing/practical-memory checks pass (46s). Existing completed-
task/retained-host controls, offset-width and later-body-error precedence remain;
added reordered counting/emission and two tight-memory rejection/cleanup cases.
An initial raw-array prototype failed the completed-writer witness; reviewing
failure cleanup led to reuse of the established sequence owner. Failed evidence
is retained and the incomplete prototype was not committed. Windows/Linux Release
builds, explicit pack, inspection, fresh locked consumer and rejection controls
pass (24s), preserving the 2 MiB multisource ceiling. All 26 fresh managed/native
outcomes and three negative controls pass; original inputs are unchanged.
The clean committed-source panel preserves all 19,976 observations and an empty
source diff. Five cells meet parity and eight remain above it; splitting at
1.0013× remains open. Host-load variability is substantial, and separate panels
do not attribute gains to a source change. One hosted push qualifies code and
report.

Sparse full-input transport remains the largest boundary gap. The timed emitter
already reads the full remaining span and the exclusive memory host borrows its
immutable snapshot. Lazy parsing alone cannot remove this required full read;
the original comparison and every agreed workload remain in scope.

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
  Batches use admitted 2–4 KiB scratch where justified, preserving host capacity
  hints and completing the 200-page public selection within 32 host operations.
- Geometric collection growth keeps tight-budget fallback. Deferred ancestry
  scratch retains complete source identity, cycles, cancellation and work limits.
- Numeric/reference lookahead avoids scanning large following strings, with
  grammar/repair ordering preserved. Known global digit literals have no job-owned
  text receipt. Decoder-local parser/tokenizer reuse resets each member's state.
- Exact-count writer hints avoid growth copies where current memory/work permits;
  tight jobs keep normal growth, with sparse keys and publication cleanup preserved.
- Bounded reverse xref-tail search preserves the window and final-marker grammar,
  removing the per-byte scan while retaining malformed-tail rejection and cleanup.
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

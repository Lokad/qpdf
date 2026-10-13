# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The target remains open. The last
clean `bb2fad5` panel records three cells at or below 1.0×, with ratios 0.70–3.07×.
[Benchmarks](BENCHMARKS.md) gives the current comparison and reproducible protocol.
Performance commits need reduced time or allocation, with no minimum percentage.

Pushed `773848e` passed [hosted CI run 38450794451](https://github.com/Lokad/qpdf/actions/runs/38450794451):
Windows 6m29s, Ubuntu 5m41s, pinned reference 5m57s. Both platforms pass 17,858
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 exact object
comparisons and 442 rewrite trios. That source remains the qualified checkpoint
while the current allocation batch completes integration.

## Current correction and next work

Uncompressed loads reuse one empty document-local scratch owner alongside the
idle parser/tokenizer. Nested loads keep independent owners and close them on
return. Cache acceptance transfers values before scratch is discarded; rejected
attempts discard their storage before reuse. Document disposal closes the idle
owner. No disposed scope is reopened and no global pool/cache is introduced.

The identical-driver pair reduces object-heavy allocation 6.8045 → 6.7175 MB/job
(about 87 KB less) and X100 800.4 → 796.2 KB/job (about 4.2 KB less). Output hashes
match. Medians 39.666 → 42.117 ms / 2.919 → 3.108 ms have overlapping IQRs;
claim allocation only. Both native parity gaps remain.

All 4,124 focused reader/parser/practical-memory checks pass (24s). The existing
rejected-header/alias control now exercises a later nested stream-length load
and independent dictionary/string/stream values after document closure.
Recovery/retry, cancellation, meaningful limits and cleanup remain covered.
Dictionary conversion now creates its private destination frozen, omitting a
construction weak reference which was immediately dropped on publication.
Mutable construction guards and conversion ownership are unchanged. The
identical-driver pair saves another 51.4 KB/job in object-heavy rewriting
(6.7178 → 6.6663 MB) and 5.2 KB/job in X100 (796.3 → 791.1 KB), with matching
hashes. Medians 35.561 → 35.405 ms / 2.872 → 2.675 ms have overlapping IQRs;
claim allocation only. All 4,148 affected reader/parser/name-storage/practical-
memory checks pass (22s), including cancellation, overlap rejection and published
aliases that release their completed execution state.

Windows/Linux Release builds and explicit pack/inspection/fresh locked consumer/
rejections pass (34s), preserving the 2 MiB multisource ceiling. All 26 fresh
managed/native outcomes and three negative controls pass; inputs are unchanged.
One clean complete panel and one hosted push qualify this accepted source batch.
Sparse full-input transport remains
the largest boundary gap; prepared-memory diagnostics do not close its original
comparison. Capture consolidation remains deferred for its small contribution.

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

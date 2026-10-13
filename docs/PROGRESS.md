# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The target remains open. The clean
`4bfc8ba` panel records three cells at or below 1.0×, with ratios 0.71–3.13×.
[Benchmarks](BENCHMARKS.md) gives comparative figures and the reproducible protocol.
Performance commits need reduced time or allocation, with no minimum percentage.

Pushed `e80abfc` passed [hosted CI run 38455595160](https://github.com/Lokad/qpdf/actions/runs/38455595160):
Windows 6m34s, Ubuntu 5m48s, pinned reference 4m39s. Both platforms pass 17,859
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 exact object
comparisons and 442 rewrite trios. The current correction passed local
qualification; its integration runs the comprehensive gates on push.

## Current correction and next work

The first parsed xref section supplies a concrete entry count to the empty
merged dictionary. Existing parser admission and capacity reconciliation size
it once where memory/work headroom permits. Tight jobs retain geometric growth;
later sections retain newest-first merging and ordinary growth. Unverified
subsection promises and overlapping totals do not supply the hint.

The identical-driver M500 pair reduces allocation 18.2471 → 18.1351 MB/job
(about 112 KB less), with matching output hashes. Medians 20.969 → 19.531 ms
and IQRs 19.738–21.376 / 19.034–20.468 overlap; claim allocation only.
X100 allocation is effectively unchanged, and medians 2.901 → 3.096 ms overlap.
Both parity gaps remain.

All 4,127 affected reader/parser/practical-memory checks pass (20s). Three final
controls pass (30ms), covering normal, 4 KiB memory and 128-unit work budgets,
signed keys, growth beyond the hint, overlap and cleanup. Existing public xref
controls retain sparse/free entries, duplicates, hybrid/incremental precedence,
cancellation and surviving aliases. Windows/Linux Release builds and explicit
pack/inspection/fresh locked consumer/rejections pass (20s), retaining the 2 MiB
multisource ceiling. All 26 fresh managed/native outcomes and three negative
controls pass; original inputs remain unchanged. One clean complete panel and
one hosted push qualify the integrated source. The clean final-source panel
preserves all 16,929 observations and an empty source diff. Timing varies
substantially under host load; different panels do not establish source gains
or regressions.

A bounded current-source profile guided this correction: six seconds of CPU
sampling and four of allocation sampling on the original ordinary-rewrite job.
PollGC stacks occupy 36.2% of sampled job intervals. Excluding them, writing
accounts for 33.5%, reading/parsing 21.4%, transport/runtime 38.7% and copying 6.5%.
These are sampled intervals, not exact CPU or retained-memory measurements.
Most bulk copies are required staging/public output copies. Xref arrays and
resizing supplied a concrete smaller allocation target. Sparse full-input
transport remains the largest boundary gap; its original comparison remains.

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

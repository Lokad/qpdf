# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The target remains open. The clean
`3d8f67c` panel records four cells at or below 1.0×, with ratios 0.66–3.52×.
[Benchmarks](BENCHMARKS.md) gives comparative figures and the reproducible protocol.
Performance commits need reduced time or allocation, with no minimum percentage.

Pushed `2489596` passed [hosted CI run 38480170263](https://github.com/Lokad/qpdf/actions/runs/38480170263):
Windows 7m08s, Ubuntu 5m37s, pinned reference 5m44s. Both platforms pass 17,865
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 exact object
comparisons and 442 rewrite trios. The current correction passed local
qualification; its integration runs the comprehensive gates on push.

## Current correction and next work

Page copying creates the admitted inheritance dictionary only for its first
materialized value. Empty results use the BCL immutable empty dictionary.
All three consumers read the existing interface; copier null validation stays
unchanged. MediaBox validation reuses the existing immutable lookup key.
Ancestry traversal, attribute resolution and source identity remain unchanged.

Identical-driver pairs reduce allocation by about 27.3 KB/job for M500
(17.8925 → 17.8652 MB) and 5.6 KB for X100 (740.2 → 734.6 KB).
Every output hash matches. Medians 19.196 → 20.596 ms and 2.663 → 2.602 ms
have overlapping IQRs; claim allocation only.

All 2,132 affected writing/inheritance/split/practical-memory checks pass (52s).
The existing public null-MediaBox fallback case now mixes inherited, local and
duplicate pages and checks cleanup after reopening the output. Existing controls
retain all four inheritable attributes, null/indirect resolution, ancestry failure,
limits, cancellation, output immutability and failed publication.
Windows/Linux Release builds and explicit pack, inspection, fresh locked consumer
and rejection controls pass (28s), preserving the 2 MiB multisource ceiling.
All 26 fresh managed/native outcomes and three negative controls pass; original
inputs are unchanged. One clean complete panel and one hosted push qualify the
committed source.

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

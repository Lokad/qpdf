# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The target remains open. The clean
`9e30a09` panel records four cells at or below 1.0×, with ratios 0.63–3.57×.
[Benchmarks](BENCHMARKS.md) gives comparative figures and the reproducible protocol.
Performance commits need reduced time or allocation, with no minimum percentage.

Pushed `f31e2a9` passed [hosted CI run 38460486021](https://github.com/Lokad/qpdf/actions/runs/38460486021):
Windows 6m44s, Ubuntu 5m10s, pinned reference 4m57s. Both platforms pass 17,862
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 exact object
comparisons and 442 rewrite trios. The current correction passed local
qualification; its integration runs the comprehensive gates on push.

## Current correction and next work

Single-source and multisource copying now prepare original object-number map
lists/inversions only when QDF is enabled. Split groups likewise prepare their
renumbered original-number maps only for QDF. Private split helper parameters
accept the existing optional-map writer contract. QDF comments retain their
previous source order and values; original document IDs and hashing are separate.
Normal, streamed and linearized output use their unchanged writer paths.

Identical-driver pairs reduce allocation by about 73.3 KB/job for M500
(18.1356 → 18.0623 MB), 16.5 KB for X100 (791.4 → 774.9 KB), 154.0 KB for
object-heavy rewriting (6.4282 → 6.2743 MB) and 32.3 KB for individual splitting
(1.5202 → 1.4879 MB). Every output hash matches. All timing ranges overlap under
substantial variability; claim allocation only.

All 2,124 affected writing/split/practical-memory checks pass (49s), including
two new QDF split cases for retained/suppressed original-number comments and
valid independently reopened groups. Existing controls protect document IDs,
content hashing, stream/linearized offsets, output immutability, cancellation,
failed publication and cleanup. Windows/Linux Release builds and explicit pack,
inspection, fresh locked consumer and rejection controls pass (21s), preserving
the 2 MiB multisource ceiling. All 26 fresh managed/native outcomes and three
negative controls pass; original inputs are unchanged. One clean complete panel
and one hosted push qualify the committed source. The clean final-source panel
preserves all 14,161 observations and an empty source diff. Timing varies
substantially under host load; different panels do not establish source gains
or regressions.

The current ordinary-rewrite profile identifies required staging/public output
copies as the dominant bulk copying. Its int-map resize samples exposed this
unused preparation. Sparse full-input transport remains the largest boundary
gap; its original comparison and every agreed workload remain in scope.

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

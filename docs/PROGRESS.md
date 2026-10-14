# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The target remains open. The clean
`5f108a0` panel records five cells at or below 1.0×, with ratios 0.61–3.43×.
[Benchmarks](BENCHMARKS.md) gives comparative figures and the reproducible protocol.
Performance commits need reduced time or allocation, with no minimum percentage.

Pushed `2fbb88d` passed [hosted CI run 38547746561](https://github.com/Lokad/qpdf/actions/runs/38547746561):
Windows 6m34s, Ubuntu 5m59s, pinned reference 6m01s. Both platforms pass 17,885
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 exact object
comparisons and 442 rewrite trios. Dependency pins and release version are unchanged.

## Current correction and next work

Xref-stream reading hints its empty destination dictionary from the validated
row count only for ordered, disjoint index ranges. Object zero is excluded;
reversed/overlapping ranges keep ordinary growth. The existing quarter-headroom
memory/work guard, row grammar, cancellation and ownership cleanup remain.

The original X100 rewrite allocates 694.175→669.955 KB/job, about 24.2 KB less,
with identical independently validated output bytes. Medians 2.244→2.147 ms
have overlapping IQRs (2.169–2.378 / 2.076–2.250); retain allocation only.
All 3,480 focused reader/public-selection/cleanup checks pass (22s), including
four public range controls: ordered/reversed success, duplicate rejection and
row-count mismatch rejection. All 26 fresh managed/native outcomes and three
negative controls pass with original inputs. Locked Release build/explicit pack,
inspection/fresh consumer/rejections pass (19s), unchanged 2 MiB multisource
ceiling. The clean-source comparison retains all 24,062 observations, original
inputs and independent semantics. Five cells meet parity; sparse selection
remains the largest gap at 3.43×. Wide timing ranges remain visible; separate
panels do not establish source-change timing attribution. One combined hosted
push qualifies implementation and refreshed report.

Full fresh input allocation/reading is a demonstrated boundary cost. Native can
read selected objects lazily; the managed reader consumes contiguous storage and
the memory host borrows its immutable snapshot. Parser-only changes cannot remove
the emitter's required full read. A fresh pinned-array trial gave overlapping
timings and unchanged allocation; it was restored. A ranged-reader redesign
would also need asynchronous nested resolution and explicit storage ownership;
it cannot silently replace the original full-read comparison. Every original comparison,
limit, cancellation, cleanup and immutable-output guarantee remains in scope.

## Useful completed work

- Four-slot local array scratch removes temporary small-array lists; larger
  arrays retain governed growth, with public copies and discard cleanup intact.
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

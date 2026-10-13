# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The goal remains open: the last
clean `0a5837c` panel meets parity in three cells, with ratios 0.80–3.56×.
[Benchmarks](BENCHMARKS.md) gives comparative figures, the protocol and raw
results. Performance commits need reduced time or allocation, with no minimum
percentage gain.

The preceding `5bbd318` passed [hosted CI run 38447125751](https://github.com/Lokad/qpdf/actions/runs/38447125751):
Windows 6m41s, Ubuntu 5m04s, pinned reference 6m11s. Both platforms pass 17,856
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 object comparisons
and 442 rewrite trios. The current reader correction passed local qualification;
CI verifies its integrated source and package on push.

## Current correction and next work

Object-stream decoding uses one scratch owner for its member loop. Cache
acceptance transfers storage; skipped members discard their charge before
advancing. Nested decoders retain separate owners. The scope closes on failure
and cancellation, while accepted public values remain usable after disposal.
No scope is reopened and no pool or global cache is introduced.

The identical-driver X100 pair reduces allocation 804.5 → 800.3 KB/job (about
4.2 KB less), with identical output hashes. Medians 3.457 → 3.536 ms and
IQRs 3.207–3.690 / 3.364–4.008 overlap; claim allocation only. The native
median is 2.656 ms and the 1.33× parity gap remains.

All 4,124 focused reader/parser/practical-memory checks pass (22s), including
raw/Flate member boundaries, trailing-member rejection/retry, repair, cache
replacement, cancellation and surviving aliases. Two new cases distinguish
skipped-member charges from actual retained memory and protect the following
accepted value's budget. Windows/Linux Release builds and explicit pack,
inspection, fresh locked consumer and rejection controls pass (package checks
34s). All 26 fresh managed/native outcomes and three negative controls pass;
original input hashes and the 2 MiB multisource ceiling remain unchanged.

The next measured gap is full-input transport in sparse selection. Changing
that boundary needs an explicit ranged-reading design with bounded acquisition,
cleanup and immutable output lifetimes. Preserve the original comparison;
prepared-memory diagnostics do not close its parity obligation. Object-stream
capture consolidation remains deferred because its measured payload contribution
is small and ordinary X100 rewriting emits no object streams.

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

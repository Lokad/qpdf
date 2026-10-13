# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The goal remains open: clean
`4b44276` meets parity in four cells, with ratios 0.66–3.48×. Ordinary/streamed
rewriting are 1.33× / 1.43×. Sparse selection is the largest gap (5.83 / 1.68 ms),
including the full 16.8 MB input
read. [Benchmarks](BENCHMARKS.md) gives the current figures, protocol and raw
results. Performance commits need reduced time or allocation, with no minimum
percentage gain.

The preceding `805a2ed` passed [hosted CI run 38437341502](https://github.com/Lokad/qpdf/actions/runs/38437341502):
Windows 6m28s, Ubuntu 6m08s, pinned reference 5m52s. Both platforms pass 17,846
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 object comparisons
and 442 rewrite trios. The current reader correction passed local qualification;
CI verifies its integrated source, packages and pinned references on push.
The clean `4b44276` benchmark panel retains all 24,825 observations, fresh
semantics and original input hashes. The accompanying report update changes
documentation/raw artifacts only.

## Current correction and next work

Object-stream decoding reuses one tokenizer alongside its existing parser within
each decoder invocation. Every member resets its bounded input window, relative
position, governor and scan work. Nested decoders keep separate instances; repair
state, trailing-byte rejection, cache ownership and cancellation remain.

The identical-driver X100 pair reduces allocation 814.6 → 808.8 KB/job
(about 5.8 KB less), with matching output hashes. Medians 3.510 → 3.340 ms
have broad overlapping IQRs (2.823–3.850 / 2.966–3.764); claim allocation only.
Native is 2.325 ms in this pair, so parity remains open. No global pool,
parsed-job cache, forced GC or CPU workers were added.

All 4,122 affected reader/parser/practical-memory checks pass (23s), including
raw/Flate member boundaries, independent repair/retry scopes, cancellation and
immutable name values surviving document closure. Useful prior writer-index
test consolidation and actual backing-retention regressions remain intact.

Release Windows/Linux builds, explicit pack/inspection, fresh locked consumer
and rejection controls pass (package checks 18s). All 26 fresh managed/native
outcomes and three negative controls pass; original inputs and the 2 MiB
multisource ceiling remain. The clean thirteen-cell panel is recorded in
[benchmarks](BENCHMARKS.md); integration runs comprehensive ordinary/reference
checks once. Separate panels under varying load do not isolate source gains.

Next, measure the largest remaining reader/writer/transport costs. Object-stream
capture consolidation is deferred: current X100 rewriting emits no object streams,
and the generation cell's 103 members occupy only 8.7 KB of combined payloads
against 922 KB allocated per job. Sparse selection remains the largest gap and
includes full-input transport. Keep all thirteen cells, public immutability and
independent negative controls. Sampled GC suspension does not establish a leak.

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

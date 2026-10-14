# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The target remains open. The clean
`4d7e683` panel records five cells at or below 1.0×, with ratios 0.70–3.94×.
[Benchmarks](BENCHMARKS.md) gives comparative figures and the reproducible protocol.
Performance commits need reduced time or allocation, with no minimum percentage.

Pushed `37fc77f` passed [hosted CI run 38488628072](https://github.com/Lokad/qpdf/actions/runs/38488628072):
Windows 7m35s, Ubuntu 5m54s, pinned reference 6m28s. Both platforms pass 17,867
ordinary checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 exact object
comparisons and 442 rewrite trios. The current diagnostic changes development
tools only; production sources, dependency pins and release version are unchanged.

## Current diagnostic and next work

The existing warmed probe accepts a separate read-input component request through
the complete-job emitter's same full-file loader. Length/hash checks occur after
timing and synchronous per-thread allocation capture. The existing comparison
runner can collect thirty samples for one selected single-input workload with
--input-read-diagnostic, preserving every original complete-job sample, option,
input, limit, expected output and negative control.

Initial sparse component probes observe a wide distribution: corrected load-only
median 8.50 ms (Q1–Q3 4.69–13.19), with 16.798 MB allocated per fresh load.
A contemporary complete-job run measures managed 7.13 ms (6.56–8.16) and native
2.37 ms (2.04–2.99). Separate-run variability prevents subtracting these medians
or claiming an exact engine/GC cost. Fifteen of thirty loads have a Gen2
collection, but this does not prove GC causes the slower samples. All complete
load hashes match the original 16.8 MB fixture; the compiled library hash matches
the qualified comparison. The first approximate total-allocation counter
understated the known input size and is retained as preliminary evidence;
complete-job allocation statistics remain unchanged.

Windows/Linux Release probe builds and all seventeen CLI checks pass (3s),
including different complete inputs and malformed diagnostic requests. One clean
committed-source sparse comparison with the separate component diagnostic records
the final evidence. The thirteen-cell parity target remains open; input-only
measurements do not replace a cell or establish an engine-speed comparison.

Full fresh input allocation/reading is a demonstrated boundary cost. Native can
read selected objects lazily; the current managed reader consumes contiguous
storage and the memory host borrows its immutable snapshot. Parser-only changes
cannot remove the emitter's required full read. The next correction must address
this boundary explicitly while retaining the original comparison and practical
limits, cancellation, cleanup and immutable output guarantees.

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

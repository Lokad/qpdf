# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The target remains open. The latest
qualified primary panel (`6a8f6c4`) records five cells at or below 1.0×, with
ratios 0.56–3.40×. [Benchmarks](BENCHMARKS.md) gives current comparative figures
and reproducible commands. Keep changes that reduce time or allocation; no
minimum percentage applies.

Pushed `2b8235c` passed [hosted CI run 38565450608](https://github.com/Lokad/qpdf/actions/runs/38565450608):
Windows 6m39s, Ubuntu 6m11s, pinned reference 4m07s. Each ordinary job passes
17,898 checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 exact object
comparisons and 442 rewrite trios. Dependency pins and version are unchanged.

## Current correction

The parser keeps up to four dictionary entries in bounded inline scratch and
constructs their final frozen name table once. Larger dictionaries, orphan-key
repair and per-value receipt callers spill into the existing table path. Duplicate
warnings/repair checks precede replacement, retaining first-key identity/order.
Scratch lookup reuses the name table's bounded comparison; public dictionary
storage, enumeration, immutability and execution-state cleanup remain unchanged.

The original 2,000-object rewrite probe allocates 5.854→4.824 MB/job (about
1.03 MB less). Its median falls 28.945→25.965 ms, with separated IQRs
27.833–30.339 / 25.733–27.342, and identical independently validated bytes.
The subsequent shared-comparison probe records 25.753→25.611 ms with overlapping
IQRs and unchanged allocation; no isolated speedup is claimed for that adjustment.
The near-parity probe ratios remain above 1.0; the complete goal stays open.

All 4,188 affected syntax/table/reader/selection checks pass (18s). Four new
public rewrite cases cover three/four/five/nine keys, duplicates before/after
spilling, escaped names/strings, independently readable output and immutable
values/key aliases surviving cleanup. All 26 original fresh managed/native
outcomes and three negative controls pass with unchanged inputs. Locked Release
build/explicit pack/inspection/fresh consumer/rejections pass (23s), preserving
the 2 MiB multisource consumer ceiling.
Clean-source comparisons retain all 25,941 primary and 6,243 additional file-host
observations, original inputs/options and independently valid fresh outputs. Five
primary cells meet parity; object-heavy rewriting remains 1.098×, sparse full-input
selection 3.395×. The additional file-host cell is 0.405×. Separate panels establish
no timing attribution for the source change. Seven existing visual integration
cases pass with unchanged tolerances; misplaced-image and stale-appearance controls
reject. Renderer/codec qualification is separate from performance timings.

A four-second allocation profile on the qualified prior binary checks every fresh
output hash; sampled types point to temporary/final dictionary backings. Counts
are approximate allocation samples, not retained-memory measurements. Existing
CPU profiles identify object-heavy parsing and content-ID writing/hashing as
remaining costs. Earlier name-table ownership, single-lookup insertion and bounded
private file-host reads remain integrated. The separate file-host comparison
does not replace the original thirteen memory-host cells or their sparse cost.

## Preserved scope

Useful completed allocation, parser, copier and writer improvements remain in
place. Further work must name a concrete defect or finite verification task;
closed supported features and historical test counts add no obligations.
Source/artifact identities and all observations, including rejected trials,
remain in the [benchmark raw archives](BENCHMARKS.md).

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

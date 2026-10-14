# Progress

Performance work targets single-CPU parity (≤1.0×) with pinned native qpdf 12.4.2
in thirteen codec-free structural workloads. The target remains open. The latest
qualified primary panel (`6a8f6c4`) records five cells at or below 1.0×, with
ratios 0.56–3.40×. [Benchmarks](BENCHMARKS.md) gives current comparative figures
and reproducible commands. Keep changes that reduce time or allocation; no
minimum percentage applies.

Pushed `84e8b40` passed [hosted CI run 38569926250](https://github.com/Lokad/qpdf/actions/runs/38569926250):
Windows 7m04s, Ubuntu 4m45s, pinned reference 6m01s. Each ordinary job passes
17,902 checks without failures/skips, locked Release build/pack, inspection,
fresh consumer and rejection controls. Reference covers 1,858 exact object
comparisons and 442 rewrite trios. Dependency pins and version are unchanged.

## Current correction

Classic content-derived IDs now hash the body while it is staged, then hash the
identical placeholder trailer before writing real IDs. The shared serializer
separates body/trailer; bounded pending bytes enter the hash without an extra
host write. Existing ID bytes, first-ID preservation, offsets, counting/capacity
hints and staged commit remain. Static IDs, QDF, object streams and linearization
retain their established routes. No complete placeholder PDF or writer framework.

The final original ID-M500 pair records 26.754→24.290 ms/job with overlapping IQRs
(26.213–31.071 / 23.526–29.088), so no confirmed speedup is claimed for the final
variant. Allocation falls 17.308769→17.300866 MB/job (about 7.9 KB less), with
identical independently validated bytes. Its ratio 1.050× remains above parity;
separate whole panels do not establish timing attribution.

All 2,725 focused writer/publication/presentation/cleanup checks pass (10s).
Seven final public controls pass (501ms): complete placeholder-file MD5, small
pending/large prefixes, preserved 3-/256-byte first IDs, newline handling, retained
immutable host packets, output limits before opening, footer failure/cancellation,
prior output and closed leases, and the successful static job's host-call ceiling.
The initial failure fixture was corrected to the documented host exception type;
production error handling did not change. All 26 fresh managed/native outcomes
and three negative controls pass, original inputs unchanged. Locked Release
build/explicit pack/inspection/fresh consumer/rejections pass (23s), preserving
the 2 MiB multisource consumer ceiling.

Earlier bounded parser scratch, scoped ownership, immutable borrowing and writer
allocation improvements remain integrated. The additional file-host comparison
stays separate from all thirteen original memory-host cells and their unresolved
full-input sparse cost. Final committed-source qualification follows.

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

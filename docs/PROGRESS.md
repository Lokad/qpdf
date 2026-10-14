# Progress

Performance targets single-CPU parity (<=1.0x) with pinned qpdf 12.4.2 using
comparable operations, options and input delivery. Clean `641ff35` records 7/13
file-backed medians at parity; the target remains open. Sparse selection is
0.350x via argv and 0.359x via job JSON. Remaining gaps are grouped splitting
1.628x, ordinary rewriting 1.398x, linearization 1.283x, streamed-xref rewriting
1.115x, content IDs 1.101x and object-heavy rewriting 1.016x. Wide ranges remain
visible; separate panels do not establish attribution. [Benchmarks](BENCHMARKS.md)
has the comparisons, resource figures and reproducible commands in three tables.

The original thirteen memory-host measurements (`bf03d9a`) remain end-to-end
evidence, including fresh complete input acquisition, registration and output
export. Native opens files and can read less. The original sparse 3.4732x result
is unchanged; a different input path never turns it into a pass. The raw archive
preserves all 159 runs and every earlier run object unchanged.

## Current correction and verification

Argv/job single-page and one-source range selection reuse bounded classic-xref
reads on custom hosts. Catalog behavior and recovery defaults remain. Omitted
required ranges, diagnostics or uncertain parsing retry once on the same stable
lease with the requested recovery mode, before publication. Accepted range sources
close before output creation, preserving one-handle limits and close-fault output
protection. Other layouts/operations and public/in-memory storage contracts remain.
Job `suppressRecovery:false` now matches an absent flag; true stays strict.

Five regressions fail on the prior implementation and pass on `641ff35`.
All 6,254 affected selection/recovery/catalog/content/publication/job checks pass
(68s). They cover bounded/short reads, changing future inputs, required catalog
payloads, repair versus strict rejection, limits, cancellation and failed cleanup.
The earlier unused compressed-source inventory removal remains integrated.

Final-source checks pass: 26 fresh semantic outcomes and three negative controls;
seven visual cases and two negatives; locked Release restore/build/explicit pack,
package inspection, fresh consumer and rejection controls. The unchanged 2 MiB
multisource consumer ceiling passes. Build/semantic/file-panel/job phases take
59.48/46.14/328.86/24.42s; package/consumer 21.93s, visual 17.94s. All 13 managed
output sets are byte-identical to the prior panel; inputs stay unchanged. There
are 13,538 file-panel and 2,776 supplementary job observations, with no exclusions.

Superseded hosted run 38626502356 was canceled after the lease-order defect was
found, conserving runner time. Ordinary Windows/Ubuntu and pinned-reference
verification accompany the corrected combined push. No extra benchmark CI jobs.

## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) stays delivered. CMYK/YCCK remains
opaque, Zopfli is unavailable, and crypto including empty-password inputs is
excluded. Other [compatibility requirements](COMPATIBILITY.md) are not waived.

`MaxMemoryBytes` bounds approximate charged working storage. Significant buffers,
copy/growth overlap, allocation/decompression amplification, work limits,
cancellation, host boundaries, cleanup, integrity, immutability and surviving
aliases remain protected. Unreleased charges do not prove retained memory.
Exact CLR sizes, individual small-object/metadata-alias receipts, fixed GC rounds
and precise bookkeeping charges remain withdrawn; see
[governance](SUPPORTED-PROFILE.md#governance). Historical test counts add no gates.

No CPU parallelism, forced production GC, parsed-document cache, runtime native
bridge or new accounting/benchmark framework. Pins, five reviewed temporary
ImageSharp 3.1.12 exceptions and release version remain. No tags or publication.

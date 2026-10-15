# Progress

Single-CPU warmed parity (<=1.0x) against pinned qpdf 12.4.2 remains open.
Clean source `6b0bc0e` has 12/13 comparable file-backed medians at parity;
equivalent sparse job JSON is 0.4344x. Remaining rewrite-X100:
1.0307x (1.869/1.814 ms).
[Benchmarks](BENCHMARKS.md) leads with comparisons and resources in three tables.
Separate panels establish no causality; paired probes replace no panel cell.

Original `bf03d9a` memory-host results remain end-to-end evidence: fresh complete
acquisition/registration, staging, defensive public export and file writes.
Native opens files and can read less. Original sparse 3.4732x stays unchanged;
a different path cannot substitute a pass. All earlier observations remain.

## Current implementation

Short numeric headers/references/xref rows format once in bounded synchronous
scratch; three fixed suffixes use literal bytes. Tight limits and unsupported
fields retain the existing path. Retaining sinks still receive immutable packets.
The governor checks requested cancellation before cleanup flags; interval counts
and cleanup suppression remain. These paired medians decrease with overlapping
ranges; no firm speedup is claimed.

The internal ASCII-name factory reuses the existing twelve syntax values. Pool
initialization uses unshared creation, arbitrary names keep validation/storage,
and public construction/byte access still copy. Its paired X100 median decreases
1.932 ->1.895 ms with non-overlapping interquartile ranges; allocation decreases
536,734 ->536,345 B/job. No new name pool, document cache, owner, forced GC or CPU
parallelism is introduced. Qualified ranged reading, immutable graph/payload reuse,
coarse ownership and readonly token/writer-field handling remain.

## Verification

Focused checks pass: 2,119 writer/inventory cases, 7,181 governance/publication/
cancellation cases, and 6,277 syntax/reader/writer/allocation-ownership cases.
Final source passes 26 fresh semantic outcomes/three negatives, seven visual cases/
two negatives, locked Release restore/build/explicit pack, inspection, fresh
consumer and rejection controls. The unchanged 2 MiB multisource ceiling passes.
Original inputs and thirteen output sets are unchanged. The archive retains
231 runs, preserving all 221 preceding objects and every partial/intermediate/
failed/noisy trial. All 53,078 file/13,643 job observations from this panel remain.

WSL build/semantics/file-panel/job phases take 21.99/26.28/426.15/37.69s;
package/consumer takes 27.20s, visual 16.40s. Host snapshots show 97.5% idle
before and 21.8% after, with background scan activity; retain this noisy panel
without causal attribution or an unchanged-source favorable retry. Main/consumer
preparatory builds disable shared compilation in their private environment;
no unrelated process is stopped. Current kernel is 6.18.54.1 (original memory
run: 6.18.40.1). One combined hosted qualification follows. Previous `3789b11`
passes [CI](https://github.com/Lokad/qpdf/actions/runs/38691222840): Ubuntu 4m35s,
Windows 6m28s, reference 6m49s; 17,940 tests/platform, 1,858 exact parser comparisons/
442 rewrite trios and both package/fresh-consumer/rejection checks pass.

## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) stays delivered: CMYK/YCCK opaque,
Zopfli unavailable, crypto excluded. Other [compatibility](COMPATIBILITY.md)
requirements are not waived. Approximate accounting protects significant capacity/
growth overlap, amplification/decompression/work limits, cancellation, cleanup,
host boundaries, integrity, immutability and surviving aliases. Charges are not
retained heap/RSS. Exact CLR sizes, individual small-object/metadata-alias receipts,
GC rounds and bookkeeping-byte precision remain withdrawn; see
[governance](SUPPORTED-PROFILE.md#governance). Pins/version stay unchanged;
no native bridge or package publication is introduced.

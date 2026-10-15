# Progress

Single-CPU warmed parity (<=1.0x) against pinned qpdf 12.4.2 remains open.
Clean source `f555a11`: 10/13 file-backed medians at parity; equivalent sparse
job JSON 0.3880x. Remaining cells: rewrite-M500 1.0113x, rewrite-X100 1.0708x,
and linearize-M500 1.0636x.
[Benchmarks](BENCHMARKS.md) leads with comparisons and resources in three tables.
Separate panels establish no causal attribution; paired probes replace no cell.

Original `bf03d9a` memory-host results remain end-to-end evidence: fresh complete
acquisition/registration, staging, defensive public export and file writes.
Native opens files and can read less. Original sparse 3.4732x stays unchanged;
a different path cannot substitute a pass. All earlier observations remain.

## Current implementation

Common short numeric headers, references and classic xref rows now format once
into bounded synchronous scratch. Fixed suffixes use shared literal bytes. The
existing path handles tight limits and unsupported fields; scratch never escapes
or crosses an await. Limits, cancellation, immutable retained packets and offsets
remain. All 2,119 writer/inventory checks pass in 45s, including buffer reuse and
wide/negative-field fallbacks for synchronous and retaining sinks. The final paired
X100 medians 1.922 ->1.888 ms overlap; no firm speedup or allocation benefit is
claimed. Integration qualification follows; the complete panel above is unchanged.

The governor checks whether cancellation was requested before reading cleanup-
suppression flags, and interval polling reuses that check. Work counters,
intervals, requested cancellation and cleanup suppression remain. All 7,181
affected governance/resource/publication/cancellation checks pass in 75s. A 68s
paired X100 probe observes 1.918 ->1.840 ms with overlapping ranges; its 1.0031x
native ratio is still above target. Final combined-source qualification follows.

Qualified readonly parser tokens, single-entry copier reads, readonly writer
fields and split async container state remain. The insensitive work-charging JIT
hint stays restored, with its trial preserved.

Bounded warmup keeps thirty fresh jobs/two seconds minimum, then waits for two
seconds without JIT counter changes. Counters follow job timing/allocation capture;
raw results retain readiness and later compilation. Earlier protocols/results
remain intact. Fresh acquisition, parsing, staging/export and file writes stay
timed. No parsed cache, forced production GC, CPU parallelism or new measurement
framework is introduced. Qualified ranged selection/catalog/recovery/leases/
fallback, immutable graph/payload reuse, coarse ownership, checked sizing, readonly
writer fields and fixed canonical integer atoms remain.

## Verification

Final source passes 26 fresh semantic outcomes/three negatives, seven visual cases/
two negatives, locked Release restore/build/explicit pack, inspection, fresh
consumer and rejection controls. The unchanged 2 MiB multisource ceiling passes.
Original inputs and all thirteen output sets are unchanged. The archive retains
221 runs with all 217 preceding objects intact, including original memory evidence
and rejected/slower/insensitive/outlier trials. All 54,842 file/10,819 job
observations from this final comparison remain.

WSL build/semantics/file-panel/job phases take 32.71/36.15/416.74/29.98s;
package/consumer takes 33.58s, visual 19.35s. Current WSL kernel is
6.18.54.1; original memory-host evidence records 6.18.40.1. Previous batch
`3789b11` passes [hosted CI](https://github.com/Lokad/qpdf/actions/runs/38691222840):
Ubuntu 4m35s, Windows 6m28s, reference 6m49s; each platform passes 17,940 tests,
reference passes 1,858 exact parser comparisons/442 rewrite trios. Release pack,
inspection and fresh-consumer/rejection checks pass on both. One combined hosted
qualification of the formatting batch follows; no component CI or timing gates.

## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) stays delivered: CMYK/YCCK opaque,
Zopfli unavailable, crypto excluded. Other [compatibility](COMPATIBILITY.md)
requirements are not waived. Approximate accounting protects significant capacity/
growth overlap, amplification/decompression/work limits, cancellation, cleanup,
host boundaries, integrity, immutability and surviving aliases. Charges are not
retained heap/RSS. Exact CLR sizes, individual small-object/metadata-alias receipts,
GC rounds and bookkeeping-byte precision remain withdrawn; see
[governance](SUPPORTED-PROFILE.md#governance). Pins/version remain unchanged;
no native bridge or package publication is introduced.

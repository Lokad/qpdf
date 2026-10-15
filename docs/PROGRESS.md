# Progress

Single-CPU warmed parity (<=1.0x) against pinned qpdf 12.4.2 remains open.
Clean source `ad3b4b4` has 12/13 comparable file-backed medians at parity;
sparse job JSON is 0.388x. The remaining cell is rewrite-X100 at 1.035x
(2.002 versus 1.935 ms). Scalar-heavy rewriting is 0.870x. Separate panels
establish no causal attribution; no paired probe replaces a panel result.
[Benchmarks](BENCHMARKS.md) leads with current comparisons and resources in
three tables, followed by the protocol and reproducible commands.

Original `bf03d9a` memory-host results remain end-to-end evidence: fresh full input
acquisition/registration, staging, defensive public export and file writes.
Native opens files and can read less. Original sparse 3.4732x stays unchanged;
a different path cannot substitute a pass. Every earlier observation remains.

## Current implementation

Writer counting, dictionary emission and stream emission read each concrete
entry pair once. Ordering, null/Length handling, work, cancellation and iterator
disposal remain; no API, owner or buffer is added. All 1,413 affected checks pass
in 13s. Performance qualification follows; no speedup is claimed from this
structural removal of duplicate reads.

Array and dictionary emission now have separate async routines; stream emission
returns its existing operation directly. Iteration, awaits, depth/work checks and
disposal remain. All 2,117 writer/inventory checks pass in 42s. The paired median
2.097 ->2.075 ms overlaps, with unchanged allocation; no firm speedup is claimed.
Readiness diagnostics show compilation continuing beyond the previous two-second
warmup, so final comparison will first wait for managed compilation to settle.

Canonical integers 0–255 reuse a fixed set of immutable values instead of fresh
objects and strings. The set has 256 values, with no document/input/host/governor
retention or per-job growth. Signed, padded and larger numbers preserve their
text; public construction stays independent. Conversion work, cancellation and
ownership remain. All 7,989 affected checks pass in 1m50s. One paired probe
allocates 4,539 ->4,500 KB/job on scalar rewriting; its 23.87 ->22.31 ms ranges
overlap. X100 timing is unchanged. Allocation is the retained benefit, without
a retained-heap/RSS claim. Both insensitive reference-token shortcuts were
restored; their patches, binary identities and raw trials remain.

Useful earlier corrections remain: unique dictionary-prefix transfer, concrete
writer token tables, stateless checked value sizing and guarded geometric xref
union hints. Ranged argv/job selection, catalog/recovery defaults, stable leases
and complete-read fallback remain qualified. Immutable graph/payload reuse and
coarse private ownership already exist; they are not missing features.

## Verification

Final source passes 26 fresh outcomes/three negatives, seven visual cases/two
negatives, locked Release restore/build/explicit pack, inspection, fresh consumer
and rejection controls. The unchanged 2 MiB multisource ceiling passes. Original
fixtures and all thirteen output sets are unchanged. All 204 archived runs
preserve the 199 preceding objects, including original memory results and every
rejected/slower/insensitive trial. All 28,695 file/5,876 job observations remain.

WSL build/semantics/file-panel/job phases take 19.60/26.51/220.35/17.56s;
package/consumer takes 29.80s, visual 18.34s. Initial hosted `8d5230a` passes the
pinned reference but fails four obsolete small-object/metadata-receipt assertions
on both ordinary platforms (17,954 other tests pass each). The correction
consolidates tiny receipt cases into three significant-payload collection routes
and checks numeric/key alias usability after cleanup without independent billing.
All 266 affected checks pass in 7s on Windows and WSL; the supported-profile text
now matches the approximate contract. Production source, timings, visual/package
evidence and the 2 MiB ceiling are unchanged. Corrective hosted qualification
follows. Windows/Ubuntu/pinned-reference logs, TRX and reports are available through
[CI](https://github.com/Lokad/qpdf/actions/workflows/ci.yml).

## Preserved scope

The [supported profile](SUPPORTED-PROFILE.md) stays delivered: CMYK/YCCK opaque,
Zopfli unavailable, crypto excluded. Other [compatibility](COMPATIBILITY.md)
requirements are not waived. Approximate accounting protects significant capacity
and growth overlap, amplification/decompression/work limits, cancellation, cleanup,
host boundaries, output integrity, immutability and surviving aliases. Charges are
not retained heap/RSS. Exact CLR sizes, individual small-object/metadata-alias
receipts, GC rounds and bookkeeping-byte precision remain withdrawn; see
[governance](SUPPORTED-PROFILE.md#governance). No CPU parallelism, forced production
GC, parsed-document cache, native bridge, dependency/version change or package
publication is introduced.

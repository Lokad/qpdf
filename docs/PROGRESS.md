# Progress

The integrated performance tree is qualified on `6221e06` (2026-10-12).
The agreed [supported profile](SUPPORTED-PROFILE.md) and its completed features
remain closed. Subsequent bounded name hash/comparison corrections pass 2,549 affected
checks; fresh staging without redundant clearing passes 4,806 affected checks.
Exclusive input-slice borrowing passes 4,005 affected checks, including complete
public jobs, material backing admission after reader closure and surviving custom
host chunks that must not pin mostly unused inputs.
These corrections await their integration checkpoint. The performance goal is separate:
establish all nine warmed structural job medians within 2× pinned qpdf 12.4.2.
That target remains unconfirmed for the large ordinary rewrite.

CMYK/YCCK JPEGs remain opaque with warnings when decoding is requested; Zopfli
remains unavailable. Both accepted limitations are tested. Crypto remains
excluded, including empty-password encrypted inputs. Other compatibility
requirements remain in the [inventory](COMPATIBILITY.md). Tagging, publication
and a release-version change are separate actions.

## Final integration qualification

Both platforms use .NET SDK 10.0.401/runtime 10.0.12. Locked restores and Release
builds passed with zero warnings/errors. Both complete ordinary suites passed
17,841 tests with no failures/skips. Complete pinned reference, finite visuals,
explicit Release pack, package inspection and fresh locked consumers passed.
Consumer controls rejected a wrong package version and corrupt external hash.

| Check | Platform | Wall time | Result |
| --- | --- | ---: | --- |
| Ordinary suite | Windows | 6m47s | Passed |
| Ordinary suite | Ubuntu 24.04 WSL2 | 6m58s | Passed |
| Complete qpdf 12.4.2 reference | Windows | 6m17s | Passed; 1,858 object comparisons and 442 rewrite trios |
| Seven-case visual comparison | Windows | 18s | Passed, including both deliberate defect controls |
| Package inspection, fresh consumers and rejection controls | Windows / Ubuntu | 19s / 24s | Passed |

The first integration attempt found 42 stale test failures on each platform;
all other 17,799 checks passed. Corrections preserve diagnostic count/byte limits,
output validity and cleanup while expecting one warning from the reused primary
reader. The inventory points to the strengthened default-ID test. Typed jobs
retain their original admission ceilings and require complete valid output or
untouched prior output on exhaustion. Label jobs retain routes, encodings,
publication failures/cancellation, surviving output snapshots and large-prefix
rejection, while dropping exact receipt/size/GC-round checks and redundant
encoding/failure combinations. All 1,484 affected checks pass.

Source archives, logs, TRX and phase timings are in the ignored
`external/reviews/2026-10-11-performance-integration/final-6221e06/` run;
Linux results use the same source. Repeat completed checks only for source
changes, failures or unresolved concerns.

## Integrated performance changes

| Mechanism | Result and retained safeguards |
| --- | --- |
| Reader and source reuse | Whole-file jobs reuse the primary document. Private jobs borrow stable memory-host inputs; arbitrary hosts retain the fresh/short-read fallback. Closed leases drop host/payload references. Public mutation isolation, versions, source/publication interactions and an actual 2 MiB retention regression remain covered. |
| Immutable values and parser scratch | Names use owned bytes, one display string and raw internal comparisons. Dictionary enumeration uses concrete entries; known arrays allocate once. Hex/escape decoding avoids intermediate copies. Parsed stream buffers transfer without cloning and fill admitted uninitialized storage in bounded 32 KiB chunks, preserving independent payload lifetimes and work/cancellation. |
| Copier and checker ownership | Private complete-job graphs use document/operation ownership. Exclusive readers using the built-in memory host borrow immutable stream slices and transfer the full pinned input charge to the job before source closure. Public readers and custom hosts retain independent payloads; surviving host write chunks cannot pin mostly unused inputs. Array-based filter/content/checker/JSON consumers materialize only the payload under their existing scratch owner; unchanged serialization borrows it directly. All 4,005 affected checks pass, including material backing admission, decompression limits, cancellation, cleanup and surviving outputs. |
| Serialization | Fixed syntax and 25 common names/five small scalars use immutable bytes. Pure counting passes reuse encoding/width rules without token buffers. Object-stream captures reuse bounded scratch. Default IDs use bounded incremental hashing, preserving original classic/QDF/linearized hashes. Replay lengths, offsets, grammar, ordering/null semantics and cancellation remain checked. |
| Bounded name hashing and comparisons | The existing governor admits the same at-most-16-byte hash and finalization work in one call. Raw comparisons up to sixteen bytes charge the exact compared count once before returning, after the caller's cancellation check. Hash values, order, comparison results, long/decoded paths, long-name observers and limits remain unchanged. Release builds and all 2,549 affected name/parser/reader/resource/copy/writer/cancellation/practical-memory checks pass after each correction. These structural reductions await their final performance/integration checkpoint. |
| Publication | Counted output sizes admit staging once for ordinary, linearized and split output. Fresh counted staging avoids redundant clearing; only written bytes can publish, and underestimated hints fall back to expandable storage. Split admission precedes publication; pass1 remains covered. Complete private staging transfers at commit, and settled storage drops immediately. All 4,806 affected host/publication/output/split/linearized/cancellation/practical-memory checks pass, including caller mutation, canceled/over-limit writes and partial publication. |

Straightforward copy/allocation reductions are accepted structurally after
correctness and practical resource checks. They have no empirical ≥10% gate.
Larger performance hypotheses remain ranked by measured contribution; retain
them through the agreed public-workload criteria. No production forced GC,
parsed-document cache, runtime native bridge or new accounting framework was added.

## Remaining performance work

The [comparison report](BENCHMARK-RESULTS.md) preserves initial, preceding and
current both-warmed runs plus the narrow stream-buffer pair. All nine current
fresh outputs independently validate, match previous managed identities and
preserve original input hashes. M500 rewrite allocation fell from approximately
92.9 to 30.6 MB/job across the integrated corrections. Allocation, collection
rates, charged estimates and process RSS remain distinct measurements.

The bounded-name source `683f800` puts eight cells within 2×; M500 rewriting
measured 2.006×. A longer seven-batch confirmation measured 2.19×; combining
both runs' batches gives 2.11×, so the target remains open. All observations
remain retained. Observe the fresh staging reduction with the existing narrow
pair, rerank all nine, then qualify the final correction batch. A nine-cell comparison closes the elapsed-time goal;
The exclusive stream-slice pair improved M500 rewriting by 23.3% with separated
IQRs (29.85 → 22.89 ms; 1.62× native), reducing allocation from 30.60 to 25.48 MB/job.
Its nine-cell rerank puts every median below 2× (0.84–1.47×), but a local build
overlapped later cells. Rerun the final host-guarded source without verification
in parallel, then perform the final combined checkpoint.
no timing gate is added to CI. Cold process results remain separate.

The approximate memory contract still admits significant buffers/growth and
preserves allocation/decompression amplification protection, work limits,
cancellation, host boundaries, cleanup and valid outputs. Five representative
normal/adversarial workload families remain sufficient. Unreleased accounting
charges do not prove retained memory. Exact CLR geometry, individual small-object
or metadata-alias receipts, fixed GC retirement and precise bookkeeping charges
remain withdrawn obligations. See the [governance contract](SUPPORTED-PROFILE.md#governance).

Hosted CI last passed on `7bc2fc8`:
[run 38154808432](https://github.com/Lokad/qpdf/actions/runs/38154808432), with
Windows 7m01s, Ubuntu 5m26s and pinned reference 3m45s. These performance commits
are local; that hosted run predates them. Use the [architecture](../ARCHITECTURE.md)
and [development guide](DEVELOPMENT.md) for boundaries, verification scripts and
the five intentionally suppressed ImageSharp 3.1.12 advisories.

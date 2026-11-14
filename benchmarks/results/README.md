# Benchmark evidence index

These records describe dated campaigns, not current-candidate performance.
[Methodology and reproduction](../../docs/BENCHMARKS.md) define elapsed time,
allocation, process RSS and cooperative admission separately. Gzip files contain
JSON with source/binary identities, input hashes and raw observations; comparisons
identified by a baseline commit and candidate patch hash retain those identities.
All existing records are preserved.

| Record | Evidence and limits |
| --- | --- |
| [2026-11-13 consolidation](2026-11-13-consolidation-local.json.gz) | Source `f22322d3`; .NET 11 RC 3 on Windows. Allocation/admission for planning, selection, rewriting, merging, splitting, JSON, Flate, linearization and twelve security workloads. Two ordinary workers and a recording-budget worker per group; seven JIT-stable batches. Input copying is included. Owner residuals are zero. Idle samples of 8–79% do not qualify throughput. |
| [2026-11-13 planner](2026-11-13-planner-local.json.gz) | Same-input before/after planning, inspection, layered configuration and R6 authentication. Source, build and driver identities distinguish the two implementations. Allocation/admission evidence; busy-host elapsed times are provisional. |
| [2026-11-13 shared widgets](2026-11-13-shared-widgets-local.json.gz) | Baseline `cd4c96e7` plus recorded candidate patch. A fixed 128-page fixture selects 16/32/64/128 widgets. Allocation, deterministic resolver reads and zero owner residuals establish component reuse; not a throughput result. |
| [2026-11-13 encrypted sizing](2026-11-13-encrypted-sizing-local.json.gz) | Baseline `83d5265d` plus recorded candidate patch. Identical plain/R4/R6 small/bulk rewrites; size-only cipher/key-withholding controls and separate actual-emission checks. Allocation/admission evidence only. |
| [2026-11-13 appearances](2026-11-13-appearance-local.json.gz) | Baseline `d1c26838` plus recorded candidate patch. One-page selection, merging and sparse selection compare appearance orchestration; zero owner residuals. Local elapsed samples are provisional. |
| [2026-11-13 bounded counts](2026-11-13-count-shapes-local.json.gz) | Recorded parent source and candidate patch. A 9 MiB unrelated stream, 2,048 small unrelated objects and 2,048 pages expose distinct acquisition/metadata costs. Allocation includes host copying; job peaks are admission estimates. |
| [2026-11-12 security](2026-11-12-security-local.json.gz) | Candidate `755b70d9`, published-source baseline `32a6117d`; .NET 11 RC 3 on Windows. Twelve security workloads, same-input plaintext controls and both-target recording-budget checks. Only 10/120 later idle samples reached 95%; timings do not qualify throughput. |
| [2026-11-05 quiet VM](2026-11-05-net11-vm.json.gz) | Source `33456c3`; .NET 11 RC 3 versus pinned qpdf 12.4.2. Six idle gates and finite output/negative controls passed. Same-build A/A median drift reaches 2.3%. These supply the historical tables in the methodology document. |
| [2026-10-12 warmed comparison](2026-10-12-warm-comparison.json.gz) | Earlier source/runtime/hardware campaign; retain its own provenance when comparing. |
| [2026-10-12 parity progress](2026-10-12-parity-progress.json.gz) | Historical workload/validation record. |
| [2026-10-11 warmed](2026-10-11-warmed.json), [Linux](2026-10-11-linux.json) | Earlier warmed and whole-process records; not interchangeable with later complete-job measurements. |

The bounded-count campaign uses 256 KiB selected-data and 64 KiB metadata caps;
exceeding them can require complete acquisition. The small-object shape exceeds
the former. The page-tree walk retains page locations in the many-page shape.
The shared-widget record concerns signature-component
discovery; it does not measure later retained-field closure changes. Historical
measurements must not be reassigned to a later binary without a new campaign.

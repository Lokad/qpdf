# Benchmarks

**.NET 11 is the performance baseline.** The historical plaintext/native tables use SDK
11.0.100-rc.3.26522.148 / runtime 11.0.0-rc.3.26522.148, code `33456c3`,
measured 2026-11-05. Password-security observations appear separately below.
The package also targets .NET 10.

The 2026-11-13 planner consolidation used the existing .NET 11 memory report on
identical inputs, with two workers per version, seven batches of 32 complete jobs
and JIT-stable warmups. Median allocated bytes/job were:

| Workload | Before consolidation | Shared planner |
| --- | ---: | ---: |
| `rewrite-tiny` (one page) | 31,680 | 32,080 |
| `tiny` (page-count inspection) | 12,128 | 10,848 |
| `config-layered` (one small job file) | 26,536 | 24,608 |
| `security-r6-auth` | 153,208 | 151,128 |

This removes serialized-setting/DOM work from argv. Separate recording-budget
runs refunded all owners. Local estimated peaks changed from 541,618 to 547,210
bytes for tiny rewriting, 16,240 to 11,157 for inspection, 29,080 to 20,188 for
layering and 65,895 to 58,615 for authentication. These are admission estimates,
not heap measurements. Timing remains provisional on the busy local host; this
comparison establishes no throughput result. `rewrite-tiny` and `config-layered`
are additional names accepted by `--memory-report`.
The [planner allocation controls](https://github.com/Lokad/qpdf/blob/master/benchmarks/results/2026-11-13-planner-local.json.gz)
retain source/binary identities, input hashes, samples and admission observations.

The historical plaintext/native measurements used a dedicated quiet VM. Six pre/postflight idle gates
passed; same-build A/A medians differed by up to 2.3% below. Treat differences
within that variability cautiously. These measurements do not isolate a .NET 11
speedup over .NET 10: older records used different code and hardware and remain
archived under `benchmarks/results/`. No timing threshold is a CI gate.

## Warmed complete jobs

Milliseconds/job are medians (Q1–Q3), seven alternating batches per engine.
M/N divides managed by native elapsed time. Both engines open fresh files with
equivalent options; input acquisition, parsing, transformation, staging, publication,
export and writes are included. Startup, protocol, validation and file deletion
are outside timing. No parsed-document cache, forced GC or fsync is used.

| Workload | Lokad.qpdf ms (Q1–Q3) | qpdf 12.4.2 ms (Q1–Q3) | M/N |
| --- | ---: | ---: | ---: |
| select-S10 | 0.16 (0.15–0.19) | 0.46 (0.45–0.46) | 0.34× |
| select-M500 | 7.34 (7.33–7.37) | 13.80 (13.68–13.86) | 0.53× |
| merge-250x2 | 8.19 (8.15–8.27) | 13.52 (13.51–13.62) | 0.61× |
| split-M500 | 9.51 (9.37–9.83) | 15.59 (15.49–15.64) | 0.61× |
| rewrite-M500 | 7.21 (7.03–7.28) | 11.77 (11.67–11.82) | 0.61× |
| rewrite-X100 | 0.55 (0.55–0.56) | 1.16 (1.16–1.17) | 0.47× |
| object-streams-X100 | 0.60 (0.59–0.64) | 1.83 (1.83–1.85) | 0.33× |
| linearize-M500 | 8.59 (8.57–8.65) | 15.17 (15.03–15.27) | 0.57× |
| check-X100 | 0.61 (0.60–0.61) | 1.43 (1.41–1.44) | 0.42× |
| rewrite-ID-M500 | 12.97 (12.85–13.06) | 18.36 (18.14–18.36) | 0.71× |
| rewrite-objects | 7.11 (7.08–7.16) | 18.54 (18.50–18.69) | 0.38× |
| select-sparse | 0.19 (0.19–0.19) | 1.22 (1.21–1.22) | 0.16× |
| split-single | 3.17 (3.16–4.10) | 3.86 (3.85–3.89) | 0.82× |
| select-sparse (job JSON) | 0.20 (0.20–0.20) | 1.34 (1.32–1.34) | 0.15× |

Fixtures cover 10/100/500 pages, two-source merging, group/individual splitting,
incremental/xref/object streams, 2,000 reachable scalars and a 16 MiB omitted stream.
They exercise shared fonts/forms/metadata and reordered pages. JPEG processing
and rendering are not measured.

## Allocation and process memory

Managed allocation is cumulative bytes/job, averaged over measured jobs; it is
not retained memory. Peak RSS includes runtime/warmup and is a dedicated worker's
process high-water mark, not a per-job working set. Output sizes sum split files.

| Workload | Managed MB allocated/job | Peak RSS MiB managed / native | Output KiB managed / native |
| --- | ---: | ---: | ---: |
| select-S10 | 0.069 | 95.7 / 6.3 | 4.0 / 4.1 |
| select-M500 | 17.151 | 150.1 / 7.8 | 5103.1 / 5105.1 |
| merge-250x2 | 22.283 | 125.1 / 8.5 | 5103.5 / 5105.5 |
| split-M500 | 26.048 | 154.0 / 8.0 | 5107.9 / 5108.7 |
| rewrite-M500 | 17.149 | 125.1 / 7.7 | 5103.1 / 5095.9 |
| rewrite-X100 | 0.535 | 98.4 / 6.5 | 70.9 / 69.5 |
| object-streams-X100 | 0.633 | 98.1 / 6.5 | 67.2 / 65.8 |
| linearize-M500 | 18.992 | 127.9 / 8.1 | 5107.0 / 5109.0 |
| check-X100 | 0.528 | 86.3 / 6.8 | 0.0 / 0.0 |
| rewrite-ID-M500 | 17.152 | 133.7 / 7.9 | 5103.1 / 5095.9 |
| rewrite-objects | 4.432 | 94.2 / 10.6 | 294.5 / 295.0 |
| select-sparse | 0.126 | 122.2 / 6.4 | 1.1 / 1.1 |
| split-single | 1.112 | 97.8 / 6.5 | 69.8 / 62.6 |
| select-sparse (job JSON) | 0.146 | 122.5 / 6.7 | 1.1 / 1.1 |

## Noise controls and provenance

Independent A/B workers below run the **same .NET 11 probe and library hashes**,
using the same alternating seven-batch protocol. Signed drift is A/B minus one;
it measures run variability, not an implementation improvement.

| A/A workload | A median ms | B median ms | Drift |
| --- | ---: | ---: | ---: |
| select-S10 | 0.15 | 0.15 | -2.3% |
| split-M500 | 9.97 | 9.76 | +2.2% |
| rewrite-M500 | 7.28 | 7.29 | -0.1% |

| Setting | Measured configuration |
| --- | --- |
| Hardware | 4 vCPUs (AMD EPYC 9V45), 15.61 GiB RAM |
| Platform | Linux VM, Ubuntu 24.04.5 LTS, kernel 7.0.0-1017-azure; inputs/outputs on its local filesystem |
| Managed | .NET 11 RC 3, Release; portable assemblies built with the pinned SDK |
| Native | qpdf 12.4.2 at the pinned source commit, GCC 13.3, Release -O3, static libqpdf, system zlib/JPEG, Zopfli off |

Each campaign requires five consecutive one-second samples with at least 95% idle,
less than 1% I/O wait/steal and over 3 GiB available memory before and after timing.
Setup/builds and other agents' VM work stay outside these measurement windows.

Warmup requires at least thirty jobs/two seconds and a two-second quiet JIT window,
bounded by thirty seconds/50,000 jobs. Measured batches accumulate at least 350 ms
and three jobs. Later JIT activity, collections, hashes and all samples remain in
the raw evidence in `benchmarks/results/2026-11-05-net11-vm.json.gz` in the source
checkout, including A/A, job JSON, idle-gate samples and validation controls.

Before timing, 26 fresh native/managed outcomes and three negative controls passed.
Native checking and PdfPig validate structure, page/text semantics, resources,
metadata, sharing, packing, linearization and IDs. Every timed output hash matches
its independently validated output, and input hashes remain unchanged. These are
finite workload checks, not general rendering equivalence. No CI timing gate applies.

## Reproduce

Use a reserved quiet Linux VM with Python 3, GNU time, a C++ toolchain, zlib/JPEG development
libraries, both .NET SDKs, and clean qpdf 12.4.2 source at the commit pinned in
[compatibility](COMPATIBILITY.md). Build its static Release executable with Zopfli
off. From the repository root (replace the native build paths if needed):

```bash
dotnet restore Lokad.Qpdf.slnx --locked-mode
dotnet build Lokad.Qpdf.slnx -c Release --no-restore
python3 tools/compare-linux-performance.py --repo "$PWD" \
  --dotnet "$(command -v dotnet)" --qpdf "$PWD/external/qpdf-build/qpdf/qpdf" \
  --native-source "$PWD/external/qpdf" --native-build "$PWD/external/qpdf-build" \
  --work-root "$PWD/external/prepared" --skip-warmed
python3 tools/compare-warm-performance.py --repo "$PWD" \
  --dotnet "$(command -v dotnet)" --qpdf "$PWD/external/qpdf-build/qpdf/qpdf" \
  --native-source "$PWD/external/qpdf" --native-build "$PWD/external/qpdf-build" \
  --input-root "$PWD/external/prepared" --work-root "$PWD/external/warm-net11" \
  --file-input --samples 7 --sample-seconds 0.35 --warmup-seconds 2
```

Use fresh work roots. The preparation command also records separate whole-process
timings; the tables above report warmed jobs. For A/A, add
`--baseline-probe tools/QpdfProbe/bin/Release/net11.0/QpdfProbe.dll` and the desired
`--workload` entries. For job JSON, use `--workload select-sparse --job-selection`.

## Cooperative memory admission

The existing harness measures allocation/admission without native PDF tools:

```sh
dotnet benchmarks/Lokad.Qpdf.Benchmarks/bin/Release/net11.0/Lokad.Qpdf.Benchmarks.dll --memory-report disabled 30 7 3
dotnet benchmarks/Lokad.Qpdf.Benchmarks/bin/Release/net11.0/Lokad.Qpdf.Benchmarks.dll --memory-report enabled 30 7 3
```

Run each in a dedicated process. Optional scenario names narrow the run. Enabled
mode shares one recording root across fresh job/host owners and includes its
overhead. Records retain allocation, JIT/GC, charged peaks, reserve/resize calls
and residuals; owner closure must refund to zero without forced collection.
Allocation/admission evidence remains useful under load. Interpret small elapsed
differences only after representative interleaved A/A controls; see the
[memory contract](MEMORY.md) for what the estimates cover.

## Consolidation allocation refresh, 2026-11-13

Source `f22322d3` uses .NET 11 RC 3 on Windows. The existing memory-report runner
measured two ordinary-admission workers and one recording-budget worker per group,
each with JIT-stable warmup and seven batches. Small/planning/authentication jobs
use batches of 32; bulk operations use batches of three. Every measured owner
refunded to zero. Allocations below exclude recording-budget instrumentation;
local/root peaks come from the separate instrumented workers and are admission
estimates, not heap measurements. Bytes/job are rounded median batch averages.

| Scenario | Allocated bytes/job | Local peak bytes | Shared-root peak bytes |
| --- | ---: | ---: | ---: |
| `tiny` | 10,848 | 11,157 | 17,089 |
| `rewrite-tiny` | 32,080 | 547,210 | 557,761 |
| `config-layered` | 24,608 | 20,188 | 34,547 |
| `select-sparse` | 17,023,205 | 635,591 | 17,437,367 |
| `rewrite-objects` | 4,124,147 | 3,566,409 | 3,569,064 |
| `merge` | 55,208 | 555,118 | 558,466 |
| `split` | 225,896 | 573,081 | 576,129 |
| `json` | 57,224 | 32,879 | 39,605 |
| `flate` | 194,168 | 164,293 | 164,783 |
| `linearize` | 256,064 | 591,340 | 592,513 |
| `security-plain-auth` | 9,064 | 10,548 | 17,696 |
| `security-r4-auth` | 18,984 | 23,221 | 34,703 |
| `security-r6-auth` | 151,128 | 58,615 | 67,780 |
| `security-plain-open` | 5,815,040 | 5,732,479 | 5,742,122 |
| `security-r4-open` | 6,353,243 | 5,937,747 | 5,946,924 |
| `security-r6-open` | 6,455,851 | 5,940,383 | 5,947,233 |
| `security-plain-rewrite` | 11,918,832 | 11,653,975 | 11,656,130 |
| `security-r4-rewrite` | 30,646,620 | 17,309,875 | 17,316,952 |
| `security-r6-rewrite` | 30,591,421 | 17,313,603 | 17,317,570 |
| `security-plain-select` | 5,871,043 | 5,736,799 | 5,742,122 |
| `security-r4-select` | 6,539,037 | 5,981,817 | 5,996,076 |
| `security-r6-select` | 6,642,849 | 5,984,910 | 5,996,385 |

Fresh host input copies are included, including the omitted 16 MiB stream in
`select-sparse`; selective engine reading does not avoid this host setup copy.
The [consolidation allocation record](https://github.com/Lokad/qpdf/blob/master/benchmarks/results/2026-11-13-consolidation-local.json.gz)
retains source/binary identities, exact security-input hashes, commands, JIT/GC,
raw samples and owner residuals. Its pre/postflight idle samples ranged from 8%
to 79%, below the quiet-host requirement. Elapsed samples remain provisional;
quiet-host timing with interleaved A/A controls is the sole deferred measurement.
This refresh establishes no current managed/native throughput comparison.

## Password-security workloads

The same memory-report runner also accepts
`security-{plain|r4|r6}-{auth|open|rewrite|select}`. Prepare its six inputs once;
keep the exact files across workers so randomized salts and password-derivation
work do not change between samples. The manifest records public credentials,
revisions, sizes and hashes. R4 preparation copies the recorded development
template; R6 preparation uses fresh public AES-256 creation.

```powershell
dotnet benchmarks/Lokad.Qpdf.Benchmarks/bin/Release/net11.0/Lokad.Qpdf.Benchmarks.dll --security-inputs artifacts/security-inputs
$env:QPDF_SECURITY_ROOT = (Resolve-Path artifacts/security-inputs).Path
dotnet benchmarks/Lokad.Qpdf.Benchmarks/bin/Release/net11.0/Lokad.Qpdf.Benchmarks.dll --memory-report enabled 30 7 32 security-plain-rewrite security-r4-rewrite security-r6-rewrite
```

Authentication uses the public password predicate on a one-page input. It
isolates authentication from bulk page/stream work but includes input copying,
handler parsing and cleanup; it is not a pure key-derivation microbenchmark.
The small and bulk encrypted inputs have different salts, so subtracting their
medians does not isolate a cipher's cost.
Opening counts 500 pages. Rewrite preserves their protection and approximately
5 MiB of stream data; selection retains pages 1, 250 and 500. All measured jobs
include fresh host input copies, execution and disposal. Preparation and output
validation stay outside capture and verify page counts, protection and selected
content. Full output operations retain their authentication cost.

Use .NET 11 memory reports for primary measurements and .NET 10 for compatibility.
`SecurityBenchmarks` exposes the same twelve workloads to BenchmarkDotNet. The
pinned BenchmarkDotNet version does not automatically recognize the .NET 11
preview runtime; its .NET 10 dry run is a harness check, not timing evidence:

```powershell
dotnet benchmarks/Lokad.Qpdf.Benchmarks/bin/Release/net10.0/Lokad.Qpdf.Benchmarks.dll --filter '*SecurityBenchmarks*' --job Dry
```

Recording-budget smoke runs cover all twelve workloads on both frameworks and
require every caller owner to refund to zero. Pinned qpdf independently checks
all six prepared inputs with both recorded credentials. Compare elapsed results
only with quiet-host and repeated same-input controls; these workloads do not
extend the historical native timing table above.

### Local observations, 2026-11-12

These Windows/.NET 11 RC 3 measurements use candidate code `755b70d9` and
published-source baseline `32a6117d`. The host failed its initial idle gate;
only 10 of 120 later idle samples reached 95%. Elapsed values are **provisional**.
Each range spans medians from two separate workers, with seven batches per
worker and identical input hashes. They are not confidence intervals or native
speed ratios.

| Complete operation | Plain ms | R4 ms | R6 ms |
| --- | ---: | ---: | ---: |
| Authenticate, one page | 0.0037–0.0043 | 0.0747–0.0750 | 1.405–1.430 |
| Open/count, 500 pages | 0.899–0.901 | 1.332–1.837 | 2.696–2.830 |
| Preserve/rewrite, 500 pages | 9.94–10.67 | 20.10–22.78 | 23.52–27.27 |
| Select 3 of 500 pages | 0.966–0.976 | 1.572–1.760 | 2.912–3.407 |

Cumulative managed allocation excludes preparation and uses ordinary local
admission, without the recording root's instrumentation. MB means 1,000,000 bytes.

| Complete operation | Plain MB/job | R4 MB/job | R6 MB/job |
| --- | ---: | ---: | ---: |
| Authenticate, one page | 0.0104 | 0.0211 | 0.1532 |
| Open/count, 500 pages | 5.816 | 6.355 | 6.458 |
| Preserve/rewrite, 500 pages | 11.916 | 30.683 | 30.627 |
| Select 3 of 500 pages | 5.870 | 6.545 | 6.649 |

Interleaved 0.1.0 controls use identical `tiny`, `rewrite-objects` and
`select-sparse` inputs. Candidate allocation increases stay below 800 bytes/job
in these three cases. Same-build elapsed medians vary by up to 33%, so this run
does not establish a plaintext throughput change. Separate recording-budget
checks on both frameworks return every owner to zero.

The source checkout retains input manifests, engine hashes, all samples, idle
observations and recording-budget results in
`benchmarks/results/2026-11-12-security-local.json.gz`. Qualified throughput
comparisons still require a quiet host; no timing threshold gates CI.

## Shared-widget discovery, 2026-11-13

The existing memory-report harness adds `shared-widgets-16`, `-32`, `-64` and
`-128` scenarios. Each selects that many pages from the same valid 128-page
ordinary-field fixture; parsing the full input contributes a fixed cost.
The same driver compares the qualified dev.4 DLL with the component-cache
change, on .NET 11 RC 3. Median ordinary-admission allocations are bytes/job:

| Selected widgets | dev.4 | Component reuse |
| ---: | ---: | ---: |
| 16 | 973,560 | 824,696 |
| 32 | 1,151,152 | 853,424 |
| 64 | 1,515,112 | 919,656 |
| 128 | 2,286,128 | 1,095,216 |

Deterministic internal copy-entry resolver reads fall from 608/2,240/8,576/33,536
to 128/256/512/1,024 at the same sizes. Discovery alone uses 64/128/256/512
reads. All measured recording-budget owners refund to zero. The
[allocation record](https://github.com/Lokad/qpdf/blob/master/benchmarks/results/2026-11-13-shared-widgets-local.json.gz)
retains library/input hashes, candidate patch hash and raw samples. Local
elapsed times remain provisional; quiet-host throughput is deferred.

## Encrypted sizing, 2026-11-13

The same memory-report driver compares source `83d5265d` with encrypted
length measurement, using retained identical inputs and .NET 11 RC 3.
Append `-small` or `-bulk` to a security scenario to select its input size.
Median ordinary-admission allocations are bytes per public rewrite job:

| Input | Before | Length measurement |
| --- | ---: | ---: |
| plain small | 29,144 | 29,144 |
| r4 small | 68,048 | 60,088 |
| r6 small | 202,984 | 194,824 |
| plain bulk | 11,918,608 | 11,918,392 |
| r4 bulk | 30,647,275 | 24,496,411 |
| r6 bulk | 30,590,640 | 24,503,107 |

All measured owner residuals are zero. The key-withholding serializer control
proves all four physical CountingSink paths avoid key derivation and payload
cipher calls for tiny and large streams; actual emission fails without the
key and succeeds after restoration. Existing byte-emitting hash/layout
passes remain. The
[allocation record](https://github.com/Lokad/qpdf/blob/master/benchmarks/results/2026-11-13-encrypted-sizing-local.json.gz)
retains exact library/input hashes and raw samples. This local run does not
establish throughput; quiet-host timing remains deferred.

## Appearance orchestration, 2026-11-13

The `select-one` public one-page scenario allocates 31,800 bytes/job
after sharing post-overlay appearance policy, versus 31,800 with the
retained R11 binary. No merge/generation graph is allocated for this path.
The same .NET 11 RC 3 driver also checks merge and sparse selection, with
zero owner residuals. Exact binary/input hashes and samples are in the
[allocation record](https://github.com/Lokad/qpdf/blob/master/benchmarks/results/2026-11-13-appearance-local.json.gz).
Local timings remain provisional.

# Development tooling

Use the build/test/pack commands in the root README. The solution is .NET 10,
with central package versions and checked-in lock files. Only ImageSharp
3.1.12 is allowed as a production dependency; its closed NuGet version range
`[3.1.12]` also prevents the produced package from selecting a different version.
PdfPig `[0.1.16]` is pinned for development validation only in `tools/QpdfProbe`
and the test project; it must never become a runtime dependency of `Lokad.Qpdf`.
ImageSharp is also referenced by the probe and tests for codec feasibility
work; the library keeps it behind the governed JPEG adapter. Test packages are
only allowed in test projects. SourceLink is private build
tooling. Debug packaging and unsafe compilation are rejected by build targets.

The five reported ImageSharp 3.1.12 advisories are intentionally, temporarily
excluded with advisory-specific `NuGetAuditSuppress` entries in
`Directory.Build.props`, accepted on 2026-10-08. NuGet audit remains enabled for
other advisories, and warnings remain errors. Remove these exclusions when the
approved codec pin is revised; dependency versions and lock files are unchanged.
See [NuGet advisory exclusions](https://learn.microsoft.com/nuget/concepts/auditing-packages#excluding-advisories).

The temporary decision uses this advisory-by-advisory source assessment:

| Advisory | Affected path | Current adapter reachability |
| --- | --- | --- |
| [GHSA-j9gm-c75j-xc9q](https://github.com/advisories/GHSA-j9gm-c75j-xc9q) | TIFF CCITT T4 encoding | Only `JpegEncoder` is constructed. |
| [GHSA-jjfr-hcj7-qf5w](https://github.com/advisories/GHSA-jjfr-hcj7-qf5w) | TIFF CCITT T6 encoding | Only `JpegEncoder` is constructed. |
| [GHSA-wmxv-xphr-5c9g](https://github.com/advisories/GHSA-wmxv-xphr-5c9g) | BigTIFF decoding | JPEG-only configuration and the DCT frame-header check restrict decoding. |
| [GHSA-j3p4-wp97-rph4](https://github.com/advisories/GHSA-j3p4-wp97-rph4) | Histogram equalization of TIFF floating-point pixels | No histogram processing; adapter pixels are `L8`/`Rgb24`. |
| [GHSA-gwg2-r3hj-4w44](https://github.com/advisories/GHSA-gwg2-r3hj-4w44) | Lazy ICC entries parsing in version 3 | DCT decoding sets `SkipMetadata = true`; no ICC entries are accessed. |

This is source-based reachability evidence for this library's current adapter,
not an exploit test or package-wide security clearance. A consumer can use other
ImageSharp paths. Review these five decisions whenever codec registration,
metadata handling, pixel types or processing operations change. This temporary
acceptance does not fix the vulnerabilities.

SourceLink is enabled when an actual repository URL is supplied, including
GitHub Actions' repository metadata. Local builds without `RepositoryUrl` disable
SourceLink/source-control queries. Use the actual repository URL when supplying it.
Untracked-source embedding is disabled, and compiler document paths are mapped
to a stable root. Inspect both `.nupkg` and `.snupkg` with the provided script.

## GitHub Actions

[CI](../.github/workflows/ci.yml) runs on pushes and pull requests with read-only
permissions; superseded PR runs cancel. Windows and Ubuntu use `global.json`,
locked restore, Release build/tests/pack, package inspection and fresh-consumer
checks, including both rejection controls. Ordinary tests require no external tools.
A separate Windows job acquires the checksum-pinned qpdf 12.4.2 oracle and runs
the existing complete reference script.

Each job allows 45 minutes; test/reference steps allow 35 minutes, leaving time
to upload evidence. Always-uploaded artifacts include logs, TRX and reference
reports. Ordinary tests collect mini dumps and sequence diagnostics for crashes
or ten-minute per-test hangs; reference probes retain request/output logs and
their existing request timeouts. Failed jobs also capture a process snapshot.
These jobs verify packages without publishing them. Benchmarks stay outside CI.

## Focused verification and memory estimates

After the locked Release restore/build, run the affected public contract tests
and historical defect controls. A focused lane must select meaningful tests and
report failures/skips; discovery counts and individual historical names are not
acceptance requirements. Use one selection per mechanism and keep important
interactions instead of route/encoding/fault/lifetime cross-products.

```powershell
pwsh -File tools/test-audit-lane.ps1 -Lane SelectionContract
pwsh -File tools/test-audit-lane.ps1 -Lane ResultsContract
pwsh -File tools/test-audit-lane.ps1 -Lane CarriersContract
pwsh -File tools/test-audit-lane.ps1 -Lane DiagnosticsContract
pwsh -File tools/test-audit-lane.ps1 -Lane LifetimeStress
```

Select only affected lanes; this list is not a mandatory sequence.
`ResultsOwnership` is a compatibility alias for `ResultsContract`; running
both repeats the same checks. `DiagnosticStress` aliases `LifetimeStress`,
which now selects a short public-job sequence with significant payloads,
failure/cancellation, actual host-storage reclamation and surviving outputs.
Other ownership selections remain available for debugging concrete failures;
superseded small-carrier receipt/GC requirements do not add release gates.
Add `-List` to inspect the current nonempty selection.

`MaxMemoryBytes` is an enforced estimated job-memory ceiling. Live/peak
counters and memory diagnostics describe charged estimates, not CLR/RSS
measurements. Counter snapshots remain immutable; unreleased charges and
cumulative allocation do not establish retained memory. Significant buffers,
growth/codec work, independent byte/count/work limits, cancellation and cleanup
remain governed. Host buffers, concurrent jobs and caller-retained outputs also
need host policy.

Approximate memory qualification uses five small workload families: two-page
selection, shared/compressed image or JSON/linearized output, amplification
negatives, original configuration fan-out failures and a short repeated-job
sequence. Reuse their existing cases. `PracticalMemoryTests` reports a few
whole-workload estimates/allocation/retention observations; it does not compare
exact object sizes or require small metadata aliases to be billed independently.
The original filtering/collation rejection controls and 4 KiB repeated hint
encoder regression remain. Pure receipt minima, GC-round matrices and repeated
small-metadata stress are consolidated or removal candidates.

The shipped library does not call `GC.Collect` or
`GC.WaitForPendingFinalizers`. Remaining test-only collections permit actual
storage observations; compaction and a prescribed retirement round are not
acceptance conditions. `GC.SuppressFinalize`/`GC.KeepAlive` manage lifetimes
without forcing collection. Production accounting changes are justified by a
concrete failure/material cost, not a new per-carrier audit.

Historical checkpoints retain their original evidence: `b1afaae` took 33.8
minutes and `eff904f` took 10m07s for ordinary tests, while the latter's
complete reference lane took 8m37s. The approximate-contract/test consolidation
passes ordinary integration in 6m17s. The final `34c8097` checkpoint passes
ordinary in 7m39s on Windows and 7m11s on Ubuntu 24.04 WSL2, complete reference
in 6m58s and finite visuals in 24s, with packages/fresh consumers qualified on
both platforms. See [progress](PROGRESS.md) for scope and validation. These
timings/counts describe those sources. Use affected
reference checks while correcting behavior; comprehensive reference, finite
visual and package-consumer checks belong at integration/release checkpoints.
Repeat for changed behavior, failures or a specific unresolved concern.
Record source, command/result and relevant artifact/oracle identity and phase
times without a repeated full-tree/hash/observation census.

## Optional reference checkout and metadata regeneration

Neither the checkout nor Python is needed by ordinary builds or tests.
For explicit metadata regeneration only:

```sh
git clone --branch v12.4.2 --depth 1 https://github.com/qpdf/qpdf.git external/qpdf
python -m pip install -r tools/compatibility-requirements.txt
python tools/update-compatibility.py --reference external/qpdf --output tests/Compatibility/options.json
```

The generator checks the commit and unchanged source inputs. It reads option
metadata, emits no upstream code, and preserves existing status/evidence for
matching option/context entries. Review the resulting diff before committing.
Regeneration must not turn partial coverage into a completion claim.

`external/` and the local handoff are ignored. Do not make restore, compile or
ordinary tests depend on them. Public generated metadata is intentionally
tracked so normal development remains independent of the reference checkout.

## Reference, independent and codec probes

The explicit Windows reference CI job acquires qpdf 12.4.2 from its official
release asset through `tools/install-reference-qpdf.ps1`. Both archive and
executable SHA-256 are checked before the existing version bootstrap. The
destination must be fresh; existing files are rejected without overwrite.
The ordinary Windows/Linux jobs still need no qpdf binary or `external/`.
For the same isolated acquisition locally:

```powershell
$oracleRoot = Join-Path 'external' ('oracle-' + [Guid]::NewGuid().ToString('N'))
$env:QPDF_EXE = ./tools/install-reference-qpdf.ps1 -Destination $oracleRoot
```

`tools/QpdfProbe` is the developer-only oracle runner. It is built with the
solution but is never packed. These commands have been executed:

```sh
dotnet build tools/QpdfProbe/QpdfProbe.csproj -c Release
dotnet run -c Release --project tools/QpdfProbe -- --help
dotnet run -c Release --project tools/QpdfProbe -- validate --input tests/Fixtures/minimal-1page.pdf
dotnet run -c Release --project tools/QpdfProbe -- probe-oracle --qpdf-exe <path-to-qpdf-12.4.2> --input tests/Fixtures/minimal-1page.pdf
dotnet run -c Release --project tools/QpdfProbe -- compare --qpdf-exe <path-to-qpdf-12.4.2> --input tests/Fixtures/minimal-1page.pdf --expected-npages 1
dotnet run -c Release --project tools/QpdfProbe -- probe-jpeg
dotnet run -c Release --project tools/QpdfProbe -- probe-flate
dotnet run -c Release --project tools/QpdfProbe -- emit --workdir <case> --out <logical>=<physical> -- <argv...>
dotnet run -c Release --project tools/QpdfProbe -- emit-job --workdir <case> --job <job.json> --out <logical>=<physical>
dotnet run -c Release --project tools/QpdfProbe -- fix-qdf --workdir <case> --in <logical-input> --out <logical>=<physical>
dotnet run -c Release --project tools/QpdfProbe -- zlib-flate --workdir <case> --in <logical-input> --out <logical>=<physical> --mode <compress|uncompress> [--level 1..9]
```

`probe-oracle` runs `qpdf --version` (must contain 12.4.2), `qpdf --show-npages`
and `qpdf --check`. `validate` opens the PDF with PdfPig 0.1.16 and reports
header version, page count and SHA-256. `compare` requires oracle, independent
and expected page counts to agree; a mismatch exits 2. `probe-jpeg` exercises
grayscale/RGB round-trips, quality endpoints (`1..100`, qpdf `0 -> 1`),
the CMYK/YCCK raw-sample gap, dimension bounds and cancellation.
`probe-flate` exercises zlib round-trips, qpdf `1..9` to BCL level buckets,
truncation/corruption behavior, expansion limits and cancellation. mit and mit-job run the managed argv and job-JSON routes over workdir inputs for the differential lane; ix-qdf and zlib-flate exercise the QDF-repair and raw-flate companions. Missing
executables or inputs are explicit failures, never silent skips. Ordinary
`dotnet test` covers these contracts with synthetic fixtures and a fake oracle;
it never requires a real qpdf binary.

Synthetic fixtures live in `tests/Fixtures` with `manifest.json` recording
SHA-256, byte counts, expected page counts and provenance. No upstream bytes.

The explicit reference lane needs a pinned executable:

```powershell
pwsh -File tools/bootstrap-qpdf.ps1 -QpdfExe $env:QPDF_EXE
pwsh -File tools/run-reference-checks.ps1 -QpdfExe $env:QPDF_EXE
```

Both scripts fail explicitly when `QPDF_EXE` is missing. The reference lane
runs the positive compares plus deliberate negative cases (wrong expected
count must exit 2; truncated input must fail validation). A real 12.4.2
executable is pinned at `external/oracle/msvc64/qpdf-12.4.2-msvc64/bin/qpdf.exe` (ignored by git; set `QPDF_EXE` to it).
Checksum, version output and JPEG/Zopfli build features are recorded in `docs/COMPATIBILITY.md`.
The differential lane (`tools/run-differential-checks.ps1`, via `QpdfProbe emit`) compares managed and oracle outputs operation by operation.
Each run creates a fresh ignored directory under `external/diff/` and preserves
its results. `-WorkRoot` on either runner selects an explicit fresh differential
directory; an existing path rejects before overwriting earlier evidence.
The reference runner also invokes `tools/run-parser-checks.ps1` with the built
probe. Its authored valid/rebuilt-xref cases compare exact object values at six
error limits and validate six rewrites with qpdf and PdfPig. Outputs/results
live in unique ignored `external/parser-checks/` directories. It is an explicit
external lane; ordinary tests use the same behavioral cases without an oracle.

Reference scripts reuse the managed probe process sequentially for up to 64
requests, then start another process. Each request still constructs a fresh
in-memory host and public job with its original limits. Only opened inputs are
read into that host. Request logs use exclusive files under the run workspace;
the differential scratch files also belong to that workspace. Parser expected
values index the existing observation rows. Native calls and every original
comparison remain. The library and ordinary tests do not use this transport.

The explicit transport controls need a freshly built probe and a fresh workspace:

```powershell
pwsh -File tools/test-reference-probe.ps1 -ProbeDll tools/QpdfProbe/bin/Release/net10.0/QpdfProbe.dll -WorkRoot external/probe-controls/fresh-run
```

They compare cold and batched outputs/exits, check fresh rewritten bytes with the
independent reader, reject cross-request output reuse, exercise lazy input access
and worker rollover, and reject malformed/oversized protocol requests. Eight
cold/batch pairs include Unicode filenames; argv/job/Unicode-label rewrite bytes
match. All 87 requests across two sequential processes and both negatives pass.

The complete fresh measured run on `07246d2` takes 415.5 seconds (6m55s), versus
2621.0 seconds (43m41s) for the unchanged instrumented baseline. Parser intervals
fall 2029.0 to 326.7 seconds and differential 582.5 to 83.1. Before phase times
use stopwatches; after phase intervals run from workspace creation through the
terminal request record, and complete time uses the outer stopwatch. All 1846
parser rows/432 rewrites, 283 authored PDF bytes, 617 differential case messages
and complete 45/60/189/108 result inventories match. All 2131 differential input
paths remain; byte variations are limited to equivalent generated JSON data,
fresh paths and native PDF IDs/attachment timestamps inherited from their seed.
Original raw hashes and comparisons remain. The library/test binaries, pinned
oracle, limits and both deliberate negatives stay exact. Native process calls
remain the largest evident cost; parallel execution was unnecessary.


## Benchmarks

`benchmarks/Lokad.Qpdf.Benchmarks` is the developer-only resource-counter and benchmark harness. It is built with the
solution but is never packed. All inputs are synthetic; no qpdf executable or upstream checkout is required. These commands have been executed:

```sh
dotnet build benchmarks/Lokad.Qpdf.Benchmarks/Lokad.Qpdf.Benchmarks.csproj -c Release
dotnet run -c Release --project benchmarks/Lokad.Qpdf.Benchmarks -- --help
dotnet run -c Release --project benchmarks/Lokad.Qpdf.Benchmarks -- --counter-report
```

`--counter-report` prints observed work/byte/host counters, peak/live memory
estimates and separate cumulative allocation totals. Optional scenario names
select a small workload set, for example `--counter-report select-few-50 linearize-10`.
Unknown names or unexpected outcomes fail explicitly. Omitting names retains
the existing ten scenarios. Allocation totals and charge debt do not measure
retained memory. Timed BenchmarkDotNet runs use `--filter`; timings are local
observations, never exact acceptance thresholds. Historical counter results
remain in `docs/BENCHMARKS.md`.

## Package consumer

`tests/Lokad.Qpdf.PackageConsumer` is a clean console project outside the
solution. It restores the packed `Lokad.qpdf` NuGet from
`artifacts/nuget` (never a project reference) plus approved dependencies
from NuGet, then exercises the public surface across selection, merge, rotation, labels,
split, replacement, appearances, QDF, linearization, and the companions, with argv
and job parity checks plus crypto rejection, using only the public API and an in-memory
host. It needs no upstream checkout, reference executable, or renderer.

```sh
dotnet pack src/Lokad.Qpdf/Lokad.Qpdf.csproj -c Release --no-build --no-restore
pwsh -File tools/verify-package-consumer.ps1 -PackagePath artifacts/nuget/Lokad.qpdf.0.1.0-alpha.1.nupkg
pwsh -File tools/test-package-consumer.ps1 -PackagePath artifacts/nuget/Lokad.qpdf.0.1.0-alpha.1.nupkg
```

The consumer pins the built package version centrally; bump it together
with `src/Lokad.Qpdf` releases. Its committed lock is the reviewed dependency
graph and external content-hash seed. Rebuilding the same unsigned alpha
changes its local artifact hash, so the verifier changes only that hash in a
copied lock before its first locked restore. It never reevaluates dependencies
or edits the tracked lock. A wrong version still fails explicitly.

Every run copies the consumer into a unique `artifacts/package-consumer/`
directory, copies exactly the supplied artifact into a new feed, and uses a
fresh package cache. Source mapping restricts `Lokad.qpdf` to that feed and
ImageSharp to NuGet. This matters because NuGet skips source lookup for a
package already in its global cache; mapping alone does not prove freshness
([NuGet source mapping](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping)).
The verifier builds without restoring, compares the consumer DLL's SHA-256
with the supplied package's DLL, then runs all public smoke scenarios.

The negative integration script uses copied locks to prove a wrong local
version and a corrupt external hash both reject before consumer execution.
CI runs the verifier and negative cases on Windows and Linux after pack;
evidence remains under `artifacts/`. Neither lane needs `external/`.

## Finite visual qualification

After the locked Release build, supply an explicit Poppler `pdftoppm` executable
and fresh workspace. Fixture-builder reuse requires PowerShell running on
.NET 10. The renderer is development tooling and is never packed:

```powershell
$visualRoot = Join-Path 'external/visual-checks' ([Guid]::NewGuid().ToString('N'))
pwsh -File tools/run-visual-checks.ps1 -QpdfExe $env:QPDF_EXE -RendererExe $env:QPDF_RENDERER_EXE -WorkRoot $visualRoot
```

The invocation reuses existing authored fixtures/builders for seven cases:
overlay, underlay, rotated-page flattening, generated text/checkbox appearances
and gray/RGB JPEG optimization. Both managed and native outputs are fresh,
pass native syntax and independent page checks, and render at 144 dpi with the
same content crops. JPEG dictionaries must preserve 200×200 dimensions,
8-bit gray/RGB colorspace and contain smaller DCT payloads; a no-op rewrite
cannot qualify optimization. Missing tools, blank reference crops, unexpected
crop dimensions and malformed raster payloads fail.

The original declared thresholds remain fixed:

| Comparison | Mean absolute channel error / 255 | Fraction of changed pixels | Any-channel change threshold |
| --- | ---: | ---: | ---: |
| Placement, rotation, appearances | 0.005 | 0.01 | 8/255 |
| Managed/native JPEG at quality 75 | 0.04 | 0.10 | 32/255 |
| Each lossy JPEG against original texture | 0.20 | 0.30 | 64/255 |

Lossless geometry/text allows minor raster differences. Independent JPEG
encoders may differ, and the original random RGB texture loses chroma detail
at quality 75; the tighter managed/native check accompanies the broader
original-texture check. Valid PDFs with a deliberately misplaced image and
stale flattened text must reject at the geometry/appearance thresholds.
Tolerances are not widened in response to a managed failure.

Results record source commit and whether implementation/tooling changes are
uncommitted, renderer version/hash, oracle hash, input identities, crops,
comparisons and elapsed time. The identified local renderer is Poppler 24.04.0;
use the same executable/settings for a comparison. Prior draft trials do not
replace the final committed-source checkpoint.

`tools/test-reference-probe.ps1` also exercises exact nonblank raster matching,
visible mismatch, blank reference, wrong expected dimensions and truncated
payload, including a leading whitespace-valued pixel byte. It reuses the
existing process/probe controls and does not require a native PDF engine.

Tooling changes keep runtime dependencies and host boundaries intact.
See [progress](PROGRESS.md) for the current integrated qualification.

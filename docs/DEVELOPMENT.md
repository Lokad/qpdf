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
metadata handling, pixel types or processing operations change. Audit stays
enabled for additional advisories; a qualified restore is no longer the policy
resolution. The explicit temporary acceptance is tracked as POLICY-01 in the
[closure checklist](AUDIT-CLOSURE.md).

SourceLink is enabled when an actual repository URL is supplied, including
GitHub Actions' repository metadata. The initial local repository has no remote,
so local SourceLink/source-control queries are disabled. Do not invent a URL.
Untracked-source embedding is disabled, and compiler document paths are mapped
to a stable root. Inspect both `.nupkg` and `.snupkg` with the provided script.

## Focused contract, ownership and stress lanes

After the locked Release restore/build, select the affected contract lane while
debugging, then its ownership or stress lane after correcting behavior:

```powershell
pwsh -File tools/test-audit-lane.ps1 -Lane SelectionContract
pwsh -File tools/test-audit-lane.ps1 -Lane SelectionOwnership
pwsh -File tools/test-audit-lane.ps1 -Lane ResultsContract
pwsh -File tools/test-audit-lane.ps1 -Lane ResultsOwnership
pwsh -File tools/test-audit-lane.ps1 -Lane DiagnosticsContract
pwsh -File tools/test-audit-lane.ps1 -Lane DiagnosticsOwnership
pwsh -File tools/test-audit-lane.ps1 -Lane CarriersContract
pwsh -File tools/test-audit-lane.ps1 -Lane CarriersOwnership
pwsh -File tools/test-audit-lane.ps1 -Lane WriterOwnership
pwsh -File tools/test-audit-lane.ps1 -Lane LifetimeStress
```

| Lane | Executed test cases | Observation scope |
| --- | ---: | --- |
| SelectionContract | 1530 | Complete public jobs across 33 routes, both encodings, faults, cancellation and fixed admission limits. |
| SelectionOwnership | 74 | All 1532 original scenarios in 72 batches and two independent regressions. |
| ResultsContract | 1587 | Command/typed producer contracts, pre-host rejection and physical shape checks. |
| ResultsOwnership | 36 | 1470 original lifetime scenarios in 24 bounded batches, plus 12 independent extraction/document controls. |
| DiagnosticsContract | 450 | Public warning histories, failure prefixes, prior targets and fixed memory ceilings. |
| DiagnosticsOwnership | 4 | Original public hidden-warning regressions. |
| CarriersContract | 1660 | Version, JSON, failed-result, selection, warning and checker producer/admission contracts. |
| CarriersOwnership | 32 | 1302 original observations in 22 bounded batches, plus ten independent GC controls. |
| WriterOwnership | 10 | All 216 original paused/completed writer backing observations; dedicated encoding/outcome contracts stay independent. |
| LifetimeStress | 1 | Seventeen independent diagnostic, presentation, ledger, checker-detail and form controls with original 32-cycle bodies and limits. |

`DiagnosticStress` remains an alias of `LifetimeStress`. The single discovered
stress case logs all 17 independent controls; it does not reduce their cycle counts.
Forced collections belong to these tests. The shipped library has no
`GC.Collect` or `GC.WaitForPendingFinalizers` calls. Test-only collection rounds
are shared because they scan the whole test process and wait for finalizers.
Compacting rounds preserve the original ownership stress controls. Finalizers
report retirement of escaped storage; tests drive that retirement at a known
boundary before checking accounting and surviving aliases. The library uses
`GC.SuppressFinalize` and `GC.KeepAlive` for lifetime management; neither forces
a collection.

Add `-List` to inspect a lane without execution. Exact discovery/result counts
reject empty or partial selections. Batch output names every original scenario
and records its phases and maintenance count; failures remain individually visible.
Jobs run independently to their original collection boundary. Global collections
are shared, while every governor retains its own quotas and original bounded
maintenance calls. Metadata observations retain no governor. Alias drop phases,
no-GC admission controls and stress pressure remain intact. Up to 64 rows share
one batch; paused writers retain independent sinks. Original one/three/four-round
collection geometry, compaction and interleaved maintenance remain explicit.
Extraction and original regression controls remain independent.

Selection focused time fell 408.5 to 74.3 seconds (5.5 times faster), with all
1532 predecessor outcomes preserved. The four additional matrices fall
965.7 to 141.6 seconds (6.8 times faster) and preserve all 2063 current
scenario outcomes and compatible predecessor failures. Their explicit GC calls
fall 24960 to 2800, retaining all 9184 maintenance calls. The command snapshot
fixture adds a 16 MiB no-GC completion/capture region after four asynchronous
snapshot comparisons failed in the first trial; equality assertions and job
budgets stay unchanged. Private feature inspection supports older predecessor
runtimes without treating missing members as behavioral failures.

The preceding combined unfiltered run on `b1afaae` passes all 18913 cases in
33.8 minutes. The historical 77-minute run used another test layout and
overlapped reference/native phases; compare the isolated focused runs for a
controlled reduction. The TEST-03 run on `45e16c5` passes all
18626 unfiltered cases in 529.8 seconds (8.8 minutes), with no
failures/skips or concurrent verification jobs. All previous scenarios and
assertions remain accounted for; batching changes discovery counts, not coverage.

Ordinary unfiltered `dotnet test` still executes every case. Run it once on the
completed correction; repeat expensive checks for changed sources, failures or
unresolved concerns. These lanes supplement that combined proof. Native/reference
and package/consumer checks remain explicit; unchanged source and artifact hashes
can retain established evidence with its scope recorded. The remaining ownership
matrices are separate optimization candidates, selected from measured timings.

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

`--counter-report` runs representative operations (50-page inspection and selection, shared-font and 20-way merges, a 30-filter decode chain, JSON inspection, compressed rewrite, photo optimization, 10-page linearization, and a pre-canceled selection) and prints deterministic governor counters: work units, decoded bytes, peak/live memory, host operations, and diagnostics. It exits nonzero when a scenario fails to complete. Timed BenchmarkDotNet benchmarks over the same synthetic inputs run locally via `--filter`; their timings are machine-specific and are never asserted. Baseline counter results are recorded in `docs/BENCHMARKS.md`.
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

## Next tooling

All operation paths are implemented with the M11 compatibility audit in
progress (see docs/PROGRESS.md). Tooling changes follow the same rules:
no new runtime dependencies, no upstream checkouts or executables for
ordinary builds and tests, and probe commands documented alongside use.

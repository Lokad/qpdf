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

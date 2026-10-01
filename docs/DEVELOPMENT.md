# Development tooling

Use the build/test/pack commands in the root README. The solution is .NET 10,
with central package versions and checked-in lock files. Only ImageSharp
3.1.12 is allowed as a production dependency; its closed NuGet version range
`[3.1.12]` also prevents the produced package from selecting a different version.
PdfPig `[0.1.16]` is pinned for development validation only in `tools/QpdfProbe`
and the test project; it must never become a runtime dependency of `Lokad.Qpdf`.
ImageSharp is also referenced by the probe and tests for codec feasibility
work; the library keeps it behind the future JPEG adapter. Test packages are
only allowed in test projects. SourceLink is private build
tooling. Debug packaging and unsafe compilation are rejected by build targets.

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
```

`probe-oracle` runs `qpdf --version` (must contain 12.4.2), `qpdf --show-npages`
and `qpdf --check`. `validate` opens the PDF with PdfPig 0.1.16 and reports
header version, page count and SHA-256. `compare` requires oracle, independent
and expected page counts to agree; a mismatch exits 2. `probe-jpeg` exercises
grayscale/RGB round-trips, quality endpoints (`1..100`, qpdf `0 -> 1`),
the CMYK/YCCK raw-sample gap, dimension bounds and cancellation.
`probe-flate` exercises zlib round-trips, qpdf `1..9` to BCL level buckets,
truncation/corruption behavior, expansion limits and cancellation. Missing
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
## Package consumer

`tests/Lokad.Qpdf.PackageConsumer` is a clean console project outside the
solution. It restores the packed `Lokad.qpdf` NuGet from
`artifacts/nuget` (never a project reference) plus approved dependencies
from NuGet, then exercises typed selection, argv and job routes, their
parity, and crypto rejection using only the public API and an in-memory
host. It needs no upstream checkout, reference executable, or renderer.

```sh
dotnet pack src/Lokad.Qpdf/Lokad.Qpdf.csproj -c Release --no-build --no-restore
dotnet restore tests/Lokad.Qpdf.PackageConsumer/Lokad.Qpdf.PackageConsumer.csproj --locked-mode
dotnet run --project tests/Lokad.Qpdf.PackageConsumer/Lokad.Qpdf.PackageConsumer.csproj -c Release --no-restore
```

The consumer pins the built package version centrally; bump it together
with `src/Lokad.Qpdf` releases. Its lock file is committed. CI runs the
smoke on Windows and Linux after pack.

## Next tooling

M1 paths are complete. M2 begins strict-reader breadth (filters, xref and
object streams, incremental and hybrid structures); the M0 codec decisions
constrain M6/M8 integration.

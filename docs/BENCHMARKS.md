# Benchmarks

.NET 11 is the performance baseline. The package also targets .NET 10.
The [evidence index](https://github.com/Lokad/qpdf/blob/master/benchmarks/results/README.md)
records source, binary and input identities for each dated campaign. Use a
quiet host and interleaved same-build A/A controls for current throughput
claims. Timing thresholds stay outside CI.

| Metric | Measurement |
| --- | --- |
| Elapsed time | Warmed jobs include input acquisition, parsing, transformation, staging and publication. Startup, preparation, validation and deletion run outside each sample. |
| Allocation | Cumulative managed bytes per job, averaged over the sampled jobs. |
| Peak RSS | Process high-water mark of a dedicated worker, including runtime and warmup. |
| Memory admission | Governed peak and residual bytes plus reserve/resize activity. See the [memory contract](MEMORY.md). |

## Native comparison and validation

Use a quiet Linux VM with Python 3, GNU time, a C++ toolchain, CMake, zlib/JPEG
development libraries and the .NET SDKs. Place a full, clean qpdf 12.4.2 checkout
under `external/qpdf` at the pinned commit.
The static warm driver links to the built-in native crypto provider. Commands
run from the repository root. [Reference pin](https://github.com/Lokad/qpdf/blob/master/tools/reference-pin.json)

```bash
dotnet restore Lokad.Qpdf.slnx --locked-mode
dotnet build Lokad.Qpdf.slnx -c Release --no-restore
mkdir -p artifacts
run=$(mktemp -d "$PWD/artifacts/performance.XXXXXX")
cli=$(command -v dotnet)
native_source="$PWD/external/qpdf"
native_build="$run/native"
oracle="$native_build/qpdf/qpdf"
probe="$PWD/tools/QpdfProbe/bin/Release/net11.0/QpdfProbe.dll"

cmake -S "$native_source" -B "$native_build" -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=OFF -DBUILD_STATIC_LIBS=ON -DBUILD_DOC=OFF -DZOPFLI=OFF \
  -DUSE_IMPLICIT_CRYPTO=OFF -DREQUIRE_CRYPTO_NATIVE=ON
cmake --build "$native_build" --parallel

python3 tools/compare-linux-performance.py --repo "$PWD" --dotnet "$cli" --qpdf "$oracle" \
  --native-source "$native_source" --native-build "$native_build" \
  --work-root "$run/prepared" --skip-warmed
python3 tools/test-performance-validation.py --repo "$PWD" --dotnet "$cli" --probe "$probe" \
  --qpdf "$oracle" --input-root "$run/prepared" --work-root "$run/validation" --file-input
python3 tools/compare-warm-performance.py --repo "$PWD" --dotnet "$cli" --qpdf "$oracle" \
  --native-source "$native_source" --native-build "$native_build" \
  --input-root "$run/prepared" --work-root "$run/warmed" \
  --file-input --samples 7 --sample-seconds 0.35 --warmup-seconds 2
```

The preparation command produces a workload manifest, fixtures, and
whole-process samples. The validation command tests page and object semantics
before warmed timing. It also rejects a wrong output-free expectation, a
marker-only object-stream claim and a no-op checker. Use the validation command
on an existing prepared root to repeat the controls independently.

For same-build A/A, use a fresh warm work root, add `--baseline-probe "$probe"`
and select the corresponding `--workload` entries. For job JSON, use
`--file-input --workload select-sparse --job-selection`. Preserve the
result identities, input hashes, validation and per-batch samples.

## Cooperative memory admission

Run each invocation in a dedicated process. Optional scenario names narrow
the run. Both modes use the same build. Enabled mode shares a recording
budget across fresh job and host owners. The report includes allocation,
JIT/GC, charged peaks and residuals.
Closing the last owner must refund all charges.

```sh
dotnet benchmarks/Lokad.Qpdf.Benchmarks/bin/Release/net11.0/Lokad.Qpdf.Benchmarks.dll --memory-report disabled 30 7 3
dotnet benchmarks/Lokad.Qpdf.Benchmarks/bin/Release/net11.0/Lokad.Qpdf.Benchmarks.dll --memory-report enabled 30 7 3
```

## Password-security workloads

Prepare the plain, R4 and R6 inputs once. Reuse the exact bytes between
workers because varying salts changes derivation work. The manifest records
the credentials, revisions and hashes. The runner accepts
`security-{plain|r4|r6}-{auth|open|rewrite|select}` with optional
`-small` or `-bulk` input suffixes.

```powershell
$run = Join-Path artifacts ('security-inputs-' + [guid]::NewGuid().ToString('N'))
$bench = 'benchmarks/Lokad.Qpdf.Benchmarks/bin/Release/net11.0/Lokad.Qpdf.Benchmarks.dll'
dotnet $bench --security-inputs $run
$env:QPDF_SECURITY_ROOT = (Resolve-Path $run).Path
dotnet $bench --memory-report enabled 30 7 32 security-plain-rewrite security-r4-rewrite security-r6-rewrite
```

Authentication measures a public one-page password predicate including
acquisition and cleanup. The other workloads open, rewrite or select pages
from the prepared document through a fresh host. Use the pinned qpdf check
to validate prepared protected documents with both credentials. Run the .NET 10
BenchmarkDotNet dry job from a checkout containing one benchmark project copy.

```powershell
dotnet benchmarks/Lokad.Qpdf.Benchmarks/bin/Release/net10.0/Lokad.Qpdf.Benchmarks.dll --filter '*SecurityBenchmarks*' --job Dry
```

The benchmark launcher exits nonzero when a measurement invocation selects no
workloads or any selected workload fails. Use `--list flat` to inspect selection.

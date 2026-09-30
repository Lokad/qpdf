# Lokad.qpdf

A .NET 10 library project for independently implementing qpdf's
non-cryptographic PDF structural operations in managed, memory-safe C#.
The intended embedding boundary is explicit inputs, host-mediated effects,
resource limits, and structured outputs and diagnostics.

**Current status: solution scaffold only.** No PDF reader, writer, command
execution, or page-selection API is implemented. The `0.1.0-alpha.1` package
is a build/packaging checkpoint, not a usable PDF processor.

The compatibility target is qpdf **12.4.2**, commit
`4eba95899886e851cc41d76886483b347612f2a8`. The initial inventory accounts for
140 documented flags across 157 option/context entries; this is a scope map,
not implemented feature coverage.

## Requirements

- Released .NET 10 SDK, pinned by `global.json`.
- Public NuGet access for the first restore; later locked restores can use a
  populated package cache.
- PowerShell 7 for the package inspection script, including on Linux.

## Build and verify

```sh
dotnet restore Lokad.Qpdf.slnx --locked-mode
dotnet build Lokad.Qpdf.slnx -c Release --no-restore
dotnet test Lokad.Qpdf.slnx -c Release --no-build --no-restore
dotnet pack src/Lokad.Qpdf/Lokad.Qpdf.csproj -c Release --no-build --no-restore
pwsh -File tools/verify-package.ps1 -PackagePath artifacts/nuget/Lokad.qpdf.0.1.0-alpha.1.nupkg
```

The library uses namespace/assembly `Lokad.Qpdf` and package ID `Lokad.qpdf`.
Only explicit Release packing produces packages. Ordinary tests and builds
do not require an upstream checkout, qpdf executable, Python, or a renderer.
CI runs the scaffold checks on Windows and Linux.

## Scope and dependencies

Passwords, encryption/decryption, cryptographic options, signing and signature
validation are excluded. Rendering, OCR and text layout are outside the target.
The implementation must not vendor code or depend on qpdf/libqpdf, wrappers,
native PDF engines or subprocess fallbacks.

The sole approved runtime package is **SixLabors.ImageSharp 3.1.12**, reserved
for the governed JPEG adapter. Its exact version is pinned in the central
versions, lock files and produced package dependency. SourceLink and test
packages are development dependencies only.

See `docs/COMPATIBILITY.md` for scope, `ARCHITECTURE.md` for the intended
boundary, `docs/DEVELOPMENT.md` for tooling, and `docs/PROGRESS.md` for the next
implementation work. These documents distinguish planned behavior from code
that already exists.

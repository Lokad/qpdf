# Agent instructions

These instructions apply to this standalone repository.

## Scope and status

Read `docs/COMPATIBILITY.md` and `ARCHITECTURE.md` before
implementation. Stay within the current requested task. A roadmap entry does
not mean that functionality exists or that it has been verified.

Implement authorized work in small, coherent local commits. Include meaningful
tests and relevant documentation updates with each behavior change. Use imperative
`Area: summary` commit subjects, such as `Reader: resolve hybrid xrefs`.
Keep commits buildable, preserve unrelated user edits, and continue without
requesting approval after each authorized local commit. Pushing, tagging and
publication are separate actions.

## Build and checks

```sh
dotnet restore Lokad.Qpdf.slnx --locked-mode
dotnet build Lokad.Qpdf.slnx -c Release --no-restore
dotnet test Lokad.Qpdf.slnx -c Release --no-build --no-restore
dotnet pack src/Lokad.Qpdf/Lokad.Qpdf.csproj -c Release --no-build --no-restore
pwsh -File tools/verify-package.ps1 -PackagePath artifacts/nuget/Lokad.qpdf.0.1.0-alpha.1.nupkg
pwsh -File tools/verify-package-consumer.ps1 -PackagePath artifacts/nuget/Lokad.qpdf.0.1.0-alpha.1.nupkg
pwsh -File tools/test-package-consumer.ps1 -PackagePath artifacts/nuget/Lokad.qpdf.0.1.0-alpha.1.nupkg
```

The consumer verifier copies the reviewed lock into an isolated run, binds only
the local package hash to the supplied artifact, and restores in locked mode
with a fresh cache/feed. External hashes and the tracked lock remain unchanged.

After an intentional dependency change, restore without locked mode once,
review and commit the new lock files, then verify locked mode again. Update
version-specific package commands when changing the project version.

## Architecture and style

- Target `net10.0`, nullable enabled, warnings as errors, and no unsafe blocks.
- No vendored code, qpdf-related dependencies, native PDF engines, interop or
  runtime subprocess bridges. Public upstream code is reference material only.
- The core package has no runtime NuGet dependencies. JPEG codecs are supplied
  by callers through the host capability. SixLabors.ImageSharp **3.1.12** is
  development-only; preserve its pin and reviewed temporary advisory exceptions.
- Runtime storage, descriptors, environment and publication must be host-mediated.
  Physical filesystem/process adapters belong in development tools or hosts.
- Passwords and crypto are excluded, including empty-password encrypted inputs.
- All exposed execution paths need bounded allocations/work and cancellation.
- Search existing code before adding logic. Share the reader, object graph,
  filters and writer across typed, argv and job JSON entry points.
- Prefer file-scoped namespaces, small types, explicit outcomes and byte-oriented
  PDF handling. Do not add default parameter values or null-forgiving operators.
- Do not invent public API placeholders to fill out the roadmap. Prove minimal
  signatures with a package consumer before stabilizing them.

## Evidence and artifacts

Use the canonical compatibility inventory and meaningful behavior fixtures.
Never mark an option complete just because it parses. Keep unmet prerequisites
visible; `--check`, page-resource defaults and image processing span subsystems.
Ordinary tests must work without `external/`; future reference-dependent jobs
must be explicit and must not silently skip unavailable tools.

Keep documentation concise and current. Do not
force-add ignored local plans, `external/`, packages or build outputs. Public files must
contain no private repository details or machine-specific absolute paths.

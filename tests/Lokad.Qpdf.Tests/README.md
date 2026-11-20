# Focused test support

`PdfTestDocument` frames classic-PDF fixtures. Feature tests supply object
bodies and expected outcomes. Physical layout, malformed inputs, password
security and native vectors use independent construction.
The fixture manifest records checksums, page counts and which inputs PdfPig
also opens. [Fixture manifest](../Fixtures/manifest.json)

The test build and reference lane use the same MSBuild target to expand the
archived R4 generation fixture. The manifest describes the expanded PDF.

`TestDocumentEngine` supplies an admitted graph for component tests.
`MemoryBudgetHost` borrows storage and a caller budget. Probe tests use the
executable copied by the matching build. Source audits use that build's
checkout path.

## Coverage ownership

Test each semantic matrix at its shared owner and sample adapter wiring.
Keep independent observations for resource limits, cancellation, output
layout and publication. The option inventory links behavior to evidence.
[Compatibility inventory](../Compatibility/options.json) ·
[Security test ownership](Security/README.md)

| Boundary | What to observe |
| --- | --- |
| Publication | Seed the actual target and observe transaction calls with recording hosts. Check admission, closure and sequential commits in `Foundation/PublicationAdmissionTests`. |
| Lifetime | Establish weak-reference collectability separately from admission refunds. Use `Foundation/PreparationAdmissionChecks` for limits and cleanup, and `Writing/InspectionFailureTests` for JSON exception translation and lease lifetime. |
| Work and cancellation | Observe actual loops using deterministic work or host boundaries. `NumericCopyCancellationTests`, `JsonSchemaMemoryTests` and `SubtreeReuseTests` cover distinct cases. Reflect only on project-owned fields. |
| Graphs and writer layout | Assert active and private references independently. Read actual xref membership and require valid linearization. Retain frozen invalid layouts. |
| Filters and attachments | Check codec eligibility, recovered bytes and decoded quotas at the filter chain. Use distinct integration cases for named publication, fileless results and typed attachment lifetimes. |
| Native comparisons | Check ordered listings and exact payloads. For bound image transforms, verify the painted stream with PdfPig and decoded samples with pinned qpdf. |

Run the attachment ownership cases with `dotnet test Lokad.Qpdf.slnx -c Release --filter FullyQualifiedName~Attachment`.

CI retains TRX files and verification logs. Failed or canceled runs can add
VSTest traces and dumps in `verification-diagnostics-*`. Use short scalar
theory arguments for stable case identifiers.

## Reference and visual checks

Run from the repository root after the locked Release build. On Windows, the
installer verifies the archive and executable digests. For a native source
build, use the pinned commit and record the clean checkout and executable hash.
[Build](../../README.md#build-from-source) · [Reference pin](../../tools/reference-pin.json)

```powershell
$run = Join-Path artifacts ('reference-' + [guid]::NewGuid().ToString('N'))
$oracle = ./tools/install-reference-qpdf.ps1 -Destination (Join-Path $run 'oracle')
$probe = 'tools/QpdfProbe/bin/Release/net11.0/QpdfProbe.dll'
./tools/run-reference-checks.ps1 -QpdfExe $oracle -ProbeDll $probe -WorkRoot (Join-Path $run 'reference')

$renderer = (Get-Command pdftoppm).Source
pwsh -NoProfile -File tools/run-visual-checks.ps1 -QpdfExe $oracle -RendererExe $renderer `
  -ProbeDll $probe -FixtureAssembly tests/Lokad.Qpdf.Tests/bin/Release/net10.0/Lokad.Qpdf.Tests.dll `
  -WorkRoot (Join-Path $run 'visual')
```

On other platforms, point `$oracle` at the pinned native build. Omitting
`-ProbeDll` in the reference runner builds the probe with a locked restore.
Each lane requires its tools and a fresh workspace. Reference results include
stage logs and distinguish the supplied native executable hash from the target
pin. The visual lane needs PowerShell on .NET 10 or later and a test assembly
with its copied fixtures and dependencies. Its receipt hashes the inputs and
binaries. Negative controls must reach pixel comparison and exceed tolerance.
[JPEG fixtures](../Fixtures/JPEG.md)

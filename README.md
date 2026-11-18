# Lokad.qpdf

Lokad.qpdf is a .NET 10 and .NET 11 library for splitting, merging, reordering, checking and
rewriting PDF documents, with password-aware document opening. It implements
qpdf-style operations in managed C#, with no native qpdf installation required at runtime.
The core package has no runtime NuGet dependencies.

Use typed C# methods, qpdf-style arguments or job JSON from your application.
The development version targets compatibility with qpdf 12.4.2;
see [compatibility](docs/COMPATIBILITY.md) for supported behavior and differences.

## What you can do

- Extract, reorder, duplicate, merge, split and rotate pages.
- Extract previews from signed PDFs, including supported password-encrypted inputs,
  retaining stored signature marks.
- Inspect document structure, check PDFs, and manage attachments and metadata.
- Open RC4 R2/R3, 128-bit RC4/AES R4 and AES-256 R5/R6 inputs through `QpdfDocument`, using user or owner credentials.
- Rewrite PDFs with compression, object streams and linearization for web delivery.
- Apply overlays and underlays, generate form appearances, flatten annotations,
  and process grayscale/RGB JPEG images with a caller-supplied codec.

## Example: reorder pages

Reference the `Lokad.qpdf` package in a .NET 10 or .NET 11 application. This example copies
pages 2 and 1, in that order, from a PDF with at least two pages:

```csharp
using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;

using var host = new InMemoryQpdfHost();
var input = QpdfPath.From("input.pdf");
var output = QpdfPath.From("output.pdf");
host.AddInput(input, await File.ReadAllBytesAsync("input.pdf"), QpdfInputOwnership.Transfer);

PdfPageSelectionResult result = await Qpdf.SelectPageAsync(
    host, input, output,
    PdfPageSelectionOptions.FromPages([2, 1]),
    CancellationToken.None);

if (!host.TryGetOutput(output, out byte[] bytes))
{
    throw new InvalidOperationException("No PDF was produced.");
}

await File.WriteAllBytesAsync("reordered.pdf", bytes);
Console.WriteLine($"Wrote {result.PageCount} pages.");
```

Implement `IQpdfHost` to connect file or cloud storage. Operations take
cancellation tokens and configurable limits and return warnings and counters.
`IQpdfMemoryHost` lets a host share a memory budget with the engine.
[Resource and ownership APIs](docs/MEMORY.md)

Use `Qpdf.ExecuteAsync` for a `QpdfCommandInvocation` or `Qpdf.ExecuteJobAsync`
for job JSON.

For authenticated reading of a supported password-encrypted input:

```csharp
await using QpdfDocument document = await QpdfDocument.OpenAsync(
    host, input, QpdfPassword.FromText("reader"), QpdfLimits.Default,
    CancellationToken.None);
Console.WriteLine(document.PageCount);
Console.WriteLine(document.Encryption); // Non-secret facts only.
```

Omitting credentials tries an empty password. Keep borrowed `FromBytes`
credentials stable until the operation completes. This selection uses bounded
repair and publishes an unencrypted copy:

```csharp
await Qpdf.SelectPagesAsync(host,
    [new PdfPageSource(input, new PdfPageSelectionOptions(1), QpdfPassword.FromText("reader"))],
    output, new PdfPageMergeOptions
    {
        RecoveryMode = QpdfRecoveryMode.BoundedRepair,
        OutputEncryption = QpdfOutputEncryption.Remove
    }, CancellationToken.None);
```

Typed page operations preserve supported input protection by default. Each
`PdfPageSource` accepts its own credential. Typed reading defaults to strict
mode. See the supported profile for recovery, output encryption and secondary
source policies. [Supported profile](docs/SUPPORTED-PROFILE.md)

For result-only JSON, call
`Qpdf.GetDocumentJsonAsync(host, input, new QpdfJsonOptions(2, ["pages"]), token)`.
Supply a stream prefix when publishing sidecars from a result-only call.
[JSON export](docs/SUPPORTED-PROFILE.md#json-export) ·
[JSON import and update](docs/SUPPORTED-PROFILE.md#json-import-and-update)

JPEG decoding and recompression use the optional `IQpdfJpegCodec` interface.
Supply your implementation with `new InMemoryQpdfHost(QpdfLimits.Default, codec)`;
custom hosts expose it through `IQpdfJpegCodecHost`. Existing JPEGs can pass through
unchanged without a codec. See the [codec contract](docs/MEMORY.md#jpeg-codecs)
for buffer ownership, cancellation and resource admission.

## Limitations

New encryption is AES-256 R6; legacy encryption can be read and preserved.
Rendering, OCR, text extraction and general PDF authoring are outside scope.
Derived PDFs retain stored signature appearances and invalidate signatures.
[Signed-input policy](docs/SUPPORTED-PROFILE.md#signed-inputs-and-exclusions)

CMYK/YCCK JPEG decoding requires a host codec with `SupportsCmykDecoding`;
four-component recompression and Zopfli compression are unavailable. See the [supported profile](docs/SUPPORTED-PROFILE.md) for details.

## Build from source

Use the .NET 11 SDK pinned in `global.json`, with the .NET 10
SDK installed for its targeting packs and runtime:

```sh
dotnet restore Lokad.Qpdf.slnx --locked-mode
dotnet build Lokad.Qpdf.slnx -c Release --no-restore
dotnet test Lokad.Qpdf.slnx -c Release --no-build --no-restore
dotnet pack src/Lokad.Qpdf/Lokad.Qpdf.csproj -c Release --no-build --no-restore
```

The package is written to `artifacts/nuget`. Ordinary builds and tests require no
native qpdf or other external PDF tools. See the contributor checks and the
full reference and visual commands.
[Build and checks](https://github.com/Lokad/qpdf/blob/master/AGENTS.md#build-and-checks) ·
[Reference and visual checks](docs/COMPATIBILITY.md#how-compatibility-is-verified)

- [Compatibility](docs/COMPATIBILITY.md): supported operations and caller-facing differences.
- [Benchmarks](docs/BENCHMARKS.md) covers methodology, validation, memory admission and dated evidence.
- [Architecture](docs/ARCHITECTURE.md): components, storage boundaries and resource ownership.
- [Release history](CHANGELOG.md).

Licensed under the [MIT license](LICENSE.txt).

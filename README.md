# Lokad.qpdf

Lokad.qpdf is a .NET 10 library for splitting, merging, reordering, checking and
rewriting PDF documents. It implements qpdf's non-cryptographic operations in
managed C#, with no native qpdf installation required at runtime.
The core package has no runtime NuGet dependencies.

Use typed C# methods, qpdf-style arguments or job JSON from your application.
The library is currently prerelease and targets compatibility with qpdf 12.4.2;
see [compatibility](docs/COMPATIBILITY.md) for supported behavior and differences.

## What you can do

- Extract, reorder, duplicate, merge, split and rotate pages.
- Inspect document structure, check PDFs, and manage attachments and metadata.
- Rewrite PDFs with compression, object streams and linearization for web delivery.
- Apply overlays and underlays, generate form appearances, flatten annotations,
  and process grayscale/RGB JPEG images with a caller-supplied codec.

## Example: reorder pages

Reference the `Lokad.qpdf` package in a .NET 10 application. This example copies
pages 2 and 1, in that order, from a PDF with at least two pages:

```csharp
using Lokad.Qpdf;
using Lokad.Qpdf.Hosting;

var host = new InMemoryQpdfHost();
var input = QpdfPath.From("input.pdf");
var output = QpdfPath.From("output.pdf");
host.AddInput(input, await File.ReadAllBytesAsync("input.pdf"), QpdfInputOwnership.Copy);

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

The included host keeps PDFs in memory; your application handles file or cloud
storage. Implement `IQpdfHost` to connect other storage directly. Operations accept
cancellation tokens and configurable limits for input/output size, estimated memory
and decoded bytes. Results include warnings and resource counters.

Hosts can expose a shared memory budget through `IQpdfMemoryHost`; the standalone
owned-read helper also accepts that budget directly. See
[cooperative memory admission](docs/MEMORY.md) for caller-scope ownership and the
current boundary between engine estimates and host-private storage.

For existing qpdf workflows, `Qpdf.ExecuteAsync` accepts a `QpdfCommandInvocation`,
and `Qpdf.ExecuteJobAsync` accepts job JSON.

JPEG decoding and recompression use the optional `IQpdfJpegCodec` interface.
Supply your implementation with `new InMemoryQpdfHost(QpdfLimits.Default, codec)`;
custom hosts expose it through `IQpdfJpegCodecHost`. Existing JPEGs can pass through
unchanged without a codec. See the [codec contract](src/Lokad.Qpdf/Public/IQpdfJpegCodec.cs)
for buffer ownership, cancellation and resource admission.

## Limitations

Encrypted PDFs, passwords, encryption/decryption and signature processing are
unsupported. Rendering, OCR and text extraction are outside the library's scope.
CMYK/YCCK JPEG images remain opaque when decoding is requested; Zopfli compression
is unavailable. See the [supported profile](docs/SUPPORTED-PROFILE.md) for details.

## Build from source

Use the .NET SDK pinned in `global.json`:

```sh
dotnet restore Lokad.Qpdf.slnx --locked-mode
dotnet build Lokad.Qpdf.slnx -c Release --no-restore
dotnet test Lokad.Qpdf.slnx -c Release --no-build --no-restore
dotnet pack src/Lokad.Qpdf/Lokad.Qpdf.csproj -c Release --no-build --no-restore
```

The package is written to `artifacts/nuget`. Ordinary builds and tests require no
native qpdf or other external PDF tools. Contributor checks are listed in
[AGENTS.md](AGENTS.md); reference checks use [the pinned-tool verifier](tools/bootstrap-qpdf.ps1).
CI also runs cold/batch probe, protocol and raster self-tests, plus deterministic
warmup-readiness tests. These require no native PDF tools and measure no performance.

- [Compatibility](docs/COMPATIBILITY.md): supported operations and caller-facing differences.
- [Benchmarks](docs/BENCHMARKS.md): dated measurements, methodology and allocation/admission tools.
- [Architecture](ARCHITECTURE.md): components, storage boundaries and resource ownership.

Licensed under the [MIT license](LICENSE.txt).

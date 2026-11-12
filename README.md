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
- Extract previews from signed, unencrypted PDFs, retaining stored signature marks.
- Inspect document structure, check PDFs, and manage attachments and metadata.
- Open AES-256 R6 inputs through `QpdfDocument`, using user or owner credentials.
- Rewrite PDFs with compression, object streams and linearization for web delivery.
- Apply overlays and underlays, generate form appearances, flatten annotations,
  and process grayscale/RGB JPEG images with a caller-supplied codec.

## Example: reorder pages

Reference the `Lokad.qpdf` package in a .NET 10 or .NET 11 application. This example copies
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
`QpdfInputReader.ReadExactlyToArrayAsync` helper also accepts that budget directly.
Custom inputs fill caller-owned `Memory<byte>` and return a count.
[API migrations](docs/COMPATIBILITY.md#resource-api-and-behavior-changes) describe
the input and codec changes. See [cooperative memory admission](docs/MEMORY.md) for caller-scope ownership and the
current boundary between engine estimates and host-private storage.

For existing qpdf workflows, `Qpdf.ExecuteAsync` accepts a `QpdfCommandInvocation`,
and `Qpdf.ExecuteJobAsync` accepts job JSON.

For authenticated reading of an AES-256 R6 input:

```csharp
await using QpdfDocument document = await QpdfDocument.OpenAsync(
    host, input, QpdfPassword.FromText("reader"), QpdfLimits.Default,
    CancellationToken.None);
Console.WriteLine(document.PageCount);
Console.WriteLine(document.Encryption); // Non-secret facts only.
```

`FromText` uses UTF-8 without normalization; `FromBytes` borrows exact bytes.
R6 uses the first 127 bytes. Keep borrowed bytes stable until opening completes.
Omitting credentials tries an empty password. `QpdfSecurityException.Reason`
distinguishes a required password, an invalid supplied password and an unsupported
handler; malformed parameters throw `QpdfParseException`.

JPEG decoding and recompression use the optional `IQpdfJpegCodec` interface.
Supply your implementation with `new InMemoryQpdfHost(QpdfLimits.Default, codec)`;
custom hosts expose it through `IQpdfJpegCodecHost`. Existing JPEGs can pass through
unchanged without a codec. See the [codec contract](docs/MEMORY.md#jpeg-codecs)
for buffer ownership, cancellation and resource admission.

## Limitations

Encrypted transformations, output encryption and argv/job password options remain
unsupported; authenticated opening currently supports R6 only. Rendering, OCR and
text extraction are outside the library's scope.
Copying or rewriting signed, unencrypted PDFs preserves stored annotation appearances
and disables the active signature fields and certification restrictions. A derived
PDF does not preserve signature validity. Blank signature fields are also disabled;
read-only inspection retains source values. Signing and signature validation are excluded.
CMYK/YCCK JPEG images remain opaque when decoding is requested; Zopfli compression
is unavailable. See the [supported profile](docs/SUPPORTED-PROFILE.md) for details.

## Build from source

Use the .NET 11 SDK pinned in `global.json` (currently RC 3), with the .NET 10
SDK installed for its targeting packs and runtime:

```sh
dotnet restore Lokad.Qpdf.slnx --locked-mode
dotnet build Lokad.Qpdf.slnx -c Release --no-restore
dotnet test Lokad.Qpdf.slnx -c Release --no-build --no-restore
dotnet pack src/Lokad.Qpdf/Lokad.Qpdf.csproj -c Release --no-build --no-restore
```

The package is written to `artifacts/nuget`. Ordinary builds and tests require no
native qpdf or other external PDF tools. Contributor checks are listed in
[architecture](docs/ARCHITECTURE.md#development); reference checks use
`tools/bootstrap-qpdf.ps1`.
CI also runs cold/batch probe, protocol and raster self-tests, oracle version and
timeout cleanup controls, deterministic warmup-readiness tests and benchmark worker
cleanup tests. These use small controlled executables and Python children,
require no native PDF tools and measure no performance.

- [Compatibility](docs/COMPATIBILITY.md): supported operations and caller-facing differences.
- [Benchmarks](docs/BENCHMARKS.md): dated measurements, methodology and allocation/admission tools.
- [Architecture](docs/ARCHITECTURE.md): components, storage boundaries and resource ownership.

Licensed under the [MIT license](LICENSE.txt).

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
[Resource APIs](docs/COMPATIBILITY.md#resource-apis) describe
the input and codec contracts. See [cooperative memory admission](docs/MEMORY.md) for caller-scope ownership and the
current boundary between engine estimates and host-private storage.

For existing qpdf workflows, `Qpdf.ExecuteAsync` accepts a `QpdfCommandInvocation`,
and `Qpdf.ExecuteJobAsync` accepts job JSON.

For authenticated reading of a supported password-encrypted input:

```csharp
await using QpdfDocument document = await QpdfDocument.OpenAsync(
    host, input, QpdfPassword.FromText("reader"), QpdfLimits.Default,
    CancellationToken.None);
Console.WriteLine(document.PageCount);
Console.WriteLine(document.Encryption); // Non-secret facts only.
```

Omitting credentials tries an empty password. `QpdfPassword.FromText` uses UTF-8
with bounded encoding recovery; `FromBytes` borrows exact bytes. Keep borrowed
credentials stable until the operation completes. Authentication failures have
explicit reasons; passwords and keys are excluded from reports.

Typed page operations preserve supported input protection by default. Pass
`QpdfOutputEncryption.Remove` to decrypt, `CreateAes256` for fresh R6 protection,
or `CopyFrom` to use an independently authenticated template. Multi-source
selection accepts a credential on each `PdfPageSource`; the first source controls
default output protection. Each split output requesting fresh encryption gets
its own key.

```csharp
await Qpdf.SelectPagesAsync(host,
    [new PdfPageSource(input, new PdfPageSelectionOptions(1), QpdfPassword.FromText("reader"))],
    output, new PdfPageMergeOptions
    {
        RecoveryMode = QpdfRecoveryMode.BoundedRepair,
        OutputEncryption = QpdfOutputEncryption.Remove
    }, CancellationToken.None);
```

Typed reading and selection are strict by default. Explicit bounded repair uses
the shared reader and returns recovery warnings; the merge option applies to
all sources and encryption templates. Repeated logical paths share one input
and require consistent credentials.

Checks, inspections, attachments, placements and JSON operations also accept
credentials. Argv uses `--password=reader` or a host-backed `--password-file`;
job JSON uses `password` or `passwordFile`. PDF transformations preserve input
protection unless an output policy such as `--decrypt` or `--encrypt` is chosen.
See the [supported profile](docs/SUPPORTED-PROFILE.md) for exact APIs, source
credentials, encoding recovery, JSON encryption policies and security reporting.

JPEG decoding and recompression use the optional `IQpdfJpegCodec` interface.
Supply your implementation with `new InMemoryQpdfHost(QpdfLimits.Default, codec)`;
custom hosts expose it through `IQpdfJpegCodecHost`. Existing JPEGs can pass through
unchanged without a codec. See the [codec contract](docs/MEMORY.md#jpeg-codecs)
for buffer ownership, cancellation and resource admission.

## Limitations

New encryption is AES-256 R6; legacy encryption can be read and preserved.
Rendering, OCR, text extraction and general PDF authoring are outside scope.
Copying or rewriting supported signed PDFs preserves stored annotation appearances
and disables the active signature fields and certification restrictions. A derived
PDF does not preserve signature validity. Blank signature fields are also disabled;
read-only inspection retains source values. Signing and signature validation are excluded.
CMYK/YCCK JPEG decoding requires a host codec with `SupportsCmykDecoding`;
four-component recompression and Zopfli compression are unavailable. See the [supported profile](docs/SUPPORTED-PROFILE.md) for details.

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

- [Compatibility](docs/COMPATIBILITY.md): supported operations and caller-facing differences.
- [Benchmarks](docs/BENCHMARKS.md): dated measurements, methodology and allocation/admission tools.
- [Architecture](docs/ARCHITECTURE.md): components, storage boundaries and resource ownership.

Licensed under the [MIT license](LICENSE.txt).

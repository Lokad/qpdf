# Changelog

## Unreleased — 0.2.0-dev.7

### PDF JSON and inspection

- Create, patch and split PDFs from qpdf JSON. Command options include stream
  preparation, packing, QDF, linearization, protection and in-place updates.
  Bound graphs can be checked or reported without producing a PDF.
  [JSON import and update](docs/SUPPORTED-PROFILE.md#json-import-and-update)
- Assemble and collate pages for JSON export. Apply supported page, image,
  catalog and attachment edits before reporting or publication.
  [Inspection profile](docs/SUPPORTED-PROFILE.md#inspection-profile) ·
  [JSON export](docs/SUPPORTED-PROFILE.md#json-export)
- Resolve exact object number and generation with
  `QpdfDocument.GetObject(PdfReference)`. Return typed JSON in memory,
  normalize filtered stream output and support named binary extraction.
  Protect consumed inputs and split targets from publication collisions.
  [Publication rules](docs/SUPPORTED-PROFILE.md#governance)

### Page and document behavior

- Give repeated pages independent headers and widget registrations. Maintain
  inherited attributes, labels and supported navigation through page edits.
- Offer scoped option and topic help. Layered configuration follows effective
  setter order. Attachment metadata and page-label options retain their
  documented defaults. [Entry paths](docs/SUPPORTED-PROFILE.md#entry-paths)
- Support direct outline roots in linearized output, process valid leading
  Identity Crypt stages during copying, and allow host codecs to decode CMYK/YCCK
  JPEGs into raw CMYK samples. [Compatibility](docs/COMPATIBILITY.md)

### Password security and typed migration

- Open Standard password-encrypted PDF revisions R2–R6. Preserve or remove
  supported protection, create AES-256 R6 output, or copy an authenticated
  template. Each source accepts its own credentials.
- Preserve stored signature appearances in derived PDFs. Publication
  invalidates signatures and disables active signature fields and restrictions.
- Add bounded repair options for typed opening, previews, selection, merging,
  splitting and attachment operations. Typed page counting traverses and
  validates the page tree. Attachment listing returns warnings and counters.
- Remove the `QpdfLimits.MaxHostOperations` setting. `HostOperations` continues
  to report actual I/O. Memory, handle, structural and decoded-byte limits
  remain available.

## 0.1.0 (2026-11-05)

First release published on NuGet.

- Target .NET 10 and .NET 11; pin the .NET 11 RC 3 SDK and use .NET 11 for
  the performance baseline. Package and public-consumer checks cover both targets.
- Provide managed qpdf 12.4.2 structural operations through shared typed, argv
  and job-JSON entry points: reading/recovery, inspection, page assembly,
  attachments, content transforms, QDF and two-pass linearization.
- Keep the core free of runtime NuGet dependencies. Hosts supply JPEG codecs,
  storage, descriptors, environment values and transactional publication.
- Add cooperative memory admission with coarse execution/result ownership,
  independent cancellation, structural/decompression limits and bounded diagnostics.
  Input reads fill caller-owned buffers; codecs borrow the active memory budget.
  Remove `QpdfLimits.MaxWork`, `QpdfExecutionCounters.TotalWork` and
  `QpdfJpegCodecContext.ChargeWork`; replace codec ReserveMemory/ResizeMemory hooks
  with `MemoryBudget`, without compatibility wrappers.
- Preserve primary-document metadata and attachments independently of selected
  pages. Keep raw PDF values and private aliases; apply explicit edits afterward.
- Preserve shared graph identities, field registrations, source resources and
  local action/layer meaning through selection and placement. Correct annotation
  geometry, appearance clipping and retained navigation after rotation baking.
- Make PDF JSON v2 byte-reversible; validate imported graphs before publication.
  Report malformed data, unsupported features, resource refusals and host faults
  through their existing distinct failure categories.
- Keep crypto/signatures excluded, CMYK/YCCK decoding opaque and Zopfli unavailable.
  Rotation baking explicitly refuses retained destinations whose prior-viewer or
  quarter-turn axis-fit state cannot be preserved.

See the [supported profile](docs/SUPPORTED-PROFILE.md),
[native differences and API migrations](docs/COMPATIBILITY.md), and
[memory/host contract](docs/MEMORY.md) for the current caller-facing contract.

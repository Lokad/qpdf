# Changelog

## Unreleased — 0.2.0-dev.7

### PDF JSON

- Create and update PDFs with command compression, QDF, object streams,
  linearization and output protection. Typed imports can select packing and
  linearization. Commands support in-place updates and opt-in retention of
  detached objects. [JSON import and update](docs/SUPPORTED-PROFILE.md#json-import-and-update)
- Split authored or patched pages with inherited attributes, continued
  labels and separate widget registrations for repeated pages. Capture
  the first linearization pass for whole PDFs or split parts.
- Check bound JSON and return listings without writing a PDF. Coalesce
  page contents before reporting or publication.
- Apply image transforms to authored and patched graphs in writers and
  reports. Bound writers can normalize page programs and prune split resources.
- Assemble and collate pages for JSON export, including empty primaries.
  Export applies resource pruning, image optimization, content coalescing
  and inline-image externalization.
- Remove metadata, AcroForm, structure and page labels during JSON export
  and import. Commands can author labels in effective page order.
- Return typed JSON without publishing a report. File sidecars require a prefix
  when there is no report target. Typed document opening, checking and JSON
  inspection can opt into bounded repair. [JSON export](docs/SUPPORTED-PROFILE.md#json-export)

### Inspection and publication

- Resolve exact generation pairs with `QpdfDocument.GetObject(PdfReference)`.
  Resolve named outline targets in both JSON versions. Form JSON uses field-local
  alternative and mapping names, and reports depth exhaustion before output.
- Preserve raw image filter descriptors with decoding disabled. Normalize
  filtered stream bytes when returning them or publishing to a host target.
- Support direct outline roots in linearized output and JSON writer packing.
  Check physical outline-hint ownership and required hints.
- Preserve ordinary graph streams tagged `/Type /XRef` during protected
  publication and accept valid leading Identity Crypt filters.
- Protect acquired input identities from output and first-pass capture
  collisions, including expanded split targets. [Publication rules](docs/SUPPORTED-PROFILE.md#governance)
- Allow bounded repair in typed attachment APIs and return warnings and counters
  from listing. Commands can list and extract attachments from one authenticated input.

### Command and graph behavior

- Resolve effective job settings after duplicate properties, with encoding
  checks and ordered includes. Empty filename declarations retain argv slots
  without choosing a file. Endpoint-free odd/even ranges work on zero pages.
- Empty attachment key and filename setters use the payload basename. Empty
  prefixes/descriptions clear earlier values. Validate authored MIME and
  timestamps before acquisition. Existing stored metadata remains readable.
- Accept omitted page-label starts and PDF whitespace inside ASCII85 terminal
  markers.
- Combine static View layer states with ON/OFF bases. Keep independent layer
  identities and refuse unsupported combinations before publication.
- Bound page-range validation and diagnostic previews.

### Password security and typed migration

- Open Standard password-encrypted PDF revisions R2–R6. Preserve or remove
  supported protection, create AES-256 R6 output, or copy an authenticated
  template. Each source accepts its own credentials.
- Preserve stored signature appearances in derived PDFs. Publication
  invalidates signatures and disables active signature fields and restrictions.
- Add bounded repair options for typed opening, previews, selection, merging
  and splitting. Typed page counting traverses and validates the page tree.
- Allow host codecs to decode CMYK/YCCK JPEGs into raw CMYK samples.
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

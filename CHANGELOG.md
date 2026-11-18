# Changelog

## Unreleased — 0.2.0-dev.7

### PDF JSON

- Create and update PDFs with command compression, QDF, object streams,
  linearization and output protection. Typed layout options select packing and
  linearization. Packing and QDF comments retain matching original pairs.
- Split an authored or patched page tree, retaining inherited page attributes,
  label continuation and independent widget registrations. Capture the first
  linearization pass for whole PDFs or split parts.
- Honor `--preserve-unreferenced` during command JSON creation and update.
  Commands prune detached objects by default. Opt-in retention includes split
  parts and preserves independently referenced source roles.
- Check bound JSON graphs and issue follow-up listings without a PDF output.
  Update a PDF in place with `--replace-input` / `replaceInput`. The source lease
  closes before staging, and auxiliary inputs are protected from replacement.
- Bind patches by object number and generation. Honor declared page preparation
  for direct or repeated pages and inherited attributes. Omitted payloads reuse
  data from the exact source stream. Apply the command parser limits to imports.
- Assemble and collate pages for JSON export, including empty primaries. Retain
  labels, source version requirements, secondary context and independently rooted
  values. Support per-source resource pruning, image optimization, coalescing
  and inline-image externalization with the ordinary content policies.
- Apply document metadata, AcroForm, structure and label removals during JSON
  export and import. Commands can author labels in effective page order. Command
  imports can coalesce contents. `--remove-info` retains a live ModDate if present.
- Return typed JSON without publishing a report. File sidecars require a prefix
  when there is no report target. Typed document opening, checking and JSON
  inspection can opt into bounded repair.

### Inspection and publication

- Resolve exact generation pairs with `QpdfDocument.GetObject(PdfReference)`.
  Resolve named outline targets in both JSON versions. Form JSON uses field-local
  alternative and mapping names, and reports depth exhaustion before output.
- Preserve raw image filter descriptors with decoding disabled. Normalize
  filtered stream bytes when returning them or publishing to a host target.
- Support direct outline roots in linearized output and JSON writer packing.
  Check physical outline-hint ownership and required hints.
- Preserve ordinary graph streams tagged `/Type /XRef` during protected
  publication. Normalize valid leading Identity Crypt stages before content
  processing and protection. Physical xref sections remain clear.
- Keep omitted primary inputs safe from expanded split output names. Protect
  templates, password files and consumed configuration from first-pass capture.
- Check stable host identities for PDF, JSON and binary outputs and first-pass
  captures before staging to protect acquired inputs and other targets.
- Share typed attachment recovery options across listing, extraction and edits.
  List with warnings and counters. Close extraction inputs before committing
  plaintext bytes to a host target. Command attachment reports and extraction
  can share one authenticated input.

### Command and graph behavior

- Resolve effective job settings after duplicate properties, with encoding
  checks and ordered includes. Empty filename declarations retain argv slots
  without choosing a file. Endpoint-free odd/even ranges work on zero pages.
- Empty attachment key and filename setters use the payload basename. Empty
  prefixes/descriptions clear earlier values. Validate authored MIME and
  timestamps before acquisition. Existing stored metadata remains readable.
- Accept omitted page-label starts and PDF whitespace inside ASCII85 terminal
  markers. Share literal escaping across parsing, normalization and writing.
- Combine static View layer states with ON/OFF bases. Keep independent layer
  identities and refuse unsupported combinations before publication.
- Bound range validation and diagnostics. Reuse work in page, label, attachment,
  placement, destination, resource-pruning and inline-image processing while
  retaining occurrence-local results and private aliases.

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

[Earlier development candidate notes](https://github.com/Lokad/qpdf/blob/cfd0da84948dab518a0cb1c0936cc94a4b51d9aa/CHANGELOG.md)

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

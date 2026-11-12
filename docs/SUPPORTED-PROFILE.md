# Supported profile

Lokad.qpdf targets qpdf 12.4.2's structural operations.
The [option inventory](../tests/Compatibility/options.json) records exact forms,
combinations and executable evidence; [compatibility](COMPATIBILITY.md) records
defaults and intentional differences. Accepted syntax alone does not establish support.

`QpdfDocument.OpenAsync` supports AES-128 R4 and AES-256 R6 with user or owner
credentials, including empty passwords. UTF-8 text is not normalized; R4 retries
PDFDocEncoding when representable. Raw bytes are borrowed until opening completes
and never reinterpreted. R4 uses the first 32 bytes and R6 uses 127. Missing credentials try
empty; a failed absent password is distinct from a failed supplied password.
Encryption facts expose permissions and authentication status without passwords
or keys. Typed `SelectPageAsync` authenticates with the same password values and
preserves R4/R6 protection, both passwords, permissions and metadata policy. It
supports ordered/repeated pages, first/last previews and rotations. Missing
credentials try empty. An explicit `QpdfOutputEncryption` policy can preserve,
remove or replace protection with fresh AES-256 R6. Creation uses UTF-8 or exact
password bytes, fresh keys/salts/IVs, the requested permission word and metadata
policy. Reserved permission bits and accessibility must have their R6 values;
nonempty user passwords require nonempty owner passwords.
Typed `SelectPagesAsync` accepts credentials on each `PdfPageSource`. The first
source controls preserved output protection, even if collation omits its pages;
secondary sources cannot replace it. `PdfPageMergeOptions.OutputEncryption`
sets an explicit policy, including for split output. Newly encrypted split outputs
use separate keys. Repeated paths share one opened document and require equal
credential values and representations, including absent versus explicit passwords;
conflicts fail before opening inputs. RC4 inputs and R5 remain unsupported.

Typed `Qpdf.CheckAsync` accepts the same credentials in `QpdfCheckOptions.Password`
and runs structural checks over the decrypted graph. Its existing skipped-stage
reporting and partial-check contract still apply.
Argv/job inspections and PDF transformations accept a primary text password through
`--password`/`password`, or the first line of a host file through
`--password-file`/`passwordFile`. Command/job secondary credentials remain unsupported.
Missing and supplied-but-invalid credentials have distinct diagnostic codes.
PDF rewrites, page selection/merging/splitting, object streams, QDF, linearization
and attachment edits preserve supported primary protection, including when none
of its pages is selected. A forced version incompatible with that protection fails
before publication. Typed attachment edits use the same path for empty-password
inputs. Explicit command output policies and encrypted JSON updates remain
unsupported; secondary command credentials and password descriptors remain pending.
File setters retain native order: a zero-byte file leaves the previous password
unchanged, while a blank first line supplies an empty password. Spaces are retained;
CR is stripped only before LF, and NUL terminates the password. Additional lines
produce a non-secret informational note without changing the exit code. File bytes
stay in bounded owned storage and are cleared after the operation. Descriptor input
(`--password-file=-`) is unavailable in this host profile.
`--suppress-password-recovery` / `suppressPasswordRecovery` disables the R4
PDFDocEncoding retry for primary inspection and JSON credentials. It leaves raw
bytes, absent passwords and successful first attempts unchanged.
Input `--password-mode` / `passwordMode` accepts `auto`, `bytes`, `unicode` and
`hex-bytes`. As in native qpdf, the first three leave input decoding unchanged.
Hex mode ignores non-hex characters, pads an odd nibble and ends at the first
decoded NUL; supported encoding recovery still applies unless suppressed.
Typed raw-byte credentials always remain exact and never take this recovery path.

`--is-encrypted` returns 0 for supported encrypted inputs and 2 for plain inputs.
`--requires-password` returns 0 when credentials do not match, 2 for plain inputs,
and 3 when the supplied or empty password matches. Job JSON uses `isEncrypted`
and `requiresPassword`. These mutually exclusive predicates return `Completed`
with no ordinary output; `--warning-exit-0` does not alter their query codes.
Malformed or unsupported security handlers fail explicitly.

`--show-encryption` / `showEncryption` reports declared permissions, cipher methods
and password-match status for supported handlers. Incorrect credentials still allow
this report and skip combined content inspections; the report does not validate
encrypted payloads. Passwords and keys are never included, even when native qpdf
would print a recovered password. Explicit output paths are rejected.

JSON inspection accepts the same primary credential, including raw bytes through
`QpdfJsonOptions.Password`. Both JSON versions report encryption facts and export
the decrypted graph; selected stream data is decrypted before decoding or sidecar
publication. `recovereduserpassword` and encryption `key` stay null. JSON with
an `/Encrypt` trailer entry still cannot be imported as a new PDF.

Signed, unencrypted pages can be copied, merged, placed and rewritten.
Stored signature appearances survive; active signature field attributes and document
certification restrictions are disabled, including blank signature fields. Derived
output does not preserve signature validity. Read-only inspection retains source
values. Signing and signature validation are excluded. Rendering, OCR, text
extraction/layout, general authoring and PDF/A/X/UA conformance are outside scope.

## Entry paths

Typed operations, `Qpdf.ExecuteAsync` argv and `Qpdf.ExecuteJobAsync` job JSON share
the engine. Hosts supply inputs, outputs, descriptors and environment values;
fileless inspection returns text or bytes. The core has no runtime NuGet dependencies.

Typed reading is strict and selection preserves encoded streams. Argv/job defaults
to bounded repair and generalized decoding/compression. Argv setters follow argument
order; job setters follow sorted-key order, including nested files. Unsupported
options and combinations fail explicitly. Execution reports exit 0/2/3 and diagnostics.

## Input profile

| Area | Support |
| --- | --- |
| Versions | PDF 1.0–1.7 and 2.0, header at offset zero; effective header/catalog version negotiation. |
| Xrefs | Classic, streamed, hybrid and incremental chains; compressed objects, generations and extents validate. |
| Values | Byte-preserving names, strings and numbers; signed 64-bit integers; bounded malformed-input recovery. |
| Streams | Declared binary extents; bounded repair where enabled. External-file descriptors stay opaque and are never opened. |
| Filters | Flate/LZW with predictors, ASCIIHex/ASCII85 and RunLength. Gray/RGB DCT decoding requires a host codec. Zopfli is unavailable. |

Unavailable optional decoding can warn and preserve encoded bytes. Structural
budget exhaustion remains a resource failure, never permission to discard retained data.

## Selection semantics

- Select ordered 1-based pages, duplicates, FirstAndLast windows or Normalized
  sets; merge, collate, rotate, label and split. Explicit empty/out-of-range
  requests reject. Range exclusions affect their preceding positive group.
- The primary owns document metadata and attachments even when none of its pages
  is selected. Secondary global collections require explicit import; page-reachable
  data copies normally. Explicit empty primary differs from a real zero-page input.
- Rebuild page ancestry, preserve shared identities/cycles and give repeated
  pages/widgets independent headers. Omitted page targets become null. Explicit
  labels apply in assembled order before splitting; unchanged whole-file labels stay raw.
- Bind retained source resources, local actions, destinations and static View
  optional-content context before cleanup. Unsupported context combinations fail
  before publication. Private aliases keep their original values.

## Page content profile

Supported transforms include coalescing, normalization, conservative resource
pruning, inline-image externalization, overlay/underlay, appearance generation,
annotation flattening and rotation baking. Contents arrays form one logical
program; unavailable fragments preserve original content and report incomplete coverage.

Stored annotation geometry and supported appearances follow page frames, UserUnit
and NoRotate anchors. Imported static appearances clip to the source page.
Form generation uses source defaults/resources; missing usable fonts preserve
appearances and prevent unsupported flattening. General interactive appearance
generation and malformed-field parity are not claimed.

Rotation baking updates retained destinations. Affected nullable positions and
quarter-turn H/V/BH/BV fits fail UnsupportedFeature before publication when their
viewer-dependent meaning cannot be preserved; see [navigation differences](COMPATIBILITY.md#known-differences-from-native-qpdf).

Image optimization handles eligible 8-bit DeviceGray/DeviceRGB images through a
host JPEG codec and keeps recompressed output only when smaller. Explicit quality
enables DCT recompression. CMYK/YCCK decoding and color-key-mask optimization remain unavailable.

## Inspection profile

Checking covers structure, streams, content, forms and linearization, with explicit
skipped stages. It does not establish rendering or conformance. Object, xref, page,
image, attachment and outline listings use the shared resolver.

PDF JSON v1/v2 preserve reference identities; v2 uses binary strings whenever text
would change bytes. Stream data can be omitted, inline or host sidecars. V2
import/update accepts generation-zero declarations, validates page structure and
replaces dangling references with null. Page/content/image/layout transforms and
stream/metadata writer controls are unavailable during import/update.

## Writer profile

The writer supports stream compression, object-stream preserve/generate/disable
modes, version/extension negotiation, deterministic/static IDs, metadata controls,
QDF and two-pass linearization. Explicit forced versions win without transcoding
opaque features. Object streams require PDF 1.5; catalog XML metadata is uncompressed.

`--remove-info` removes Info and catalog XML metadata; `--remove-metadata` removes
only XML metadata. Independently rooted originals remain intact. Final-root
reachability retires replaced graphs; explicit unreferenced preservation excludes
old catalog/page/container structure. Numbering and compressed bytes may differ from native.

## Attachments profile

Raw name-tree keys and filespec graphs are authoritative; listings are projections.
List/extract/add/remove/copy share typed, argv and job implementations.

- Match decoded ordinal keys; sort output by encoded bytes. Full-collection
  operations reject duplicate logical keys; selective extraction uses the first match.
- Remove before additions, then imports. Missing keys and collisions without
  replacement reject. Removed/replaced graphs survive only through independent roots
  or explicit unreferenced preservation. Split retains the final attachment collection.
- New keys/filenames/prefixes must be nonempty; authored text must be valid UTF-16
  and MIME names ASCII without NUL. Existing empty keys remain removable/extractable.
  Dates are omitted unless supplied; additions record size/checksum and a default basename.

## QDF and linearization profile

QDF provides editable objects, indirect lengths and optional original-ID comments.
Fix-QDF repairs consistent LF-framed, sequentially numbered input; ambiguous framing
rejects. Zlib-flate stops at the first complete frame and rejects truncation/checksum errors.

Linearization uses two passes and page/shared/outline hints. Checking validates
physical layout and available hints. First-pass capture must be distinct from
input and output targets; split capture keeps the last group's pass. Linearization
disables QDF; empty split jobs create no output.

## Governance

Limits cover input/output/decoded bytes, memory, filters, host operations/handles,
diagnostics, nesting and containers. Nesting is 1..499, default 256; filter default
is 25. Invocation settings cannot raise host ceilings, including under
`--no-default-limits`. Cancellation is cooperative and independent of memory
admission; there is no cumulative CPU fuel. Diagnostics have bounded actual storage
and explicit overflow. Recoverable malformed optional graphs can drop; profile,
resource, cancellation and host failures remain fatal.

Single-target publication is transactional. Multiple targets commit sequentially;
earlier successes survive later failure. A close fault cannot undo a commit.
Caller-owned results and pending I/O retain their required owners; see
[memory and host ownership](MEMORY.md).

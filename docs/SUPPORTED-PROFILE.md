# Supported profile

Lokad.qpdf targets qpdf 12.4.2's structural operations.
The [option inventory](../tests/Compatibility/options.json) records exact forms,
combinations and executable evidence; [compatibility](COMPATIBILITY.md) records
defaults and intentional differences. Accepted syntax alone does not establish support.

## Entry paths

Typed operations, `Qpdf.ExecuteAsync` argv and `Qpdf.ExecuteJobAsync` job JSON share
the engine. Hosts supply inputs, outputs, descriptors and environment values;
fileless inspection returns text or bytes. The core has no runtime NuGet dependencies.
Response arguments use one argument per UTF-8 line. `@-` reads the stable host input
keyed `-`; an unmapped descriptor fails through the host. Missing ordinary `@file`
inputs remain literal, and nested response tokens are not expanded again.

Typed selection preserves encoded streams. Argv/job defaults
to bounded repair and generalized decoding/compression. Argv setters follow argument
order; job setters follow sorted-key order, including nested files. Unsupported
options and combinations fail explicitly. Execution reports exit 0/2/3 and diagnostics.

Empty main filenames select no file; they do not imply `--empty`. Empty argv
arguments still occupy their input/output slots, including response-file blank
lines. Whitespace stays literal. Nonempty file declarations remain exclusive,
and an output declaration cannot follow active `--replace-input`.

Typed document opening, checking, counting, selection and JSON inspection are
strict by default. The full `QpdfDocument.OpenAsync` overload accepts credentials,
`QpdfRecoveryMode` and limits; `QpdfCheckOptions.RecoveryMode` and
`QpdfJsonOptions.RecoveryMode` expose the same opt-in bounded repair. Undefined
policies reject before host access. Repair preserves authentication and resource
limits and reports warnings on the document/result (check diagnostics).

## Input profile

| Area | Support |
| --- | --- |
| Versions | PDF 1.0–1.7 and 2.0, header at offset zero; effective header/catalog version negotiation. |
| Xrefs | Classic, streamed, hybrid and incremental chains; compressed objects, generations and extents validate. |
| Values | Byte-preserving names, strings and numbers; signed 64-bit integers; bounded malformed-input recovery. |
| Streams | Declared binary extents; bounded repair where enabled. External-file descriptors stay opaque and are never opened. |
| Filters | Flate/LZW with predictors, ASCIIHex/ASCII85 and RunLength. DCT decoding uses a [host JPEG codec](#jpeg-codec-capabilities). Zopfli is unavailable. |

Bounded xref repair can rebuild a table with a zero-entry subsection.

Empty `DecodeParms` arrays select the filters' defaults.
ASCII85 inline-image framing accepts PDF whitespace inside the `~>` end marker,
matching stream decoding while leaving subsequent content outside the packet.
Copying consumes a valid leading default/Identity Crypt stage without changing
payload bytes; remaining filters and independent private aliases retain their
meaning. JSON-authored Identity stages also support explicit output encryption.
Unavailable optional decoding can warn and preserve encoded bytes. Structural
budget exhaustion remains a resource failure, never permission to discard retained data.

## Password security

### Authentication

`QpdfDocument.OpenAsync` supports RC4 R2/R3, 128-bit RC4/AES R4 and AES-256 R5/R6
with user or owner credentials, including empty passwords. Authentication tries
the supplied representation first, consuming up to 32 bytes for R2–R4 or 127
for R5/R6. UTF-8 text is not normalized.
Missing credentials try empty; failed absent and supplied passwords are distinct.

Failed text authentication can retry PDFDoc, WinAnsi and MacRoman encodings when
the bounded prefix is representable. Command byte credentials can convert among
UTF-8/PDFDoc/WinAnsi/MacRoman or from well-formed BOM-marked UTF-16 to the single-byte
encodings. Recovery tries at most nine distinct alternatives from a source prefix
of at most 512 bytes, with the same revision-specific authentication limits.
Typed raw bytes remain borrowed until the operation completes and are never
reinterpreted by recovery.

Standalone `--show-crypto` reports `managed` through the result diagnostics; it takes no other arguments or job JSON key.
Encryption facts expose permissions and authentication status without passwords
or keys. Typed `SelectPageAsync` authenticates with the same password values and
preserves R2–R6 protection, both passwords, permissions and metadata policy. It
supports ordered/repeated pages, first/last previews and rotations.
Overloads with an explicit `QpdfRecoveryMode` permit bounded
syntax/cross-reference repair and return its warnings; existing overloads remain
strict. Both policies preserve opaque stream data and stored appearances. Missing
credentials try empty. An explicit `QpdfOutputEncryption` policy can preserve,
remove or replace protection with fresh AES-256 R6. Creation uses UTF-8 or exact
password bytes, fresh keys/salts/IVs, the requested permission word and metadata
policy. Reserved permission bits and accessibility must have their R6 values;
nonempty user passwords require nonempty owner passwords.
Bounded repair can reanchor an intact terminal cross-reference stream after
damaged footer offsets, using ordinary section/coverage validation to preserve
compressed objects and encryption metadata. Later objects or sections prevent
using an older stream. Resource/cancellation failures abort without weaker repair.

### Page sources and output protection

Typed `SelectPagesAsync` accepts credentials on each `PdfPageSource`. The first
source controls preserved output protection, even if collation omits its pages;
secondary sources cannot replace it. `PdfPageMergeOptions.OutputEncryption`
sets an explicit policy, including for split output. Newly encrypted split outputs
use separate keys. Repeated paths share one opened document and require equal
credential values and representations, including absent versus explicit passwords;
conflicts fail before opening inputs. R2 uses 40-bit RC4; R3 accepts 40..128-bit keys in eight-bit steps.
`PdfPageMergeOptions.RecoveryMode` defaults to Strict; BoundedRepair applies one
policy to all source documents and any output-encryption template, returns repair
warnings and retains the same source identity, credential and publication rules.
Preserved/copied R2/R3 output keeps RC4 and requires PDF 1.3/1.4 respectively.
Preserved/copied R4 output uses AES-128 and requires PDF 1.6, including RC4 inputs.
Newly selected weak encryption remains unsupported.
Existing R5 AES-256 inputs can be inspected, transformed, preserved or copied as
templates. Preservation keeps R5 and requires PDF 1.7 extension level 3; new
encryption continues to use R6. No fresh R5 creation is exposed.

### Inspection and secondary sources

Typed `Qpdf.CheckAsync` accepts the same credentials in `QpdfCheckOptions.Password`
and runs structural checks over the decrypted graph. Its existing skipped-stage
reporting and partial-check contract still apply.
Argv/job inspections and PDF transformations accept a primary text password through
`--password`/`password`, or the first line of a host file through
`--password-file`/`passwordFile`. Page sources accept `--password=...` after their
file specification inside `--pages`, or `pages[].password` in job JSON. This applies
to PDF transformations and transformed inspections, including empty-password inputs.
Global password-mode and recovery settings apply to these credentials. A password
specified on any occurrence applies to all occurrences of that file; contradictory
nonempty setters reject before opening inputs. Primary-page password setters are ignored
because the primary document uses its main credential. With an empty primary,
secondary protection is removed unless an explicit new output policy is requested.
Missing and supplied-but-invalid credentials have distinct diagnostic codes.
PDF rewrites, page selection/merging/splitting, object streams, QDF, linearization
and attachment edits preserve supported primary protection, including when none
of its pages is selected. A forced version incompatible with that protection fails
before publication. Typed attachment listing/extraction accepts primary credentials
with explicit limits; extraction publishes plaintext attachment bytes. Typed
add/remove/copy edits accept primary credentials and an explicit output policy.
Their existing overloads try empty passwords and preserve supported protection.
Argv/job attachment and object-stream extraction also accept primary credentials
when an explicit output path is supplied. Extracted payloads are plaintext;
raw stream mode retains compression, while filtered mode decodes it.
`--decrypt` / `decrypt` explicitly removes protection after authentication,
including from split outputs. Output-free inspections ignore this output policy.
`--encrypt user owner 256 [options] --` creates fresh AES-256 R6 protection, as
does the named form `--encrypt --user-password=... --owner-password=... --bits=256 --`.
The job equivalent is `"encrypt":{"userPassword":"...","ownerPassword":"...","256bit":{}}`.
Print, modification, extraction, annotation, form and assembly permissions and
cleartext metadata are supported; accessibility remains enabled for R6. Argv
permission setters retain their order; job JSON applies keys in lexical order.
Encryption/decryption setters replace the preceding output policy. Split outputs
receive independent keys, including with an empty primary document. New encryption
cannot combine with deterministic IDs. Legacy creation, force-R5 and allow-insecure
remain unsupported.
Attachment-copy sources accept independent text credentials inside their argv/job
blocks, including the global password mode and recovery settings. Typed
`QpdfAttachmentCopy` accepts `QpdfPassword` text/raw credentials. These readers close
after copying their plaintext graph; the primary's output policy protects the
result. A source credential is required independently even when the copy source
names the primary file. Missing credentials try empty passwords.
Overlay/underlay argv blocks and job entries also accept independent passwords,
including the global mode and recovery settings. The same file can use different
valid credentials in separate placements; each distinct credential authenticates
before its graph is reused. A missing placement credential does not inherit the
primary password. Publication and transformed inspections share this reader path.
Typed page selection, merging and splitting accept
`QpdfOutputEncryption.CopyFrom(template, password)`. The host authenticates this
independent R2–R6 input with either password, then copies both password entries,
permissions, metadata policy and the first identifier. Its reader closes before
publication; split outputs retain the template key. A plaintext template removes
input protection. Template passwords may be text, raw bytes or absent for empty.
Argv/job operations use the same policy through `--copy-encryption=file` /
`copyEncryption`, with `--encryption-file-password=text` / `encryptionFilePassword`.
The credential follows global password-mode and recovery settings. Copy, decrypt
and encrypt setters replace one another in argv order or job lexical-key order.
Output-free inspections do not open the template. Copied encryption cannot combine
with deterministic IDs or an insufficient forced version; it never falls back to
plaintext after such a failure.
### Authenticated JSON updates and creation

Typed `UpdatePdfFromJsonAsync` accepts an input password and the same explicit
preserve/remove/create/template output policies. Its existing overloads try an
empty password and preserve supported protection. JSON object patches supply
plaintext strings and stream data, including catalog metadata; protection is
applied after final numbering. Full exports may repeat the authenticated input's
unchanged `/Encrypt` reference and dictionary. Preservation rejects handler edits;
use an explicit remove/create/template policy to define different output protection.
Argv/job updates use `--update-from-json` / `updateFromJson` with primary text or
host-file credentials, including password modes and recovery settings. Preserve,
decrypt, encrypt and template policies use the same version and metadata rules.
Typed `CreatePdfFromJsonAsync` accepts an output policy with explicit limits.
Plaintext JSON can produce new AES-256 or copied R2–R6 protection, including the
chosen metadata policy. Creation has no authenticated PDF input: Preserve produces
plaintext only when JSON has no `/Encrypt` declaration. Full encrypted exports
require an explicit remove/create/template policy, which replaces that declaration.
The writer raises the PDF version when required by protection.
Argv/job `--json-input` / `jsonInput` accepts the same output policies and global
password modes. Primary text passwords are ignored for plaintext JSON; password
files are still read through the host, and missing files fail. Template inputs
use the operation's recovery mode and their independent credential.
### Command password files and modes

File setters retain native order: a zero-byte file leaves the previous password
unchanged, while a blank first line supplies an empty password. Spaces are retained;
CR is stripped only before LF, and NUL terminates the password. Additional lines
produce a non-secret informational note without changing the exit code. File bytes
use [owned password storage](MEMORY.md#engine-guarantees-and-limits). For descriptor
input (`--password-file=-`), the host opens the logical path `"-"` as a stable
`IQpdfInput` lease with the same limits and first-line semantics. The host owns
descriptor acquisition and any snapshot storage; core never reads ambient stdin.
An unmapped descriptor fails through the host before opening the PDF.
`--suppress-password-recovery` / `suppressPasswordRecovery` disables the
[authentication retries](#authentication) across password-bearing operations;
it changes neither the supplied representation nor successful first attempts.
Input `--password-mode` / `passwordMode` accepts `auto`, `bytes`, `unicode` and
`hex-bytes`. As in native qpdf, the first three leave input decoding unchanged.
Hex mode ignores non-hex characters, pads an odd nibble and ends at the first
decoded NUL; the authentication recovery policy still applies unless suppressed.
For new R6 output, auto, bytes and unicode encode the managed text as UTF-8;
hex-bytes uses the same bounded hex decoder. Both output passwords use this mode.

### Reports and predicates

`--is-encrypted` returns 0 for supported encrypted inputs and 2 for plain inputs.
`--requires-password` returns 0 when credentials do not match, 2 for plain inputs,
and 3 when the supplied or empty password matches. Job JSON uses `isEncrypted`
and `requiresPassword`. These mutually exclusive predicates return `Completed`
with no ordinary output; `--warning-exit-0` does not alter their query codes.
Predicates take precedence over other inspections and ignore `--remove-restrictions`;
acceptance does not establish restriction removal.
Malformed or unsupported security handlers fail explicitly.

`--show-encryption` / `showEncryption` reports declared permissions, cipher methods
and password-match status for supported handlers. Incorrect credentials still allow
this report and skip combined content inspections; the report does not validate
encrypted payloads. Passwords and keys are never included, even when native qpdf
would print a recovered password. Explicit output paths are rejected.

JSON inspection accepts the same primary credential, including raw bytes through
`QpdfJsonOptions.Password`. Both JSON versions report encryption facts and export
the decrypted graph; selected stream data is decrypted before decoding or sidecar
publication. `recovereduserpassword` and encryption `key` stay null.

### Signed inputs and exclusions

Signed pages can be copied, merged, placed and rewritten after authenticating
encrypted sources. Publication unregisters signature fields, including blank
fields, and turns their widgets into static stamps with their stored
appearance. It removes active signature types, values, defaults, seed
constraints, locks and calculation-order entries, catalog `/Perms` and
AcroForm `/SigFlags`. Independent private aliases retain their stored data.
Publication invalidates signatures. Signing and validation are outside scope.

`--remove-restrictions` / `removeRestrictions` explicitly selects this default
PDF publication policy. Non-predicate inspections and JSON exports reject the
modifier. Read-only inspection keeps source values. [Predicate precedence](#reports-and-predicates)

## Selection semantics

`SelectPageAsync` accepts `QpdfRecoveryMode.BoundedRepair` and reports repair
in `PdfPageSelectionResult.Warnings`.
The full overload accepts rotations, input credentials and the output protection
policy. `PdfPageMergeOptions.RecoveryMode` applies to all sources and templates.

- Typed callers can select explicit 1-based pages, first/last windows,
  normalized sets, or a `PdfPageSelectionOptions.FromRange` expression such
  as `z-1:odd`. Typed ranges and command ranges share syntax and resolve
  against the source page count. Exclusions affect their preceding positive
  group. Explicit page lists require at least one page. Ranges can select
  zero pages, including endpoint-free `:odd` or `:even` on an empty input.
  Invalid or out-of-range endpoints fail.
- The primary owns document metadata and attachments even when none of its pages
  is selected. Secondary global collections require explicit import; page-reachable
  data copies normally. Explicit empty primary differs from a real zero-page input.
- Rebuild page ancestry, preserve shared identities/cycles and give repeated
  pages/widgets independent headers. Omitted page targets become null. Explicit
  labels apply in assembled order before splitting; unchanged whole-file labels stay raw.
  Label starts default to 1 even with an empty slash field: `1:D//A-` numbers
  pages with prefix A-, while `1://Cover` supplies a prefix without numbering.
- Bind retained source resources, local actions, destinations and static View
  optional-content context before cleanup. Unsupported context combinations fail
  before publication. Private aliases keep their original values.

Combined layer contexts interpret static ON/OFF bases and their member arrays,
with explicit OFF membership taking precedence. Output uses an ON base and an
equivalent OFF list; same-named groups retain separate identities. OFF bases in
the default configuration are tolerated input, outside the
[specified ON-base requirement](https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/PDF32000_2008.pdf)
(Table 101). Combining Unchanged bases, automatic states, alternate configurations
or non-View intents rejects; a single retained configuration keeps its existing policy.

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

### JPEG codec capabilities

Image optimization uses a host JPEG codec for eligible 8-bit
DeviceGray/DeviceRGB images and keeps smaller candidates. An explicit quality
from 0 to 100 enables DCT recompression. Encoder quality is codec-specific.
CMYK/YCCK recompression and color-key-mask optimization remain unavailable.

A codec can opt into four-component decoding with `SupportsCmykDecoding`.
It returns tightly packed C/M/Y/K samples, converts YCCK using JPEG markers,
and applies no Adobe sample inversion, ICC conversion, PDF Decode array or
orientation. The writer retains the PDF color space and Decode array.
Existing codecs default to no four-component capability; unsupported decoding
preserves opaque streams with an explicit warning or skipped check.
[Codec ownership](MEMORY.md#jpeg-codecs)

## Inspection profile

Checking covers structure, streams, content, forms and linearization, with explicit
skipped stages. It does not establish rendering or conformance. Object, xref, page,
image, attachment and outline listings use the shared resolver.
Argv and job JSON can inspect a graph created from JSON, updated from a PDF,
or created and then patched in the same job. PDF updates authenticate the
source before binding the patch.
Page counts, pages with images, objects and attachments use the bound graph
and its declared generations. Encryption reports describe the authenticated
source. Attachment extraction returns bytes or commits
them to an explicit host output target after closing the inputs.
Optional `--check` validates the effective page tree, reachable streams, page
content and forms before listing or extraction. It reports physical header,
xref-offset and linearization checks as skipped. A failed check stops later
inspections. Page edits and physical-layout listings are unavailable here.

Filtered stream inspection accepts normalization for returned bytes or an explicit
host output target. Raw extraction retains its encoded bytes.
Image filter/parameter descriptors retain stored aliases, live references and
array shapes. With decoding disabled they project raw specifications without
building or validating a decoder plan; input and output limits still apply.
Image filterability checks decode level, filter count and supported parameters;
it does not validate encoded frames or establish host JPEG capability.
Label JSON supplies default starts and per-page counters without changing source
dictionaries.
Outline projections resolve catalog names, destination name trees and `/D`
wrappers, including local GoTo actions. Missing names project null; destination
page positions validate the complete object number/generation pair.

Typed `Qpdf.CountPagesAsync` traverses the page tree and returns its count,
warnings and final counters after input cleanup. Its credential, recovery and
limits overload can request bounded repair. Standalone counts can omit large
unrelated payloads in compact classic-xref inputs. Other layouts, protected
inputs and uncertain validation use complete acquisition on the same lease.
Memory refusal remains a failure. Document opening and combined inspection
retain their separate complete-input contracts.

### JSON export

Commands can combine JSON export with `--json-input` or
`--update-from-json`. The report describes the bound graph and uses its
declared pairs in descriptors, object keys and filters. A create-and-patch
job can export the result directly. Physical updates authenticate before
binding. The report and its sidecars protect every consumed input.
Page, page-label, outline and form reports assign separate object identities
to direct and repeated page occurrences and report the repair. An object-table
report alone preserves the existing page tree.
When exported objects share a number across generations, nonzero generations
add a suffix to the sidecar name to keep the files distinct.

Argv/job JSON inspection accepts page-source assembly and collation, including
an empty primary. Page descriptors and object exports share the assembled graph;
labels, secondary actions/forms/layers and source version requirements survive
export and recreation. The primary retains catalog and security policy.
Page-source JSON applies the same per-source no/yes/auto resource-pruning policy
as command PDF selection, retaining independent private resource bindings.
Coalescing, inline-image externalization and eligible image optimization,
including nested Forms, share the ordinary content owners before JSON projection.
Optimization uses the caller's JPEG codec, thresholds, quality and inline policy;
it retains originals when candidates are not smaller or encoding declines.
Prepared direct and repeated pages remain linked from the visible tree.
Source generations and independent aliases retain their meaning. Inherited
resource pushdown is reported only when performed.
JSON export applies Info, XML metadata, AcroForm, structure and page-label
removals to the active roles. Independent aliases retain their original values.
Authored labels replace removed or existing labels in the final page order.
Other form and frame transforms remain unavailable during JSON export.

Typed `GetDocumentJsonAsync` returns JSON. An explicit output path also
publishes the report. None and inline stream modes can return reports without
output capability. File mode requires a stream prefix when the report has no
target, including in v1. Set `QpdfJsonOptions.DecodeLevel` to choose the filter
decode depth. An omitted level uses generalized decoding for full reports
and leaves streams encoded in minimal reports. Caller budget scopes must
outlive returned text and warnings. [Publication checks](#governance)

`--test-json-schema` checks generated syntax and the selected version's nested
help description: required/unknown keys, wildcard maps and array patterns.
Selected keys and minimal output determine the required root fields. Descriptive
leaves admit arbitrary PDF values; this is not primitive-value or JSON Schema
validation. Deep PDF/outline syntax remains bounded, and PDF model limits apply.

PDF JSON v1/v2 preserve reference identities; v2 uses binary strings whenever text
would change bytes. Object-table projection turns dead array references into null
and omits null-valued dictionary fields. Live references to null objects retain
their identity in arrays; stored descriptor fields keep their own projection rules.
V1 emits stream dictionaries and creates no sidecars, including when inline
or file settings are supplied. V2 can omit stream payloads, include them, or
write host sidecars.

## JSON import and update

PDF JSON v2 creates PDFs with `--json-input` or `CreatePdfFromJsonAsync` and
patches existing PDFs with `--update-from-json` or `UpdatePdfFromJsonAsync`.
Commands also accept `jsonInput` and `updateFromJson` in job JSON.
Combining them applies the patch to the JSON-created graph before publication.

### Binding and limits

Creation and update bind complete object number and generation pairs before
assigning output numbers. JSON edges can refer to live source pairs and
objects declared in either stage of a combined job. Original PDF edges to
stale, free or missing pairs remain null even if a patch assigns that pair.
A stream patch without payload data borrows bytes from its exact source
pair. Sidecars can share a read by logical path while retaining separate
stream dictionaries. The bound graph validates the page tree and resolves
dangling references to null.

Updates apply v2 traversal and inheritance metadata before binding the patch.
Direct children gain identities, repeated leaf pages receive independent
headers, and inheritance uses the nearest non-null value. Declared preparation
rejects repeated Pages nodes and shared Kids arrays. Creation validates the
flags and starts from the declared tree.

`MaxContainerSize` bounds imported PDF containers, including headers and the
trailer. Dictionary counts use decoded names and the parser's duplicate-pair
rule. Unknown JSON envelope fields are outside that ceiling.
`--parser-max-container-size` / `global.parserMaxContainerSize` can tighten
the container limit. Command nesting settings bound imported values and
the update's source. Parser error,
damaged-container and recovery settings apply to the update's source PDF.
JSON-authored values use strict parsing. Host ceilings apply with
`--no-default-limits` as well.

### Output options

Command creation and update share the writer's compression, decode level,
packing, QDF normalization, linearization, first-pass capture and protection
options. Streams over the filter limit stay opaque with a warning. Stream
preparation precedes encryption. Typed imports retain encoded payloads and
default to flat output. Overloads taking `QpdfJsonImportOptions` select
packing and linearization. Commands default to preserving eligible source
packing, and creation starts without source members. See the
[writer](#writer-profile), [layout](#qdf-and-linearization-profile) and
[protection](#authenticated-json-updates-and-creation) policies.

Commands apply document removal flags after binding. Authored page labels
replace the active tree in bound page order, including when removal is requested.
Independent aliases retain their values. Commands publish catalog and Info
reachability by default. `--preserve-unreferenced` also retains eligible
detached declarations and source objects. Superseded page-tree, security and
physical layout roles require a retained edge. Typed imports retain detached
declarations.

Command imports can coalesce page contents before stream encoding. Shared
arrays and streams remain available to private references. An edited direct
page gains an indirect identity. An undecodable chunk keeps the original
array and reports a warning. Other page, content, image and attachment edits
remain unavailable during import and update.

### Splitting and replacement

`--split-pages` / `splitPages` partitions the bound page order. Parts
inherit page attributes, continue labels and replace references to foreign
pages with null. Repeated pages get separate widget registrations. Explicit
unreferenced preservation includes eligible detached roots in every part.
Naming, capture and publication follow the ordinary split rules. Fresh
protection uses a separate key for each part.

`--replace-input` / `replaceInput` updates a PDF in place. The source lease
closes before staging and the host commits to the source key. Named output
and typed updates require a distinct target. Splitting is unavailable with
replacement or inspection. [Publication checks](#governance)

## Writer profile

The writer supports stream compression, object-stream preserve/generate/disable
modes, version/extension negotiation, deterministic/static IDs, metadata controls,
QDF and two-pass linearization. Explicit forced versions win without transcoding
opaque features. A forced version below 1.5 disables object streams. Catalog
XML metadata is uncompressed.

`--remove-info` retains only an existing, resolved non-null Info ModDate and removes catalog
XML metadata; absent, null, missing and stale-generation dates omit Info without
an optional-metadata warning. Live indirect dates retain their reference identity.
`--remove-metadata` alone removes
only XML metadata and retains Info. Independently rooted originals remain intact. Final-root
reachability retires replaced graphs; explicit unreferenced preservation excludes
old catalog/page/container structure. Numbering and compressed bytes may differ from native.

## Attachments profile

Raw name-tree keys and filespec graphs are authoritative; listings are projections.
Attachment JSON lists stream entries under every `/EF` name, sorted by raw name
bytes. Filename preference uses UF/F/Unix/DOS/Mac. Null values, non-stream values
and optional entries with unreadable syntax are omitted.
List/extract/add/remove/copy share typed, argv and job implementations.
The full typed overloads accept `QpdfRecoveryMode` for primary, copy-source and
encryption-template inputs. Typed listing with explicit recovery returns immutable
metadata, warnings and closed-handle counters in `QpdfAttachmentListResult`.
Command listing, reports and extraction share one authenticated document.
Targeted extraction uses the same inspection validation and reports as returned
payloads; JSON inspection/import/update cannot be combined with it. Input cleanup
finishes before targeted payload publication, with backing admitted through commit.

- Match decoded ordinal keys; sort output by encoded bytes. Full-collection
  operations reject duplicate logical keys; selective extraction uses the first match.
- Remove before additions, then imports. Missing keys and collisions without
  replacement reject. Removed/replaced graphs survive only through independent roots
  or explicit unreferenced preservation. Split retains the final attachment collection.
- Empty command key/filename setters independently restore the payload basename;
  typed authored names must be nonempty. Empty copy prefixes and descriptions
  mean omission and clear earlier command values. Authored text must be valid UTF-16
  and MIME names ASCII without NUL. Existing empty keys remain removable/extractable.
  Dates are omitted unless supplied; additions record size/checksum and a default basename.

Command MIME setters require `/`; `/`, `type/`, `/subtype` and `text/a/b` are valid.
Explicit empty values reject; typed null/empty means omission. Argv validates
each setter, while duplicate job properties use the last value.

Authored dates use `D:YYYYMMDDHHmmSS`, optionally followed by `Z` or a signed
`HH'mm'` offset. Validation checks syntax, including ASCII digits, rather than
calendar ranges. Typed null/empty dates mean omission; explicit empty command
setters reject. Existing stored dates remain readable and survive copying.
Authoring rejects embedded NUL; native job JSON can truncate the timestamp at NUL.

## QDF and linearization profile

QDF provides editable objects, indirect lengths and optional original-ID comments.
Command JSON imports preserve the declared number and generation in those
comments. `--no-original-object-ids` suppresses them. QDF normalizes imported
page streams by their shared identities by default. Resource-dependent framing
across incompatible contexts keeps the original program and warns.
Fix-QDF repairs consistent LF-framed, sequentially numbered input; ambiguous framing
rejects. Zlib-flate stops at the first complete frame and rejects truncation/checksum errors.

Linearization uses two passes and page, shared and outline hints. Checking
validates physical layout and available hints. `--linearize-pass1` /
`linearizePass1` captures the first pass when linearization is enabled.
Split capture keeps the last group's pass. Linearization takes precedence
over QDF. [Publication checks](#governance)
The linearized writer keeps the outline closure outside object streams so its
hints describe one contiguous physical group; other eligible objects still pack.
Direct outline dictionaries are promoted before physical planning, including
JSON creation/update; unrelated direct catalog values retain their representation.

## Governance

Limits cover input/output/decoded bytes, memory, filters, open handles,
diagnostics, nesting and containers. Nesting is 1..499, default 256; filter default
is 25. Invocation settings cannot raise host ceilings, including under
`--no-default-limits`. Cancellation is cooperative and independent of memory
admission; there is no cumulative CPU fuel. Diagnostics have bounded actual storage
and explicit overflow. Recoverable malformed optional graphs can drop; profile,
resource, cancellation and host failures remain fatal.

Named PDF outputs, capture files, JSON reports and sidecars, and extracted
payloads must differ from acquired inputs and other targets. Early argument
checks use known logical paths. Split targets are expanded and checked after
the output page count is known. Before staging, the engine probes existing
readable target identities without requesting payload bytes. JSON reports
and sidecars probe after closing their input leases. A write-only or absent
target can report `Unsupported` or `NotFound`.

Explicit replacement may reuse the designated primary PDF acquisition.
Configuration, patch, sidecar, password-file and template inputs remain
protected, including after their leases close.

Single-target publication is transactional. Multiple targets commit sequentially.
Split preparation finishes before the first commit. Empty split jobs produce
no files. Earlier successes survive later failure and post-commit close faults.
Caller-owned results and pending I/O retain their required owners; see
[memory and host ownership](MEMORY.md).

# Architecture

This describes the intended implementation. At the scaffold checkpoint the
library builds and packs but has no PDF execution code or public API.

Three entry points will converge on one operation plan: familiar qpdf argv,
qpdf job JSON, and typed operations including bounded page selection. The
operation plan will use one binary reader, object graph, filter pipeline and
writer. PDF object JSON and job JSON are different formats.

The host owns stable input identities and ranged reads, output staging and
publication, scratch storage, descriptors and any supplied environment values.
The library owns PDF semantics, explicit results and per-job memory/work limits.
No runtime path gains filesystem, process or network authority implicitly.

Begin with strict reading, opaque stream preservation and a small complete
read/select/write path. Extend this same engine to the full page-selection
contract before adding recovery or advanced transformations. Forms and graph
identity precede general cross-document imports. Content analysis precedes
resource pruning. A stable writer and replay model precede QDF and linearization.

Memory safety includes both safe managed operations and resource governance.
Validate lengths/offsets and checked arithmetic before allocating; bound work,
decompression, graphs and cancellation intervals. ImageSharp and BCL codecs
need explicit allocation/input/output policies; managed code alone is not an
exhaustion limit.

The initial solution contains a packable library project and a test project
referencing its built assembly. Add a dedicated public API consumer, probe and
benchmarks when their operations exist. Do not create duplicate engines or
empty project layers solely to match the eventual architecture.

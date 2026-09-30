# Development tooling

Use the build/test/pack commands in the root README. The solution is .NET 10,
with central package versions and checked-in lock files. Only ImageSharp
3.1.12 is allowed as a production dependency; its closed NuGet version range
`[3.1.12]` also prevents the produced package from selecting a different version.
Test packages are only allowed in test projects. SourceLink is private build
tooling. Debug packaging and unsafe compilation are rejected by build targets.

SourceLink is enabled when an actual repository URL is supplied, including
GitHub Actions' repository metadata. The initial local repository has no remote,
so local SourceLink/source-control queries are disabled. Do not invent a URL.
Untracked-source embedding is disabled, and compiler document paths are mapped
to a stable root. Inspect both `.nupkg` and `.snupkg` with the provided script.

## Optional reference checkout and metadata regeneration

Neither the checkout nor Python is needed by ordinary builds or tests.
For explicit metadata regeneration only:

```sh
git clone --branch v12.4.2 --depth 1 https://github.com/qpdf/qpdf.git external/qpdf
python -m pip install -r tools/compatibility-requirements.txt
python tools/update-compatibility.py --reference external/qpdf --output tests/Compatibility/options.json
```

The generator checks the commit and unchanged source inputs. It reads option
metadata, emits no upstream code, and preserves existing status/evidence for
matching option/context entries. Review the resulting diff before committing.
Regeneration must not turn partial coverage into a completion claim.

`external/` and the local handoff are ignored. Do not make restore, compile or
ordinary tests depend on them. Public generated metadata is intentionally
tracked so normal development remains independent of the reference checkout.

## Next tooling

The executable oracle/independent-renderer runner and codec feasibility probes
are not present yet. Add them as subsequent focused commits. Pin executable
versions, build features and checksums; demonstrate a deliberate failing
comparison as well as a successful smoke case. Keep these jobs separate from
ordinary tests and fail explicitly when required tools are unavailable.

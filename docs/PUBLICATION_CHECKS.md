# Publication checks — 27 September 2026

This source snapshot was selected from the author's supplied portable rebuild archive.

## Completed

- Selected the desktop source, offline UI, builder, and relevant check scripts into a separate publication directory.
- Excluded agent records, nested project copies, original build artifacts, binaries, personal settings, machine diagnostics, scratch work, and the offline vendor cache.
- Scanned the selected text files for local user paths and common credential/private-key patterns. No matches were found. Pattern scanning is not a formal security audit.
- Restored both pinned dependencies from NuGet in the isolated checkout.
- Built the x64 application using .NET Framework MSBuild. Build succeeded; the legacy ToolsVersion setting produced a toolset fallback notice.
- Ran the supplied automation structural verifier: passed.
- Ran three startup/exit smoke passes: passed after correcting the verifier to exclude runtime folders already present before each pass. The original timestamp filter had incorrectly counted another application's pre-existing runtime directory.
- Ran smoke processes with hidden windows. No application source behavior was changed for publication.

## Scope

These results confirm source packaging, compilation, structural checks, and startup/exit checks on the author's machine. They do not certify every hardware action, physical hotkey, firmware route, or third-party compatibility.

The repository is a source publication. No compiled public release or universal-device support claim is included.

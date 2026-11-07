# Agent Note: One metadata format without compatibility modes

Status: implemented

The colon-ID rejection in this historical decision is superseded by [resource ID keys](../bug-fix/2026-11-07-resource-id-keys.md). Existing complete keys are retained and matched verbatim without restoring compatibility modes.

## Problem

The user explicitly removes the old compatibility requirement. Keeping author/published detection, colon/path codecs, source-root fallback and old read entry points after that decision leaves two protocols inside the nominally shared Reader. It also duplicates tests that protect behavior no longer requested. Flat, Tree and Mixed must describe storage organization only.

## Decision

Use only `MetadataReader.Read(...).Root` for document reading and only canonical reversible path resource IDs (`scope/local`) in the normalized model and exports. Qualify every short resource ID, including a single document without Includes; preserve validated canonical path IDs and reject colon-qualified IDs. Keep Mod/Application business IDs unchanged. Remove `MetadataBuilder.Load`, `LoadSource`, `Open`, `MetadataFlattener`, compatibility detection, dual codecs, `ForceSourceTree`, `LegacySourcePaths`, `MetadataExportIdFormat`, `ExportLegacy`, `IdFormat`/`SourceBase` markers and `--id-format`. `MetadataId` owns PathPrefix, Qualify and LocalId; Parser remains the low-level XML-to-model projection, not an alternative reader.

Interpret Source relative to its defining document and expose it relative to the entry directory. SourceRoot supplies containment and qualification, never a second address fallback. HTTP documents cannot reference local files. Explicit resource URLs remain supported; Image.Url takes priority over informational Source. Include contributes actual full contents, including Manifest bodies. External Manifest Source stays external unless explicitly inlined. A short external stub uses the target definition scope; body resolution shares SourceRoot and requires exact canonical identity, never suffix/LocalId matching or alias rewriting. Preserve scoped reference precedence, permanent ambiguity, URI/path checks, cycles and deduplication. Reading preserves the declared schema/revision; Build/export set them explicitly.

Build uses normal Export and its existing validation/variable/image processing. Flat/Tree/Mixed, selective Include inlining and explicit manifest/resource processing remain real features. SourceExporter still creates short-ID local author declarations explicitly; that write operation is not a read compatibility mode. Cache uses Reader for every acquired XML and one normalized snapshot record, with no old-cache migration fallback. Preserve last-complete graphs, cache-only/Fresh/Stale behavior and raw-byte/validator pairing.

## File and test organization

Library files use Reading, Exporting, Building and existing Cache responsibility folders; models/query contracts remain at root. Tests use Reading, Exporting, Building, Cache and Queries. Namespaces and shared TestData placement stay unchanged. Meaningful old-entry-point cases are consolidated into current tests; obsolete-mode tests and fixed-stopwatch PerformanceTests are removed without dropping security/offline/domain coverage. The [active plan](../../../docs/exec-plans/2026-11-07-composable-metadata.md) owns the directory table and integration evidence.

## Related decision audit

The active proposed/implemented/rejected notes were searched for composition, local source, unified reading, source export and cache behavior before this proposal. No historical Decision is rewritten or archived.

| Prior note | Classification | Retained and superseded boundary |
|---|---|---|
| [Composable metadata](../../implemented/architecture/2026-11-07-composable-metadata.md) | Partially superseded; crosslinked | Keep one composition/layout core, captured graph and package APIs; overturn dual ID formats, marker/detection branches, compatibility read APIs and default legacy publication. Its verification applies only to that earlier contract. |
| [Unified reader](../../implemented/feature/2026-11-01-unified-metadata-reader.md) | Partial overlap | Keep unified reading and XML source abstraction; the old Open/strict Load split is removed. Historical rationale remains accessible through the composable decision. |
| [Local source loading](../../implemented/feature/2026-10-28-local-mod-source-load.md) | Partial overlap | Keep in-memory source reads and containment; remove LoadSource, source-root fallback and stub conversion. |
| [Source-format host API](../../implemented/feature/2026-10-31-source-format-host-api.md) | Partial overlap | Keep explicit short-ID author writes and manifest projection; replace LocalId matching with exact canonical identity. |
| [XML runtime](../../implemented/architecture/2026-10-04-metadata-xml-runtime.md), [cadence](../../implemented/architecture/2026-10-09-registered-resource-cache-cadence.md), [transfer](../../implemented/architecture/2026-10-12-metadata-transfer-to-local-file.md) | Partial overlap | Keep transfer ownership, cache-only display and last complete graph; no strict raw-flat reader branch or old snapshot migration protocol. |
| [Rejected cache framework](../../rejected/architecture/2026-10-02-runtime-metadata-cache.md) | Rejection remains valid | Do not add leases, stores or a migration framework. |

Model/style/language and deployment decisions are unrelated. No note is fully absorbed: earlier unique rationale and evidence remain historical, not current acceptance evidence.

## Alternatives considered

- **Keep thin compatibility delegates and two codecs**: reduces immediate caller churn and preserves old consumers. The user explicitly rejects that requirement; it retains obsolete surface and tests rather than simplifying the contract.
- **Infer author/published mode from Include or markers**: accommodates ambiguous old inputs. A single format removes that ambiguity instead of maintaining heuristics.
- **Delete layout or local author export together with legacy modes**: removes more code, but erases genuine requested storage and Desktop write features. Remove protocol choices, not those capabilities.

## Verification

The parent reports the final Metadata full suite at 429/429 passed with 0 warnings and 0 failures. Actual single-format Build, Tree export, all six Flat/Tree/Mixed × external/inline smoke combinations, and HTTP root 304/child update, complete-graph fallback and offline reopening passed. Security review found no remaining Image-only Url issue, and the semantic review findings were corrected with targeted coverage. This evidence is parent-reported and does not claim Desktop verification or GUI/game execution.

The final Exporter shape simplification uses only absent/empty Source to identify a full Manifest, matching Reader and cache even for empty bodies; the affected export checks passed 9/9 with no warnings. This targeted result is separate from, not added to, the full 429-test run.

## Consequences

This intentionally breaks colon-format and old API consumers; caches may start new. Exact external Manifest identity requires shared SourceRoot context, not a forgiving fallback. Flat/Tree/Mixed remain storage organization choices, while local author export remains an explicit write feature. Desktop dependency delivery and its own validation remain pending.

Against prior PR HEAD dcaa1d5, final tracked C# comparison excluding bin/obj (including blank/comment lines) observed production 43 files/7617 lines to 41/6941 (−676) and tests 36/11167 to 32/9504 (−1663), for 2339 fewer lines total. This is an observed consequence, not a target or acceptance threshold, and excludes documentation.

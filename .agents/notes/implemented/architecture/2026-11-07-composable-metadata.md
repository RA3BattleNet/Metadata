# Agent Note: One composition core with layout-only exports

Status: implemented

Partially superseded by [single metadata format](../../implemented/simplification/2026-11-07-single-metadata-format.md): the user removes dual-format detection, legacy read APIs/source behavior and default legacy publication. The shared composition/layout/cache decision remains relevant. The Decision and verification below describe the earlier contract and are not evidence for the new cutover.

## Problem

The earlier unified-reader entry point treated Include detection as a choice between author and published semantics. A single author document can contain inheritance or short resource IDs without Include; a generated split document can already contain canonical IDs. Guessing from Include, a short ID or SchemaVersion alone cannot distinguish these cases. Export and runtime consumers must not each reinvent qualification, inherited reference resolution or Source rebasing.

The refactor must simplify the existing implementation, not preserve an old flattener under a new reader and then add another exporter/cache resolver. Flattened and split documents express the same metadata; their difference is organization. Existing author files, schemas, business IDs, public model/query/image APIs and default legacy publication remain compatible.

## Decision

One Reader composition core serves Open, explicit legacy LoadSource, the public Flattener compatibility methods, and composed runtime roots. Load/Parser retain strict published-document boundaries. Required legacy public methods are thin delegates; superseded internal composition branches are removed.

The Reader returns a normalized legacy-ID model/document plus captured raw documents, Include edges and normalized own-node provenance. It owns composition, inheritance, typed reference resolution and Source normalization, but never writes output, evaluates variables or converts images. The cache fetches XML asynchronously through the existing transfer boundary and supplies a captured memory source; it does not implement ID resolution or inheritance.

Exporter consumes this result. One layout algorithm selects which Include edges remain external: Flat retains none, Tree retains the document organization, Mixed retains the selected subset. It does not parse and compose each layout independently. SourceExporter shares author-manifest element normalization without serialize/parse roundtrips. Default legacy Build uses the same output core through ExportLegacy without generated format markers; new tree/mixed export is a separate operation.

The old Flattener body and Open's separate IO branches are replaced by delegates to Reader. Build no longer owns a duplicate resource-copy/layout path. Composition, captured provenance and complete-result caching share the semantic core rather than adding independent parsers. Desktop's dependent removal of URI glue remains under its separate proposed adaptation until default-submodule verification.

The full file responsibilities, interfaces, six phases and acceptance matrix remain in [the implementation plan](../../../docs/exec-plans/2026-11-07-composable-metadata.md). Metadata implementation is verified; Desktop integration and dependency delivery remain pending and are not implied by this note's status.

## Identity and address contract

- Resource identity is `(logical document scope, local ID)`, compared OrdinalIgnoreCase. A single codec supports legacy `scope:local` and reversible path `scope/local` encodings. Keep existing `QualifyId`, `LocalId` and `PathPrefix` signatures/legacy results. Physical relocation does not redefine encoded scope.
- Document identity is separate: HTTP paths remain case-sensitive; local paths follow filesystem platform rules. Repeated documents deduplicate; cycles and distinct duplicate canonical registrations fail, and ambiguous aliases remain ambiguous. Reference resolution retains existing field-specific kind rules, including Icon support for Image or Markdown.
- Generated exports explicitly declare `IdFormat="path"` or `IdFormat="legacy"` and `SourceBase="document"`. Reject unknown declared formats. Do not modify author XML/XSDs. Path-format IDs decode to legacy canonical IDs in the ordinary model.
- Preserve unmarked old flat IDs and Source when no composition construct exists. Qualify author composition once, with explicit compatibility options where the input is otherwise ambiguous. Do not infer asset addresses from ID strings.
- Legacy unmarked flat HTTP documents retain existing file Source values for images/Markdown; author/normalized composition containment checks do not retroactively constrain that published-document contract. Local no-disk cached-leaf access uses Reader with an offline XML source to decode path IDs and expand local Includes, rather than strict Parser. Missing cached HTTP XML returns Unavailable instead of starting network access. These compatibility corrections remain within this same composition decision.
- The unified result exposes Source relative to the entry URI, while explicit legacy LoadSource preserves SourceRoot-relative Source and legacy manifest stub semantics. Default unmarked author-included full manifests become stubs; marked normalized full manifests remain inline regardless of organization. SourceRoot is a containment boundary, not a license to rewrite a published document's logical scope.
- Preserve Image/Markdown/Manifest Source and URL-only semantics across layouts. Check output collisions and traversal before writing. A flat input lacks original topology/comments; deterministic regrouping is not lossless restoration.

## Complete snapshots and package access

Snapshot/package Get/GetCached/Refresh operations coexist with compatible URI-oriented APIs. Inline manifests come from the captured root with null SourceUri and Status inherited from the snapshot in all three operations: a complete Fresh root permits Fresh inline data without a separate request, but Stale is never elevated. External cached-only access stays Stale. External registration resolution uses the same frozen snapshot and Source identity. Preload handles both forms without independently refreshing inline data or inventing URIs.

A successful runtime root represents a complete composed graph. Root 304 still revalidates included documents. A normalized root and minimal private graph metadata publish only after all composition inputs succeed; failure preserves the previous complete root and offline reopening uses no network. Public root/leaf/image URI mappings and image cadence remain unchanged. Raw validators stay associated with raw bytes, not normalized serialization. First flat-to-composition migration preflights graph paths and preserves the previous complete materialized snapshot before raw replacement. Default transfer disables redirects; injected handlers/custom transfers must do likewise because response checks cannot undo an already-sent redirect. No public transfer option or general store/lease/multiprocess framework is added.

## Related decision audit

The active implemented/rejected notes were searched for unified reading, local source, SourceExporter and cache behavior before proposing this note. No prior Decision is reversed in place; historical verification remains evidence for that earlier change, not this refactor.

| Prior note | Classification | Preserved boundary and change |
|---|---|---|
| [Unified reader](../../implemented/feature/2026-11-01-unified-metadata-reader.md) | Partial overlap; crosslinked | Keep unified Open, XML source abstraction, strict Load and containment. Replace Include-only semantic branching with explicit format-aware single composition. Preserve its historical decision and verification. |
| [Local source loading](../../implemented/feature/2026-10-28-local-mod-source-load.md) | Partial overlap; crosslinked | Keep pure in-memory author reading, explicit SourceRoot-relative compatibility and stubs. Share the core rather than a second LoadSource pipeline. |
| [Source-format host API](../../implemented/feature/2026-10-31-source-format-host-api.md) | Partial overlap; crosslinked | Keep library-owned short-ID author declarations, standalone manifest parsing and no claimed topology recovery. Reuse applicable core in the broader layout exporter. |
| [XML runtime cache](../../implemented/architecture/2026-10-04-metadata-xml-runtime.md) | Partial overlap; crosslinked | Keep transfer ownership, last-valid publication and snapshot-bound identity; extend roots to captured composed graphs and add package access. The old Include prohibition remains the historical strict-parser decision, not an instruction to reject Reader composition. |
| [Registered-resource cadence](../../implemented/architecture/2026-10-09-registered-resource-cache-cadence.md) | Partial overlap; crosslinked | Keep cache-only display, explicit refresh and independent image behavior. Add equivalent package access for inline manifests without network-freshness fiction. |
| [Transfer to local file](../../implemented/architecture/2026-10-12-metadata-transfer-to-local-file.md) | Partial overlap; crosslinked | Keep IMetadataTransfer, validators, staging, ownership and cancellation; graph capture uses this boundary rather than synchronous HTTP in the cache. |
| [Rejected runtime-cache framework](../../rejected/architecture/2026-10-02-runtime-metadata-cache.md) | Related rejection remains valid; crosslinked | Do not restore leases, multiversion object storage, multiprocess pointers or a MetadataRuntime framework. Minimal complete-result persistence is not that framework. |

Other notes found through shared words concern data model/language/style or deployment, not the composition decision. They are unchanged. None of the audited notes is fully absorbed or suitable for archival/deletion by this decision.

## Alternatives considered

- **Keep Include-only detection and extend each consumer**: smallest apparent reader change and no new captured-result contract. It cannot distinguish single-file author semantics from normalized split output, and spreads composition into exporter/cache/Desktop. Reject in favor of one semantic core.
- **Add Reader while retaining the old flattener**: initially minimizes risk to legacy calls. It creates two implementations whose ID, inheritance and Source behavior must remain synchronized indefinitely. Replace the implementation and delegate only required legacy public entry points.
- **Use three independent Flat/Tree/Mixed pipelines**: each layout is easy to reason about locally. This repeats parsing/resolution, makes Mixed a separate dialect, and contradicts organization-only flattening. Use retained-edge selection over one captured graph.
- **Rewrite all publication to the new tree/path format**: one output dialect would reduce long-term exporter branches. Existing binaries and published layout are required compatibility boundaries; retain legacy Build and make new composition export explicit.
- **Restore the rejected general cache framework**: could model graph generations and multi-process readers. The task needs the last complete result, not leases, stores, cleanup policy or another state abstraction; keep persistence minimal.

## Consequences

Flattening is an organization choice over one model rather than a second interpretation. Shared composition/layout eliminates independent ID and Source rules; captured provenance supports deterministic export without rereading disposed HTTP sources. Explicit format markers introduce an output dialect old binaries do not understand, so legacy Build stays unchanged and new Tree/Mixed artifacts require the Reader. Captured XML/provenance costs memory; flat input still cannot recover original comments/topology. The cache retains only the last complete result, not leases or multiversion storage. Broader storage/format options require a new demonstrated consumer need rather than extension for its own sake.

## Verification

The parent ran the full Metadata suite before final simplification: 442 passed, 0 failed, with one existing test CS8602 warning and no production warning. After simplification, the affected Reader/Exporter/Cache/Transfer checks passed 86/86 with 0 failures and 0 warnings. Following Desktop integration's compatibility corrections, the affected Reader/CompositionCache/MetadataClient checks passed 68/68 with 0 failures and 0 warnings, and the actual Metadata smoke passed again. These are three distinct runs, not counts to add together or a claim of a later full-suite run.

Actual legacy CLI Build succeeded from immutable repository data. Actual repository-tree export produced 53 XML documents; Reader reread confirmed 15 Mods, 3 Applications, 50 Image identities and all resource files. The external real smoke exercised six Flat/Tree/Mixed and inline false/true combinations plus HTTP root 304/child update, retained old snapshot, child-failure Stale fallback and cold-client offline package access. The parent reran the actual smoke and legacy CLI after simplification; both passed.

Security source re-review reported no remaining findings for the two identified vulnerabilities. Executed regression coverage includes redirect rejection and migration-preflight/cold-offline; it does not simulate a crash or mid-write IO failure. The parent previously ran docs generation/checks successfully and will refresh after lifecycle/link changes. Desktop build/service acceptance and pushed-submodule dependency delivery remain pending in the implementation plan.

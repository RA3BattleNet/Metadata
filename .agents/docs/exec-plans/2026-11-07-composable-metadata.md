# Single-format composable Metadata implementation plan

Status: Metadata single-format implementation verified; Desktop dependency integration and delivery pending.

## Scope and decision

The user explicitly removes all legacy compatibility. This plan follows the [single-format decision](../../notes/implemented/simplification/2026-11-07-single-metadata-format.md), which partially supersedes the [earlier composable decision](../../notes/implemented/architecture/2026-11-07-composable-metadata.md). Unified semantics with storage/organization/acquisition choices is the governing rule, not separate input dialects. Earlier test counts belong to the previous contract, not current acceptance. Original author Metadata/ XML/XSDs and unrelated code remain unchanged.

One Reader and one path-ID format serve all inputs and outputs. Flat/Tree/Mixed change organization only. Do not retain wrappers, format enums, marker detection, legacy publication or old cache migration paths. Keep real resource/layout processing and domain behavior. Delivery updates existing [Metadata PR #65](https://github.com/RA3BattleNet/Metadata/pull/65) and dependent [Desktop PR #182](https://github.com/RA3BattleNet/Desktop/pull/182); do not create replacement PRs or merge them.

## Shared interfaces and semantics

- `MetadataReader.Read(pathOrUrl, options).Root` is the document-reading entry point. The result also captures normalized Document, EntryPath, SourceRoot and Documents with raw/normalized own-node XML and Include edges. Result-owned XML is immutable by convention; export uses captured provenance, not a disposed HTTP source.
- Options retain SourceRoot, ValidateReferences, CustomSource and InlineManifests. Remove ForceSourceTree and LegacySourcePaths. Reader does not write, replace variables, convert images or invent schema/revision values. Parser is only low-level XML projection.
- Canonical resource IDs are reversible escaped `scope/local`, scope being the defining document path relative to SourceRoot without `.xml`. Always qualify short IDs, including single-file input; validate/preserve path IDs and reject colon-qualified IDs. Keep business entity IDs unchanged. MetadataId owns PathPrefix/Qualify/LocalId; LocalId unescapes the final slash segment. Remove EncodePath/DecodePath dual codecs, Flattener and compatibility Detector wrappers.
- Interpret Source relative to its defining document and project it entry-directory-relative in Root. SourceRoot is containment/qualification only, never a resource-address fallback. Explicit URLs remain supported, Image.Url is authoritative, and HTTP documents cannot reference local files. Preserve URI/path security, cycle/dedup checks, own/descendant/global reference precedence and permanent ambiguity.
- Include contributes actual contents, including full Manifest bodies. External Manifest Source stays external unless InlineManifests is requested. A short stub ID is scoped to its target definition. External-body reads share SourceRoot and resolve exact canonical ID; no suffix/LocalId match or alias rewrite.
- `MetadataExporter.Export(input, outputDirectory, options)` uses one layout algorithm and returns the written XML paths. Layout remains Flat/Tree/Mixed; Mixed selects retained edges with Inline. InlineManifests, ResolveVariables and ConvertImages remain explicit. No IdFormat enum, root IdFormat/SourceBase marker or `--id-format` exists.
- Build calls normal Export and explicitly sets schema/revision while retaining XSD, variables, WebP and domain validation. Remove ExportLegacy and Builder.Load/LoadSource/Open with their separate HTTP/temp/retry pipeline. SourceExporter still explicitly creates short-ID local author declarations, including ExportManifestElement, without changing frozen installed bytes.
- Cache reads every acquired XML through Reader and persists one normalized complete snapshot contract. Raw validators remain paired with raw bytes, never transformed XML. Preserve root-304 child revalidation, last-complete fallback, zero-network offline reads, current `.uri`/`.etag` safety and Fresh/Stale semantics; no old-layout migration fallback or strict raw-flat branch.
- Snapshot/package Get/GetCached/Refresh take captured RootSnapshotResult and PackageEntry. Inline results inherit snapshot status and have no synthetic URI; external lookup requires exact registration and snapshot binding. Desktop keeps per-path authorization, cancellation and image cadence.
- Final public read-surface reduction also removes Parser.LoadFromFile and URI-only cache GetLeafAsync/GetCachedLeafAsync/RefreshLeafAsync with Match/TrySelect/RequireLeaf glue. Retain Parser.Load(XDocument)/ParseManifest as projection, package-context cache operations and PreloadLeavesAsync; SourceManifest has only Resolve(MetadataReadResult declaration, Metadata package).
- A Manifest with nonempty Source is always external; supplying a body at the same time fails. Full included manifests without Source remain full. This is one shape rule for all inputs, not a compatibility branch.

## Ownership and integration

Owners edit current paths without checks, commits, submodule edits or directory moves. Reader owns composition/ID/source and relevant tests; Exporter owns exporter/Builder/CLI/SourceExporter and build/export tests; Cache owns cache/SourceManifest and cache tests; Desktop owns consumers/tests. Parent integrates contracts, handles remaining references, moves files after integration, verifies once, reviews/simplifies touched code, records actual results and promotes the proposals only after evidence. Generated docs remain parent-owned.

## Requested production directory simplification

Namespaces stay unchanged. Parent completed production/test responsibility moves, including Reading/SourceManifest and Cache tests; Metadata integration verification is recorded below. Desktop dependency integration remains pending.

| Target under `Ra3.BattleNet.Metadata/` | Files/responsibility |
|---|---|
| `Reading/` | MetadataReader, MetadataId, MetadataDocumentInfo, MetadataSourceOptions, IMetadataXmlSource, PhysicalFileXmlSource, HttpXmlSource, MetadataSourceManifest, MetadataParser (low-level projection), MetadataValidator (reference validation) |
| `Exporting/` | MetadataExporter, MetadataExportOptions, MetadataSourceExporter, MetadataXml |
| `Building/` | MetadataBuilder, MetadataInheritance, SchemaValidator, VariableResolver, MarkdownValidator, ImagePostProcessor, ImagingInvoker |
| `Cache/` | Existing client, disk cache, transfer and snapshot/result files; no new general framework |
| Root | Existing models, query/core contracts and Program entry point; no unrelated model movement |
| Removed | MetadataFlattener, compatibility-only MetadataDetector, old mode/codec/read API branches and obsolete wrappers |

## Requested unit-test simplification

Keep shared TestData in its existing copied location, namespaces and AppContext/RepoRoot calculation valid. Remove obsolete-API assertions and duplicate format-choice rows; migrate meaningful cases to current behavior instead of weakening coverage to hide failures.

| Target under `Ra3.BattleNet.Metadata.Tests/` | Coverage and consolidation |
|---|---|
| `Reading/` | Reader; consolidated source/http/path/ID/composition cases. Merge meaningful LoadSource/Open/Include/IdScope coverage into Reader or focused source/path tests. Remove fixed-stopwatch PerformanceTests rather than replacing it with another timing threshold. |
| `Exporting/` | MetadataExport and SourceFormat author-write tests; Flat/Tree/Mixed and inline/external equivalence using one path format |
| `Building/` | BuildTests (replace StageA naming), Schema, ManifestValidation, Imaging and Inheritance; preserve unrelated Skudef/Mount behavior |
| `Cache/` | Client, Transfer, Image, Composition and JSON contract; preserve ETag/URI safety/offline/last-complete behavior, remove obsolete cache-shape compatibility tests |
| `Queries/` | Query, Style, Language, Compatibility and other model/query behavior; no legacy reader shims |
| `TestData/` | Shared fixtures unchanged in placement; update only fixtures needed by the single-format contract |

Desktop retains service-boundary tests while migrating removed APIs and colon resource-ID assumptions; no submodule source edits or duplicate resolver.

## Acceptance matrix

All rows require new parent evidence; none is a recorded pass.

| Concern | Required observation |
|---|---|
| API removal | All affected callers migrated; no old Builder read API, Flattener, dual codec, marker/mode option, ExportLegacy or CLI id-format shim remains |
| Reading/identity | Short IDs always qualify; path IDs survive relocation; colon IDs fail; scope precedence/ambiguity/type rules, cycles and dedup hold |
| Sources/manifests | Defining-document-relative Source, containment and HTTP/local boundary hold; Image.Url wins; included full bodies stay full; external stubs/bodies share exact identity |
| Actual Build | Build immutable repository author tree to external temporary output with existing schema/domain/variable/resource checks and no author XML/XSD edits |
| Layouts | Actual Reader -> Flat/Tree/Mixed -> Reader for both external and inlined manifests; equivalent model, references and real resources without format markers |
| Output safety | Traversal, encoded separators, source/output overlap and collisions fail before unsafe writes |
| Cache/network | Real loopback root 304 + changed child; child failure preserves prior complete Stale snapshot; cold restart/offline uses zero network; validators match raw bytes; no binary-resource prefetch |
| Tests/structure | Current full suite plus production/test responsibility moves; no lost security, domain, image, cancellation or offline cases; deterministic tests, no stopwatch gates |
| Desktop delivery | Push Metadata PR #65 commit, pin it in normal Desktop submodule, run default-reference tests/build and actual service smoke before PR #182 update; no external-project override |
| Documentation/review | Parent refreshes generated docs/checks, performs touched-code simplify/review, records exact evidence and limitations, and only then promotes proposals |

## Evidence and delivery boundary

No check is run by the documentation owner. The parent reports the following final Metadata evidence. Desktop integration and dependency delivery remain pending; no GUI/game execution is inferred. Generated documentation is parent-owned. Push Metadata first, then pin and validate Desktop's normal submodule; no merge is authorized.

- Final full Metadata suite: 429/429 passed, 0 warnings and 0 failures. This is one complete suite, not a sum of targeted runs. The later source change only removed an obsolete API comment, not behavior.
- Actual Build and Tree export from unchanged repository data succeeded with 15 Mods and 3 Applications. Actual six Flat/Tree/Mixed × InlineManifests false/true roundtrips retained canonical path IDs without markers, inheritance, full manifest semantics and image bytes.
- Final real HTTP/offline smoke passed: root 304 plus child 200, child 404 with last-complete fallback, unchanged old captured snapshot and new-client offline reopening after stopping the server.
- Review findings for empty/default manifests, HTTP double decoding and contextual normalized disk identity were corrected and covered. The security finding around Image-only Url handling was corrected; parent reports no remaining issue in that review.
- Final tracked C# comparison against dcaa1d5, excluding bin/obj and including blank/comment lines: production 43 files/7617 lines -> 41/6941 (−676); tests 36/11167 -> 32/9504 (−1663); combined −2339 lines, excluding docs. These are observed reductions, not acceptance thresholds.

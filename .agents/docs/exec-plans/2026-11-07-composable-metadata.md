# Composable Metadata implementation plan

Status: Metadata implementation verified; Desktop integration and dependency delivery pending.

## Scope and delivery

Extend the unified-reader work without changing the author XML tree, its XSDs, the existing model/query/image APIs, business Mod/Application IDs, or the default legacy Build/publish layout. Work only in the isolated Metadata and Desktop worktrees. The original unified-reader and main worktrees remain untouched.

Flattened or split XML is an organization choice, not a different metadata meaning. Replace and merge existing implementations instead of stacking new modules beside them: Reader owns the sole composition core, public Flattener methods become thin compatibility delegates, Exporter selects retained Include edges through one layout algorithm, cache only acquires/persists the graph, and Desktop removes replaced URI glue. Acceptance includes identifying removed duplicate paths; new option/type/adapter families are not a substitute for simplification.

The Metadata changes include the original unified-reader commits. Delivery updates the existing [Metadata PR #65](https://github.com/RA3BattleNet/Metadata/pull/65), targeting main, by safely pushing the integrated HEAD to `feat/unified-metadata-reader`; do not open a duplicate Metadata PR. Desktop receives a separate PR targeting main, explicitly dependent on PR #65 and its pushed commit. The parent owns validation, commits, pushes, gitlink update, and both PR descriptions. No merge is authorized.

Metadata verification evidence is recorded below; this plan does not claim Desktop acceptance or completed PR delivery. Decision rationale and the previous-note audit are in [the implemented Metadata architecture note](../../notes/implemented/architecture/2026-11-07-composable-metadata.md).

## Ownership and file responsibilities

| Area | Responsible files or existing boundary | Responsibility |
|---|---|---|
| Reader | `MetadataReader`, `MetadataReadResult`, `MetadataDocumentInfo`, `MetadataBuilder.Open`, `MetadataSourceOptions`, `MetadataFlattener`, XML source implementations | One composition pipeline; capture documents and provenance; normalize IDs and Source; preserve strict Load/Parser and legacy LoadSource contracts |
| Identity | Shared `MetadataId` codec and existing `MetadataFlattener.QualifyId/LocalId/PathPrefix` signatures | Canonical scope/local identity, reversible legacy/path encodings, correct alias/type resolution |
| Export | `MetadataExporter`, `MetadataExportOptions`, existing `MetadataSourceExporter`, serializer/build integration | Flat/Tree/Mixed projection, coherent Include graph, safe output/resource layout, explicit build-only processing |
| Runtime | `Cache/*.cs`, `MetadataSourceManifest` | Async graph acquisition through `IMetadataTransfer`, snapshot persistence, package-based manifest access, offline/stale semantics |
| Desktop | Existing catalog, download, maintenance, local declaration/import services and their tests | Consume package APIs and unified read; preserve installed facts, cancellation, image and Fresh gates |
| Tests | Existing Metadata and Desktop test projects plus focused graph/roundtrip/service coverage | Prove compatibility and the full end-to-end matrix below |
| Documentation | `.agents/docs`, architecture and decision notes in each worktree | Explain boundaries, formats, migration, acceptance and PR ordering; generated files remain parent-owned |

Owners must communicate final public signatures before dependent callsites are migrated. Renamed public symbols require reference discovery first. No owner runs build/test/lint/format scripts while another owner is editing.

## Interface contract

`MetadataReader.Read(string pathOrUrl, MetadataSourceOptions? options = null)` returns a `MetadataReadResult`. `MetadataBuilder.Open` uses this reader; `Load` and `MetadataParser.Load` remain strict published-document entry points. `LoadSource` remains an explicit compatibility operation, including source-root-relative Source and legacy stub behavior.

The result exposes `Root`, normalized `Document`, `EntryPath`, `SourceRoot`, and `Documents`. The normalized document uses legacy canonical IDs for existing consumers. `MetadataDocumentInfo` has `Path`, `RelativePath`, raw `Document`, own-node `NormalizedDocument`, and `Includes`; each `MetadataIncludeInfo` has `SourcePath` and `TargetPath`. Options are `SourceRoot`, `ValidateReferences`, `ForceSourceTree`, `CustomSource` and `InlineManifests` (default false). Result-owned XML is immutable by convention. Captured data, not a disposed HTTP source, is the durable export input. Custom `IMetadataXmlSource` allows the cache to supply an asynchronously prefetched graph.

`MetadataExporter.Export(MetadataReadResult input, string outputDirectory, MetadataExportOptions? options = null)` returns void and writes root `metadata.xml`. The owner's contract uses `MetadataExportLayout` Flat (default), Tree or Mixed; `MetadataExportIdFormat` Legacy (default) or Path; `Inline` as `Func<string, bool>?` on target RelativePath (required for Mixed); and `InlineManifests`, `ConvertImages`, `ResolveVariables` booleans defaulting false. One recursive Include-edge selection handles all layouts. Reading never evaluates variables or converts images.

The confirmed cache APIs are `Task<LeafResult> GetPackageAsync/GetCachedPackageAsync/RefreshPackageAsync(RootSnapshotResult snapshot, PackageEntry package, CancellationToken ct = default)`. Existing URI-oriented leaf APIs remain compatible. Inline results contain snapshot ManifestNode/ManifestEntry, have null SourceUri, and inherit snapshot.Status in all three operations: a complete Fresh graph can authorize Fresh inline data, but a Stale root is never elevated. External cached-only access remains Stale and external packages require strict Raw membership/Source binding. No synthetic URI represents an inline manifest.

Desktop must use the package-oriented operations for production display, installation and maintenance. SourceExporter remains the single library-owned single-Mod author declaration exporter. Exact final option/property names must be synchronized with the implementing owners before acceptance.

The cache owner's planned private representation is `.compositions/<origin SHA256>.xml`: one atomically published complete materialized root plus raw-graph URI/hash list, committed only after the full graph succeeds. URL-mapped raw bytes/ETags remain separate and unchanged; root 304 still revalidates children. This replaces the old strict-root PublishRoot implementation without adding cache ID/merge logic. The finalized helper is `XElement MetadataSourceExporter.ExportManifestElement(XElement manifest)`: clone the node and normalize short author IDs without serialize/parse roundtrips. Old ExportWhole/ExportVersion APIs stay unchanged; transient ExportManifest overloads are removed. Desktop preserves frozen installed XML and separately generates local author XML through this helper, removing duplicate LocalId transformations. These are synchronized implementation contracts, not acceptance evidence.

The parent reports legacy Build integration as Reader with ForceSourceTree/legacy Source paths and SourceRoot, followed by ModCompatibility validation, ExportLegacy, shared XML version/revision/variable processing, then existing XSD/hard validation. ExportLegacy returns written XML paths and uses the same Compose/Prepare/Write core without generated format markers. Its compatibility profile preserves original external manifest Source filenames, ID/Source-only stubs and already-inline manifest bodies; deterministic generated manifest paths are only a fallback when no Source exists. Replaced paths include CopyReferencedResources and repeated hard-validation traversal. Exporter remains the only output layout core; these removals do not imply verification has passed.

Reader owner reports the previous 449-line Flattener implementation replaced by a 45-line public wrapper, with Open's three IO branches removed. `MetadataId` exposes Qualify, LocalId, EncodePath and DecodePath. Captured own-node NormalizedDocument stores canonical IDs, absolute Source and absolute Include targets; the public result rebases Source to the entry except preserved unmarked flat and explicit legacy SourceRoot behavior. These are implementation-shape facts reported before checks, not behavior acceptance.

## Format and compatibility rules

1. Canonical resource identity is `(logical document scope, local ID)`. Legacy encoding is `scope:local`; path encoding is `scope/local`, with reversible escaping per segment. Preserve logical scope after physical relocation. Never infer a Source URL from an ID.
2. Generated composition exports declare `IdFormat="path"` or `IdFormat="legacy"` and `SourceBase="document"`. Unknown declared formats fail. These markers are generated-output metadata, not author-data/XSD edits.
3. Unmarked author composition is qualified exactly once. Old flat documents without composition constructs or markers retain IDs and Source unchanged. Declared path IDs decode to legacy canonical IDs in `Root`. Default author-included full-manifest handling remains legacy stubs, but marked normalized full manifests stay inline regardless of split organization.
4. Resource ID equality remains OrdinalIgnoreCase. Document identity respects HTTP path case and local filesystem rules. Repeated references to one document deduplicate; cycles and distinct registrations of one canonical key fail. An ambiguous alias remains ambiguous permanently; references to it fail. Preserve existing field-specific reference-kind rules, including Icon's established Image-or-Markdown support.
5. The source root is a containment boundary. Unified results resolve Source relative to the entry URI; explicit legacy LoadSource preserves SourceRoot-relative behavior. URL-only resources remain URL-only. Validate Include and asset traversal, encoded separators, and output path collisions before writing.
6. Tree/Mixed output is readable by the new Reader, not promised compatible with old binaries. SchemaVersion `1.0` alone does not establish old-reader compatibility. Default Build and the published legacy layout remain unchanged.
7. Flat output with external manifests is a compatible projection; one truly self-contained XML requires explicit manifest inlining. A flat model cannot restore lost original split/comment provenance; any regrouping must be deliberate and deterministic.
8. Export reuses Reader composition/provenance, not a second flattening implementation. Failure may clean only the intended output, never author sources or unrelated directories.
9. Keep root/leaf/image URI cache mappings stable. Persist the last complete normalized snapshot plus minimal private graph metadata; never associate raw HTTP validators with normalized bytes. A root 304 still revalidates its child graph.

## Six implementation phases

### Phase 1 - Baseline and contracts

Read both contribution guides and existing active notes; record the overlap audit without reversing old Decisions. Publish this plan before code integration. Fix owner boundaries and exact shared APIs. Capture baseline compatibility requirements from existing tests and the immutable author tree. Keep plans/notes proposed until parent verification.

Exit: every dependent owner has the result/export/package contract; the old PR carry-forward and Desktop dependency are explicit.

### Phase 2 - One Reader and identity codec

Implement captured graph loading, cycle/dedup identity, stable typed ID resolution, strict marker handling, normalization and provenance. Migrate Open to Reader while preserving strict Load and explicit LoadSource behavior. Support local, HTTP, custom in-memory, mixed old/new and single-file author inputs without Include-only guessing.

Exit: the Reader supplies all information needed by Exporter and cache; no consumer implements a parallel resolver.

### Phase 3 - Export layouts and compatibility

Implement Flat/Tree/Mixed using the captured result, deterministic safe paths and collision checks. Retain assets, Markdown, manifests, URL-only fields and coherent Includes. Keep optional inlining and export-time processing explicit. Reuse shared core from SourceExporter without changing its established author declaration contract or legacy Build output.

Exit: all layouts have independent Reader roundtrip coverage, source files remain unchanged, and unsupported old-binary use is documented.

### Phase 4 - Complete cached graphs and package APIs

Prefetch XML asynchronously through transfer, compose with the shared Reader, and atomically publish only a fully valid graph. Revalidate Includes after root 304; preserve last complete fallback on child failure. Reopen normalized snapshots offline with no network and reject incomplete/corrupted private state. Add package access for inline and external manifests while retaining URI leaf APIs and current image behavior.

Exit: snapshot-bound package lookup and preloading work for both inline/external manifests; source validators remain attached only to matching raw bodies.

### Phase 5 - Desktop migration

Migrate catalog/display, package download and maintenance consumers to snapshot/package APIs. Use unified Open for applicable local imports, while author writes remain short-ID XML through the library exporter. Preserve frozen installed manifest bytes, asset/base-URI resolution, cancellation, and each existing path's authorization policy. Metadata remains a normal git submodule dependency; do not edit its files inside Desktop or change project references to an external worktree.

Exit: no production path requires an external ManifestSource URI for an inline package; no duplicate ID/Source implementation is introduced in Desktop.

### Phase 6 - Integration, evidence and delivery

After all owners finish, the parent performs one coordinated Metadata verification pass, simplification of touched code and evidence-backed review/security review. Run the acceptance matrix, record real results and limitations, regenerate generated documentation using repository scripts, and only then promote proven notes to implemented. Commit exact changed paths using the agreed Conventional Commit convention. Push the integrated Metadata HEAD to update PR #65. In Desktop, check out that exact remotely available commit inside the Metadata submodule and stage only its gitlink; then build/test through the unchanged default submodule project reference. External `MetadataProjectPath` injection is not final delivery evidence. Push/open the dependent Desktop PR and crosslink both PRs.

Exit: every acceptance row has evidence or an explicit unresolved blocker; no placeholder implementation, unverified implemented status, duplicate Metadata PR, or modification to the original worktrees/source data is delivered.

## Acceptance matrix

All entries below are required checks, not recorded passes.

| Concern | Required exercise | Acceptance |
|---|---|---|
| Legacy public API | Existing Metadata MSTest suite; strict Load/Parser; explicit LoadSource; SourceExporter tests | Existing supported results, short author IDs and strict rejection boundaries remain compatible |
| Legacy production build | Actual CLI Build from immutable repository data to an external temporary output | Existing schema1.0/published layout remains usable; no source XML/XSD change |
| Composition | Local, HTTP and custom-source graphs; diamond, repeated include, cycle, distinct duplicate registration | Dedup is deterministic; cycles/conflicts fail before a partial root escapes |
| ID formats | Old flat, unmarked author, declared legacy/path, mixed documents; escaped scope segments | Canonical root identity is stable and reversible; unknown formats/type mismatches/ambiguous references fail |
| Addressing | Relocated nested documents, source-root override, relative assets/Markdown/manifests, URL-only resources | Assets resolve from declared document/entry bases; legacy SourceRoot behavior remains explicit |
| Export | Actual Reader -> Flat/Tree/Mixed -> Reader for inline/external manifests | Equivalent model/reference/resource semantics and independently readable artifacts; no second composition path |
| Output safety | Traversal, encoded separators, overlapping source/output and case/path collisions | Failure before unsafe writes; cleanup affects intended output only |
| Complete cache refresh | Real loopback HTTP graph, root 200 and 304 with changed child | Child change becomes visible only after complete successful graph publication |
| Fallback and offline | Failed child after a complete refresh; restart; zero-network offline open; corrupted state | Last complete graph survives; incomplete/corrupt states cannot masquerade as a valid snapshot |
| Packages | Inline and external Get/GetCached/Refresh/preload against frozen snapshots | No fake URI; lookup is bound to its snapshot; cached reads do not invent Fresh status or change an existing authorization gate |
| Images | Existing registered-image tests and actual generated-tree asset resolution | URI mapping, byte/revision behavior and image freshness cadence do not regress |
| Desktop | Build/tests through the default Metadata submodule reference at the pushed PR #65 commit; real service scenario from generated tree | Read/catalog/package/image/local-import paths work; existing per-path authorization, cancellation and frozen installed bytes hold |
| Desktop runtime | Launch application if feasible; otherwise actual service smoke | State precisely which GUI/runtime boundary was or was not exercised |
| Documentation/review | Parent note/docs scripts, generated-doc refresh, touched-code simplify and reviewer/security pass | Links and lifecycle are valid; evidence is real; author data and unrelated code remain untouched |

## Risks and mitigations

- Mixed normalized/author documents can double-qualify IDs. Use explicit generated markers and one codec; test physical relocation and cross-document references rather than guessing from strings.
- Short aliases can accidentally regain validity after a later registration. Preserve ambiguity permanently and test registration-order permutations.
- URI identity and resource identity use different equality rules. Keep resource OrdinalIgnoreCase separate from HTTP/file identity and output collision handling.
- Root 304 can hide child changes or let failed children poison cache. Revalidate graph and publish one complete successful normalized snapshot, retaining prior complete data.
- Raw validators and transformed XML have different byte identities. Retain captured raw state with validators and separate the normalized snapshot representation.
- Inline manifests remove the old URI prerequisite. Migrate all Desktop package consumers together; keep strict snapshot identity and existing per-path authorization. The current Stale maintenance-directory policy and failure-time Fresh HTTP(S) fallback confirmation remain distinct; do not reintroduce a blanket Fresh-only maintenance gate.
- Flat input has no lost author topology to recover. Document deterministic regrouping limits and do not advertise a lossless reverse transform.
- Tree/Mixed artifacts are not old-reader artifacts. Keep default publish unchanged and state the new-reader requirement at export/API boundaries.
- Parallel owners can disagree on API names. Shared interface changes require direct messages and dependent callsite updates before parent verification.
- PR ordering can silently compile against the old gitlink. Push Metadata first, check out that exact remote commit in Desktop's submodule, validate unchanged default project references, and crosslink the dependent PR. Do not substitute an external project reference for this evidence.

## Evidence state

The documentation assignment runs no build, test, formatter, script, runtime smoke or generated-document regeneration. The following results are reported by the parent from actual integration checks, not independently rerun here. Metadata implementation is verified and its architecture note is implemented; Desktop verification and dependency delivery remain pending.

- Latest full Metadata suite: `dotnet test Metadata.sln`, 442 passed, 0 failed, including layout self-reference, normalized inline, Url priority, empty manifest, parent/sibling and global-reference precedence, SourceManifest composition, redirect safety and flat-migration/cold-offline regressions. One existing test CS8602 warning remains; production code produced no warning in this run. The earlier 432-pass run preceded these additions and is not the final coverage count.
- After final simplification, the affected Reader/Exporter/Cache/Transfer checks passed 86/86 with 0 failures and 0 warnings. This is targeted post-change evidence, not a claim that a full 443-test suite ran. Parent reran the actual six-layout/HTTP/offline smoke and legacy CLI after simplification; all passed.
- Actual legacy CLI: `dotnet run --no-build --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=<external temporary directory> --content-revision=composition-smoke` succeeded. Current repository data contains 15 Mods and 3 Applications, not the earlier reported 17 entities.
- An actual repository-external throwaway program exercised six combinations of Flat/Tree/Mixed with manifest inlining false/true. Parent reports successful rereads of identity, inheritance, full manifests and image bytes.
- A real HttpListener graph smoke observed root 304 plus child 200 exposing the child update while the old captured snapshot remained unchanged. Child 404 returned the previous complete graph as Stale. After stopping the server, a new client successfully used OpenSnapshot/GetCachedPackage offline.
- Actual repository-tree CLI export produced 53 XML files; Reader reread confirmed 15 Mods, 3 Applications, 50 Image identities, and actual file existence for every resource. Temporary smoke files remain outside the repositories and are still in use; the parent will recycle them after verification.
- Parent reports the second read-only security review found the redirect and first-migration snapshot vulnerabilities closed, with no remaining findings in that review. This is source-review evidence, separate from the passing 442-test suite. The migration regression exercises a preflight conflict, not a simulated crash or mid-write IO failure. Redirect response checking on an injected HttpClient cannot prevent its handler's earlier request; disabling handler redirects remains the host's responsibility. No public transfer option was added. Parent ran `node scripts/verify-docs.mjs --write` successfully for 17 notes and 34 docs before lifecycle edits; the parent refreshes generated docs and checks links again after this migration. Final simplification reviews and affected-path checks are complete. Desktop build/service acceptance waits for the validated Metadata push and normal submodule pin.
- The first real default-submodule Desktop suite exposed two upstream compatibility gaps: old flat HTTP image/Markdown file Source was incorrectly constrained, and no-disk local cached-leaf access still used strict Parser for path IDs/composition. Owners corrected these within the same design: leave old flat Source unchanged while constraining author/normalized composition, and use Reader with an offline source for local cached leaves; missing cached HTTP XML remains Unavailable without networking. Two Reader and one cache regression were added. The subsequent affected Reader/CompositionCache/MetadataClient run passed 68/68, 0 failures and 0 warnings; actual Metadata smoke passed again. Full 442, post-simplification 86 and compatibility-fix 68 are three separate runs, not a combined full-suite count. The corrected Metadata commit must be pushed before Desktop repins and revalidates.

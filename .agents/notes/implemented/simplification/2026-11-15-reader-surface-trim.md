# Agent Note: Trim the reader, exporter and build surface without changing output

Status: implemented

## Problem

The single-format PR already merged reading, exporting and building into one path, but the merged code still carried a second implementation of the same ideas. `PhysicalFileXmlSource` decided "local or remote" inside every interface member and built a throwaway `HttpXmlSource` for remote paths, while `MetadataReader.Read` already chose a source by entry form and `MetadataExporter` sniffed source types to decide whether a caller-injected source existed. Manifest body selection existed once in the reader and once in the exporter with different filters. `MetadataBuilder.Build` re-read every exported XML just to replace variables, duplicating the exporter's `ResolveVariables` pass, and it was that extra load/save round trip that silently normalized output indentation. `MetadataDocumentInfo` kept two copies of each document while only the normalized one had readers. Reference rewriting rebuilt the whole include closure for every document.

## Decision

- `PhysicalFileXmlSource` performs local I/O only; `HttpXmlSource` owns remote paths. Source selection lives in `MetadataXmlSource.For`, and the exporter asks `MetadataXmlSource.IsDefault` instead of naming concrete types.
- Full-manifest detection and "pick exactly one body by ID" live in `MetadataManifestBody` (`IsFull`, `TrySelect`); the reader and exporter both use them and keep their own error text.
- `Build` passes `ResolveVariables` to `Export` and drops its own load/replace/save loop. The leftover-variable check stays. Because output no longer passes through `XDocument.Load`, the exporter trims whitespace text nodes before saving so published bytes stay independent of author formatting.
- `MetadataDocumentInfo` keeps only `NormalizedDocument`; the unused raw `Document` property is removed.
- Each document's reference namespace (own registrations plus include closure) is computed once and cached in the session instead of being rebuilt per document.
- The two registration-validation tests moved into `ValidationTests`, and stale `LoadFromFile_*` test names now say `Read_*`.

## Alternatives considered

- **Keep the HTTP branches inside the local source** so any caller can pass any path. It keeps one class pretending to be two and creates an `HttpClient` per call; the factory is smaller and makes ownership explicit.
- **Share only the predicates and leave both selection loops.** Smaller diff, but the same matching rule stays in two places and the next rule change has to be made twice.
- **Keep the raw `XDocument` for consumers that want to diff source against normalized XML.** No current reader exists in the repository, and the PR is not merged, so an unused copy is pure cost; adding it back later is cheap.
- **Leave the builder's post-export pass and skip `ResolveVariables`.** Byte-identical output without extra exporter work, but keeps a second variable implementation and a full re-read of every output file.

## Consequences

Published XML is unchanged: indentation, ordering, short IDs, reference resolution and validation messages are the same as before. The only behavior difference is a smaller public surface - `MetadataDocumentInfo.Document` is gone; consumers read `NormalizedDocument` instead. Desktop dependency work must not reference the removed property. The exporter now normalizes whitespace itself, so `export` from the CLI also emits stable formatting regardless of author XML.

## Verification

- `dotnet test Metadata.sln`: 430/430 passed, 0 failed, including the two moved validation tests.
- Actual `build --src=./Metadata --dst=<temp>` was run at the commit before this change and after it. Both produced 93 files; every file had identical length and SHA256 except the root `metadata.xml`, whose only difference was the `${TIMESTAMP}` build time. Ignoring that line, `metadata.xml` compared identical.
- `node scripts/verify-docs.mjs` passed with regenerated indexes.

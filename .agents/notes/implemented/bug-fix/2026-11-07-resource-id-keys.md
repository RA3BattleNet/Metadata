# Agent Note: Preserve resource registration keys without colon policing

Status: implemented

## Problem

The [single-format decision](../simplification/2026-11-07-single-metadata-format.md) incorrectly equated a single read protocol with a mandatory path-shaped registration key. The current production document failed before lookup on `mods/corona/manifests/3.258:manifest-3258`, even though the registration, reference and external body share that exact key. The user explicitly requires removing the colon ban and unnecessary ID checks.

## Decision

Remove resource/entity colon rejection and path-segment validation from ID qualification. Existing full keys containing slash or colon are preserved verbatim. Newly scoped short IDs still use escaped scope/local to prevent collisions across definitions. Reference rewriting checks the complete registration set before scoped aliases. Keep duplicate/ambiguous-registration checks and exact external Manifest identity. ID strings are not Source addresses: actual file/URI containment and HTTP/local restrictions remain unchanged. No colon decoder, separator conversion, format detector or compatibility API is added.

The follow-up defensive-code review found LocalId still manually rejected non-hex percent sequences while building aliases, preventing exact-key lookup. Remove that hand-written escape validator; Uri.UnescapeDataString still decodes generated short-name escapes without rejecting literal percent text. Also remove OfflineSource's unused disk parameter and unreachable remote raw-cache fallback. The source remains local-only and uses the existing shared Reader path containment; no network read or complete-snapshot fallback behavior changes.

## Alternatives considered

- **Require republishing production with path IDs**: produces uniform-looking keys but rejects valid exact registrations for no consumer benefit. The user rejects this restriction.
- **Convert colon keys to slash keys**: makes existing keys look generated but mutates identity and requires conversion across references and external bodies. Preserve exact strings instead.

## Consequences

Current production is readable without republishing XML. There is still one read algorithm and exact key lookup, not an old-format mode. The earlier colon-rejection decision and its associated rejection-only tests are superseded; path validation remains on actual addresses. This change does not modify source metadata, XSDs or the installed RisingStorm declaration.

## Verification

- Regression exercises colon-only Image, slash/colon Markdown, colon entity ID and an external Manifest with exact registration/body identity, including inline resolution and reference validation.
- Focused Reader/ID suite: 34 passed. Full Metadata suite: 426 passed, zero failures. Four obsolete invalid-ID rows were removed and one behavior regression added; totals are not added across runs.
- Actual `MetadataReader.Read` against `https://metadata.ra3battle.cn/metadata.xml` with reference validation succeeded: 15 Mods, 3 Applications, 50 image projections and 20 Markdowns. The failing production key remains unchanged.
- Actual MetadataClient root refresh and package preload loaded all 22 of 22 production Manifest bodies, zero failures.
- Actual installed RisingStorm mod.xml read with reference validation and MetadataSourceManifest.Resolve succeeded: current version 077R, full Manifest with RisingStorm_077R.big.
- No BIG download/hash validation, GUI or game launch is claimed.
- Follow-up full suite after both review fixes: 428/428 passed. Existing full-ID regression now covers literal percent and escaped-percent suffixes as well as colon registrations and exact external body identity.
- Actual probe: scope:icon%raw, scope/icon%raw and scope:icon%20 all retain exact registration/reference identity. Production root/reference validation and 22/22 external manifests passed again; installed RisingStorm declaration/full Manifest passed again.
- Actual no-disk-cache local package read expanded a leaf Include and resolved a scope:manifest%raw body successfully. Existing cache regressions also cover local file changes and remote Include rejection without network access.

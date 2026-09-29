# Release artifact gate

This tool audits a staged directory. It never loads candidate assemblies, approves a dependency, downloads a file, creates a package, or publishes a Release. An audit PASS confirms that the staged bytes match an explicit reviewed manifest and the technical rules below. It does not establish complete runtime functionality or legal permission by itself.

Run from the repository root (paths below are relative examples):

```powershell
./tools/Test-ReleaseCandidate.ps1 `
  -CandidateDirectory ../release-candidate `
  -ReportDirectory ../release-review `
  -AllowlistPath ../release-review/reviewed-artifacts.json `
  -RunSelfTests
```

The script builds the auditor and puts its test fixtures and report outside the candidate. It throws on any failure. A future packaging pipeline must require a successful audit of the exact directory to be packaged; this tool does not silently remove rejected files.

`tools/New-ReviewedReleaseArchive.ps1` is the explicit packaging entry point once packaging is separately authorized. It takes `CandidateDirectory`, `AllowlistPath`, `ArchivePath`, and `ReportDirectory`. It freezes the reviewed allowlist/policy inputs, requires a successful input audit, copies only exact approved files into its own new snapshot, audits that snapshot, creates a temporary ZIP, and verifies every ZIP entry against the reviewed hashes before exposing the final archive. Unknown candidate files cause rejection rather than silent omission. Existing archives are never overwritten. It neither uploads nor creates a Release. Do not substitute direct compression of a build or development directory.

The checked-in `docs/release-allowlist.json` approves only the two reviewed root legal documents. It intentionally does not approve an executable distribution. Add project artifacts only after building and reviewing their identities. Add third-party artifacts only after satisfying their existing policy conditions, source/version verification and exact licence/NOTICE requirements. Do not generate an allowlist by accepting everything in a build directory.

## Exact approval schema

`schemaVersion` is `1`. All paths are exact forward-slash paths relative to the staged directory. No roots, backslashes, `..`, alternate data streams, globs or reparse points are allowed. Paths must not collide on a case-insensitive filesystem.

- `files`: each entry has `path`, `kind` (`project`, `thirdParty`, `license`, or `config`), 64-hex `sha256`, `reviewStatus: "PASS"`, and nonempty review `evidence`. All approved files must exist and all existing files must be approved.
- A `thirdParty` entry also has exact `component` and `version` matching `docs/redistribution-policy.json`, `conditionsSatisfied: true`, and `requiredNotices`. The policy's exact `licenseFile` path must appear in this list, exist in the staged directory, and have its own hash-bound `license` rule. Additional necessary upstream notices must also be listed. Metadata-only notices are insufficient: at least one independently reviewed licence text path (`LICENSE`, `LICENCE`, `COPYING`, or OFL text) is required. Its filename alone does not prove permission; the reviewer must verify the actual text and provenance. `EXCLUDE` policy cannot be overridden. `CONDITIONAL` remains conditional in the policy; satisfying a particular artifact's conditions does not globally change it to PASS.
- `requiredFiles`: additional mandatory paths. Root `LICENSE` and `THIRD_PARTY_NOTICES.md` are always mandatory even if omitted here.
- `assemblies`: every managed PE file, including third-party managed assemblies, requires `{ "path": "...", "resources": [...] }`, even when `resources` is empty.
- Each resource requires exact `name`, `sha256` and `format` (`utf8-text` or `dotnet-resources`). Unknown, missing or changed resources fail. Binary payloads masquerading under a text name fail.
- `.resources` entries are inspected with `ResourceReader.GetResourceData`; serialized values are never deserialized. Primitive values are permitted inside an exact hash-bound reviewed container. Binary/serialized values are rejected unless an individual `allowedBinaryValues` entry provides exact `name`, serialized-byte `sha256`, and review `evidence`. ZIP, gzip, PE, font, nested `.resources`, and known EXCLUDE values remain forbidden regardless of this override. Do not approve a serialized value whose origin or content cannot be established.
- `excludedSha256`: optional additional forbidden fingerprints. Every `releaseApproved: false` fingerprint from `docs/local-dependencies.json` is also forbidden, including renamed files and matching embedded resources.
- `forbiddenFileNames`: additional disallowed basenames. Six runtime package names, `classdata.tpk`, Oodle `oo2core_*.dll`, and debug/log/dump/backup/video extensions are always rejected.

Whole-artifact hashes bind the audit to the reviewed build. Rebuilding requires reviewing and updating the artifact manifest. The tool does not treat a filename or a known NuGet package name as sufficient approval. It does not infer legal permissions from metadata or generate approvals automatically.

## Inspection and offline tests

The compiled auditor accepts:

```text
ReleaseAudit audit <candidate-directory> <allowlist.json> <redistribution-policy.json> <local-dependencies.json> <report.json>
ReleaseAudit inspect <assembly>
ReleaseAudit self-test <output-directory>
```

`inspect` uses `PEReader`/metadata and returns names, hashes, lengths and detected formats only. Inspection output is evidence, not distribution approval. It does not use `Assembly.Load` or execute resources. `self-test` builds inert PE metadata fixtures in the specified output directory; it does not invoke production games, APIs, payload installers, or any fixture executable.

Unknown models and fonts are blocked as unknown files or unreviewed binary resources. The gate is not a universal file-format detector: an explicit hash approval still requires human source/licence review. Resource checks cover managed manifest resources; native PE image bytes remain bound by whole-file hashes and require their own origin review.

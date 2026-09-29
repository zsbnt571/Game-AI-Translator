# Offline runtime payload equivalence harness

This console harness loads a reviewed baseline assembly and the external-payload candidate in separate assembly contexts. It does not start either application, any game, any loader/hook, or a translation request.

Run from a local checkout with .NET 8 and the Windows Desktop runtime:

```powershell
dotnet run --project tools/RuntimePayloadTests/RuntimePayloadTests.csproj -c Release -- `
  --source-root <candidate-checkout> `
  --baseline <reviewed-baseline-GameTranslator.dll> `
  --candidate <candidate-GameTranslator.dll> `
  --report-root <independent-review-directory>
```

The assemblies and their dependencies must already be built. The baseline must contain the six reviewed runtime ZIPs. The harness copies those ZIP streams into its own ignored `scratch/<run>/payloads` directory, validates them against the candidate's pinned catalogue, and changes only those scratch copies during negative tests. No runtime packages are downloaded.

Coverage:

- Raw ZIP SHA-256/length and every expanded file byte against the baseline; no candidate embedded fallback; verified handles deny writes.
- Missing ZIP/sidecar, every sidecar identity field, requested engine/backend/architecture/version, unknown IDs, relative root, and modified ZIP rejection.
- Real generic Unity Mono x86/x64 and IL2CPP x64 install/ledger/idempotence/restore on inert file structures, preserving compatible preexisting loader files, cache and save sentinels.
- Legacy installer configuration, backup, ledger, reinstall and restore equivalence.
- UE5 actual installer and restore on an inert PE/PAK structure. A matching empty catalogue/stamp is seeded to guarantee the existing-catalog fast path; no extraction worker is launched.
- Failed fresh installation leaves both synthetic game files and isolated application storage unchanged.
- An interrupted Unity update recovers old owned bytes and ledger before reporting unavailable replacement packages, and can subsequently be restored without a ZIP.
- Specialized Unity payload/configuration, exact-game refusal, and incomplete journal restore. The exact real-game hash gate is preserved; full specialized installation is intentionally not claimed.

Reports include all check names, package-entry hashes and assembly identity hashes. Random backup IDs, absolute fixture roots, and archive timestamps are normalized only for equivalence comparisons. Original-file preservation checks use their actual byte hashes. The test creates a new run directory and does not delete older evidence.

These checks establish offline installation and recovery behavior, not in-game compatibility, rendering, performance, or distribution rights for runtime components.

# External Unreal components

This source tree contains Fusion bridge source and notices, not a complete packaged runtime.

- UE4SS: experimental build `3.0.1-1145-g44afb36d`, MIT. Pinned origin and archive hash: `experimental-provenance.json`. Runtime binary excluded.
- rxi/json.lua: MIT, commit `dbf4b2dd2eb7c23be2773c89eb059dadd6436f94`. Vendored source retains its copyright and licence header.
- CUE4Parse: NuGet `1.2.2.202609`, Apache-2.0. Restored for the optional catalogue worker; not vendored as a binary here.
- Microsoft.Bcl.Memory: NuGet `9.0.14`, an optional catalogue worker dependency. Check package terms during restoration.
- Oodle: proprietary external decoder, excluded; see `Source/UnrealCatalog/Native/PROVENANCE.md`.
- .NET: runtime notices are retained for reference. No self-contained runtime is committed.

Read-only text query validation does not establish successful in-game replacement. Runtime and gameplay compatibility require separate validation.

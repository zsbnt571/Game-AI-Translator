# Fusion 1.1.127 — Validation

Application assembly SHA256: `EEBB1E4D45BA396D86C68DE663C2BF3E07B9781262717670B2E98A54E726EEF1`.

| Check | Result | Scope |
|---|---:|---|
| Interface-language selection and persistence | 332 passed | Saved values, language-list ordering and UI behavior |
| Shared translation/launch host regression | 852 passed | Controlled scenarios, with the bundled .NET 8.0.33 runtime |
| Isolated application startup and settings readback | 2 passed | Portable package startup and isolated settings persistence |
| GameMaker tool command-line help | 2 passed | Tool startup only |

These controlled checks are not end-to-end compatibility tests for every game or translation provider. They do not establish universal font, dialogue or engine compatibility. See the [known limitations](KNOWN-ISSUES.en.md).

The 1.1.127 publication correction changes names and documentation, not application code. The executable and runtime payloads retain their tested hashes. Package manifests and archive checksums are regenerated for the corrected documentation. ZIP members are checked against the final file inventories.

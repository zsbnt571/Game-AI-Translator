# Fusion 1.1.128 — Validation

Application assembly SHA256: `B313C1805A5A68854DE13AAC069EAEE9A584B7BCD815CDBDAFC72818F77B5790`.

The following checks were rerun against this public application assembly. The translation and interface suites used the exact bundled .NET 8.0.33 runtime; loaded runtime files were checked against the public package.

| Check | Result | Scope |
|---|---:|---|
| Interface-language selection and persistence | 332 passed | Nine-language ordering, saved values and interface behavior |
| Gender-evidence rules | 513 passed | Current-text evidence, rejected ambiguity and unaffected languages |
| Request builders and adapter hosts | 902 passed | Controlled request, cold/warm cache and host scenarios |
| Cache and failed-retry safety | 68 passed | Existing translations, late role information and failure retention |
| Shared translation/launch host regression | 852 passed | Controlled launch, queue, value-edit and Ren'Py host scenarios |
| Isolated application startup and settings readback | 2 passed | Complete portable package with fresh synthetic settings |
| Version and stable application identity | 2 passed | Public assembly identity getters |

The 902 checks include 335 request comparisons and 36 cold/warm sessions across 18 adapter-host routes. These are controlled hosts and providers: they are not 18 real engines or games. Real game launches and translation API requests for this final public-package verification were both **0**. Earlier development-stage API or engine tests are not counted as tests of this public binary.

Both application and source ZIPs are verified member by member for size, CRC and SHA256 against their final file inventories. `PACKAGE-VERIFICATION.json` and `SHA256SUMS.txt` identify the distributed archives. The complete source asset includes redistributable build inputs; third-party components are not all rebuilt by this validation.

These results do not establish full-game compatibility or guarantee every provider response. See [known limitations](KNOWN-ISSUES.en.md). A clean Windows machine without existing optional WebView2 components was not tested.

# FUSION R1 0.6.0.177 Preview 1 — Known issues

- The reported Sloppy Fields crash is still undiagnosed. Restoring catalogue admission and recording error dialogs does not establish a crash fix.
- GameMaker pretranslation fallback requires a conservatively inferred single-language script catalogue and matching fresh runtime evidence. Mixed, uncertain or unextractable content may still rely on live collection. Extraction is not full-game coverage.
- Standard error dialogs are scoped to the verified game process. Brief/custom windows, blocked access or direct process termination may escape capture. Do not delete saves or existing mods for troubleshooting.
- Short words, names, Han-only text and limited context can be misidentified. Nonselected sources and existing target-language text are retained. In-flight requests may finish during a locale switch.
- The 245 + 756 + 22 checks are controlled tests including real extracted resources, not new live API quality comparisons, long commercial-game sessions or all-engine visual acceptance. Historical .176 API results are not newly repeated .177 evidence.
- Unity IL2CPP, Unreal and other engines retain compatibility limits. Detection/extraction does not prove writeback, glyph, layout or complete gameplay. Existing loaders/mods may trigger conflict protection.
- Changing target/style/instructions does not automatically replace all cached translations. First translation remains dependent on API latency, quota and model behavior.
- The default CPU dependencies are bundled; service/credits, platform components, games and GPU drivers are not. Optional GPU runtime files are reused without new all-hardware inference acceptance.
- Proprietary Oodle and some optional heavy vision models are excluded. Experimental image translation may leave background/layout artifacts.
- No new all-DPI/theme visual acceptance or clean-Windows-VM acceptance is claimed. Ten local package check groups and 15 Unicode-path cases passed. See the separate release asset PACKAGE-VERIFICATION.json for details.

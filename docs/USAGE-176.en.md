# FUSION R1 0.6.0.176 Preview 1 — Usage


Download and start
Choose the win-x64-with-runtime ZIP, fully extract it, and launch Fusion-R1/GameTranslator.exe. Source archives are for developers.
Use a writable Windows 10/11 x64 folder and allow at least 5 GB free space for extraction; personal caches and history may require more. No native Linux, macOS or ARM package is provided.

Configure API and translation preferences separately
Settings > Translation profiles stores endpoint, API key, model and connection timeouts. Test connection does not save changes; save the profile explicitly.
The Translation preferences section is BELOW the API settings on that same page in .176; it is not a separate sidebar page. Target, source scope, style, instructions and protection settings use Save preferences.
Games and captures share those saved preferences independently of API profiles. New captures use the change; restart games launched with translation. Existing translations are retained.
Migrating an older configuration initializes shared preferences from the old common/default profile once. Review target, source selection and style after migration.
The interface display language does not select the translation target.

Which source mode?
Automatic: best when source languages are unknown or genuinely mixed. It handles collected non-target text with less manual setup. Short/ambiguous text may still require a model request or be misclassified.
Single language: restricts translation to that identified source, preserving other identified languages. Useful when you only want English translated; actual Japanese or Korean may intentionally remain.
Custom combination: choose this option in Source language, then select one or more languages. A checkbox position does not set primary/secondary priority. Choose Automatic or a single source to leave this mode; the combination is remembered.

Inactive language packs versus visible mixed text
The tested Ren'Py/Godot active-locale paths did not collect inactive-pack-exclusive strings in bulk. Selecting English + Japanese does not order a full scan/translation of an unused Japanese language pack.
Content genuinely supplied by the active game, including shared menus, bilingual dialogue or concurrently displayed language choices, is different. Automatic can translate identified Korean; an English + Japanese combination normally preserves it.
Current-engine collection can see prebuilt/hidden controls in some games, and resource catalogue/pretranslation tools have a broader scope than visible text. No universal guarantee is made for every engine/mod.
Previously observed requests may complete during a game language switch. Use strings exclusive to an inactive pack when diagnosing leakage.

Target examples with English + Japanese selected
Chinese target: translate encountered English/Japanese; keep existing Chinese and identified Korean.
English target: preserve existing English, translate Japanese to English, keep identified Korean.
Japanese target: preserve Japanese, translate English to Japanese, keep identified Korean.
Automatic also permits other detected non-target languages in actually encountered content.
Mixed text is handled by spans. Target-language portions, excluded-source portions, variables, IDs and code should remain intact. Very short text, names, shared Han characters and ambiguous Roman text remain limitations.

Styles
Natural: fluent language that retains the source voice.
Game-oriented: dialogue, quests, skills and UI wording with consistent terms and intact gameplay information.
Cultural localization: contextual handling of jokes/metaphors without globally replacing real names, brands or the story setting.
Literary: restrained expressive phrasing without invented facts, emotion or imagery.
Custom: your instructions; empty instructions still aim for accurate natural wording.
Literal (legacy profile): retains the older close-to-source option.
All modes prioritize meaning. Short menu labels often look identical across styles. Idioms are tools, not a translation objective.
Changing style does not clear existing translations. To compare, use Translations & terms to retranslate the SAME source, backing up manual edits first.
The final 258-row real API regression used Natural style. It tests language scope/protection, not an equally large semantic review of every style.

Games and captures
Import the game, choose its API profile, and follow the game's available install/translation launch action. Detection, extraction, translation generation and in-game display are separate stages.
First translations may show source text while the service responds; valid caches/manual edits are retained. Existing mods/loaders may trigger conflict protection; test backed-up copies rather than forcing overwrites.
Capture translation is available from the top navigation and the shortcut shown under Settings > Keys.
OCR language under Capture processing controls reading pixels; translation source scope controls which recognized text is translated. They are different settings.
RapidOCR runs locally by default. Text translation goes to your selected API provider; an explicitly selected image/vision-input feature can also send its image.
Translated-image rendering is experimental and can leave background artifacts, layout issues or poor small/vertical text.
No general-purpose translation LLM or cloud credits are bundled.

Upgrade, rollback and privacy
The public ZIP has no API profiles, game library, screenshots, saves or personal history.
Exit Fusion and affected games. Back up the actual old data directory (check Storage in the old app if customized), then copy it to new Fusion-R1/data. Copy data, not old runtimes/executables.
Never let two versions write the same directory. Verify library, target/source preferences and API connectivity after migration. Windows account/device-bound key encryption may require re-entry.
Keep the old app and pre-upgrade data backup; use that backup for rollback. Follow the app's instructions for existing game bridge updates; do not remove mods/saves manually.
For reports, include app/OS/game versions, source/target choices, exact steps, and a minimal text/screenshot example. Redact keys, tokens, personal paths and private content from logs. Games and saves are not required.

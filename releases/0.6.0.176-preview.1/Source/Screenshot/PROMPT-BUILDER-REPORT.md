# Prompt Builder Report

`TranslationPromptBuilder` is the single source for batch and plain-test system prompts.

Mandatory core rules always remain present:

- translate complete supplied text to the selected target;
- treat mixed-language text as one semantic whole;
- do not summarize, explain, add, omit, continue, or answer;
- preserve enabled IDs/codes, numbers, variables/placeholders;
- for batch requests, return strict JSON keyed by the supplied stable IDs.

Style layers:

- Natural: fluent wording while preserving meaning and tone.
- Literal: faithful wording with minimal paraphrase.
- GameLocalization: concise application/game UI and idiomatic dialogue.
- Custom: appends the user's instruction without replacing mandatory core rules.

The prompt is deterministic for identical settings. Tests verify target inclusion, style differences, custom append behavior and core-rule preservation.


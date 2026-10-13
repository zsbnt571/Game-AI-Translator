# Provider Foundation Report

## Implemented

- Explicit provider enum and registry.
- First and only registered type: **OpenAI Compatible**.
- `OpenAiCompatibleProvider` is a minimal adapter over the proven `TranslationService` HTTP path.
- Selection is explicit; URL-pattern guessing is not used.
- Existing shared `HttpClient`, `stream=false`, `thinking.type=disabled`, compact JSON, timeout, cancellation and response parsing remain intact.

## Connection test

The dialog has an explicit user-triggered test using a short `Hello.` request. Result categories include success, authentication failure, model not found, timeout, and generic connection failure. Automated tests inject a fake tester; no real API was called.

## Deliberately not implemented

- Native Gemini
- Native Anthropic
- OAuth, organization/account discovery, provider-specific streaming, provider-specific tool APIs


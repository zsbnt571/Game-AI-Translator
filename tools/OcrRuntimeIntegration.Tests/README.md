# OCR runtime integration checks

Build the candidate main application, then run:

```text
dotnet run --project tools/OcrRuntimeIntegration.Tests -- <candidate-output>/GameTranslator.dll
```

The test executable creates an independent temporary application directory,
starts its own hidden child host there, and loads the specified candidate
assembly read-only. Only synthetic `dependencies.json` files are written.

It checks absent, malformed, absolute, escaping, oversized, and custom-relative
runtime configurations; fixed Python selection; ignored legacy vision pointers;
missing OCR without worker startup; readiness-cache invalidation; and refusal of
network repair. It does not launch the application UI, OCR engine, real API, or
game. It never reads an existing user's configuration.

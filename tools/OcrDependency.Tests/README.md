# Offline OCR dependency validation tests

Run `dotnet run --project tools/OcrDependency.Tests/OcrDependency.Tests.csproj`.

The tests use synthetic metadata and invalid model placeholders in a new temporary
directory. They verify missing dependencies, fixed versions, x64 identity, import
isolation, local path boundaries, exact model hash rejection, cancellation, and
non-mutating checks. No Python environment, model, API key, or network is needed.

These tests do not claim that a real OCR environment loads or recognizes text.
User-provided environments still need the application's explicit runtime check.

`test_probe.py` uses an explicitly selected test Python and standard-library
`unittest`. It tests local distribution metadata, import-hook isolation,
duplicate metadata rejection, network-repair refusal, the network guard, and
worker syntax. It does not require or import OCR packages or models. Run:

```text
<test-python> -I -S -B tools/OcrDependency.Tests/test_probe.py
```

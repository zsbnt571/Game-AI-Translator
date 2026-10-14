# Desktop acceptance checks

Build with the Windows .NET 8 SDK and `-p:FusionAssembly="C:\Fusion-0.6.0.179\GameTranslator.dll"`, then run `Regression026.exe "C:\Fusion179-checks\desktop"` from the resulting bin/Release/net8.0-windows folder.

Uses isolated synthetic settings and briefly creates native windows. Checks bindings, templates, retained controls, window state and capture exclusion. Disables global input and calls no translation API. Does not certify full visual parity, DPI behavior or real games.

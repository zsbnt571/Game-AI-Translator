# Source pretranslation regression checks (178/179)

Use .NET 8 or newer SDK on Windows and an extracted public main package.

```powershell
dotnet build SourcePretranslation178.csproj -c Release -p:FusionAssembly="C:\Fusion-0.6.0.179\GameTranslator.dll"
dotnet bin/Release/net8.0-windows/Regression026.dll "C:\Fusion179-checks\queue" "C:\Fusion-0.6.0.179"
dotnet bin/Release/net8.0-windows/Regression026.dll "C:\Fusion179-checks\recovery" "C:\Fusion-0.6.0.179" --install-only
```

Use fresh empty output folders. The 249 queue checks use synthetic text and an in-memory provider. The 35 recovery checks write only synthetic fixtures. No real API requests or game execution occur.

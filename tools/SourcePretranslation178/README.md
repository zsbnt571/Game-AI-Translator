# 178 controlled regression checks

Use .NET 8 or newer SDK on Windows with the complete public main package extracted. Set `FusionAssembly` to the absolute path of its GameTranslator.dll.

```powershell
dotnet build SourcePretranslation178.csproj -c Release -p:FusionAssembly="C:\Fusion-0.6.0.178\GameTranslator.dll"
dotnet bin/Release/net8.0-windows/Regression026.dll "C:\Fusion178-checks\queue" "C:\Fusion-0.6.0.178"
dotnet bin/Release/net8.0-windows/Regression026.dll "C:\Fusion178-checks\recovery" "C:\Fusion-0.6.0.178" --install-only
```

Use new empty output folders. The 148 queue checks use synthetic text and an in-memory provider; 22 recovery checks write only synthetic game fixtures under the supplied output directory. No real API requests or game execution occur. These are controlled checks, not commercial-game or visual acceptance.

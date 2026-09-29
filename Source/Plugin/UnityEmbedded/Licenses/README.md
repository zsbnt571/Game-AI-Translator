# Unity embedded translation: third-party notices

This source repository retains upstream notices and provenance for external loader/dependency inputs. Their binaries are not included. FusionUnityEmbedded is a separately built plugin; a binary distribution requires its own packaging and licence review.

## Referenced external loaders

- Mono x86/x64: BepInEx 5.4.23.5 (MIT).
- IL2CPP x64: BepInEx 6.0.0-be.788+5b766a3 (LGPL-2.1).
- UnityDoorstop 4.5.0 (LGPL-2.1) is bundled with both loaders.
- Il2CppInterop 1.5.3 is LGPL-3.0-only; both LGPL and incorporated GPL texts are included.

Official distributions:
- https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5
- https://builds.bepinex.dev/projects/bepinex_be/788

## Provenance

sources.json contains pinned original source URLs, license hashes and version evidence. The be.788 source project and actual assembly inventory were checked for this upgrade. Dobby does not expose a source revision in its shipped binary; this uncertainty is retained. AssetsTools NuGet packages declare MIT without a repository commit; their license text is from the pinned upstream URL in the inventory. Capstone package 2.3.1 identifies its assembly as 2.3.2.0.

| Component | Version | License |
|---|---|---|
| BepInEx-5 | 5.4.23.5 | [BepInEx-5.txt](BepInEx-5.txt) |
| BepInEx-6 | 6.0.0-be.788+5b766a3 | [BepInEx-6.txt](BepInEx-6.txt) |
| HarmonyX-5 | 2.9.0 | [HarmonyX-5.txt](HarmonyX-5.txt) |
| HarmonyX-6 | 2.10.2 | [HarmonyX-6.txt](HarmonyX-6.txt) |
| Mono.Cecil-5 | 0.10.4 | [Mono.Cecil-5.txt](Mono.Cecil-5.txt) |
| Mono.Cecil-6 | 0.11.4 | [Mono.Cecil-6.txt](Mono.Cecil-6.txt) |
| MonoMod-5 | 22.1.29.1 | [MonoMod-5.txt](MonoMod-5.txt) |
| MonoMod-6 | 22.07.31.01; Backports 1.1.2; ILHelpers 1.1.0 | [MonoMod-6.txt](MonoMod-6.txt) |
| UnityDoorstop-5 | 4.5.0 | [UnityDoorstop-5.txt](UnityDoorstop-5.txt) |
| UnityDoorstop-6 | 4.5.0 | [UnityDoorstop-6.txt](UnityDoorstop-6.txt) |
| Il2CppInterop-LGPL | 1.5.3 | [Il2CppInterop-LGPL.txt](Il2CppInterop-LGPL.txt) |
| Il2CppInterop-GPL | 1.4.5 historical GPL component | [Il2CppInterop-GPL.txt](Il2CppInterop-GPL.txt) |
| Cpp2IL | 2022.1.0-development.1452+558ddd9; includes StableNameDotNet and WasmDisassembler | [Cpp2IL.txt](Cpp2IL.txt) |
| AssetsTools.NET | 3.0.5; MonoCecil/Cpp2IL extensions 3.0.4 | [AssetsTools.NET.txt](AssetsTools.NET.txt) |
| AsmResolver | 6.0.0-beta.5+124e161 | [AsmResolver.txt](AsmResolver.txt) |
| AssetRipper.CIL | 1.2.2+06216e6 | [AssetRipper.CIL.txt](AssetRipper.CIL.txt) |
| AssetRipper.Primitives | 3.2.0+d759b3d | [AssetRipper.Primitives.txt](AssetRipper.Primitives.txt) |
| Iced | 1.21.0 | [Iced.txt](Iced.txt) |
| Capstone.NET | 2.3.1 | [Capstone.NET.txt](Capstone.NET.txt) |
| SemanticVersioning | 2.0.2 | [SemanticVersioning.txt](SemanticVersioning.txt) |
| Disarm | 2022.1.0-master.99+8574010 | [Disarm.txt](Disarm.txt) |
| Dobby | bundled BepInEx 6; no binary version | [Dobby.txt](Dobby.txt) |
| dotnet-runtime | 6.0.7 | [dotnet-runtime.txt](dotnet-runtime.txt) |
| dotnet-runtime-third-party | 6.0.7 | [dotnet-runtime-third-party.txt](dotnet-runtime-third-party.txt) |
| Mono.Cecil-desktop | 0.11.6 | [Mono.Cecil-desktop.txt](Mono.Cecil-desktop.txt) |

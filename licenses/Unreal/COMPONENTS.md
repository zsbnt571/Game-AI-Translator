# Fusion Unreal adapter — .176 distribution notes

This is the current repository guide for components in the .176 source/release materials. Original upstream license files remain unchanged.

- UE4SS: UE4SS-RE/RE-UE4SS experimental build `3.0.1-1145-g44afb36d`, MIT. [Provenance](experimental-provenance.json) and [license](UE4SS-MIT.txt) are retained.
- rxi/json.lua: MIT, commit `dbf4b2dd2eb7c23be2773c89eb059dadd6436f94`. Its [license](rxi-json-MIT.txt) remains with the code.
- CUE4Parse: NuGet `1.2.2.202609`, repository commit `b4e95441bcf0c975eb3adb68c0fb44c740c2cf62`, [Apache-2.0](CUE4Parse-Apache-2.0.txt). The catalog project pins Microsoft.Bcl.Memory `9.0.14`.
- The isolated catalog helper uses a self-contained .NET 10 runtime. [Microsoft license](DotNet-LICENSE.txt) and [third-party notices](DotNet-THIRD-PARTY-NOTICES.txt) are retained.
- Proprietary Oodle `oo2core_9_win64.dll` is **excluded** from the public source and executable assets. The worker can use a separately authorized compatible codec; MIT/Apache licenses for other components do not relicense that codec. It is not downloaded automatically.

Use the current [build instructions](../../docs/BUILD-176.md), [dependency guide](../../docs/DEPENDENCIES-176.en.md) and [known issues](../../docs/KNOWN-ISSUES.en.md). Historical .81–.85 build paths and private codec records are not current build instructions. Text extraction or engine detection does not establish successful embedded replacement, font coverage or complete gameplay compatibility.

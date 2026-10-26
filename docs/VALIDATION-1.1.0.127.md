# 验证记录 / Validation / Проверки — 1.1.0.127

本页按实际程序集和证据层级分列，不把历史结果累计成新版本已经通关的游戏数量。  
Results are separated by assembly and evidence level. Historical results are not games newly completed on 127.  
Результаты разделены по сборке и типу доказательств; старые проверки не считаются новыми прохождениями игр.

## 最终公发构建 / Final public build / Итоговая публичная сборка

Version: 1.1.0.127  
Release: 1.1.0-preview.3+dialogue.5  
GameTranslator.dll SHA-256: **EEBB1E4D45BA396D86C68DE663C2BF3E07B9781262717670B2E98A54E726EEF1**

The final public build uses the serviced .NET 8.0.33/10.0.14 runtimes. The four project-owned desktop/helper components were published from the public-source copy. This does not establish byte-identical reproduction of the retained local installation or rebuild every embedded native payload. Historical layout-fixture machine paths are removed, build paths are mapped and debug symbols are omitted. Two optional historical layout checks require explicitly supplied FUSION_LAYOUT_FIXTURE_ROOT materials; those original-game fixtures are not distributed or counted as executed passes.

| Scope | Actual result | Boundary |
| --- | --- | --- |
| Interface-language regression | 332 passed; 0 failed | Final DLL, isolated public .NET 8.0.33 host; selection/save/read-back, nine languages plus Follow system, historical settings forms |
| Shared production host | 852 passed; 0 failed | Final DLL and isolated public .NET 8.0.33 runtime: 683 shared host + 64 repeated edits + 44 names + 61 catalog checks; controlled fixtures |
| Runtime loading audit | Verified in the five test processes | Loaded runtime/native modules and retained System.Text.Json/Encodings.Web 9.0.1 were checked against the isolated public-copy paths and hashes; not a separate set of gameplay cases |
| Public package startup and state read-back | 2 passed; 0 failed | Hidden isolated application startup and synthetic settings persistence; package bytes unchanged |
| GameMaker tool startup | Both copies returned exit code 0 for --help | Source and application copies; business assembly hashes retained. Does not exercise a real game or every tool command |
| Public build-entry command flow | 28 controlled checks passed | Command order, runtime/RID properties, existing-output refusal and failure stopping; mock compiler, not another real compilation |
| Translation API requests in 127 | 0 | No new real-provider translation acceptance |
| Original games launched in 127 | 0 | No new original-game or save-file acceptance |

The five runtime-host runs preserved the public input files and test source files. The package lifecycle checks also preserved all package bytes. These checks do not execute every bundled dependency or establish full gameplay compatibility.

## 运行时维护范围 / Runtime servicing scope / Границы обновления сред

Six runtime locations were serviced: the application root (Core + WindowsDesktop 8.0.33), tools/cover-capture (Core + WindowsDesktop 8.0.33), tools/unity-schema (Core 8.0.33), tools/unreal-catalog (Core 10.0.14), adapters/gamemaker in the application, and Dependencies/GameMakerTool in the complete source asset (Core 10.0.14 in both). GameMaker business assemblies are retained; servicing is limited to runtime-owned assets and associated metadata.

UnityEmbeddedIl2Cpp64.zip and its embedded copy retain .NET 6.0.7. That unsupported runtime has not been serviced in this round; replacing its major version requires separate loader compatibility work. These six .NET 8/10 updates are not a claim that every embedded runtime has been updated.

[Microsoft's 25 October 2026 out-of-band advisory](https://devblogs.microsoft.com/dotnet/dotnet-october-2026-oob-security-updates/) identifies 8.0.33/10.0.14 and states that self-contained applications need republishing. The [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) records .NET 6 as out of support. The earlier 8.0.32/10.0.13 fixes were not used as the final servicing target.

## 修补前公发历史 / Earlier public-build history / Прежняя публичная сборка

The pre-servicing public DLL was **4B5B8F1B8BB6F1089AFACA0B16171502895F6F7BBB08B78171427D9D478BC39A**. Its 332 interface and 852 host checks used the test hosts' system runtime; an earlier package also passed 2 isolated startup/read-back checks. These records remain historical. They are not evidence for the final bundled runtime and are not added to the final 332/852/2 results above.

## 已保留的本地127 / Retained local 127 / Сохранённая локальная 127

The retained local installation DLL is **68019EE028636E8A7056868F3ED1934D0F83498037F7D2DFD7F55E95D0009077**. Its identity differs from the final public DLL above.

| Scope | Recorded result | Boundary |
| --- | --- | --- |
| Local release build | 0 errors; 55 warnings | Warnings are not represented as resolved |
| Local language-order regression | 332 passed | This earlier run belongs to the local DLL; do not add it to the public 332 to imply 664 independent cases |
| Expanded language menus | 2 images reviewed | Isolated desktop; not all DPI/display combinations |
| Safe updater | 85 passed | Synthetic directories, process results, replacement/rollback; separate from actual installation acceptance |

## 历史126 / Historical 126 / Историческая 126

These are retained 126 results, not reruns of those entire suites on 127.

| Scope | Recorded result | Boundary |
| --- | --- | --- |
| Shared host | 852 controlled checks | Controlled fixtures |
| Translation quality | 66 regression checks | Controlled cases |
| Godot production host | 18 controlled checks | Separate from actual engine execution |
| Godot engine | 17 checks | Actual Godot 4.6.2 neutral scene; not original-game completion |
| Basic interface | 611 checks; 18 images | Application controls |
| Extended interface | 1,197 checks; 171 images | Automated rendering/measurement; 76 image reviews or identical-hash reuse, not 171 new human reviews |
| Character-context host | 112 controlled checks | Context identity, failure history and cache protection |
| Adapter protocol | 38 checks | Final bridge source, run before the final 126 application candidate; not falsely relabeled as a later rerun |
| Existing translation paths | 8 cold/warm groups | Simplified/Traditional Chinese, Japanese and Korean; prompt/delivery/cache comparison |
| Ren’Py engine | 39 checks | Two isolated neutral-engine scenes, synthetic provider; includes Chinese → Spanish → Chinese in the same engine process |
| Final real-provider sample | 10 requests | 8 definite grammatical expectations passed; 2 unknown-addressee cases recorded only as observations |

The final provider sample checked Spanish/French/Russian first-person agreement and French explicit third-person pronouns. The unknown-addressee observations were Spanish. HTTP 200 and generic quality acceptance were not used as proof of an unknown person's gender.

The first round made 10 requests and produced 2 failed feminine-agreement results. Those failures remain recorded. A generic evidence/prompt correction preceded the final round. Both rounds total **20 requests and 42,353 tokens**. This is a small provider sample, not certification of every language, provider or game. Original-game launches in 126 were 0.

## 历史124 / Historical 124 / Историческая 124

| Scope | Recorded result | Boundary |
| --- | --- | --- |
| Product controlled checks | 1,317 | Controlled product checks |
| Actual Godot engine isolation | 17 | Neutral scene, not a complete game |
| Delivery-tool checks | 99 | Delivery tooling, not additional game cases |
| Independent late-cache supplement | 13 | Did not exercise the complete production scheduler |
| Original-game launches / API calls | 0 /0 | No new original-game or provider acceptance in 124 |

125 subsequently addressed the production Godot cache-preparation race; its full-game limitations remain in the [known issues](KNOWN-ISSUES.en.md). The historical supplement alone was not used to claim that production timing had already been fixed in 124.

[简体中文更新](RELEASE-NOTES-1.1.0.127.md) · [English release notes](RELEASE-NOTES-1.1.0.127.en.md) · [Русские изменения](RELEASE-NOTES-1.1.0.127.ru.md)


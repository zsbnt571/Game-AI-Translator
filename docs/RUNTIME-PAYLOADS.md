# 外部运行依赖

**简体中文** | [English](RUNTIME-PAYLOADS.en.md)

主程序公开构建不嵌入运行 ZIP。RPG Maker、Ren'Py、Godot 的源码桥接脚本继续编入主程序。此变更不授予任何外部组件再分发许可，也不保证所有引擎开箱即用。

## 固定版本与目录

`Source/Screenshot/RuntimePayloadCatalog.json` 是编入主程序的完整性清单，记录包 ID、引擎、后端、位数、包版本、文件名、大小和 SHA-256。包版本 `0.6.0.85` 描述现有适配运行包，不是未来 Release 标签。

| 包 ID | 使用位置 | 当前分发状态 |
|---|---|---|
| unity-mono-x86 | Unity Mono x86 | EXCLUDE；合法本地提供 |
| unity-mono-x64 | Unity Mono x64 | EXCLUDE；合法本地提供 |
| unity-il2cpp-x64 | Unity IL2CPP x64 | EXCLUDE；合法本地提供 |
| unity-legacy | 旧 Unity Mono 安装路径 | EXCLUDE；合法本地提供 |
| unity-specialized | 现有专用 Unity 适配路径 | EXCLUDE；合法本地提供 |
| unreal-runtime | Unreal x64 桥接 | EXCLUDE；合法本地提供 |

默认只读取程序旁的 `runtime-payloads/<package-id>/<package-version>/<file-name>`，及相邻的 `<file-name>.json` 描述文件。可显式设置 `GAME_AI_TRANSLATOR_PAYLOAD_ROOT` 为绝对目录；不扫描其他目录、不联网下载、不静默选用其他版本，也不回退到内嵌 ZIP。

如果已合法取得与源码完整性清单一致的依赖，可在源码仓库根目录使用 PowerShell 7：

```powershell
# 两个路径均为示例；DestinationRoot 必须是本地绝对目录，不能是 Release 暂存目录。
pwsh -File ./tools/Import-RuntimePayloads.ps1 -SourceRoot 'X:\local-source-copy' -DestinationRoot 'X:\local-runtime-payloads'
$env:GAME_AI_TRANSLATOR_PAYLOAD_ROOT = 'X:\local-runtime-payloads'
```

导入器读取 SourceRoot 内既有的 `Source/` 依赖位置，预先校验六包，再创建外部目录与描述文件。不同内容不覆盖。运行时以编译时清单为准重新核验描述文件、文件大小及 SHA-256；不会信任描述文件自行宣称的哈希。校验和安装读取使用同一只读文件句柄。

这些文件必须留在本地，不能因外置而加入公开 ZIP 或安装包。将来审核通过新的包，需要显式更新包版本和固定清单，再完成回归和分发审查。

## 安装与恢复

- 新安装：读取层在原资源读取位置返回校验后的流；缺包、错版本、错后端、错位数或校验失败时，不能开始新的安装写入。
- 恢复原版：使用已有安装记录和备份，不要求外部运行 ZIP 存在。
- 未完成事务：保留原先的恢复顺序。恢复事务可能按旧记录写回备份，即使随后新安装因缺包失败；这不能误报成“缺包情况下绝无文件变化”。
- 依赖一致时，后续安装位置、配置生成、文件所有权、备份和适配逻辑保持原路径；离线等价验证不代表所有真实游戏重测通过。

## 其他本地依赖与公开产物

`classdata.tpk` 和 Oodle 的固定路径、缺失行为及校验见[本地依赖说明](LOCAL-RUNTIME-DEPENDENCIES.md)。主程序不再把相邻 UnrealCatalog 整个输出目录自动复制进产物；工具单独构建、单独审查。OCR 环境和模型仍不随构建导入。

默认构建仅代表可编译，不代表输出目录可直接压缩发布。`FusionIncludeLocalDependencies=true` 只用于隔离的内部构建，可能带入 classdata 或 Oodle，该输出不能发布；此开关不会重新启用六个 ZIP 的内嵌。

最终产物需通过 `tools/Test-ReleaseCandidate.ps1` 的逐文件允许清单及程序集资源检查。`docs/release-allowlist.json` 默认只批准已审查的许可证文本，并未预先批准任何新 DLL。每次构建需独立审核实际文件哈希、来源、对应许可和内嵌资源。未知文件或 EXCLUDE 内容一律失败。

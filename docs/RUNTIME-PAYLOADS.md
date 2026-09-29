# 外部运行依赖

**简体中文** | [English](RUNTIME-PAYLOADS.en.md)

主程序公开构建不嵌入运行 ZIP。RPG Maker、Ren'Py、Godot 的源码桥接脚本仍按既有路径使用。依赖缺失只影响需要该依赖的功能；主程序和设置页面可以独立启动。依赖已准备好不代表所有游戏已兼容，也不授予再分发许可。

## 固定目录

默认根目录为 `<application-directory>/runtime-payloads`，其中 `<application-directory>` 是主程序实际所在目录。安装包不包含以下依赖文件。用户合法自行取得后，通过本地导入器放入：

```text
runtime-payloads/
  unity-mono-x86/0.6.0.85/UnityEmbeddedMono32.zip
  unity-mono-x64/0.6.0.85/UnityEmbeddedMono64.zip
  unity-il2cpp-x64/0.6.0.85/UnityEmbeddedIl2Cpp64.zip
  unity-legacy/0.6.0.85/UnityMono.zip
  unity-specialized/0.6.0.85/<固定清单中的文件名>.zip
  unreal-runtime/0.6.0.85/UnrealRuntime.zip
  unity-classdata/classdata-129e1f80f930/classdata.tpk
  unreal-oodle/oodle9-6f5d41a7892e/oo2core_9_win64.dll
```

每个文件还必须有相邻的 `<完整文件名>.json`，例如 `UnityEmbeddedMono32.zip.json`。描述文件包含 `id`、`engine`、`backend`、`architecture`、`version`、`fileName`、`bytes`、`sha256`。运行时把这些字段与编译时固定清单比较，描述文件不能自行批准新哈希或其他版本。

六个运行 ZIP 的固定清单是 `Source/Screenshot/RuntimePayloadCatalog.json`。`0.6.0.85` 是已有运行包版本，不是公开 Release 标签。classdata 和 Oodle 的完整性约束在共享读取层中固定；其 `classdata-…`、`oodle9-…` 版本标识表示这一份准确文件，不是对未知上游版本的推测。

可显式设置 `GAME_AI_TRANSLATOR_PAYLOAD_ROOT` 为本地绝对目录。设置后只读该根目录；不会在默认目录继续寻找，不扫描整台电脑、不下载、不静默选用其他版本、不回退到 DLL 内嵌资源。Unreal 目录工具由主程序启动时继承主程序明确指定的同一根目录。手动启动该工具时应显式设置此变量。

## 本地导入

在解压后的程序目录（或源码根目录）使用 PowerShell 7，不要求用户 clone 源码。程序包提供 `tools/Import-RuntimePayloads.ps1` 和 `metadata/runtime-payload-catalog.json`。以下路径明确是示例，替换为自己合法持有依赖的目录；目标不能是待发布的 RC 暂存目录。

```powershell
# 来源目录仅含用户已合法取得的原始文件，按准确文件名读取，不扫描。
./tools/Import-RuntimePayloads.ps1 -SourceRoot 'X:\local-dependencies' -FlatSource `
  -DestinationRoot 'X:\application\runtime-payloads' -PackageId 'unity-mono-x64'

# 类型数据库和 Oodle 也使用同一个导入器和根目录。
./tools/Import-RuntimePayloads.ps1 -SourceRoot 'X:\local-dependencies' -FlatSource `
  -DestinationRoot 'X:\application\runtime-payloads' -PackageId 'unity-classdata','unreal-oodle'
```

不传 `-FlatSource` 时，导入器从明确的源码依赖位置读取。未指定 `-PackageId` 时，仅选择原有六个运行 ZIP；不会顺便导入其他文件。全部选中输入校验通过后才开始创建目标文件，已有不同内容不会被覆盖。导入器生成准确的相邻描述文件，运行时仍再次校验。不得把导入后的用户目录加入公开安装包。

导入器在源码副本中使用源码清单，否则只读取程序包中固定的 `metadata/runtime-payload-catalog.json`；也可用 `-CatalogPath '<明确清单文件路径>'` 显式指定。它不搜索依赖。修改导入清单不能更改主程序编译时的版本和 SHA 约束，因此不能把未批准的包变成 Ready。

## 依赖状态

| 状态 | 含义 |
|---|---|
| Ready | 文件、描述、固定版本/后端/位数、大小和 SHA-256 均通过；仅代表这份依赖可读取 |
| Missing | 固定位置缺少依赖或相邻描述文件 |
| Invalid version | 描述或请求版本不等于固定版本 |
| Wrong architecture | 位数不匹配；类型数据库使用 `Any`，它不是可执行二进制 |
| Wrong backend | 后端不匹配 |
| Hash mismatch | 描述中的完整性约束、文件大小或实际 SHA-256 不符 |
| Not required | 当前安装路径不需要这些运行包 |
| Invalid | 身份、格式、链接路径或其他校验不通过 |

查看状态不会创建依赖目录或修改游戏。状态检查并非安装成功记录；安装时仍使用重新校验后保持打开的同一个只读文件流。文件或目录链接被拒绝。

## 安装、恢复与分发边界

- 新安装仍在原读取依赖的位置取得验证后的数据流；缺包或错包不能开始新的安装写入。
- 恢复原版使用既有安装记录和备份，不需要这些外部包仍然存在。
- 未完成事务保留原恢复顺序。旧事务可能先写回原备份，再因新依赖缺失停止新安装；不能将这描述为所有操作均无写入。
- 此层不负责选择或重命名适配器。已有通用、旧版和专用路径各自选择对应包。
- 同一运行包外置前后的离线等价验证，不代表所有真实游戏重新测试通过。
- 所有六个混合运行 ZIP 仍为 **EXCLUDE**。classdata 和 Oodle 的本地使用不代表可公开分发，详见[本地依赖说明](LOCAL-RUNTIME-DEPENDENCIES.md)。
- OCR 环境和模型使用单独的明确本地设置，本轮不打包或自动下载。

最终发布目录必须通过逐文件允许清单、许可证/NOTICE 和程序集内嵌资源检查。未知文件或 EXCLUDE 内容一律阻止打包。

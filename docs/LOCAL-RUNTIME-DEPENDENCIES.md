# 本地运行依赖

[English](LOCAL-RUNTIME-DEPENDENCIES.en.md)

默认公开构建不包含尚未批准再分发的本地运行依赖。文件存在于开发机上，不代表允许随安装包分发；SHA-256 只用于确认固定版本的完整性，不是分发授权。运行包的具体读取规则以对应运行包清单为准。

## Unity 类型数据库

需要时，用户可合法自行提供 `classdata.tpk`，放到 `<application-directory>/adapters/unity/classdata.tpk`。`<application-directory>` 表示实际主程序所在目录，并非游戏目录。

- 固定长度：289,605 字节。
- SHA-256：`129e1f80f930415db6779fe6089afa75280cb51462bcee812beab6cd81a764c6`。
- 公开构建不自动复制；显式内部构建 `-p:FusionIncludeLocalDependencies=true` 可能复制本地文件，不能将这类输出当成公开发行包。
- 缺失或完整性校验失败时会明确记录类型数据库不可用。自带字段结构的资源及文本文件仍按原路径读取；需要类型数据库的资源可能只能使用已有运行时补译。
- 使用校验过的同一文件流交给原 AssetsTools.NET 解析器；不改写解析算法，不搜索其他目录，不联网补下载。

## Unreal 本地解码依赖

可选资源目录工具继续使用原 Oodle 解码接口。用户需要合法自行提供固定版本文件到 `<application-directory>/tools/unreal-catalog/oo2core_9_win64.dll`，即 `FusionUnrealCatalog.exe` 同目录。

- 固定长度：637,952 字节。
- SHA-256：`6f5d41a7892ea6b2db420f2458dad2f84a63901c9a93ce9497337b16c195f457`。
- 默认可以编译目录工具而不提供 Oodle；默认构建和发布不复制 Oodle。
- 显式内部构建 `-p:FusionIncludeLocalDependencies=true` 保留原有构建期 SHA 校验，并可复制本地 Oodle。这不是发布许可。
- 工具运行时在原生加载之前再次检查长度和 SHA；缺失或错误版本使本次资源目录提取失败并给出依赖错误，不替换解码算法、不尝试其他 DLL。
- 工具保留校验文件的只读句柄直至此次处理结束，拒绝并发覆盖或替换。
- 默认公开构建遇到输出目录已有 Oodle 会报错，要求使用独立干净输出目录；不会删除用户自行提供的文件。

以上依赖的缺失检查只位于实际读取依赖的功能中，不是通用于全部安装、恢复或卸载流程的前置检查。恢复仍使用现有安装记录及备份。最终公开包必须另行通过允许清单和程序集内嵌资源检查；本地运行正常不能替代分发审查。

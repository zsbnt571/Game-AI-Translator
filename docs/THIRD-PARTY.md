# Third-party dependencies / 第三方依赖

The authoritative component table is [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md). It identifies purpose, source, licence-file location, source-repository inclusion, conditional Release permission and user-provided inputs for each reviewed component.

项目级组件表见 [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)，逐项说明用途、来源、许可证位置、源码仓库范围、Release 条件及用户自行提供的输入。

- [Redistribution policy / 再分发决策](redistribution-policy.json): CONDITIONAL requires the exact packaged version to satisfy its licence obligations; EXCLUDE must not enter a Release. Unlisted files default to EXCLUDE.
- [Dependency evidence / 依赖证据](third-party-dependencies.json): restored package metadata, resolved licence evidence and retained loader notices.
- [Local inputs / 本地依赖](local-dependencies.json): integrity hashes and local-only classifications. A hash or local import is not redistribution permission.

No executable Release is approved. Keep proprietary decoders, game reference assemblies, unknown binaries and unverified model/runtime bundles out of the source repository and Release. Preserve upstream copyright and legal notices. Project GPL licensing does not replace their terms.

尚未批准可执行 Release。专有解码器、游戏引用程序集、来源不明二进制及未核清的模型/运行时包不得进入源码仓库或 Release。保留上游版权和法律声明；项目 GPL 不替代它们的条款。

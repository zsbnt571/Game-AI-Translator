# Game AI Translator · Fusion 1.1.165

**简体中文** | [English](README.en.md) | [Русский](README.ru.md)

Fusion 是 Windows x64 游戏翻译、截图 OCR、游戏库与实验性 AI 工具软件。

## 下载与开始

从[1.1.165 发布页](https://github.com/zsbnt571/Game-AI-Translator/releases/tag/v1.1.165)下载 **Fusion-1.1.165.zip**，完整解压后运行 **GameTranslator.exe**。本版本为预发布版。

1. 在“设置 → 翻译方案”填写兼容的 API 或已运行的本地模型服务，并测试连接。
2. 选择目标语言、提示词预设与需要的词库，保存设置。
3. 导入游戏，使用该引擎支持的翻译方式，或使用截图翻译。

本包包含所需 .NET、Python 与 OCR 组件，不包含大语言模型。API 服务及其费用由你自行选择。部分网页功能需要 Windows WebView2 Runtime。

## 这次有什么

九语言界面与近期通知/报错完善；6 个提示词预设、6 个词库；长笔记与标签浏览；引擎图标外观；截图与无声录像；统一按键设置；翻译测试区与实验性 AI 工具。

详见[累计更新清单](docs/RELEASE-NOTES-1.1.165.md)与[验收范围](docs/VALIDATION-1.1.165.md)。最终主程序 3,967 项检查通过，0 失败；不等于所有游戏和模型均完成实测。

## 数据、升级与源码

默认数据保存在软件目录的 `data`，聊天保存在 `AI chat`；截图与录像分别在 `data/screenshot`、`data/video`。自定义数据位置继续按配置使用。

升级前从托盘退出软件并备份整个旧目录。先单独解压新包，再迁移自己的数据；不要让两个版本同时使用同一份数据。公开包不含个人密钥、数据或聊天记录。

开发者下载 **Fusion-1.1.165-Source.zip**；GitHub 自动生成的 Source code 压缩包不含二进制构建输入。[构建说明](docs/BUILD.md)列出所需工具和仍未完成的完整辅助组件重建验证。

项目自有代码为 GPL-3.0-only，依赖遵循各自许可证。参阅[第三方声明](THIRD_PARTY_NOTICES.md)。问题可提交到 [Issues](https://github.com/zsbnt571/Game-AI-Translator/issues)，请去掉密钥与私人数据。

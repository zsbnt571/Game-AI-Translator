# Fusion 1.1.165 — 九语言界面与翻译工作台 / Nine-language UI and translation workspace

**Windows x64 · Pre-release / 预发布版**

## 中文

这是相对公开版 1.1.128 的累计更新。1.1.165 的重点是补齐近期新增界面、通知与错误提示的九语言支持。

- **九语言界面**：简体中文、繁体中文、English、日本語、한국어、Español、Français、Deutsch、Русский。补齐设置、提示词与词库、标签、截图录像、测试区等近期功能的文字；修复切换语言后的下拉框和动态通知残留。已识别的服务错误与未知错误使用所选语言的摘要，技术详情进入诊断日志。
- **提示词预设 + 词库**：6 个可选预设、6 个内置词库（369 条词条），包括小说叙事、Galgame、菜单短句、社交平台和玩家交流场景。可组合选择词库，保留单游戏术语、附加要求和原有缓存。AO3 预设是 Fusion 针对游戏格式重写的适配方案，不是上游原文。内置词条主要面向英/日译简体中文，界面九语言不等于词库九向互译。
- **游戏库**：长笔记、格式编辑与链接；单独的标签浏览页；可选主副分类；引擎图标黑白/原色切换与预览；紧凑右键菜单和文件夹导航调整。
- **截图与录像**：截图保存 PNG、游戏窗口录像保存 MP4；快捷键集中到“设置 → 按键”，无需打开媒体窗口。默认保存到软件的 `data/screenshot` 与 `data/video`，按游戏归档。当前录像无声音；游戏最小化、退出或调整尺寸时会停止并保存。
- **设置与测试区**：按原有用途整理设置入口；支持提示词/术语试译、对比及显示相关测试。API 高级参数与支持服务的思考开关保留。
- **AI 工具实验功能**：对话与项目工作区布局、助手连接和状态处理继续完善。实验版提示保留；AI 修改可能影响游戏文件或存档，请先备份。

**验收**：最终主程序完成 3,967 项受控检查，0 失败；覆盖 207 个界面状态。检查包含设置、提示词/词库、测试区、多语言和凭据保存。此轮没有新发起真实游戏或翻译 API 测试，不代表所有游戏、模型或 Mod 已验收。

**升级**：先从托盘“退出”Fusion，完整备份旧目录；将新 ZIP 解压到新目录验证。迁移自己的 `data`、`AI chat` 和任何自定义数据位置时保留备份，不要让两个版本同时使用同一份数据。新包不含用户配置、密钥、游戏或聊天记录。跨 Windows 账户迁移后，受系统加密保护的 API 密钥可能需要重新填写。

界面语言位于“设置 → 软件通用”。游戏名称、路径、自定义标签、笔记、聊天和模型生成内容保留原文；Windows 原生文件对话框使用系统语言。

## English

This is a cumulative update from public version 1.1.128. Version 1.1.165 focuses on localization of recently added UI, notifications and errors.

- Nine interface languages: Simplified/Traditional Chinese, English, Japanese, Korean, Spanish, French, German and Russian. Recent settings, presets, glossaries, tags, capture and test-workspace text is localized. Language-switch refresh, dynamic messages and provider-error summaries have been improved.
- Six prompt presets and six glossary packs with 369 entries cover narrative, visual novels, concise menus, social-platform language and player communities. Multiple packs can be selected while retaining per-game terms and extra requirements. The AO3-inspired preset is rewritten by Fusion for game formats. Bundled terms primarily target English/Japanese to Simplified Chinese; they are not nine-direction glossaries.
- Rich game notes and links, a separate tag browser, optional primary/secondary categories, monochrome/original engine icons, compact context menus and folder navigation.
- PNG screenshots and MP4 window recording. Configure global shortcuts in **Settings → Keys** without opening the media window. Defaults are `data/screenshot` and `data/video`, grouped by game. Recording is currently silent and saves when the game is minimized, exits or changes size.
- Reorganized settings and a translation test workspace for prompt/term comparisons and display-related checks. Advanced API parameters and supported reasoning controls remain available.
- Experimental AI assistant/project tools with refined layout and state handling. Experimental warnings remain; AI edits may affect game files or saves, so back them up first.

**Validation:** 3,967 controlled checks passed with zero failures; 207 UI states covered. No new live translation-provider requests or original-game sessions were performed for this localization release. This is not universal game, model or Mod acceptance.

**Upgrade:** exit Fusion from its tray menu and back up the entire old installation. Extract into a separate folder first. Preserve your own `data`, `AI chat` and custom data-location configuration; do not run two versions against the same data. No user configuration, credentials, games or chat records are shipped. Windows-account-bound encrypted API keys may need re-entry after account migration.

Change interface language in **Settings → General**. User-authored content, game titles, paths and model output are not translated by the UI localization layer. Native Windows file dialogs follow the OS language.

## Downloads and build boundary

- **Fusion-1.1.165.zip**: runnable Windows x64 application. Extract the whole folder and run `GameTranslator.exe`.
- **Fusion-1.1.165-Source.zip**: current source, reviewed redistributable build inputs, licenses and corresponding third-party sources. GitHub's automatic source archives omit binary build inputs.
- **SHA256SUMS.txt**, **PACKAGE-VERIFICATION.json**, **VALIDATION-1.1.165.md**: integrity and validation records.

The desktop was rebuilt with .NET 8.0.33. Unchanged capture helpers, other helpers, Unity payloads, native bridges, bundled assistants and runtimes were retained from verified 1.1.164. A full rebuild of every auxiliary component on a clean machine is not yet validated. Existing engine limitations continue to apply. Runtime inclusion does not include a local language model; configure your own compatible API/service.

Project-owned code is GPL-3.0-only. Component licenses, attribution and corresponding-source material are included. See `THIRD_PARTY_NOTICES.md` and `docs/BUILD.en.md` in the source archive.

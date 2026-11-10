# Fusion 1.1.165 — 从公开版 1.1.128 到现在的累计更新

**Windows x64 · 预发布版 / Pre-release**

这份说明补全了首份发布说明中省略的字体、角色与性别、测试区、AI 工具等变化。它介绍 1.1.129–1.1.165 的累计产品变化；末尾单独列出本次 1.1.165 的验收范围。早期已有的基础字体选择、自动性别证据和资源预翻译，在这里按后续改进说明，不算作从零新增。

## 1. 游戏字体自定义与显示

- 在游戏详情的翻译显示设置中选择自定义字体、预览或恢复默认。字体选择按游戏保存；支持的适配可在运行时应用和恢复，不具备热更新条件的版本在下次启动时生效。
- 扩充字体能力识别、字体文件校验、缺字检查与加载失败回退。界面会区分“选择已保存”“等待下次启动”和实际加载状态，避免把选择成功当作游戏画面已经替换成功。
- 累计完善 Ren’Py、RPG Maker、Godot、Unity、GameMaker、WOLF、Defold、BAKIN 和部分 Web 引擎的相关适配。支持取决于具体引擎版本及文字组件，不能据此理解为同一引擎的所有游戏都兼容。
- 特别限制：RGSS 使用已安装字体；部分适配只接受独立 TTF/OTF；Unity 的旧版或自定义文字组件可能无法替换；GameMaker 限已接入的 2.3+ VM；Unreal 自定义字体仅在识别到相应 UE 4.27 原生字体接口时应用。图片文字、位图字体和其他未识别路径不保证适用。
- 测试区可检查默认补字方案或自定义字体，编辑样例，调整预览字号、行距与换行，查看多语种缺字、排版和打字效果。这里的预览不会直接更改游戏字体。

## 2. 角色资料与性别增强

- 增加按游戏保存的角色资料和角色管理界面，可查看识别结果，为已识别角色选择男性、女性、不指定，或恢复自动识别。
- 翻译设置中的“角色与性别增强”控制这些信息是否参与请求。主要用于译成英语、西班牙语、法语、德语和俄语时需要的性别语法一致性，不是给所有目标语言增加一个通用性别翻译开关。
- 区分“读到角色名册”“资源预翻译中已绑定台词”和“运行时已确认说话者”。没有可靠台词归属时继续普通翻译，不凭名字猜测，也不把说话者性别套到听者或第三人称上。
- 手动设置优先于自动资料；“不指定”表示不提供性别提示，不代表替角色指定某种性别身份。
- 角色译文按身份和上下文分开缓存；开关或设置变化时取消失效请求，保留人工译文、普通缓存及可用的历史变体，防止旧请求回写或不同角色的相同台词相互混用。
- 具体效果仍依赖游戏提供的角色信息和翻译模型；不承诺每个游戏都能识别所有角色。

## 3. 翻译准备、补译和稳定性

- 继续完善“读取文本 → 准备译文 → 启动时复用缓存”的流程；Unreal 的规范文本目录接入准备译文，提取支持取消和同进程重复任务复用。
- 译文管理可对所选内容立即补译、停止补译，保留人工修订和失败记录。
- 修复部分旧 RPG Maker MV / NW.js 收到译文后当前对白仍不刷新的问题，保留打字进度、原文和游戏绘制状态。
- 继续完善 Godot 资源读取和多引擎字体、角色运行路径。仍按游戏与引擎的实际适配范围使用，不能把资源提取成功等同于游戏内显示已通过。

## 4. 提示词预设与词库

- 在“设置 → 翻译方案 → 翻译内容”选择一个预设，并独立选择多个词库；不再要求用户把表达风格和提示词拆成两套重复设置。
- 新增六个场景预设：AO3 叙事适配、Galgame 剧情对白、忠实对白、菜单与短句、推特与网络表达、玩家交流与社区。原有翻译风格和附加要求继续兼容。
- AO3 是 Fusion 面向游戏格式独立重写的适配预设，并非沉浸式翻译上游原提示词的逐字复制。
- 六个可选内置词库共 369 条：日语通用游戏、日语 Galgame、英语菜单、AO3 标签、英语社交平台和日语社交平台。主要面向英/日译简体中文。
- 管理窗口支持查看内容、复制为自定义、新建，以及导入两列 CSV、TSV 或 JSON 原文到译文的映射；选择后在翻译内容页保存。
- 词库可同时开启。只应用语言匹配、当前文本命中的词条；人工单游戏术语优先，词库冲突按选择顺序处理。已有译文不会因选词库而整体清空。

## 5. 测试区：先试再决定

- **翻译试用**：输入文本，比较 API、提示词预设或词库，查看译文、耗时及命中术语，支持临时术语和停止。
- 试用只有点击后才向所选服务发起请求，可能产生 API 费用；结果不写入游戏缓存、正式术语或角色偏好。
- **字体预览**：用于查看字体覆盖、缺字和对白排版；与正式游戏字体设置分开。
- **截图录像自检**：使用独立测试窗口检查 PNG 截图和无声 MP4 录像。
- **高级诊断**：集中引擎检查、运行环境状态、角色与缓存检查及报告。诊断样例通过不等于原游戏、所有显卡或全部模型已经验收。

## 6. AI 工具与 Mod 工作区（实验功能）

- 接入 Codex、OpenCode 助手。可以直接聊天、编程、分析文件或选择项目工作；关联游戏为可选项，需要 Fusion 游戏工具时再关联。
- AI 连接与翻译方案独立；兼容助手可以共用 API 连接。支持选择或搜索模型，以及在服务支持时调整当前对话的思考档位。账号登录和 API 连接按各助手支持范围处理。
- 支持文件/图片附件、执行过程、工具活动、批准请求、澄清问题、停止任务及保存会话。图片输入取决于模型能力。
- 对话支持重命名、置顶、菜单操作和草稿恢复；移除列表中的对话时保留项目文件及已保存的执行材料。
- 支持请求批准、适用助手的自动审查和完全访问等权限选项。完全访问可能直接修改项目外文件或执行命令；不应把所有模式描述为始终只读。
- Mod 库支持 Fusion 文本文件 Mod 的草稿、预览、导入/导出、安装、备份与停用恢复；文件变化、冲突或游戏正在运行时会阻止不合适的安装操作。这个管理流程目前面向支持的独立 UTF-8 文本文件，不是任意游戏 Mod 格式管理器。
- 已接入引擎可使用受支持的数值读取、查找、校验与确认修改流程；尚未接入 CE 通用内存扫描。
- 实验版风险提示保留，提供关闭、继续使用和“不再提醒”。修复最大化时侧栏偏离左边的问题，聊天正文和输入区保留适合阅读的宽度。
- 本版使用随包固定版本的助手，并不声称已同步上游最新版本；会话迁移保留 Fusion 聊天资料，不保证恢复每个原生助手的全部内部上下文。

## 7. 游戏库、笔记与标签

- 游戏详情新增长笔记，放在游戏翻译、游戏修改前；支持加粗、斜体、下划线、高亮、列表、撤销/重做、自动保存、Ctrl+S 和搜索。网页链接可 Ctrl+单击，或右键打开/复制。
- 保留原游戏库；默认开启引擎分类，主分类、副分类按需启用，可选开发状态、个人评级或自定义标签组。
- 独立标签浏览页使用顶部筛选，支持批量打标签。开发状态含更新中、完结、废弃；评级和其他标签可自行管理。未打标签的游戏仍在全部游戏中。
- 真实文件夹可展开已收录的游戏，进入详情后保持导航位置。移除容易混淆的“新建虚拟文件夹”入口，已有虚拟收纳作为分类继续保留，不移动磁盘游戏。
- 整理紧凑右键菜单、资料编辑入口和重复命名；分类显示设置固定在左下角，并缩小占用空间。
- 游戏路径失效时仍可打开详情、笔记和资料，启动控件显示不可用状态；重新定位保留原游戏身份与已有资料，不要求重新添加全部游戏。

## 8. 截图与录像

- 保存游戏窗口原尺寸 PNG，录制 H.264 MP4；按游戏显示媒体、打开图片/视频或保存文件夹。
- 快捷键统一在“设置 → 按键”：默认 Ctrl+Alt+F9 截图，Ctrl+Alt+F10 开始/停止录像；可修改、禁用或恢复默认，并检查冲突。
- Fusion 运行时，在已加入游戏库的游戏窗口前台使用即可，不用先打开媒体窗口。
- 默认分别保存到软件目录的 `data/screenshot/游戏名`、`data/video/游戏名`。按稳定游戏身份保留归属，改名或重新定位不会把原媒体当作新游戏；旧媒体文件继续保留。
- 当前录像无声音。游戏最小化、退出或改变尺寸时结束并尝试保存；单段最长两小时，空间不足会停止。录音、回放缓冲、剪辑、直播和全屏独占兼容保证不在本次范围内。

## 9. 外观、设置与数据保存

- 设置按常用、翻译、库与数据、工具整理；保留已有设置和高级 API 参数，区分即时保存、待保存项、API 方案与翻译内容的独立保存。
- 引擎图标提供黑白/原色，支持对照预览与 16–28 px 大小，应用后即时刷新并持久保存。不同 RPG Maker 版本和 WOLF 使用更合适的图案；任意图片替换、文件夹和收藏图标自定义仍未加入。
- 数据入口集中到“设置 → 存储位置”。普通资料默认 `data`，AI 聊天默认 `AI chat`，截图、录像、笔记、译文、历史、日志等有明确目录入口。
- 保留用户配置、游戏库、笔记、人工译文和聊天；数据迁移采用校验复制并保留来源，现有自定义位置不会被静默重置。
- 移除反复出现的密钥恢复弹窗/横幅，同时保留加密凭据及真正执行请求时的凭据检查。跨 Windows 账户迁移后，系统保护的密钥仍可能需要重新输入。

## 10. 1.1.165 的九语言补齐

- 简体中文、繁體中文、English、日本語、한국어、Español、Français、Deutsch、Русский。
- 补齐近期设置、预设/词库、标签、截图录像、测试区的界面、通知和错误提示；修复切换语言后的动态文字与下拉选项残留。
- 未知接口或系统异常显示所选语言的摘要，脱敏技术详情记录到日志。
- 界面语言位于“设置 → 软件通用”。游戏原文、用户自定义名称/提示词/词条、路径、聊天和模型输出保留原文；Windows 原生对话框跟随系统语言。九语言界面不代表内置词库九向互译。

## 验收与当前边界

- 本次最终 1.1.165 主程序：**3,967 项受控检查通过，0 失败；覆盖 207 个界面状态**。包括设置 156、预设/词库 50、测试区 82、多语言 3,638、凭据保存与恢复 41 项。
- 这些是本次实际完成的检查，没有把历史版本的测试数量累加。本次未重新发起真实翻译 API、原游戏或 AI 聊天生成测试；历史游戏/API 验证不能代替所有当前场景的验收。
- 桌面主程序已重建；未改动的辅助组件、原生桥接、Unity 载荷、助手及运行时按哈希沿用已核验的 1.1.164。全部辅助组件从源码在干净机器完整重建仍未验收。
- 当前源码已发布，并接续原公开仓库历史；不声称找回所有中间本地开发历史。
- F95 内置浏览/外部标签、公开社区分享、CE 通用扫描、通用 CG 解锁、超分辨率/DLSS，以及录音等计划不作为本次已完成内容。

## 下载和升级

- `Fusion-1.1.165.zip`：Windows x64 软件包，完整解压后运行 `GameTranslator.exe`。
- `Fusion-1.1.165-Source.zip`：当前源码、审核过的可再分发构建输入、许可证及对应第三方源码。GitHub 自动生成的 Source code 压缩包不包含全部二进制构建输入。
- 本文件为第二次文档修订，英文与中文逐项对应；补充包内首份发布说明。`VALIDATION-1.1.165.md`、`PACKAGE-VERIFICATION.json`、`SHA256SUMS.txt` 提供验证范围与原附件校验。本累计说明的校验见 `CUMULATIVE-CHANGES-1.1.165-r2.sha256`。

升级前从托盘退出 Fusion 并备份旧目录。先把新包解压到独立目录；迁移时保留自己的 `data`、`AI chat` 和自定义位置设置，不要让两个版本同时使用同一份数据。公开包不含个人游戏、配置、API 密钥或聊天记录。

---

## English

### Fusion 1.1.165 — cumulative changes since 1.1.128

**Windows x64 · Pre-release**

This release brings together the changes made since the last public version, 1.1.128: expanded game font support, character and grammatical gender controls, a translation test workspace, experimental AI and Mod tools, richer library features, and game capture. Version 1.1.165 completes the nine-language localization of the recently added interfaces, notifications and error messages.

Font selection, basic automatic gender evidence and resource pretranslation were already available in 1.1.128. The notes below distinguish improvements to those features from new additions. The validation section describes the checks performed specifically for 1.1.165.

### 1. Custom game fonts and text display

- Choose a font, preview it or restore the default from a game's translation display settings. Choices are saved per game. Supported adapters can apply and restore fonts while the game is running; versions without live font replacement apply the change on the next launch.
- Improved capability detection, font-file validation, missing-character checks and fallback when a font cannot load. The interface distinguishes a saved selection, a change waiting for the next launch and the reported loading state, so saving a choice is not presented as proof that the game has rendered it.
- Extended the relevant integrations for Ren’Py, RPG Maker, Godot, Unity, GameMaker, WOLF, Defold, BAKIN and several web-based engines. Compatibility depends on the engine version and text component, rather than the engine name alone.
- Some restrictions remain: RGSS uses installed fonts; several adapters require standalone TTF or OTF files; older or custom Unity text components may not accept a replacement. GameMaker support is limited to the integrated 2.3+ VM paths. Unreal font replacement requires the recognized UE 4.27 native font interface. Image-based text, bitmap fonts and unrecognized rendering paths are not covered by a general compatibility guarantee.
- The test workspace lets you preview Fusion's default missing-character fallback fonts or a custom font, edit sample text, adjust font size, line spacing and wrapping, and inspect multilingual coverage and typewriter effects. These previews do not change a game's saved font settings.

### 2. Character profiles and grammatical gender

- Added character profiles saved separately for each game, with a management interface for reviewing detected information. For an identified character, choose male, female, no gender hint, or restore automatic detection.
- The character and gender enhancement setting controls whether this information is included in translation requests. It currently assists grammatical agreement when translating into English, Spanish, French, German or Russian; it is not a universal gender-rewriting option for every target language.
- The system distinguishes a character found in a roster, a character linked to dialogue during resource pretranslation, and a speaker identified at runtime. When dialogue cannot be attributed reliably, ordinary translation continues. A name alone does not establish gender, and the speaker's gender is not applied to the listener or to other people mentioned in the line.
- Manual choices take priority over automatic information. Choosing no gender hint omits that guidance; it does not assign the character a particular gender identity.
- Character-specific translations are cached by identity and context. Changing the setting or a character choice cancels requests that are no longer applicable, while retaining manual translations, ordinary caches and usable previous variants. This prevents a late response from overwriting current work or identical lines from being mixed between speakers.
- Results still depend on the character information available from the game and on the translation model. Not every game exposes enough information to identify every speaker.

### 3. Pretranslation, selective retries and stability

- Improved the workflow from extracting text to preparing translations and reusing them at launch. Unreal text catalogs now feed into translation preparation; extraction can be cancelled, and duplicate work within the same process can reuse the existing task.
- The translation manager can translate or retry selected entries immediately, and stop that work without discarding manual edits or failure records.
- Fixed a problem in some older RPG Maker MV / NW.js runtimes where the current dialogue did not refresh after its translation arrived. The fix preserves typewriter progress, the original text and the game's drawing state.
- Continued work on Godot resource reading and the font and character integrations used by multiple engines. Successful resource extraction does not, by itself, establish that the translated text has displayed correctly in the original game.

### 4. Prompt presets and glossary packs

- Choose one prompt preset and any compatible glossary packs in **Settings → Translation profiles → Translation content**. The preset contains the translation instructions and tone, avoiding two overlapping selections for style and prompts.
- Six new presets cover AO3-inspired narrative translation, Galgame dialogue, faithful dialogue, menus and short lines, Twitter/social-media language, and player communities. Existing translation styles and additional user instructions remain compatible.
- The AO3-inspired preset is an independent Fusion adaptation for game formats. It is not a verbatim copy of Immersive Translate's upstream prompt.
- Six optional bundled packs contain **369 entries**: general Japanese game terms, Japanese Galgame terms, English menus, AO3 tags, English social-media terms and Japanese social-media terms. These packs primarily support translation from English or Japanese into Simplified Chinese.
- The manager lets you inspect entries, copy a preset or pack for customization, create your own content, and import glossaries from two-column CSV or TSV files or JSON source-to-translation mappings. Save the translation-content settings after making your selection.
- Several packs can be enabled together. Only entries matching the configured languages and the current text are applied. Manually defined per-game terms take priority; conflicts between packs follow their selection order. Choosing a pack does not clear all existing translations.

### 5. Test workspace: try settings before applying them

- **Translation trials:** enter sample text and compare API connections, prompt presets or glossary packs. Review the translations, elapsed time and matched terms, add temporary terms, and stop a trial when needed.
- Requests are sent only when you start a trial and may incur charges from the selected provider. Trial results are not written into game caches, saved terminology or character preferences.
- **Font preview:** inspect character coverage, missing glyphs and dialogue layout independently of the font settings used by an actual game.
- **Screenshot and recording self-test:** use a separate test window to check PNG capture and silent MP4 recording.
- **Advanced diagnostics:** access engine checks, runtime status, character and cache checks, and their reports in one place. Passing a diagnostic example does not verify every original game, graphics configuration or model.

### 6. AI assistants and the Mod workspace — experimental

- Added Codex and OpenCode assistants for conversation, coding, file analysis and project work. Linking a game is optional; link one when you need Fusion's game-specific tools.
- AI connections are separate from translation profiles. Compatible assistants can share an API connection. Models can be selected or searched, and supported services expose reasoning controls for the current conversation. Account sign-in and API support depend on the assistant.
- The workspace supports file and image attachments, execution progress, tool activity, approval requests, follow-up questions, stopping a task and saving the conversation. Image input requires a model that supports it.
- Conversations can be renamed, pinned and managed through their menus, with draft restoration. Removing a conversation from the list preserves its project files and saved execution materials.
- Available permission options include requesting approval, automatic review where supported, and full access. Full access can allow commands to run and files outside the project to be changed; not every mode is read-only.
- The Mod library supports drafts, previews, import/export, installation, backups and disabling a Mod to restore its original files. It checks for changed files, conflicts and running games before proceeding. This workflow currently manages supported standalone UTF-8 text-file Mods, rather than every game's Mod format.
- Supported game adapters expose numeric inspection, searching, verification and confirmed changes. **Generic Cheat Engine memory scanning is not included.**
- The experimental-feature notice remains, with options to close it, continue or stop showing it again. The maximized layout has been fixed so the sidebar stays at the left while the conversation and input area retain a comfortable reading width.
- The release includes fixed assistant versions; it does not claim to bundle the latest upstream versions. Moving the AI data preserves Fusion's chat records, but does not guarantee restoration of every native assistant's complete internal context.

### 7. Game library, notes and tags

- Added long-form notes before the translation and modification tabs in game details. Notes support bold, italic, underline, highlighting, lists, undo/redo, autosave, Ctrl+S and search. Open web links with Ctrl+click, or use the context menu to open or copy them.
- The original library layout remains available. Engine grouping is enabled by default; primary and secondary categories can be enabled as needed, using development status, personal ratings or custom tag groups.
- A separate tag browser provides filters at the top and batch tagging. Development status includes ongoing, completed and abandoned games. Ratings and other tags can be customized, and games without tags remain visible in the complete library.
- Real folders can expand to show games already in the library, and opening game details preserves the navigation position. The confusing virtual-folder creation entry has been removed. Existing virtual collections remain available as categories; game files are not moved on disk.
- The context menu is more compact, repeated editing entries have been consolidated, and category-display settings stay fixed at the bottom left with a smaller footprint.
- If a game's executable is missing, its details, notes and saved information remain accessible while launch controls show that it is unavailable. Relocating the executable preserves the existing game record and its associated information, without requiring the whole library to be imported again.

### 8. Screenshots and recording

- Save PNG screenshots at the game window's original size and record H.264 MP4 video. Browse media by game, open an image or video, or go directly to its saved folder.
- All capture shortcuts are available under **Settings → Keys**. The defaults are **Ctrl+Alt+F9** for a screenshot and **Ctrl+Alt+F10** to start or stop recording. Shortcuts can be changed, disabled or restored to their defaults, with conflict checks.
- While Fusion is running, use the shortcuts with a library game's window in the foreground. Opening the media window first is no longer required.
- Files are saved by default under `data/screenshot/<game name>` and `data/video/<game name>` inside the software folder. A stable game identity keeps media associated with the same game after a rename or relocation, and existing media files are retained.
- Recording is currently **video only, with no audio**. Minimizing, closing or resizing the game ends the recording and attempts to save it. A recording is limited to two hours and stops if disk space runs low. Audio capture, replay buffers, editing, streaming and guaranteed exclusive-fullscreen compatibility are outside this release's scope.

### 9. Appearance, settings and data storage

- Settings are organized by everyday options, translation, library/data and tools. Existing options and advanced API parameters are retained. The interface distinguishes settings saved immediately from pending changes, and keeps the separate save actions for API profiles and translation content clear.
- Engine icons can use monochrome or original colors. A side-by-side preview supports sizes from **16 to 28 px**, with immediate sidebar refresh and persistent settings after applying a choice. RPG Maker versions and WOLF use more appropriate artwork. Arbitrary image replacement and custom folder/favorite icons are not included yet.
- **Settings → Storage locations** brings the data-folder entries together. General data defaults to `data`, and AI conversations to `AI chat`, with clear access to screenshots, recordings, notes, translations, history and logs.
- Existing configuration, library records, notes, manual translations and chats are preserved. Data migration verifies the copied files and retains the source; an established custom location is not silently reset.
- Repeated credential-recovery popups and banners have been removed, while encrypted credentials and checks made when sending a request remain. Windows-protected API keys may still need to be entered again after moving to a different Windows account.

### 10. Nine-language coverage in 1.1.165

- The interface supports **Simplified Chinese, Traditional Chinese, English, Japanese, Korean, Spanish, French, German and Russian**.
- This update completes the recently added settings, prompt/glossary, tagging, capture and test-workspace interfaces, notifications and error messages. It also fixes dynamic text and dropdown options left in the previous language after switching.
- Unknown provider or system errors show a summary in the selected interface language; sanitized technical details are recorded in the logs.
- Change the interface language under **Settings → General**. Game text, user-defined names, prompt and glossary content, paths, chats and model output retain their original language. Native Windows dialogs follow the operating-system language. A nine-language interface does not mean the bundled glossaries support translation in all nine language directions.

### Validation and remaining limitations

- The final 1.1.165 desktop application passed **3,967 controlled checks, with zero failures**, covering **207 UI states**: settings 156, presets/glossaries 50, test workspace 82, localization 3,638, and credential persistence/recovery 41.
- These figures are the checks run for this release, not a total accumulated from earlier versions. This round did not repeat live translation-provider requests, original-game sessions or AI-generated conversations. Earlier game/API results do not establish acceptance of every current scenario.
- The desktop application was rebuilt. Unchanged helpers, native bridges, Unity components, assistants and runtimes were retained from verified 1.1.164 and checked by hash. Rebuilding every auxiliary component from source on a clean machine has not yet been validated.
- The current source is published on top of the existing public repository history. This does not claim that every intervening local development commit has been recovered.
- Planned features such as integrated F95 browsing and external tags, public community sharing, generic CE scanning, universal CG-gallery unlocking, upscaling/DLSS and recording audio are not presented as completed features in this release.

### Downloads and upgrading

- **[Download Fusion 1.1.165 for Windows x64](https://github.com/zsbnt571/Game-AI-Translator/releases/download/v1.1.165/Fusion-1.1.165.zip)** — extract the entire folder and run `GameTranslator.exe`.
- **[Download the source package](https://github.com/zsbnt571/Game-AI-Translator/releases/download/v1.1.165/Fusion-1.1.165-Source.zip)** — includes the current source, reviewed redistributable build inputs, licenses and corresponding third-party source. GitHub's automatically generated source archives do not include all binary build inputs.
- **[Read the complete bilingual release notes](https://github.com/zsbnt571/Game-AI-Translator/releases/download/v1.1.165/CUMULATIVE-CHANGES-1.1.165-r2.md)** — this revised document supplies a full English counterpart to the Chinese notes. Its checksum is in `CUMULATIVE-CHANGES-1.1.165-r2.sha256`. `VALIDATION-1.1.165.md`, `PACKAGE-VERIFICATION.json` and `SHA256SUMS.txt` retain the validation scope and checksums for the original packages and attachments.

Before upgrading, exit Fusion from its system-tray menu and back up the old installation. Extract the new package into a separate folder first. Preserve your own `data`, `AI chat` and custom data-location settings when migrating, and do not run two versions against the same data. The public packages contain no personal games, configuration, API keys or chat history.

Source commit: `406740dd13246eb9ae95e69c190d9c24c99030e1`. Project-owned code is GPL-3.0-only; dependencies retain their own licenses. This revision updates the release documentation only. The tested binaries and the software/source package checksums are unchanged.

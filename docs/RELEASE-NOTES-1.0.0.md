# Fusion R1 1.0.0 Preview 1

**游戏修改：实验阶段 / Game modification: Experimental / Изменение игровых данных: экспериментально**

[中文](#中文) · [English](#english) · [Русский](#русский)

[Downloads / 下载 / Загрузка](https://github.com/zsbnt571/Game-AI-Translator/releases/tag/v1.0.0-preview.1) · [Source / 源码 / Исходники](https://github.com/zsbnt571/Game-AI-Translator/tree/v1.0.0-preview.1)

Main application: **Fusion-1.0.0.zip** · Complete source: **Fusion-1.0.0-Source.zip**

Optional NVIDIA background-processing dependencies: [Fusion-0.6.0.178-GPU.zip](https://github.com/zsbnt571/Game-AI-Translator/releases/download/v0.6.0.178-preview.1/Fusion-0.6.0.178-GPU.zip) — reused from 178; not standalone, and does not increase the interface frame rate.

## 中文

**这是预览版本。游戏修改功能仍处于实验阶段，支持范围取决于引擎版本、游戏结构和实际读取到的数据。**

本版整合了公开的 0.6.0.178 俄语预览版之后的界面重做、翻译流程与游戏工具改进。1.0.0 是新的版本编号起点，不代表所有引擎、游戏或功能已完成稳定版验收。

### 界面与游戏库

- 主窗口、游戏库、详情、设置、截图、历史、译文管理和修改页面采用统一的 WPF 界面，整理按钮、菜单、焦点提示、浅色/深色主题和窄窗口布局。
- 游戏库右侧现在整页纵向滚动：标题、搜索、近期横向栏和游戏网格一起移动。左侧导航独立滚动；横向栏仍可横向滚动。进入详情再返回时保留浏览位置。
- 切换游戏时保留完整布局，目标游戏的标题、封面和按钮准备好后一起显示。减少封面突然消失、内容上下跳动，以及同一页面反复刷新按钮的现象。
- 减少重复点击当前分类产生的重绘；修改分类往返复用已读数据，保留选中对象和滚动位置。修复已复现的分类切换长时间等待、旧请求覆盖新页面，以及搜索/译名更新触发重复键错误的路径。
- 游戏名称旁及修改页提供截图封面入口，可检查原图和横幅裁剪。客户区截取和黑帧检查得到改进；截图失败会提示，并保留已有封面。

### 翻译优先显示与补救

- 有意义、玩家可见的外语名称和词句默认翻译，包括人物、称谓、地点、国家、技能、职业、物品、任务、菜单和结局名称。可以自行将某个名称设为“保留原文”。
- 单字母/假名输入格、快捷键、控制符、占位符、代码及路径继续受到保护，不会被扩写成普通词句；语言选择项保留原来的名称。
- 已有可用译文优先显示。少量原文残留或统一译名未完全匹配时，保留可读结果并安排有限补译，避免一直没有译文。补救状态及原因可在译文管理中查看；达到重试上限后停止自动请求。
- 控制符、数值或占位符损坏，以及仅返回解释/拒答而没有正文的结果，不会作为有效游戏译文写入。已有可用结果和人工更正继续保留。
- 预译、实时翻译和修改页名称翻译共用这些处理规则；当前对白优先，已保存缓存优先复用。动态计数和已识别时间格式的复用也得到改进，减少重复请求与原译文来回闪动。

### 名称一致性与译文管理

- “词语与名称”独立页面按人物、地点、技能、物品等分类，显示原词、统一译名、状态和来源，方便提前检查或修改重要名称。
- 同一对象采用统一名称，普通词语仍按句子语境翻译。任务和事件标题单独适用，不再把整个任务标题机械套进包含相同短语的普通对白。
- 名称规则按游戏及翻译目标语言分别保存。修改统一译名会安排相关自动译文更新；人工更正保留，冲突会列出。
- 清理有可靠来源证据的错误自动名称候选，例如误收为人物名的对白或内部字段标签。普通译文、人工规则和导入词条不会因此被一并清空。

### 文本读取与预译

- “读取文本与准备译文”提供文件结果、原文/译文、进度和错误说明，分别显示原文出现次数、去重条目、当前语言范围及精确缓存命中，便于理解尚未完成的部分。
- 改进受支持 WOLF 资源、配对数据库和地图的读取与收集，处理较深目录导致的采集失败；准备好的译文逐批保存，并在之后启动时复用。部分定制逐字显示路径只对经过资源校验的版本启用。
- 修复符合条件的 Ren’Py 原始对白/菜单和 Godot 共享显示字段未进入预译的问题。运行后才产生的文本仍可能需要实时补译；进度达到 100% 只表示当前已发现范围完成。

### 游戏修改——实验阶段

- 常用、角色、关系、物品、变量、地图、事件、锁定和通用数据按实际可读取能力组织；不存在或尚未识别的类别不会被当成已有能力展示。
- 常用页集中放置金钱等候选及常用操作，不再要求从大量角色属性中查找，也不带搜索条件。角色和关系按对象分组，完整数据仍可在对应变量或数据库页查看。
- WOLF 角色页采用与 RPG Maker 接近的列表和详情布局，原名与译名同时显示；右侧只显示选中对象实际读取到的属性。更换分类或收到后台译名时保留未提交的数值输入。
- 常用页增加可能的 CG/回想字段分组，默认折叠。它展示有依据的候选，不会自动解锁，也不保证识别完整回想系统。
- RPG Maker 已能读取的物品图标继续显示。WOLF 的通用图标映射尚不完整；地图预览是可读取的格点与事件数据，并非完整贴图编辑器。

### 调速

滑条、预设倍率按钮和减/加按钮操作同一个速度值；拖动时预览、结束后提交。当前 WOLF 接口范围为 **0.25–100×**，RPG Maker MV/MZ 为 **1–100× 整数加速**。实际速度受游戏负载影响，选择 100× 不表示能够达到实际百倍运行。其他引擎尚未统一提供可靠调速接口；这也不改变软件界面的帧率。

### 语言、方案与性能

- 补齐近期新增页面的英语和俄语，包括动态计数、进度、错误、分类和地图说明。已打开页面切换语言时更新文字，保留选择、滚动位置及输入；长表头自动调整或换行。
- 软件界面语言与游戏翻译目标独立：按钮使用界面语言，游戏名称和字段译名使用当前游戏方案的目标语言，原文保持独立显示。
- 翻译方案支持高级 JSON 请求参数，并提供常用示例；删除仍被使用的方案时可选择替代方案。API 连接设置与翻译内容设置分别保存。
- 游戏库减少不可见内容绘制，复用封面与缩放结果，并减少相同状态的布局更新。持续呈现失败时可针对当前窗口启用兼容绘制。**本版没有承诺稳定 60 FPS；高分辨率最大化仍可能明显变慢。**

### 验证与仍有限制的范围

本次发布构建完成 **615 / 615 项受控检查，全部通过**，包含 342 项界面与语言检查和 273 项修改/翻译回归检查。这些结果不等于真实游戏长期游玩或物理屏幕帧率验收。受控检查、真实控件截图、引擎/API 样本和实际游戏测试分别说明验证范围，不能推广为全部游戏兼容或屏幕帧率保证。

自动名称、金钱和回想识别仍依赖游戏的数据结构；部分大库首次名称准备可能较慢。图片内文字、自定义绘制、未知封包和动态文本仍可能漏读或漏译。所有引擎、长期游玩、高倍速音画表现和各类显示器组合尚未全面验证。

下载并完整解压 **Fusion-1.0.0.zip**，运行 `GameTranslator.exe`。**Fusion-1.0.0-Source.zip** 为开发者源码包。首次使用建议解压到新目录，并配置自己的翻译 API；费用由服务商决定。

## English

**This is a preview release. Game modification is EXPERIMENTAL. Available features depend on the engine version, game structure and data that Fusion can actually read.**

This release brings together the interface, translation and game-tool changes made after the public 0.6.0.178 Russian preview. The 1.0.0 number starts a new version series; it does not mean that every engine, game or feature has completed stable-release validation.

### Interface and library

- The main window, library, game details, settings, capture, history, translation management and modification pages use a consistent WPF interface, with updated buttons, menus, focus cues, light/dark themes and narrow layouts.
- The right side of the library now scrolls as one page, including its heading, search, recent shelf and game grid. The sidebar remains independent, the shelf retains horizontal scrolling, and returning from game details preserves the browsing position.
- Switching games keeps the layout intact until the new title, cover and controls are ready. This reduces disappearing covers, page jumps and repeated button resets on an otherwise unchanged page.
- Reopening modification categories reuses loaded data and preserves selection and scrolling. Repairs address reproduced long waits, outdated replies replacing the latest page, and duplicate-key errors during search or translated-name updates.
- Capture a cover from the game title area or modification page, then inspect the original image and banner crop. Client-area capture and black-frame checks are improved; failed captures report the problem and keep the previous cover.

### Usable translations first

- Meaningful player-visible foreign names and text are translated by default, including people, honorifics, places, countries, skills, classes, items, quests, menus and endings. You can explicitly choose to keep an individual name unchanged.
- Character-entry alphabet/kana cells, shortcut keys, control tokens, placeholders, code and paths stay protected. Language-selection labels retain their original names.
- Readable translations remain visible while small untranslated fragments or naming inconsistencies receive limited repair attempts. Translation management shows the status and reason; automatic requests stop at the retry limit.
- Results with damaged control tokens, numbers or placeholders, or responses containing only an explanation/refusal, are not accepted as valid game text. Existing usable translations and manual corrections are preserved.
- Pretranslation, live translation and modification-page name translation share these rules. Current dialogue takes priority and saved results are reused. Reuse of changing counters and recognized clock formats also reduces repeated requests and switching between original and translated text.

### Consistent names and translation management

- The separate Words and Names page groups people, places, skills, items and other supported categories. It shows the source, preferred translation, status and evidence so important names can be reviewed in advance.
- Identified entities keep consistent names while ordinary words remain context-sensitive. Quest and event titles have their own scope instead of forcing their complete fixed wording into unrelated dialogue.
- Naming rules are stored separately for each game and target language. Changing a preferred name schedules related automatic translations for updating; manual corrections stay intact and conflicts are listed.
- Removes incorrect automatic name candidates when reliable source evidence identifies dialogue or internal field labels. It does not clear ordinary translations, manual rules or imported terminology along with them.

### Text reading and pretranslation

- Read Text and Prepare Translations shows file results, source/translation pairs, progress and errors. Source occurrences, unique entries, the selected language scope and exact cache matches have separate counts.
- Improves collection from supported WOLF resources, paired databases and maps, including failures caused by deep directory paths. Results are saved in batches and reused on later launches. Some custom typewriter-display support is enabled only for resource-verified versions.
- Repairs eligible Ren’Py original dialogue/menu and Godot shared display fields that previously failed to enter pretranslation. Text created at runtime can still need live translation; 100% means completion of the currently discovered scope.

### Game modification — Experimental

- Common, characters, relationships, items, variables, maps, events, locks and general data are organized around capabilities actually read from the game. Missing or unidentified categories are not presented as supported features.
- Common actions collect money candidates and frequently used controls without a search filter or a long list of every character attribute. Characters and relationships are grouped by object; full data remains available in the appropriate variable/database pages.
- WOLF character browsing now follows the RPG Maker-style list/detail layout, displaying original and translated names together. Only the selected object's actual attributes are shown. Category changes and arriving name translations preserve unsaved numeric input.
- Common actions include a collapsed group of possible CG/replay fields. These are evidence-based candidates, not automatic unlocking or guaranteed coverage of the game's gallery system.
- Readable RPG Maker item icons remain available. Generic WOLF icon mapping is incomplete; its map preview shows readable tile/event data rather than a complete textured map editor.

### Speed controls

A slider, preset buttons and minus/plus controls edit the same speed value. Dragging previews the value and releasing commits it. The current WOLF interface supports **0.25–100×**; RPG Maker MV/MZ supports **1–100× integer acceleration**. Actual speed depends on game load, so choosing 100× does not guarantee a hundredfold result. Other engines do not yet share a reliable universal speed interface. Game speed does not set Fusion's interface frame rate.

### Languages, profiles and performance

- Completes English and Russian text on recently added pages, including dynamic counts, progress, errors, categories and map instructions. Open pages update when the language changes while preserving selection, scrolling and input. Long headers resize or wrap.
- Interface language and game translation target remain independent: controls use the interface language, game-name and field translations use the current game's target, and original text stays separate.
- Translation profiles support advanced JSON request parameters and examples. Removing a profile that is still in use lets you choose a replacement. API connection settings and translation-content settings are stored separately.
- The library avoids drawing invisible content, reuses cover/size caches and reduces layout work for unchanged states. Persistent presentation failures can trigger compatible rendering for the affected window. **This release does not promise stable 60 FPS; high-resolution maximized windows can still be significantly slower.**

### Validation and remaining limits

This release build passed **615 of 615 controlled checks**: 342 interface/localization checks and 273 modification/translation regression checks. These results do not establish extended real-gameplay or physical display-FPS acceptance. Controlled checks, real-control screenshots, engine/API samples and actual game tests have separate scopes. They do not establish universal game compatibility or a physical display-FPS guarantee.

Automatic names, money and replay detection depend on game data. Initial name preparation in some large libraries can remain slow. Image text, custom rendering, unknown archives and runtime text may still be missed. All engines, extended play, high-speed audiovisual behavior and monitor combinations have not been exhaustively tested.

Download and fully extract **Fusion-1.0.0.zip**, then run `GameTranslator.exe`. **Fusion-1.0.0-Source.zip** contains developer sources. A new folder is recommended for first use. Supply your own translation API; usage charges depend on your provider.

## Русский

**Это предварительная версия. Изменение игровых данных — ЭКСПЕРИМЕНТАЛЬНАЯ функция. Доступные возможности зависят от версии движка, структуры игры и данных, которые Fusion действительно может прочитать.**

Выпуск объединяет изменения интерфейса, перевода и игровых инструментов после публичной русской предварительной версии 0.6.0.178. Номер 1.0.0 начинает новую серию версий и не означает, что все движки, игры и функции прошли проверку стабильного выпуска.

### Интерфейс и библиотека

- Главное окно, библиотека, сведения об игре, настройки, захват, история, управление переводами и изменение данных используют единый интерфейс WPF. Обновлены кнопки, меню, обозначение фокуса, светлая/тёмная темы и узкие макеты.
- Правая часть библиотеки прокручивается целиком: заголовок, поиск, полка недавних игр и основная сетка. Боковая панель прокручивается отдельно; горизонтальная прокрутка полки сохранена. После возврата из сведений об игре восстанавливается позиция просмотра.
- При переключении игр макет сохраняется, пока не готовы новый заголовок, обложка и кнопки. Это уменьшает исчезновение обложки, скачки страницы и повторный сброс кнопок без изменения состояния.
- Возврат к категориям изменения данных использует уже прочитанные данные, сохраняя выбор и прокрутку. Исправлены воспроизведённые причины долгого ожидания, замены новой страницы устаревшим ответом и ошибок повторяющихся ключей при поиске или обновлении переведённых названий.
- Сделать снимок для обложки можно рядом с названием игры или на странице изменения данных. Перед сохранением доступны оригинал и обрезка баннера. Улучшены захват игровой области и проверка чёрных кадров; при ошибке старая обложка сохраняется.

### Сначала доступный перевод

- Осмысленные названия и текст, видимые игроку, по умолчанию переводятся: имена, обращения, места, страны, умения, классы, предметы, задания, меню и концовки. Для отдельного названия можно явно выбрать сохранение оригинала.
- Отдельные буквы и кана в таблицах ввода имени, горячие клавиши, управляющие коды, заполнители, код и пути защищены. Названия языков в меню выбора остаются в исходном виде.
- Читаемый перевод остаётся на экране, пока оставшиеся небольшие фрагменты или несогласованные названия получают ограниченное число попыток исправления. Состояние и причина доступны в управлении переводами; по достижении лимита автоматические запросы прекращаются.
- Результаты с повреждёнными кодами, числами или заполнителями, а также ответы, содержащие только объяснение или отказ, не принимаются как игровой перевод. Существующий пригодный перевод и ручные исправления сохраняются.
- Предварительный перевод, перевод во время игры и перевод названий на странице изменения данных используют общие правила. Текущий диалог имеет приоритет, сохранённые переводы используются повторно. Улучшено повторное использование меняющихся счётчиков и распознанных форматов времени, что уменьшает лишние запросы и чередование оригинала с переводом.

### Единообразие названий и управление переводами

- Отдельная страница слов и названий группирует персонажей, места, умения, предметы и другие поддерживаемые категории. Она показывает оригинал, единый перевод, состояние и источник, позволяя заранее проверить важные названия.
- Имена распознанных объектов остаются единообразными; обычные слова переводятся по контексту. Названия заданий и событий имеют отдельную область применения и не навязывают целую фиксированную фразу обычному диалогу.
- Правила названий хранятся отдельно для каждой игры и целевого языка. Изменение единого названия ставит связанные автоматические переводы на обновление; ручные исправления сохраняются, конфликты показываются отдельно.
- Ошибочные автоматические кандидаты удаляются только при наличии достоверных сведений, что это реплика или внутреннее поле. Обычные переводы, ручные правила и импортированные термины вместе с ними не очищаются.

### Чтение текста и подготовка переводов

- Окно чтения и подготовки показывает результаты по файлам, оригиналы и переводы, ход работы и ошибки. Число вхождений текста, уникальных записей, записей выбранного языкового диапазона и точных совпадений кэша разделено.
- Улучшен сбор из поддерживаемых ресурсов, парных баз данных и карт WOLF, включая ошибки при слишком глубоком расположении папок. Переводы сохраняются партиями и повторно используются при следующем запуске. Некоторые нестандартные способы посимвольного вывода поддерживаются только для версий с проверенными ресурсами.
- Исправлено включение подходящих исходных реплик/меню Ren’Py и общих отображаемых полей Godot в предварительный перевод. Текст, создаваемый во время игры, всё ещё может требовать перевода на лету; 100% относится только к обнаруженному объёму.

### Изменение игровых данных — экспериментально

- Основные действия, персонажи, отношения, предметы, переменные, карты, события, блокировки и общие данные организованы по реально прочитанным возможностям. Отсутствующие или нераспознанные категории не выдаются за доступные функции.
- Основная страница собирает кандидаты денежных значений и часто используемые действия без поиска и без длинного перечня всех характеристик. Персонажи и отношения сгруппированы по объектам; полные данные остаются на соответствующих страницах переменных и баз данных.
- Страница персонажей WOLF приближена к списку и деталям RPG Maker: оригинальные и переведённые имена видны одновременно. Справа показаны только фактически прочитанные характеристики выбранного объекта. Переключение категорий и получение переведённых названий сохраняют несохранённый числовой ввод.
- В основных действиях есть свёрнутая группа возможных полей CG/воспоминаний. Это кандидаты с основаниями для распознавания, а не автоматическое открытие галереи или гарантия полного охвата.
- Доступные значки предметов RPG Maker сохранены. Универсальное сопоставление значков WOLF пока неполное; просмотр карты показывает прочитанные клетки и события, а не полноценный редактор карты с текстурами.

### Управление скоростью

Ползунок, готовые множители и кнопки минус/плюс изменяют одно значение. При перетаскивании показан предварительный результат; после отпускания он применяется. Текущий интерфейс WOLF поддерживает **0,25–100×**, RPG Maker MV/MZ — **целочисленное ускорение 1–100×**. Реальная скорость зависит от нагрузки игры: выбор 100× не гарантирует стократного ускорения. Для остальных движков пока нет общего надёжного способа управления скоростью. Скорость игры не задаёт частоту кадров интерфейса Fusion.

### Языки, профили и производительность

- Дополнен английский и русский перевод новых страниц: динамические счётчики, прогресс, ошибки, категории и пояснения карт. Открытые страницы обновляются при смене языка, сохраняя выбор, прокрутку и ввод. Длинные заголовки расширяются или переносятся.
- Язык интерфейса и язык перевода игры независимы. Кнопки используют язык интерфейса; переводы игровых имён и полей — целевой язык текущего профиля. Оригинал отображается отдельно.
- Профили поддерживают дополнительные параметры запроса в JSON и примеры. При удалении используемого профиля можно выбрать замену. Параметры подключения к API и содержимого перевода сохраняются отдельно.
- Библиотека не рисует невидимое содержимое, повторно использует обложки и результаты масштабирования, сокращает повторную компоновку. При устойчивых ошибках отображения отдельное окно может перейти на совместимый режим. **Стабильные 60 кадров/с не обещаются; развёрнутые окна высокого разрешения всё ещё могут работать заметно медленнее.**

### Проверки и оставшиеся ограничения

Эта сборка прошла **615 из 615 контролируемых проверок**: 342 проверки интерфейса/локализации и 273 регрессионную проверку изменения данных/перевода. Это не подтверждает длительную игру или фактическую частоту кадров дисплея. Контролируемые проверки, снимки настоящих элементов, примеры движков/API и игровые тесты имеют отдельные границы. Они не устанавливают универсальную совместимость или гарантию фактической частоты кадров дисплея.

Распознавание названий, денег и воспоминаний зависит от структуры данных. Первая подготовка имён в больших наборах может оставаться медленной. Текст внутри изображений, нестандартная отрисовка, неизвестные архивы и динамический текст могут быть пропущены. Все движки, длительная игра, поведение звука/изображения при высоком ускорении и сочетания мониторов не проверены исчерпывающе.

Полностью распакуйте **Fusion-1.0.0.zip** и запустите `GameTranslator.exe`. **Fusion-1.0.0-Source.zip** содержит исходный код для разработчиков. Для первого запуска рекомендуется новая папка. Настройте собственный API перевода; стоимость запросов определяется провайдером.

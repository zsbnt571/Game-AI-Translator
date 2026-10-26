extends Node

# Godot 4 bridge. Godot 3 uses the separately compiled dialect.
# Text is looked up immediately before drawing, without changing the game's locale.
# Catalog reads serialized scenes without instantiating their game nodes.
var session = ""
var secret = ""
var enabled = false
var epoch = 0
var cache = {}
# Session-start entries stay exact-only and never enter template indices.
var startup_exact = {}
var startup_loaded = false
var staged_cache = {}
var activation_exact = {}
var activation_observed = {}
var staged_exact = {}
var staged_exact_bytes = 0
var staged_exact_ready = false
var preparing = false
var pending = {}
var entries = []
var known = {}
var catalog = []
var catalog_seen = {}
var catalog_metadata = []
var catalog_skipped = 0
var catalog_warnings = []
var source_language = "Auto"
var language_revision = 0
var built_language_revision = -1
var selected_catalog = []
var selected_metadata = []
var selected_language = ""
var language_excluded = 0
var language_letter_pattern = null
var language_locale_pattern = null
var directories = ["res://"]
var resources = []
var paths_seen = {}
var scan_complete = false
var directory_reader = null
var directory_path = ""
var json_work = []
var optimized_work = {}
var scan_max_ms = 0
var scan_overruns = 0
var last_id = 0
var last_request = 0
var elapsed = 0.0
var chinese_font = null
var custom_font = null
var current_locale = ""
var refreshing = false
var entry_cursor = 0
var entry_refresh_max_ms = 0.0
var scan_thread = Thread.new()
var catalog_mutex = Mutex.new()
var scan_stop = false
var collection_started = false
var font_preference_mode = "default"
var font_preference_path = ""
var font_preference_family = ""
var pending_writes = {}
var dirty_viewports = {}
var slot_groups = []
var slot_known = {}
var slot_cursor = 0
var slot_rows = 0
var slot_limit_hits = 0
var slot_max_ms = 0.0


func _ready():
    session = OS.get_environment("FUSION_GODOT_SESSION")
    secret = OS.get_environment("FUSION_GODOT_SECRET")
    if session == "" or secret == "":
        set_process(false)
        return
    process_mode = Node.PROCESS_MODE_ALWAYS
    # SceneTree.process_frame is emitted before game _process callbacks and
    # frame_pre_draw can run after CanvasItems prepared their draw commands.
    # Refresh after ordinary and deferred game writers, before that preparation.
    process_priority = 2147483647
    last_request = _ticks()
    current_locale = TranslationServer.get_locale()
    enabled = OS.get_environment("FUSION_GODOT_TRANSLATION") == "1"
    if enabled:
        _load_startup(_json(_read(session + "/startup.json", 33554432)))
    _prepare_font()
    get_tree().connect("node_added", Callable(self, "_added"))
    RenderingServer.connect("frame_pre_draw", Callable(self, "_before_draw"))
    RenderingServer.connect("frame_post_draw", Callable(self, "_finish_redraw"))
    if enabled:
        _ensure_collection()
        _refresh(true)
    _write("hello.json", {"pid": OS.get_process_id(), "secret": secret, "protocol": 73, "version": Engine.get_version_info().string})

# A connected standby bridge must not enumerate game text or resources.
# Enable/preparation starts collection once, without relaunching the process.
func _ensure_collection():
    if collection_started or not is_inside_tree():
        return
    collection_started = true
    _collect(get_tree().root)
    # Inspect configured resources to identify their language before publishing one source-language catalogue.
    _project_catalog_resources()
    scan_thread.start(Callable(self, "_scan_worker"))

func _project_catalog_resources():
    for setting in ProjectSettings.get_property_list():
        var key = str(setting.name)
        # POT entries are source files, not translation binaries. Resolve their
        # data-only import mapping before dispatching by the actual file type.
        if key in ["locale/translations", "internationalization/locale/translations", "locale/translations_pot_files", "internationalization/locale/translations_pot_files"]:
            var value = ProjectSettings.get_setting(key)
            if typeof(value) in [TYPE_ARRAY, TYPE_PACKED_STRING_ARRAY]:
                for path in value:
                    if typeof(path) == TYPE_STRING:
                        _queue_resource(path, true)

# Read only the host's bounded snapshot for this session. Normal launches do
# not retain translations for a later enable, and malformed rows keep originals.
func _load_startup(startup):
    if not enabled or typeof(startup) != TYPE_DICTIONARY or startup.get("secret", "") != secret or typeof(startup.get("entries")) != TYPE_DICTIONARY:
        return
    if startup.entries.size() > 100000:
        return
    for source in startup.entries:
        var text = startup.entries[source]
        if _valid(source) and typeof(text) == TYPE_STRING and text.strip_edges() != "" and text.length() <= 12000 and not source.to_utf8_buffer().has(0) and not text.to_utf8_buffer().has(0):
            startup_exact[source] = text
    startup_loaded = true

func _ticks():
    return Time.get_ticks_msec()

func _read(path, limit = 8388608):
    var f = FileAccess.open(path, FileAccess.READ)
    if f == null:
        return ""
    if f.get_length() > limit:
        f.close()
        return ""
    var value = f.get_as_text()
    f.close()
    return value

func _exists(path):
    return FileAccess.file_exists(path)

func _json(text):
    if text.strip_edges() == "":
        return null
    return JSON.parse_string(text)

func _write(name, value):
    pending_writes[name] = {"text": JSON.stringify(value), "prepared": false}
    _flush_write(name)

func _flush_write(name):
    if not pending_writes.has(name):
        return
    var pending = pending_writes[name]
    var path = session + "/" + name
    if not pending.prepared:
        var f = FileAccess.open(path + ".tmp", FileAccess.WRITE)
        if f == null:
            return
        f.store_string(pending.text)
        var write_error = f.get_error()
        f.close()
        if write_error != OK:
            return
        pending.prepared = true
    if FileAccess.file_exists(path):
        if DirAccess.remove_absolute(path) != OK:
            return
    if DirAccess.rename_absolute(path + ".tmp", path) == OK:
        pending_writes.erase(name)

func _flush_writes():
    # One non-blocking attempt per pending file per frame. A failed publication
    # retains its already-computed response; the command/epoch is not repeated.
    for name in pending_writes.keys():
        _flush_write(name)

func _directory_start(path):
    directory_path = path
    directory_reader = DirAccess.open(path)
    if directory_reader == null:
        return
    directory_reader.include_hidden = true
    directory_reader.include_navigational = false
    directory_reader.list_dir_begin()

func _read_bytes(path, limit):
    var f = FileAccess.open(path, FileAccess.READ)
    if f == null:
        return _bytes([])
    var value = _bytes([])
    if f.get_length() <= limit:
        value = f.get_buffer(f.get_length())
    f.close()
    return value
func _scan_cancelled():
    catalog_mutex.lock()
    var result = scan_stop
    catalog_mutex.unlock()
    return result

func _scan_worker(_unused = null):
    while true:
        catalog_mutex.lock()
        var stop = scan_stop
        var revision = language_revision
        var mode = source_language
        var rebuild = built_language_revision != revision
        catalog_mutex.unlock()
        if stop:
            break
        if not scan_complete:
            _scan_step()
        elif rebuild:
            _build_language_catalog(mode, revision)
        OS.delay_msec(10 if scan_complete else 1)
    return null

func _catalog_page(cursor):
    catalog_mutex.lock()
    var ready = scan_complete and built_language_revision == language_revision
    var size = selected_catalog.size() if ready else 0
    var start = int(clamp(int(cursor), 0, size))
    var stop = min(start + 128, size)
    var texts = []
    var metadata = []
    for i in range(start, stop):
        texts.append(selected_catalog[i])
        metadata.append(selected_metadata[i].duplicate())
    var result = {"texts": texts, "metadata": metadata, "next": stop, "total": size, "skipped": catalog_skipped, "done": ready, "scanning": not ready, "warnings": catalog_warnings.duplicate(), "sourceLanguage": source_language, "selectedLanguage": selected_language if ready else "", "skippedByLanguage": language_excluded if ready else 0}
    catalog_mutex.unlock()
    # IPC pages run on the main thread. Resolve actual serialized localization
    # keys only after releasing the scanner lock, with at most 128 lookups.
    # The raw row cursor remains unchanged even when localized values duplicate
    # existing Translation rows; the session deduplicates actual source text.
    var active_locale = TranslationServer.get_locale()
    for i in range(metadata.size()):
        var row = metadata[i]
        if not row.kind in ["scene", "scene_binary", "resource_text", "script_tr", "script_display", "script_display_unconfirmed"]:
            continue
        var localized = str(TranslationServer.translate(row.source))
        if localized == row.source or not _valid(localized):
            continue
        row["originalKey"] = row.source
        row["resolvedBy"] = "TranslationServer"
        row.source = localized
        # Requested locale may fall back to a different Translation resource.
        # Preserve the request separately; do not claim it is the result language.
        row["requestedLocale"] = active_locale
        row["originalLocale"] = row.locale
        row["originalLanguage"] = row.language
        row["resolvedLocale"] = ""
        row["resourceLocale"] = ""
        row.locale = ""
        row.language = _text_language(localized)
        row["languageEvidence"] = "text-inference"
        texts[i] = localized
    # Serialized text and traced display fields in shared resources are original content.
    # An ISO locale inferred from those words is not required. Localization
    # files, locale-dependent scripts and arbitrary literals still need independent evidence.
    var active_sources = []
    for row in metadata:
        var base_field = row.kind in ["scene", "scene_binary", "resource_text", "script_display", "json"] and row.locale == "" and _path_language(row.path) == ""
        if (base_field or row.get("resolvedBy", "") == "TranslationServer") and not active_sources.has(row.source):
            active_sources.append(row.source)
    result["activeSourceTexts"] = active_sources
    # Explicit-resource preparation is distinct from actual display provenance.
    # The desktop still requires the user's single matching source language.
    var selected_resources = []
    for row in metadata:
        if row.kind in ["translation_text", "gettext", "optimized_value", "translation", "translation_plural"] and row.get("resourceLocale", "") != "":
            if not selected_resources.has(row.source):
                selected_resources.append(row.source)
    result["selectedResourceTexts"] = selected_resources
    result["epoch"] = epoch
    return result

func _locale_language(value):
    var token = str(value).strip_edges().to_lower().replace("-", "_")
    var aliases = {"english":"en", "eng":"en", "japanese":"ja", "jp":"ja", "jpn":"ja", "simplifiedchinese":"zh", "simplified_chinese":"zh", "chinese":"zh", "schinese":"zh", "korean":"ko", "kr":"ko", "kor":"ko"}
    if aliases.has(token):
        return aliases[token]
    if language_locale_pattern == null:
        language_locale_pattern = RegEx.new()
        language_locale_pattern.compile("^(?:und|[a-z]{2}(?:_(?:latn|cyrl|arab|hans|hant|jpan|kore|deva|hebr|thai|grek))?(?:_(?:[a-z]{2}|[0-9]{3}))?)$")
    if language_locale_pattern.search(token) == null:
        return ""
    var base = token.split("_")[0]
    if base in ["en", "ja", "zh", "ko", "fr", "de", "es", "pt", "ru", "it", "pl", "tr", "uk", "vi", "th", "id", "ar", "he", "el", "hi", "und", "nl", "sv", "fi", "da", "no", "cs", "hu"]:
        return base
    return ""

func _path_language(path):
    # Whole path/file tokens only, never substrings such as 'enemy' or 'plan'.
    var normalized = str(path).to_lower().replace("\\", "/").replace("-", "/").replace(".", "/").replace("_", "/")
    for token in normalized.split("/"):
        var language = _locale_language(token)
        if language != "":
            return language
    return ""

# Preserve a declared locale (including script/region); whole file/path tokens
# are accepted, while guessed text language and arbitrary substrings are not.
func _resource_locale(path, locale):
    if not _locale_language(locale) in ["", "und"]:
        return str(locale)
    var normalized = str(path).to_lower().replace("\\", "/").replace(".", "/")
    for token in normalized.split("/"):
        if not _locale_language(token) in ["", "und"]:
            return token
    return ""

func _text_language(value):
    if language_letter_pattern == null:
        language_letter_pattern = RegEx.new()
        language_letter_pattern.compile("\\p{L}")
    var kana = 0
    var han = 0
    var hangul = 0
    var latin = 0
    var cyrillic = 0
    var scripts = {}
    for at in range(value.length()):
        var code = value.unicode_at(at)
        if (code >= 0x3040 and code <= 0x30ff) or (code >= 0xff66 and code <= 0xff9d):
            kana += 1
        elif code >= 0x3400 and code <= 0x9fff:
            han += 1
        elif (code >= 0xac00 and code <= 0xd7af) or (code >= 0x1100 and code <= 0x11ff):
            hangul += 1
        elif (code >= 65 and code <= 90) or (code >= 97 and code <= 122):
            latin += 1
        elif code >= 0x400 and code <= 0x52f:
            cyrillic += 1
        elif language_letter_pattern.search(value.substr(at, 1)) != null:
            var script = "und"
            if (code >= 0x600 and code <= 0x6ff) or (code >= 0x750 and code <= 0x77f) or (code >= 0x8a0 and code <= 0x8ff):
                script = "ar"
            elif code >= 0x590 and code <= 0x5ff:
                script = "he"
            elif code >= 0xe00 and code <= 0xe7f:
                script = "th"
            elif (code >= 0x370 and code <= 0x3ff) or (code >= 0x1f00 and code <= 0x1fff):
                script = "el"
            elif code >= 0x900 and code <= 0x97f:
                script = "hi"
            scripts[script] = int(scripts.get(script, 0)) + 1
    if kana > 0:
        return "ja"
    if hangul > 0:
        return "ko"
    if han > 0:
        return "han"
    if cyrillic > 0:
        return "ru"
    var selected = ""
    var weight = 0
    for script in scripts:
        if script != "und" and int(scripts[script]) > weight:
            selected = script
            weight = int(scripts[script])
    if selected != "":
        return selected
    if latin == 0 and scripts.size() > 0:
        return "und"
    return "en" if latin > 0 else ""

func _catalog_identity(value, path, locale):
    var language = _locale_language(locale)
    if language == "":
        language = _path_language(path)
    return language if language != "" else _text_language(value)

func _choose_catalog_language(mode):
    # An actual active translation resource outranks a fallback or allow-list.
    # The operating-system locale alone is not proof of a game resource.
    var active = _locale_language(current_locale)
    if active != "":
        for row in catalog_metadata:
            if _locale_language(row.locale) == active or _path_language(row.path) == active:
                return active
    if mode == "Mixed":
        mode = "Auto"
    if mode != "Auto":
        return _locale_language(mode)
    var weights = {}
    var english_prose = 0
    var english_prose_letters = 0
    var english_labels = {}
    var inspected = 0
    for row in catalog_metadata:
        inspected += 1
        if inspected % 128 == 0 and _scan_cancelled():
            return ""
        var language = row.language
        weights[language] = int(weights.get(language, 0)) + min(row.source.length(), 512)
        if language != "en":
            continue
        var text = row.source.strip_edges()
        var words = text.split(" ", false)
        # A Latin character name or engine identifier is not evidence that the
        # original story is English. Require multiple real lines or UI labels.
        if words.size() >= 3 and text.length() >= 12 and text.find("_") < 0 and text.find("/") < 0:
            english_prose += 1
            english_prose_letters += text.length()
        if text.to_lower() in ["start", "new game", "continue", "load game", "save game", "settings", "options", "quit", "exit", "back", "resume"]:
            english_labels[text.to_lower()] = true
    if (english_prose >= 2 and english_prose_letters >= 60) or english_labels.size() >= 3:
        return "en"
    if int(weights.get("ja", 0)) > 0:
        return "ja"
    var selected = ""
    var weight = -1
    for language in weights:
        if language != "" and int(weights[language]) > weight:
            selected = language
            weight = int(weights[language])
    return "zh" if selected == "han" else selected

func _build_language_catalog(mode, revision):
    # The scanner is the only writer. Publish one stable filtered page space
    # after discovery; never send earlier-language pages that cannot be recalled.
    var selected = _choose_catalog_language(mode)
    var texts = []
    var metadata = []
    var seen = {}
    var excluded = 0
    var inspected = 0
    for row in catalog_metadata:
        inspected += 1
        if inspected % 128 == 0 and _scan_cancelled():
            return ""
        var matches = selected == "*" or row.language == selected or (row.language == "han" and selected in ["zh", "ja"])
        if mode in ["Auto", "Mixed"] and row.locale == "" and _path_language(row.path) == "":
            # A genuine foreign line inside shared source is not a backup pack.
            matches = true
        if not matches:
            excluded += 1
        elif not seen.has(row.source):
            seen[row.source] = true
            texts.append(row.source)
            metadata.append(row)
    catalog_mutex.lock()
    if not scan_stop and revision == language_revision:
        selected_catalog = texts
        selected_metadata = metadata
        selected_language = selected
        language_excluded = excluded
        built_language_revision = revision
    catalog_mutex.unlock()

func _prepare_catalog_language(mode):
    if not mode in ["Auto", "English", "Japanese", "SimplifiedChinese", "Korean", "Mixed"]:
        mode = "Auto"
    catalog_mutex.lock()
    if source_language != mode:
        source_language = mode
        language_revision += 1
        selected_catalog = []
        selected_metadata = []
    catalog_mutex.unlock()


func _bytes(value):
    return PackedByteArray(value)

func _utf8(value):
    return value.to_utf8_buffer()

func _valid(value):
    return typeof(value) == TYPE_STRING and value.strip_edges() != "" and value.length() <= 6000 and not value.is_valid_float() and not value.begins_with("res://") and not value.begins_with("user://")

func _warning(value):
    catalog_skipped += 1
    catalog_mutex.lock()
    if catalog_warnings.size() < 32 and not catalog_warnings.has(value):
        catalog_warnings.append(value)
    catalog_mutex.unlock()

func _catalog_add(value, path, key, locale, kind):
    if typeof(value) != TYPE_STRING:
        return
    if value.length() > 6000:
        catalog_skipped += 1
        return
    var language = _text_language(value) if kind == "gettext_source" else _catalog_identity(value, path, locale)
    var identity = language + "\u001f" + value
    if not _valid(value) or catalog_seen.has(identity):
        return
    if catalog.size() >= 100000:
        _warning("文本目录达到 100000 条上限，仍可实时补译。")
        return
    catalog_mutex.lock()
    catalog_seen[identity] = true
    catalog.append(value)
    catalog_metadata.append({"source": value, "path": path, "key": key, "locale": locale, "kind": kind, "language": language, "resourceLocale": _resource_locale(path, locale)})
    catalog_mutex.unlock()

func _catalog_path_valid(path):
    if not path.begins_with("res://") or path.length() > 4096 or path.find("\\") >= 0 or path.to_utf8_buffer().has(0):
        return false
    for part in path.substr(6).split("/"):
        if part in ["", ".", ".."] or part.find(":") >= 0:
            return false
    return true

func _catalog_resource_path(path):
    # Plain file reads never execute Resource loaders or attached scripts.
    # Exported custom resources are reachable through these bounded sidecars.
    var visited = {}
    for hop in range(8):
        if not _catalog_path_valid(path):
            _warning("文本资源导入路径无效或超出游戏目录。")
            return ""
        if visited.has(path):
            _warning("文本资源导入映射形成循环：" + path)
            return ""
        visited[path] = true
        var sidecars = [path] if path.get_extension().to_lower() in ["remap", "import"] else [path + ".remap", path + ".import"]
        var sidecar = ""
        for candidate in sidecars:
            if FileAccess.file_exists(candidate):
                sidecar = candidate
                break
        if sidecar == "":
            return path
        if _file_size(sidecar) > 262144:
            _warning("文本资源导入映射超过读取上限：" + sidecar)
            return ""
        var sidecar_bytes = _read_bytes(sidecar, 262144)
        # Exported import metadata may include a C-string terminator. Remove
        # only trailing padding before UTF-8 decoding; interior NUL is invalid.
        while sidecar_bytes.size() > 0 and sidecar_bytes[sidecar_bytes.size() - 1] == 0:
            sidecar_bytes.resize(sidecar_bytes.size() - 1)
        if sidecar_bytes.size() == 0 or sidecar_bytes.has(0):
            _warning("文本资源导入映射无法解析：" + sidecar)
            return ""
        var remap = ConfigFile.new()
        if remap.parse(sidecar_bytes.get_string_from_utf8()) != OK or typeof(remap.get_value("remap", "path", null)) != TYPE_STRING:
            _warning("文本资源导入映射无法解析：" + sidecar)
            return ""
        path = remap.get_value("remap", "path", "")
    _warning("文本资源导入映射超过层数上限。")
    return ""

func _queue_resource(path, configured = false):
    path = _catalog_resource_path(path)
    if path == "":
        return
    if not path.get_extension().to_lower() in ["translation", "po", "tscn", "scn", "tres", "res", "json", "gd", "gdc"]:
        if configured:
            _warning("配置的文本资源暂不支持或未找到可读导入资源：" + path)
        return
    # Imported 3D model scene data cannot contain GUI controls. Do not decompress
    # model geometry merely to discover there are no interface text properties.
    if path.get_extension().to_lower() == "scn":
        for model_extension in [".glb-", ".gltf-", ".fbx-", ".blend-", ".dae-", ".obj-"]:
            if path.get_file().to_lower().find(model_extension) >= 0:
                return
    if paths_seen.has(path) or paths_seen.size() >= 50000:
        return
    paths_seen[path] = true
    resources.append(path)
    scan_complete = false

func _scan_step():
    var start = _ticks()
    var count = 0
    while count < 128 and _ticks() - start < 3:
        if _scan_cancelled():
            break
        count += 1
        if optimized_work.size() > 0:
            _optimized_step()
        elif json_work.size() > 0:
            _json_step()
        elif resources.size() > 0:
            var path = resources.pop_front()
            if path.get_extension().to_lower() in ["json", "gd", "tscn", "tres", "po"]:
                _extract_text(path)
            elif path.get_extension().to_lower() == "gdc":
                _extract_script_binary(path)
            else:
                _binary_extract(path)
        elif directory_reader != null:
            var name = directory_reader.get_next()
            if name == "":
                directory_reader.list_dir_end()
                directory_reader = null
            elif name != "." and name != "..":
                var path = directory_path + ("" if directory_path.ends_with("/") else "/") + name
                if directory_reader.current_is_dir():
                    if directories.size() < 10000 and name != ".git":
                        directories.append(path)
                else:
                    var extension = path.get_extension().to_lower()
                    if extension in ["translation", "po", "tscn", "scn", "tres", "res", "json", "gd"]:
                        if not path.ends_with("/fusion_translation.gd"):
                            _queue_resource(path)
                    elif extension == "gdc":
                        _queue_resource(path)
                    elif extension in ["remap", "import"] or FileAccess.file_exists(path + ".import") or FileAccess.file_exists(path + ".remap"):
                        # Exported listings may expose the logical extension
                        # while hiding the .import file that maps its bytes.
                        _queue_resource(path)
        elif directories.size() > 0:
            _directory_start(directories.pop_front())
        else:
            catalog_mutex.lock()
            scan_complete = true
            catalog_mutex.unlock()
            break
    var duration = _ticks() - start
    scan_max_ms = max(scan_max_ms, duration)
    if duration > 16:
        scan_overruns += 1

func _extract_text(path):
    var limit = 8388608 if path.get_extension().to_lower() in ["tscn", "tres", "json"] else 262144
    var size = _file_size(path)
    if size < 0 or size > limit:
        _warning("文本文件不可读或超过读取上限：" + path)
        return
    var source = _read(path, limit)
    if source == "":
        if _read_bytes(path, limit).size() > 0:
            _warning("文本文件无法解析：" + path)
        return
    if path.get_extension().to_lower() == "po":
        _extract_po(source, path)
    elif path.get_extension().to_lower() == "json":
        var data = _json(source)
        if data != null:
            json_work.append({"value":data,"path":path,"key":"","depth":0,"accepted":false,"index":0})
    elif path.get_extension().to_lower() == "tres":
        _text_resource(source, path)
    elif path.get_extension().to_lower() == "gd":
        _script_catalog(_script_text_tokens(source), path)
    else:
        var expression = RegEx.new()
        var kind = "script_tr"
        if path.get_extension().to_lower() == "tscn":
            kind = "scene"
            expression.compile("(?m)^(?:text|bbcode_text|tooltip_text|hint_tooltip|placeholder_text)\\s*=\\s*(\"(?:[^\"\\\\]|\\\\.)*\")")
        else:
            expression.compile("(?:tr|tr_n)\\s*\\(\\s*(\"(?:[^\"\\\\]|\\\\.)*\")")
        for found in expression.search_all(source):
            var literal = found.get_string(1).replace("\n", "\\n").replace("\r", "\\r")
            _catalog_add(_json(literal), path, str(found.get_start()), "", kind)


# Read script syntax as data only. Strings enter the catalog only when a known
# display assignment/call (or its bounded local constant dependency) uses them.
func _script_u32(reader):
    if reader.bad or reader.stream.get_available_bytes() < 4:
        reader.bad = true
        return 0
    return reader.stream.get_u32()

func _script_skip(reader, count):
    if count < 0 or count > reader.stream.get_available_bytes():
        reader.bad = true
        return
    reader.stream.seek(reader.stream.get_position() + count)

func _script_literal_string(reader):
    var length = _script_u32(reader)
    if reader.bad or length > 1048576 or length > reader.stream.get_available_bytes():
        reader.bad = true
        return ""
    var value = reader.stream.get_data(length)[1].get_string_from_utf8()
    _script_skip(reader, (4 - length % 4) % 4)
    return value

func _script_literal(reader):
    # GDScript tokenizer literals are primitives. Reject every Object/container
    # type; never deserialize objects or compile/load the script to read strings.
    var header = _script_u32(reader)
    var type = header & 0xffff
    if type == 0:
        return null
    if type in [1, 2, 3]:
        _script_skip(reader, 8 if header & 0x10000 else 4)
        return null
    if type in [4, 21]:
        return _script_literal_string(reader)
    if type == 22:
        var names = _script_u32(reader)
        var subnames = _script_u32(reader)
        _script_u32(reader)
        if not names & 0x80000000 or (names & 0x7fffffff) + subnames > 4096:
            reader.bad = true
            return null
        for ignored in range((names & 0x7fffffff) + subnames):
            _script_literal_string(reader)
        return null
    reader.bad = true
    return null

func _extract_script_binary(path):
    var data = _read_bytes(path, 8388608)
    var tokens = _script_binary_tokens(data)
    if tokens == null:
        _warning("编译脚本文字格式暂不支持或数据不完整；仍可实时补译：" + path)
        return
    _script_catalog(tokens, path)

func _script_binary_tokens(data):
    if data.size() < 12 or data.size() > 8388608 or data.slice(0, 4).get_string_from_ascii() != "GDSC":
        return null
    var version = data.decode_u32(4)
    if not version in [100, 101]:
        return null
    var expanded = data.decode_u32(8)
    if expanded > 16777216:
        return null
    var body = data.slice(12)
    if expanded > 0:
        body = body.decompress(expanded, FileAccess.COMPRESSION_ZSTD)
        if body.size() != expanded:
            return null
    var reader = {"stream":StreamPeerBuffer.new(),"bad":false}
    reader.stream.data_array = body
    var identifiers_count = _script_u32(reader)
    var constants_count = _script_u32(reader)
    var lines_count = _script_u32(reader)
    if version == 100:
        _script_u32(reader) # Legacy reserved column count.
    var token_count = _script_u32(reader)
    if reader.bad or identifiers_count > 50000 or constants_count > 100000 or lines_count > 250000 or token_count > 250000:
        return null
    var identifiers = []
    for index in range(identifiers_count):
        if index % 128 == 0 and _scan_cancelled():
            return null
        var length = _script_u32(reader)
        if reader.bad or length > 16384 or length * 4 > reader.stream.get_available_bytes():
            return null
        var bytes = reader.stream.get_data(length * 4)[1]
        for at in range(bytes.size()):
            bytes[at] = bytes[at] ^ 0xb6
        identifiers.append(bytes.get_string_from_utf32())
    var constants = []
    for index in range(constants_count):
        if index % 128 == 0 and _scan_cancelled():
            return null
        constants.append(_script_literal(reader))
        if reader.bad:
            return null
    _script_skip(reader, lines_count * 16)
    if reader.bad:
        return null
    var symbols = {22:"+",23:"-",24:"*",26:"/",27:"%",28:"=",29:"+=",57:"const",60:"func",70:"var",73:"[",74:"]",75:"{",76:"}",77:"(",78:")",79:",",80:";",81:"."}
    symbols[84 if version == 101 else 83] = ":"
    symbols[85 if version == 101 else 84] = "$"
    var tokens = []
    for index in range(token_count):
        if index % 128 == 0 and _scan_cancelled():
            return null
        if reader.stream.get_available_bytes() < 5:
            return null
        var position = reader.stream.get_position()
        var packed = reader.stream.get_u8()
        if packed & 0x80:
            reader.stream.seek(position)
            packed = _script_u32(reader)
        var line = _script_u32(reader)
        var type = packed & 0x7f
        var value = "?"
        var kind = "symbol"
        if type in [1, 2]:
            var identifier = packed >> 8
            if identifier >= identifiers.size():
                return null
            value = identifiers[identifier]
            kind = "id"
        elif type == 3:
            var constant = packed >> 8
            if constant >= constants.size():
                return null
            if typeof(constants[constant]) == TYPE_STRING:
                value = constants[constant]
                kind = "string"
        elif symbols.has(type):
            value = symbols[type]
        elif type >= (100 if version == 101 else 99):
            return null
        if reader.bad:
            return null
        tokens.append({"kind":kind,"value":value,"line":line})
    if reader.stream.get_available_bytes() != 0:
        return null
    return tokens


func _script_text_tokens(source):
    var lexer = RegEx.new()
    lexer.compile("(?s)\"\"\".*?\"\"\"|'''.*?'''|\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)*'|#[^\\n]*|[A-Za-z_][A-Za-z_0-9]*|:=|==|!=|<=|>=|[+*/%!-]?=|[^\\s]")
    var tokens = []
    var line = 1
    var previous_end = 0
    for found in lexer.search_all(source):
        if tokens.size() >= 250000:
            return []
        line += source.substr(previous_end, found.get_start() - previous_end).count("\n")
        var token = found.get_string()
        previous_end = found.get_end()
        if token.begins_with("#"):
            continue
        var value = token
        var kind = "symbol"
        if token.begins_with('"') or token.begins_with("'"):
            var edge = 3 if token.begins_with('"""') or token.begins_with("'''") else 1
            value = token.substr(edge, token.length() - edge * 2).c_unescape()
            kind = "string"
        elif token.is_valid_identifier():
            kind = "id"
        tokens.append({"kind":kind,"value":value,"line":line})
        line += token.count("\n")
    return tokens

func _script_human_text(value, sentence = false):
    if not _valid(value):
        return false
    var trimmed = value.strip_edges()
    if trimmed in ["...", "…"] or (trimmed.length() == 1 and trimmed.unicode_at(0) < 128) or trimmed.trim_suffix("x").is_valid_float():
        return false
    if trimmed.find("://") >= 0 or trimmed.begins_with("/") or trimmed.begins_with("\\"):
        return false
    for extension in [".png", ".jpg", ".webp", ".ogg", ".wav", ".tscn", ".scn", ".tres", ".res", ".gd", ".gdc", ".json", ".cfg"]:
        if trimmed.to_lower().ends_with(extension):
            return false
    # Machine keys and paths are not prose, including values in UI dictionaries.
    if trimmed.find(" ") < 0 and trimmed.find("\n") < 0 and (trimmed.find("_") >= 0 or trimmed.find("/") >= 0):
        return false
    if sentence:
        return (trimmed.length() >= 12 and trimmed.split(" ", false).size() >= 3) or (trimmed.length() >= 8 and (trimmed.find("。") >= 0 or trimmed.find("！") >= 0 or trimmed.find("？") >= 0))
    return true

func _text_map(data):
    # A flat ID -> sentence table is common for descriptions/dialogue. Do not
    # translate its identifiers, or interpret ordinary settings as a text table.
    if typeof(data) != TYPE_DICTIONARY or data.size() < 2 or data.size() > 20000:
        return false
    for key in data:
        if str(key).to_lower() in ["path", "file", "script", "url", "id", "key", "type"] or not _script_human_text(data[key], true):
            return false
    return true

func _script_expression_end(tokens, start):
    var depth = 0
    var at = start
    var line = tokens[start].line if start < tokens.size() else 0
    while at < tokens.size() and at - start < 20000:
        var value = tokens[at].value if tokens[at].kind == "symbol" else ""
        if depth == 0 and (tokens[at].line > line or value in [",", ";", ")", "]", "}"]):
            break
        if value in ["(", "[", "{"]:
            depth += 1
        elif value in [")", "]", "}"]:
            depth -= 1
        line = tokens[at].line
        at += 1
    return at

func _script_metadata_literal(tokens, at, start):
    var depth = 0
    var argument = 0
    for before in range(at - 1, max(start - 1, at - 512), -1):
        if tokens[before].kind != "symbol":
            continue
        var value = tokens[before].value
        if value in [")", "]", "}"]:
            depth += 1
        elif value in ["(", "[", "{"]:
            if depth > 0:
                depth -= 1
            elif value == "(" and before > 0:
                var call_name = tokens[before - 1].value
                return (call_name == "tr" and argument > 0) or (call_name == "tr_n" and argument > 1) or (call_name in ["get", "has", "set_item_text", "set_item_tooltip"] and argument == 0) or call_name in ["load", "preload", "get_node", "get_node_or_null"]
            else:
                return value == "[" and before > 0 and (tokens[before - 1].kind == "id" or tokens[before - 1].value in ["]", ")"])
        elif value == "," and depth == 0:
            argument += 1
    return false

func _script_catalog(tokens, path):
    var definitions = {}
    var sinks = []
    var scope = ""
    # Traced display strings can be prepared without a reported game locale.
    # A script containing locale selectors may instead contain alternate packs:
    # retain its discoveries, but wait for runtime/TranslationServer evidence.
    # "No" and "id" are common UI/metadata tokens. They become locale evidence
    # only with a selector, or together as distinct dictionary language keys.
    var display_kind = "script_display"
    var locale_selector = false
    var ambiguous_keys = {}
    for at in range(tokens.size()):
        var token = tokens[at]
        if token.kind == "id" and (str(token.value).to_lower().find("locale") >= 0 or str(token.value).to_lower().find("lang") >= 0):
            locale_selector = true
        if token.kind == "string" and str(token.value).to_lower() in ["no", "id"] and at + 2 < tokens.size() and tokens[at + 1].value == ":":
            var value = tokens[at + 2]
            if value.value in ["{", "["] or (value.kind == "string" and _script_human_text(value.value, true)):
                ambiguous_keys[str(token.value).to_lower()] = true
    for token in tokens:
        if token.kind == "string" and _locale_language(token.value) != "" and (not str(token.value).to_lower() in ["no", "id"] or locale_selector or ambiguous_keys.size() >= 2):
            display_kind = "script_display_unconfirmed"
            break
    for at in range(tokens.size()):
        if tokens[at].kind != "string" and tokens[at].value == "func" and at + 1 < tokens.size():
            scope = str(at) + ":" + tokens[at + 1].value
        tokens[at]["scope"] = scope
    for at in range(tokens.size()):
        var token = tokens[at]
        if token.kind == "symbol" and token.value in ["=", ":="] and at > 0 and tokens[at - 1].kind == "id":
            var name = tokens[at - 1].value
            for before in range(at - 1, max(-1, at - 10), -1):
                if tokens[before].line != token.line:
                    break
                if tokens[before].value in ["var", "const"] and before + 1 < at:
                    name = tokens[before + 1].value
                    break
            var span = {"start":at + 1,"end":_script_expression_end(tokens, at + 1),"depth":0}
            var member_assignment = at > 1 and tokens[at - 2].value == "."
            if not member_assignment:
                var definition_key = token.scope + "\u001f" + name
                if not definitions.has(definition_key):
                    definitions[definition_key] = []
                if definitions[definition_key].size() < 64:
                    definitions[definition_key].append(span)
            if member_assignment and name in ["text", "bbcode_text", "tooltip_text", "hint_tooltip", "placeholder_text", "dialog_text"]:
                sinks.append(span)
        elif token.kind == "id" and at + 1 < tokens.size() and tokens[at + 1].value == "(":
            var argument_count = 0
            if token.value in ["tr", "set_text", "set_bbcode", "append_text", "add_text", "set_tooltip_text", "set_placeholder", "set_placeholder_text", "add_item", "add_check_item", "add_radio_check_item"]:
                argument_count = 1
            elif token.value in ["tr_n", "set_item_text", "set_item_tooltip"]:
                argument_count = 2
            var start = at + 2
            for ignored in range(argument_count):
                var end = _script_expression_end(tokens, start)
                sinks.append({"start":start,"end":end,"depth":0})
                if end >= tokens.size() or tokens[end].value != ",":
                    break
                start = end + 1
    var visited = {}
    var work = 0
    var inspected = 0
    while sinks.size() > 0 and work < 10000 and inspected < 500000:
        var span = sinks.pop_back()
        work += 1
        if span.depth > 8:
            continue
        var identity = str(span.start) + ":" + str(span.end)
        if visited.has(identity):
            continue
        visited[identity] = true
        for at in range(span.start, span.end):
            inspected += 1
            if inspected >= 500000 or (inspected % 128 == 0 and _scan_cancelled()):
                return
            var token = tokens[at]
            if token.kind == "string":
                # Dictionary keys, get_node paths and format-method arguments
                # are metadata, even inside a display expression.
                if at + 1 < span.end and tokens[at + 1].value == ":":
                    continue
                if at > 0 and tokens[at - 1].value in ["$", "^", "&"]:
                    continue
                if at > 1 and tokens[at - 1].value == "(" and tokens[at - 2].value in ["load", "preload", "get_node", "get_node_or_null"]:
                    continue
                if at > 1 and tokens[at - 1].value == "[" and (tokens[at - 2].kind == "id" or tokens[at - 2].value in ["]", ")"]):
                    continue
                if at > 1 and tokens[at - 1].value == "(" and tokens[at - 2].value in ["get", "has"]:
                    continue
                if _script_metadata_literal(tokens, at, span.start):
                    continue
                if _script_human_text(token.value):
                    _catalog_add(token.value, path, "line:" + str(token.line), "", display_kind)
            elif token.kind == "id":
                if (at > span.start and tokens[at - 1].value == ".") or (at + 1 < span.end and tokens[at + 1].value == "("):
                    continue
                var definition_key = token.scope + "\u001f" + token.value
                if not definitions.has(definition_key):
                    definition_key = "\u001f" + token.value
                if definitions.has(definition_key):
                    for definition in definitions[definition_key]:
                        sinks.append({"start":definition.start,"end":definition.end,"depth":span.depth + 1})


func _text_field(key):
    var lower = key.to_lower().replace("_", "")
    return lower in ["text", "bbcodetext", "tooltiptext", "hinttooltip", "placeholdertext", "message", "messages", "dialog", "dialogue", "dialogtext", "dialoguetext", "caption", "description", "desc", "title", "body", "prompt", "choices", "options", "subtitle", "content", "sentence", "displayname"]

func _text_resource(source, path):
    if _text_translation_resource(source, path):
        return
    var lex = RegEx.new()
    lex.compile("\"(?:[^\"\\\\]|\\\\.)*\"|[A-Za-z_][A-Za-z_0-9]*|[{}\\[\\]:=,()]")
    var stack = []
    var previous = ""
    var accepted = false
    var count = 0
    for found in lex.search_all(source):
        count += 1
        if count > 500000:
            _warning("文本资源字段超过读取上限：" + path)
            return
        var token = found.get_string()
        if token in ["=", ":"]:
            accepted = _text_field(previous)
        elif token in ["{", "[", "("]:
            stack.append({"kind":token,"accepted":accepted})
            accepted = accepted and token == "["
        elif token in ["}", "]", ")"]:
            if stack.size() > 0:
                stack.pop_back()
            accepted = false
        elif token == ",":
            accepted = stack.size() > 0 and stack[-1].kind == "[" and stack[-1].accepted
        else:
            var value = _json(token.replace("\n", "\\n").replace("\r", "\\r")) if token.begins_with("\"") else token
            if accepted and token.begins_with("\""):
                _catalog_add(value, path, str(found.get_start()), "", "resource_text")
            previous = str(value)
            accepted = false

func _text_translation_resource(source, path):
    var header = RegEx.new()
    header.compile('\\[gd_resource[^\\n]*type="(?:Translation|OptimizedTranslation|PHashTranslation)"')
    if header.search(source) == null:
        return false
    var locale = _path_language(path)
    var locale_expression = RegEx.new()
    locale_expression.compile('(?m)^locale\\s*=\\s*"([^"]+)"')
    var locale_match = locale_expression.search(source)
    if locale_match != null:
        locale = locale_match.get_string(1)
    var messages = RegEx.new()
    messages.compile('(?m)^messages\\s*=\\s*')
    var found = messages.search(source)
    if found == null:
        return true
    # Translation resources are data, not scripts. Parse only their bounded
    # literal message table; never load a resource or execute constructors.
    # Godot 3 saves alternating strings, while Godot 4.6 saves context/key and
    # plural-form StringName arrays. Older plain string dictionaries still work.
    var tokens = _translation_tokens(source, found.get_end())
    var cursor = {"at": 0, "bad": false}
    var table = _translation_literal(tokens, cursor, 0)
    var rows = []
    if not cursor.bad and cursor.at == tokens.size():
        rows = _translation_rows(table)
    if cursor.bad or rows == null:
        _warning("翻译资源消息表无法解析：" + path)
        return true
    for row in rows:
        _catalog_add(row.value, path, row.key, locale, "translation_text")
    return true

func _translation_tokens(source, start):
    var tokens = []
    var at = start
    var depth = 0
    var opened = false
    while at < source.length() and tokens.size() < 400000:
        if tokens.size() % 128 == 0:
            catalog_mutex.lock()
            var cancelled = scan_stop
            catalog_mutex.unlock()
            if cancelled:
                return []
        var c = source.substr(at, 1)
        if c in [" ", "\t", "\r", "\n"]:
            at += 1
            continue
        if c == "&":
            # StringName's serialized prefix is allowed only before a string.
            at += 1
            if at >= source.length() or source.substr(at, 1) != '"':
                return []
            c = '"'
        if c == '"':
            var begin = at
            at += 1
            var closed = false
            while at < source.length():
                c = source.substr(at, 1)
                if c == "\\":
                    at += 2
                elif c == '"':
                    at += 1
                    closed = true
                    break
                else:
                    at += 1
            if not closed:
                return []
            var literal = source.substr(begin, at - begin).replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t")
            var value = _json(literal)
            if typeof(value) != TYPE_STRING:
                return []
            tokens.append({"kind": "string", "value": value})
            continue
        if c in ["{", "[", "("]:
            opened = true
            depth += 1
            if depth > 8:
                return []
            tokens.append(c)
            at += 1
            continue
        if c in ["}", "]", ")"]:
            depth -= 1
            if depth < 0:
                return []
            tokens.append(c)
            at += 1
            if opened and depth == 0:
                return tokens
            continue
        if c in [",", ":"]:
            tokens.append(c)
            at += 1
            continue
        var matched = false
        for constructor in ["PoolStringArray", "PackedStringArray"]:
            if source.substr(at, constructor.length()) == constructor:
                tokens.append(constructor)
                at += constructor.length()
                matched = true
                break
        if not matched:
            return []
    return []

func _translation_at(tokens, at, value):
    return at < tokens.size() and typeof(tokens[at]) == TYPE_STRING and tokens[at] == value

func _translation_literal(tokens, cursor, depth):
    if cursor.bad or depth > 8 or cursor.at >= tokens.size():
        cursor.bad = true
        return null
    var token = tokens[cursor.at]
    cursor.at += 1
    if typeof(token) == TYPE_DICTIONARY:
        return token
    if token in ["PoolStringArray", "PackedStringArray"]:
        if not _translation_at(tokens, cursor.at, "("):
            cursor.bad = true
            return null
        cursor.at += 1
        token = "("
    if not token in ["{", "[", "("]:
        cursor.bad = true
        return null
    var close = "}" if token == "{" else ("]" if token == "[" else ")")
    var values = []
    while cursor.at < tokens.size() and not _translation_at(tokens, cursor.at, close):
        var item = _translation_literal(tokens, cursor, depth + 1)
        if cursor.bad:
            return null
        if token == "{":
            if not _translation_at(tokens, cursor.at, ":"):
                cursor.bad = true
                return null
            cursor.at += 1
            var value = _translation_literal(tokens, cursor, depth + 1)
            if cursor.bad:
                return null
            values.append({"key": item, "value": value})
        else:
            values.append(item)
        if _translation_at(tokens, cursor.at, ","):
            cursor.at += 1
        elif not _translation_at(tokens, cursor.at, close):
            cursor.bad = true
            return null
    if not _translation_at(tokens, cursor.at, close):
        cursor.bad = true
        return null
    cursor.at += 1
    return {"kind": "map" if token == "{" else "array", "values": values}

func _translation_rows(table):
    if typeof(table) != TYPE_DICTIONARY:
        return null
    var rows = []
    if table.kind == "array":
        if table.values.size() % 2 != 0:
            return null
        for at in range(0, table.values.size(), 2):
            if table.values[at].kind != "string" or table.values[at + 1].kind != "string":
                return null
            rows.append({"key": table.values[at].value, "value": table.values[at + 1].value})
        return rows
    if table.kind != "map":
        return null
    for pair in table.values:
        var key = ""
        if pair.key.kind == "string":
            key = pair.key.value
        elif pair.key.kind == "array" and pair.key.values.size() == 2 and pair.key.values[0].kind == "string" and pair.key.values[1].kind == "string":
            key = str(pair.key.values[0].value.length()) + ":" + pair.key.values[0].value + pair.key.values[1].value
        else:
            return null
        if pair.value.kind == "string":
            rows.append({"key": key, "value": pair.value.value})
        elif pair.value.kind == "array":
            for at in range(pair.value.values.size()):
                if pair.value.values[at].kind != "string":
                    return null
                rows.append({"key": key + "#" + str(at), "value": pair.value.values[at].value})
        else:
            return null
    return rows

func _file_size(path):
    var f = FileAccess.open(path, FileAccess.READ)
    if f == null:
        return -1
    var length = f.get_length()
    f.close()
    return length
func _extract_po(source, path):
    var locale = _path_language(path)
    var locale_header = RegEx.new()
    locale_header.compile("Language: ([A-Za-z_@-]+)")
    var found_locale = locale_header.search(source)
    if found_locale != null:
        locale = _locale_language(found_locale.get_string(1))
    var key = ""
    var value = ""
    var field = ""
    for line in (source + "\n").split("\n"):
        var trimmed = line.strip_edges()
        if trimmed == "":
            if key != "":
                if value != "":
                    _catalog_add(value, path, key, locale, "gettext")
                var original_language = _text_language(key)
                _catalog_add(key, path, key, original_language, "gettext_source")
            key = ""
            value = ""
            field = ""
        elif trimmed.begins_with("msgid "):
            field = "key"
            var decoded = _json(trimmed.substr(6))
            key = decoded if typeof(decoded) == TYPE_STRING else ""
        elif trimmed.begins_with("msgstr ") or trimmed.begins_with("msgstr[0] "):
            field = "value"
            var quote = trimmed.find("\"")
            var decoded = _json(trimmed.substr(quote)) if quote >= 0 else null
            value = decoded if typeof(decoded) == TYPE_STRING else ""
        elif trimmed.begins_with("\""):
            var decoded = _json(trimmed)
            if typeof(decoded) == TYPE_STRING:
                if field == "key":
                    key += decoded
                elif field == "value":
                    value += decoded

func _json_step():
    var task = json_work.pop_back()
    var data = task.value
    if task.depth > 16:
        return
    if typeof(data) == TYPE_STRING:
        if task.accepted:
            _catalog_add(data, task.path, task.key, task.get("locale", ""), "json")
    elif typeof(data) == TYPE_ARRAY or typeof(data) == TYPE_DICTIONARY:
        if task.index >= min(data.size(), 20000):
            return
        var child = task.index
        var accepted = task.accepted
        var locale = task.get("locale", "")
        var language_values = task.get("language_values", false)
        if typeof(data) == TYPE_DICTIONARY:
            if not task.has("keys"):
                task.keys = data.keys()
                task.text_map = _text_map(data)
                for locale_field in ["locale", "language", "lang"]:
                    if data.has(locale_field) and typeof(data[locale_field]) == TYPE_STRING:
                        var declared_locale = _locale_language(data[locale_field])
                        if declared_locale != "":
                            locale = declared_locale
                            task["locale"] = locale
                            break
                var language_keys = {}
                var clear_map = true
                for candidate in task.keys:
                    var candidate_language = _locale_language(str(candidate))
                    if candidate_language != "" and typeof(data[candidate]) in [TYPE_STRING, TYPE_ARRAY, TYPE_DICTIONARY]:
                        language_keys[candidate_language] = true
                    elif not str(candidate) in ["key", "context", "comment"]:
                        clear_map = false
                # Even a single-language object is a locale branch. Business
                # objects with id/no/name/text fail clear_map and stay shared.
                task.language_map = language_keys.size() >= 2 or (clear_map and language_keys.size() >= 1)
            child = task.keys[task.index]
            var child_locale = _locale_language(str(child))
            # A version/comment alongside language branches must not erase
            # their locale. Only the ambiguous business keys id/no need a map.
            if child_locale != "" and (task.language_map or not child_locale in ["id", "no"] or typeof(data[child]) in [TYPE_DICTIONARY, TYPE_ARRAY] or _script_human_text(data[child], true)):
                locale = child_locale
                language_values = true
                accepted = true
            else:
                accepted = language_values or task.text_map or _text_field(str(child))
        task.index += 1
        json_work.append(task)
        json_work.append({"value":data[child],"path":task.path,"key":task.key+"/"+str(child),"depth":task.depth+1,"accepted":accepted,"index":0,"locale":locale,"language_values":language_values})

func _optimized_step():
    # Original keys are absent in optimized resources. Read values for pretranslation,
    # then use the game's TranslationServer resolution when the real key is displayed.
    var work = optimized_work
    var buckets = work.buckets
    var strings = work.strings
    var index = work.index
    if index + 2 > buckets.size():
        optimized_work = {}
        return
    var count = int(buckets[index])
    if count < 0 or count > 100000 or index + 2 + count * 4 > buckets.size():
        _warning("压缩语言表结构无法读取：" + work.path)
        optimized_work = {}
        return
    if work.row >= count:
        work.index += 2 + count * 4
        work.row = 0
        return
    var position = index + 2 + work.row * 4
    work.row += 1
    var offset = int(buckets[position + 1])
    var length = int(buckets[position + 2])
    var raw_length = int(buckets[position + 3])
    if offset < 0 or length < 0 or raw_length < 0 or raw_length > 24000 or offset + length > strings.size():
        catalog_skipped += 1
        return
    var data = []
    for at in range(offset, offset + length):
        data.append(strings[at])
    var value = _decode(data) if length == raw_length else _smaz(data, raw_length)
    _catalog_add(value, work.path, "", work.locale, "optimized_value")

func _decode(data):
    while data.size() > 0 and data[data.size() - 1] == 0:
        data.pop_back()
    return _bytes(data).get_string_from_utf8()

func _smaz(data, expected):
    var output = []
    var position = 0
    while position < data.size() and output.size() <= expected:
        var code = int(data[position])
        position += 1
        if code < 254:
            for byte in _utf8(SMAZ[code]):
                output.append(byte)
        elif code == 254:
            if position >= data.size():
                return ""
            output.append(data[position])
            position += 1
        else:
            if position >= data.size():
                return ""
            var count = int(data[position]) + 1
            position += 1
            if position + count > data.size():
                return ""
            for i in range(count):
                output.append(data[position + i])
            position += count
    return _decode(output) if output.size() == expected else ""

func _collect(node):
    _added(node)
    for child in node.get_children():
        _collect(child)

func _added(node):
    if not collection_started and not enabled and not preparing:
        return
    _slot_added(node)
    if node == self or node.has_meta("fusion_rich_display") or (node.get_parent() != null and node.get_parent().has_meta("fusion_rich_display")) or known.has(node.get_instance_id()) or entries.size() >= 20000:
        return
    _discard_cloned_display(node)
    var properties = []
    if node is Label or (node is Button and not node is OptionButton) or node is Label3D:
        properties.append("text")
    elif node is RichTextLabel:
        properties.append("text")
    elif node is LineEdit:
        properties.append("placeholder_text")
    if node is Control:
        properties.append("tooltip_text")
    if properties.size() == 0:
        return
    known[node.get_instance_id()] = true
    for property in properties:
        var value = str(node.get(property))
        var entry = {"id": node.get_instance_id(), "ref": weakref(node), "property": property, "source": value, "last": value, "fonts": {}}
        entries.append(entry)
        if not property in ["tooltip_text", "hint_tooltip"] and node is Control:
            node.theme_changed.connect(Callable(self, "_font_style_changed").bind(entry))

# duplicate() copies our display child and the transparent paint state of its
# original parent. Reclaim only a display whose recorded owner is a different
# node; otherwise the old translation can cover the clone's new game text.
func _discard_cloned_display(node):
    if not (node is Label or node is RichTextLabel):
        return
    for child in node.get_children():
        if not child is CanvasItem or not child.has_meta("fusion_rich_display") or not child.has_meta("fusion_text_owner_id"):
            continue
        if child.get_meta("fusion_text_owner_id") == node.get_instance_id():
            continue
        if child.has_meta("fusion_text_hidden_modulate") and child.has_meta("fusion_text_original_modulate") and node.self_modulate == child.get_meta("fusion_text_hidden_modulate"):
            node.self_modulate = child.get_meta("fusion_text_original_modulate")
        child.hide()
        child.queue_free()

# Collection text lives behind item APIs, not the node's text property. Keep
# only weak references and re-read the current slot before using a cache hit.
# Indexed items can move: a unique owned text can follow a reorder; ambiguous
# equal translations are never installed for different originals in one list.
func _slot_added(node):
    if node is PopupMenu and node.get_parent() is OptionButton:
        return
    if node is TabContainer:
        _added(node.get_tab_bar())
    if not (node is PopupMenu or node is OptionButton or node is ItemList or node is Tree or node is TabBar):
        return
    var id = node.get_instance_id()
    if slot_known.has(id) or slot_groups.size() >= 2000:
        return
    slot_known[id] = true
    var group = {"id":id,"ref":weakref(node),"property":"items","fonts":{},"rows":{},"owned":{},"next":0,"cycle":0,"tree_ref":null,"column":0,"cleanup":[],"cleanup_at":0,"cleanup_owned":{}}
    slot_groups.append(group)
    if node.has_signal("theme_changed"):
        node.theme_changed.connect(Callable(self, "_font_style_changed").bind(group))

func _slot_read(node, row):
    if node is Tree:
        var item = row.ref.get_ref()
        return item.get_text(row.index) if item != null and row.index < node.columns else null
    if node is TabBar:
        return node.get_tab_title(row.index) if row.index < node.tab_count else null
    return node.get_item_text(row.index) if row.index < node.item_count else null

func _slot_write(node, row, text):
    if node is Tree:
        var item = row.ref.get_ref()
        if item != null and row.index < node.columns:
            item.set_text(row.index, text)
    elif node is TabBar:
        node.set_tab_title(row.index, text)
    else:
        node.set_item_text(row.index, text)

func _slot_cycle(group, node):
    group.cleanup = group.rows.keys()
    group.cleanup_at = 0
    group.cleanup_owned = {}
    if group.cleanup.is_empty():
        _slot_cycle_ready(group, node)

func _slot_cycle_ready(group, node):
    group.owned = group.cleanup_owned
    group.cleanup = []
    group.cycle += 1
    group.next = 0
    group.column = 0
    var item = node.get_root() if node is Tree else null
    group.tree_ref = weakref(item) if item != null else null

func _slot_step(group, node):
    # Reclaim stale rows and rebuild ownership a row at a time. A large menu's
    # cycle boundary must not add an unbudgeted full-table walk to a draw frame.
    if not group.cleanup.is_empty():
        var key = group.cleanup[group.cleanup_at]
        var row = group.rows[key]
        if row.cycle < group.cycle:
            group.rows.erase(key)
            slot_rows -= 1
        elif row.last != row.source:
            group.cleanup_owned[row.last] = row.source
        group.cleanup_at += 1
        if group.cleanup_at >= group.cleanup.size():
            _slot_cycle_ready(group, node)
        return
    if group.cycle == 0:
        _slot_cycle(group, node)
    var row
    var key
    if node is Tree:
        var item = group.tree_ref.get_ref() if group.tree_ref != null else null
        if item == null:
            _slot_cycle(group, node)
            return
        key = str(item.get_instance_id()) + ":" + str(group.column)
        row = {"ref":weakref(item),"index":group.column}
        group.column += 1
        if group.column >= node.columns:
            group.column = 0
            var next = item.get_next_in_tree()
            group.tree_ref = weakref(next) if next != null else null
    else:
        var count = node.tab_count if node is TabBar else node.item_count
        if group.next >= count:
            _slot_cycle(group, node)
            return
        key = group.next
        row = {"index":group.next}
        group.next += 1
    var current = _slot_read(node, row)
    if current == null:
        return
    if not group.rows.has(key):
        if slot_rows >= 20000:
            slot_limit_hits += 1
            return
        row.source = group.owned.get(current, current)
        row.last = current
        group.rows[key] = row
        slot_rows += 1
    row = group.rows[key]
    row.cycle = group.cycle
    if current != row.last:
        row.source = group.owned.get(current, current)
    var result = row.source
    if _valid(result):
        var localized = str(TranslationServer.translate(result))
        var translated = _lookup(localized)
        if translated == null and localized != result:
            translated = _lookup(result)
        if translated != null:
            result = translated
        elif pending.size() < 4096:
            pending[localized] = true
    if result != row.source and group.owned.has(result) and group.owned[result] != row.source:
        result = row.source
    if result != current:
        _slot_write(node, row, result)
        if _slot_read(node, row) != result:
            return
        _redraw_viewport(node.get_viewport())
    row.last = result
    if result != row.source:
        group.owned[result] = row.source
        _font(node, group, result)

func _slots_refresh():
    var began = Time.get_ticks_usec()
    if not enabled:
        # Explicit disable/exit restores only values that still equal ours.
        for group in slot_groups:
            var node = group.ref.get_ref()
            if node == null:
                continue
            for row in group.rows.values():
                var current = _slot_read(node, row)
                if current != null and group.owned.has(current):
                    _slot_write(node, row, group.owned[current])
            _font(node, group, "")
            slot_rows -= group.rows.size()
            group.rows.clear()
            group.owned.clear()
            group.cleanup.clear()
            group.cleanup_owned.clear()
            group.cycle = 0
        return
    var count = 0
    while not slot_groups.is_empty() and count < 128:
        slot_cursor %= slot_groups.size()
        var group = slot_groups[slot_cursor]
        var node = group.ref.get_ref()
        if node == null:
            slot_rows -= group.rows.size()
            slot_known.erase(group.id)
            slot_groups.remove_at(slot_cursor)
        else:
            if node.is_inside_tree() and node.get_viewport() != null:
                _slot_step(group, node)
            slot_cursor += 1
        count += 1
        if Time.get_ticks_usec() - began >= 2000:
            break
    slot_max_ms = max(slot_max_ms, (Time.get_ticks_usec() - began) / 1000.0)

func _font_style_changed(entry):
    entry.font_style_dirty = true
    entry.rich_style_dirty = true

# Render plain labels separately too: assigning a long translation to the
# game's Label changes its minimum size and can enlarge an entire container.
func _text_bounds(node):
    if not node.is_inside_tree() or node.get_viewport() == null:
        return Rect2()
    var bounds = Rect2(Vector2.ZERO, node.size)
    var inverse = node.get_global_transform_with_canvas().affine_inverse()
    bounds = bounds.intersection(inverse * node.get_viewport().get_visible_rect())
    var parent = node.get_parent()
    for ignored in range(12):
        if parent == null or parent is Window or parent is SubViewport:
            break
        # A Control is a layout/transform parent, not an implicit clipping box.
        # Games place prompts and speaker names outside nonclipping panels.
        # Only an actual clipping ancestor may constrain that authored area.
        if parent is Control and parent.clip_contents:
            bounds = bounds.intersection(inverse * parent.get_global_transform_with_canvas() * Rect2(Vector2.ZERO, parent.size))
        parent = parent.get_parent()
    return bounds

func _label_release(node, entry):
    if not entry.has("label_display"):
        return
    var display = entry.label_display.get_ref()
    if node.self_modulate == entry.label_hidden_modulate:
        node.self_modulate = entry.label_original_modulate
    if display != null:
        display.hide()
        display.queue_free()
    entry.erase("label_display")
    entry.erase("label_fonts")

func _label_present(node, entry, text):
    var bounds = _text_bounds(node)
    if bounds.size.x <= 1 or bounds.size.y <= 1:
        # Never leave the game text transparent when no replacement can draw.
        _label_release(node, entry)
        return
    var display = entry.label_display.get_ref() if entry.has("label_display") else null
    if display == null:
        display = Label.new()
        display.name = "FusionTranslatedLabel"
        display.set_meta("fusion_rich_display", true)
        display.mouse_filter = Control.MOUSE_FILTER_IGNORE
        display.focus_mode = Control.FOCUS_NONE
        display.set_anchors_and_offsets_preset(Control.PRESET_TOP_LEFT)
        display.clip_text = true
        display.clip_contents = true
        if not entry.has("label_display") or node.self_modulate != entry.label_hidden_modulate:
            entry.label_original_modulate = node.self_modulate
            entry.label_hidden_modulate = Color(node.self_modulate, 0.0)
        entry.label_display = weakref(display)
        entry.label_fonts = {"property":"text", "fonts":{}}
        entry.label_layout_key = []
        node.add_child(display)
    if node.self_modulate != entry.label_hidden_modulate:
        entry.label_original_modulate = node.self_modulate
        entry.label_hidden_modulate = Color(node.self_modulate, 0.0)
    node.self_modulate = entry.label_hidden_modulate
    display.self_modulate = entry.label_original_modulate
    display.set_meta("fusion_text_owner_id", node.get_instance_id())
    display.set_meta("fusion_text_original_modulate", entry.label_original_modulate)
    display.set_meta("fusion_text_hidden_modulate", entry.label_hidden_modulate)
    var settings = node.label_settings
    var original_font = settings.font if settings != null and settings.font != null else node.get_theme_font("font")
    var original_size = settings.font_size if settings != null else node.get_theme_font_size("font_size")
    # LabelSettings wins over theme overrides. Copy its paint values only to
    # our child, so shared settings and the game's font resource stay intact.
    var style_key = [original_font, original_size, settings]
    for property in ["font_color", "outline_color", "outline_size", "shadow_color", "shadow_size", "shadow_offset", "line_spacing"]:
        style_key.append(settings.get(property) if settings != null else null)
    var key = [text, bounds, style_key, node.autowrap_mode, node.horizontal_alignment, node.vertical_alignment, node.uppercase, node.text_direction, node.language]
    if entry.get("font_style_dirty", false) or key != entry.label_layout_key:
        display.theme = node.theme
        display.theme_type_variation = node.theme_type_variation
        display.add_theme_font_override("font", original_font)
        display.add_theme_font_size_override("font_size", original_size)
        entry.label_fonts.fonts.clear()
        for name in ["font_color", "font_outline_color", "font_shadow_color"]:
            display.add_theme_color_override(name, node.get_theme_color(name))
        for name in ["line_spacing", "outline_size", "shadow_offset_x", "shadow_offset_y", "shadow_outline_size"]:
            display.add_theme_constant_override(name, node.get_theme_constant(name))
        if settings != null:
            display.add_theme_color_override("font_color", settings.font_color)
            display.add_theme_color_override("font_outline_color", settings.outline_color)
            display.add_theme_color_override("font_shadow_color", settings.shadow_color)
            display.add_theme_constant_override("outline_size", settings.outline_size)
            display.add_theme_constant_override("shadow_outline_size", settings.shadow_size)
            display.add_theme_constant_override("shadow_offset_x", int(settings.shadow_offset.x))
            display.add_theme_constant_override("shadow_offset_y", int(settings.shadow_offset.y))
            display.add_theme_constant_override("line_spacing", int(settings.line_spacing))
        for name in ["normal", "focus"]:
            if node.has_theme_stylebox(name):
                display.add_theme_stylebox_override(name, node.get_theme_stylebox(name))
        for property in ["horizontal_alignment", "vertical_alignment", "uppercase", "text_direction", "language", "justification_flags"]:
            display.set(property, node.get(property))
        display.text = text
        _font(display, entry.label_fonts, text)
        _label_fit(node, display, bounds, original_size, entry.label_fonts.get("font_cjk", false))
        entry.label_layout_key = key
        entry.font_style_dirty = false
        _redraw_viewport(node.get_viewport())
    display.visible_ratio = node.visible_ratio
    display.visible = bounds.size.x > 1 and bounds.size.y > 1

func _label_fit(node, display, bounds, original_size, cjk):
    var font = display.get_theme_font("font")
    var inset = display.get_theme_stylebox("normal").get_minimum_size()
    var outline = display.get_theme_constant("outline_size")
    var shadow_outline = display.get_theme_constant("shadow_outline_size")
    if cjk:
        var limit = max(1, int(floor(original_size / 8.0)))
        outline = min(outline, limit)
        shadow_outline = min(shadow_outline, limit)
    # Keep strokes inside the panel as well as the glyph advance rectangle.
    var padding = max(outline, shadow_outline)
    bounds = Rect2(bounds.position + Vector2(padding, padding), Vector2(max(1.0, bounds.size.x - padding * 2), max(1.0, bounds.size.y - padding * 2)))
    var available = Vector2(max(1.0, bounds.size.x - inset.x), max(1.0, bounds.size.y - inset.y))
    var minimum = min(original_size, max(10, int(floor(original_size * 0.4))))
    var long_line = font.get_multiline_string_size(display.text, HORIZONTAL_ALIGNMENT_LEFT, -1.0, original_size).x > available.x
    # A one-line original can fit two translated lines at a modestly smaller
    # size. Testing only the original line height would clip long warnings.
    var wrap = node.autowrap_mode != TextServer.AUTOWRAP_OFF or available.y >= font.get_height(original_size) * 1.8 or (long_line and available.y >= font.get_height(minimum) * 1.8)
    display.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART if wrap else TextServer.AUTOWRAP_OFF
    var flags = TextServer.BREAK_MANDATORY | TextServer.BREAK_WORD_BOUND | TextServer.BREAK_ADAPTIVE if wrap else TextServer.BREAK_MANDATORY
    var lower = minimum
    var upper = original_size
    var chosen = minimum
    var measured_text = display.text.to_upper() if display.uppercase else display.text
    # Largest fitting size; short headings keep their original large type.
    for ignored in range(8):
        if lower > upper:
            break
        var size = int((lower + upper) / 2)
        var measured = font.get_multiline_string_size(measured_text, HORIZONTAL_ALIGNMENT_LEFT, available.x if wrap else -1.0, size, -1, flags)
        var lines = max(1, int(round(measured.y / max(1.0, font.get_height(size)))))
        measured.y += max(0, lines - 1) * display.get_theme_constant("line_spacing")
        if measured.x <= available.x + 0.5 and measured.y <= available.y + 0.5:
            chosen = size
            lower = size + 1
        else:
            upper = size - 1
    display.add_theme_font_size_override("font_size", chosen)
    if cjk:
        var limit = max(1, int(floor(chosen / 8.0)))
        display.add_theme_constant_override("outline_size", min(outline, limit))
        display.add_theme_constant_override("shadow_outline_size", min(shadow_outline, limit))
    # The fallback font's metric height can exceed the shaped Label line
    # height. A second manual line limit would hide already-fitted lines.
    display.max_lines_visible = -1
    display.position = bounds.position
    display.size = bounds.size
    # Font's measuring API and Label's visible-line calculation differ for
    # fallback faces. Confirm the final shaped control, not just the estimate.
    display.visible_characters = -1
    for ignored in range(32):
        var lines = display.get_line_count()
        if display.get_visible_line_count() >= lines or chosen <= minimum:
            break
        chosen -= 1
        display.add_theme_font_size_override("font_size", chosen)
        if cjk:
            var limit = max(1, int(floor(chosen / 8.0)))
            display.add_theme_constant_override("outline_size", min(outline, limit))
            display.add_theme_constant_override("shadow_outline_size", min(shadow_outline, limit))
        display.size = bounds.size

# Buttons keep their skin, focus and input behavior. Game replacements win
# when our temporary clipping/font-size overrides are removed.
func _button_release(node, entry):
    if not entry.has("button_layout"):
        return
    var state = entry.button_layout
    if state.has("owned_minimum") and node.custom_minimum_size == state.owned_minimum:
        node.custom_minimum_size = state.original_minimum
    if node.clip_text == state.owned_clip:
        node.clip_text = state.original_clip
    if state.has("owned_size") and node.get_theme_font_size("font_size") == state.owned_size:
        if state.original_size == null:
            node.remove_theme_font_size_override("font_size")
        else:
            node.add_theme_font_size_override("font_size", state.original_size)
    entry.erase("button_layout")

func _button_prepare(node, entry, active):
    if not active:
        _button_release(node, entry)
        return
    if not entry.has("button_layout"):
        entry.button_layout = {"original_clip":node.clip_text, "owned_clip":true, "original_size":node.get_theme_font_size("font_size") if node.has_theme_font_size_override("font_size") else null, "base_size":node.get_theme_font_size("font_size")}
        if node.get_parent() is Container:
            entry.button_layout.original_minimum = node.custom_minimum_size
            entry.button_layout.owned_minimum = node.get_combined_minimum_size()
            node.custom_minimum_size = entry.button_layout.owned_minimum
    var state = entry.button_layout
    if node.clip_text != state.owned_clip:
        state.original_clip = node.clip_text
    if state.has("owned_size") and node.get_theme_font_size("font_size") != state.owned_size:
        state.original_size = node.get_theme_font_size("font_size") if node.has_theme_font_size_override("font_size") else null
        state.base_size = node.get_theme_font_size("font_size")
    state.bounds = _text_bounds(node)
    state.control_size = node.size
    node.clip_text = true

func _button_fit(node, entry, text):
    if not entry.has("button_layout"):
        return
    var state = entry.button_layout
    var font = node.get_theme_font("font")
    var available = state.bounds.size - node.get_theme_stylebox("normal").get_minimum_size()
    if node.icon != null:
        available.x -= node.icon.get_width() + node.get_theme_constant("h_separation")
    var size = state.base_size
    var measured = font.get_multiline_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1.0, size)
    var ratio = min(1.0, min(max(1.0, available.x) / max(1.0, measured.x), max(1.0, available.y) / max(1.0, measured.y)))
    size = max(min(size, max(10, int(ceil(size * 0.5)))), int(floor(size * ratio)))
    if node.get_theme_font_size("font_size") != size:
        node.add_theme_font_size_override("font_size", size)
    state.owned_size = size
    if node.size != state.control_size:
        node.size = state.control_size
    entry.font_style_dirty = false

# append_text/add_text/push_* update RichTextLabel's document without updating
# its text property. Never assign text back to such a document: doing so erases
# the game's tag stack, inline objects, and subsequent append position. An owned
# child supplies translated glyphs while the original document remains intact.
func _rich_release(node, entry):
    if not entry.has("rich_display"):
        return
    var display = entry.rich_display.get_ref()
    if node.self_modulate == entry.rich_hidden_modulate:
        node.self_modulate = entry.rich_original_modulate
    if display != null:
        display.visible = false
        display.queue_free()
    entry.erase("rich_display")
    entry.erase("rich_fonts")

func _rich_signal(value, original_ref, signal_name):
    var original = original_ref.get_ref()
    if original != null:
        original.emit_signal(signal_name, value)

func _rich_present(node, entry, text):
    var bounds = _text_bounds(node)
    if bounds.size.x <= 1 or bounds.size.y <= 1:
        _rich_release(node, entry)
        return
    var display = entry.rich_display.get_ref() if entry.has("rich_display") else null
    if display == null:
        display = RichTextLabel.new()
        display.set_meta("fusion_rich_display", true)
        display.name = "FusionTranslatedText"
        display.focus_mode = Control.FOCUS_NONE
        display.set_anchors_and_offsets_preset(Control.PRESET_TOP_LEFT)
        # A game may delete the owned child while the original is still hidden.
        # Rebuilding it must not adopt our transparent draw state as the original.
        if not entry.has("rich_display") or node.self_modulate != entry.rich_hidden_modulate:
            entry.rich_original_modulate = node.self_modulate
            entry.rich_hidden_modulate = Color(node.self_modulate, 0.0)
        entry.rich_display = weakref(display)
        entry.rich_fonts = {"fonts": {}}
        entry.rich_style_dirty = true
        entry.rich_layout_key = []
        entry.rich_scroll_original = 0.0
        entry.rich_scroll_display = 0.0
        node.add_child(display)
        for signal_name in ["meta_clicked", "meta_hover_started", "meta_hover_ended"]:
            display.connect(signal_name, Callable(self, "_rich_signal").bind(weakref(node), signal_name))
    # self_modulate affects the original CanvasItem only, so its game-owned
    # children still draw normally. Preserve scripts' later opacity changes.
    if node.self_modulate != entry.rich_hidden_modulate:
        entry.rich_original_modulate = node.self_modulate
        entry.rich_hidden_modulate = Color(node.self_modulate, 0.0)
    node.self_modulate = entry.rich_hidden_modulate
    display.self_modulate = entry.rich_original_modulate
    display.set_meta("fusion_text_owner_id", node.get_instance_id())
    display.set_meta("fusion_text_original_modulate", entry.rich_original_modulate)
    display.set_meta("fusion_text_hidden_modulate", entry.rich_hidden_modulate)
    var prefix = entry.get("rich_prefix", "")
    # A document built with text="[center]" followed by append_text has no
    # closing tag in its text property. Preserve its leading format on our
    # owned display, while treating the translated parsed text as literal data.
    var presented_text = prefix + _rich_escape_plain(text) if prefix != "" else text
    display.bbcode_enabled = entry.get("rich_bbcode", false) or prefix != ""
    var interactive_text = node.selection_enabled or (entry.get("rich_bbcode", false) and text.find("[url") >= 0)
    display.mouse_filter = Control.MOUSE_FILTER_PASS if interactive_text else Control.MOUSE_FILTER_IGNORE
    var style_changed = entry.get("rich_style_dirty", true)
    if style_changed:
        _rich_sync_style(node, display, entry)
    for property in ["text_direction", "language", "scroll_active", "selection_enabled", "tab_size", "justification_flags", "custom_effects", "meta_underlined"]:
        if display.get(property) != node.get(property):
            display.set(property, node.get(property))
    display.position = bounds.position
    if display.size != bounds.size:
        display.size = bounds.size
    display.clip_contents = true
    display.visible = bounds.size.x > 1 and bounds.size.y > 1
    if display.text != presented_text:
        display.text = presented_text
        _redraw_viewport(node.get_viewport())
    var reveal_ratio = min(node.visible_ratio, entry.get("rich_reveal_ratio", 1.0))
    if display.visible_ratio != reveal_ratio:
        display.visible_ratio = reveal_ratio
    entry.rich_fonts.property = "text"
    _font(display, entry.rich_fonts, text)
    var outline_limit = max(1, int(floor(node.get_theme_font_size("normal_font_size") / 8.0)))
    var padding = max(node.get_theme_constant("outline_size"), node.get_theme_constant("shadow_outline_size"))
    if entry.rich_fonts.get("font_cjk", false):
        padding = min(padding, outline_limit)
    bounds = Rect2(bounds.position + Vector2(padding, padding), Vector2(max(1.0, bounds.size.x - padding * 2), max(1.0, bounds.size.y - padding * 2)))
    display.position = bounds.position
    display.size = bounds.size
    # Color/alpha animation changes paint, not the translated glyph layout.
    var layout_key = [entry.get("rich_layout_text", presented_text), bounds, node.autowrap_mode, node.scroll_active]
    if style_changed or layout_key != entry.rich_layout_key:
        _rich_fit(node, display, entry.rich_fonts.get("font_cjk", false), bounds)
        entry.rich_layout_key = layout_key
    var original_scroll = node.get_v_scroll_bar()
    var translated_scroll = display.get_v_scroll_bar()
    var original_range = max(0.0, original_scroll.max_value - original_scroll.page)
    var translated_range = max(0.0, translated_scroll.max_value - translated_scroll.page)
    if original_scroll.value != entry.rich_scroll_original:
        translated_scroll.value = original_scroll.value / original_range * translated_range if original_range > 0 else 0.0
    elif translated_scroll.value != entry.rich_scroll_display:
        original_scroll.value = translated_scroll.value / translated_range * original_range if translated_range > 0 else 0.0
    entry.rich_scroll_original = original_scroll.value
    entry.rich_scroll_display = translated_scroll.value

func _rich_sync_style(node, display, entry):
    # theme_changed on the game control invalidates this snapshot. Reassigning
    # theme/font overrides every frame would repeatedly reshape every glyph.
    if display.theme != node.theme:
        display.theme = node.theme
    if display.theme_type_variation != node.theme_type_variation:
        display.theme_type_variation = node.theme_type_variation
    for name in ["normal_font", "bold_font", "italics_font", "bold_italics_font", "mono_font"]:
        display.add_theme_font_override(name, node.get_theme_font(name))
    entry.rich_fonts.fonts.clear()
    for name in ["default_color", "font_shadow_color", "font_outline_color", "selection_color", "selected_font_color"]:
        display.add_theme_color_override(name, node.get_theme_color(name))
    for name in ["line_separation", "outline_size", "shadow_offset_x", "shadow_offset_y", "shadow_outline_size", "table_h_separation", "table_v_separation"]:
        display.add_theme_constant_override(name, node.get_theme_constant(name))
    for name in ["normal", "focus"]:
        display.add_theme_stylebox_override(name, node.get_theme_stylebox(name))
    entry.rich_style_dirty = false

func _rich_fit(node, display, cjk, bounds):
    # Fit only this owned translation into the game's existing rectangle. The
    # original document, theme sizes, scale and container minimum stay intact.
    var names = ["normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size"]
    for name in names:
        display.add_theme_font_size_override(name, node.get_theme_font_size(name))
    var original_normal_size = node.get_theme_font_size("normal_font_size")
    var normal_font = display.get_theme_font("normal_font")
    var minimum_size = min(original_normal_size, max(10, int(floor(original_normal_size * 0.4))))
    var minimum_height = normal_font.get_height(minimum_size)
    var can_wrap = node.autowrap_mode != TextServer.AUTOWRAP_OFF or bounds.size.y >= minimum_height * 1.8
    display.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART if can_wrap else TextServer.AUTOWRAP_OFF
    if bounds.size.x > 1 and bounds.size.y > 1:
        for ignored in range(5):
            var ratio = 1.0
            ratio = min(ratio, bounds.size.x / max(1.0, float(display.get_content_width())))
            if not node.scroll_active:
                ratio = min(ratio, bounds.size.y / max(1.0, float(display.get_content_height())))
            if ratio >= 0.99:
                break
            for name in names:
                var original_size = node.get_theme_font_size(name)
                var minimum = min(original_size, max(10, int(ceil(original_size * 0.5))))
                display.add_theme_font_size_override(name, max(minimum, int(floor(display.get_theme_font_size(name) * ratio))))
    # Dense CJK strokes cannot carry a 20px halo at a 28px type size. Keep the
    # original style untouched; constrain only our derived display's outline.
    var outline_limit = max(1, int(floor(display.get_theme_font_size("normal_font_size") / 8.0)))
    for name in ["outline_size", "shadow_outline_size"]:
        display.add_theme_constant_override(name, min(node.get_theme_constant(name), outline_limit) if cjk else node.get_theme_constant(name))

func _rich_refresh(node, entry, source):
    entry.rich_mode = true
    entry.rich_reveal_ratio = 1.0
    var request_source = source
    var document = null
    var waiting_reveal = false
    if enabled and entry.get("rich_bbcode", false):
        if entry.get("rich_document_source", null) != source:
            entry.rich_document_source = source
            entry.rich_document = _rich_style_document(source)
        document = entry.get("rich_document")
        if document != null:
            if document.reveal != null:
                # Prove a moving visibility boundary before treating transparent
                # text as a typewriter animation. Static hidden spans stay hidden.
                var reveal = document.reveal
                if entry.get("rich_reveal_key", null) != reveal.text:
                    entry.rich_reveal_key = reveal.text
                    entry.rich_reveal_chars = document.visible
                    entry.rich_reveal_active = false
                    entry.rich_reveal_since = _ticks()
                elif entry.rich_reveal_chars != document.visible:
                    entry.rich_reveal_active = true
                    entry.rich_reveal_chars = document.visible
                if entry.rich_reveal_active:
                    document = reveal
                    entry.rich_reveal_ratio = float(entry.rich_reveal_chars) / max(1, entry.rich_document.total)
                elif not document.closed or _ticks() - entry.rich_reveal_since < 180:
                    waiting_reveal = true
            else:
                entry.erase("rich_reveal_key")
            request_source = document.text
    var translated = null
    var menu = _rich_menu_document(source) if enabled and entry.get("rich_bbcode", false) else null
    if menu != null:
        if lookup_dirty:
            _index_cache()
        request_source = menu.canonical
        var menu_words = rich_menu_cache.get(menu.key)
        if menu_words != null:
            translated = _rich_menu_present_text(source, menu, menu_words)
            entry.rich_layout_text = JSON.stringify(menu_words)
    elif enabled and _valid(request_source):
        translated = _lookup(request_source)
        if translated == null and document != null:
            translated = rich_style_cache.get(request_source)
        if translated != null and document != null:
            entry.rich_layout_text = translated
            translated = _rich_paint(translated, document)
        else:
            entry.rich_layout_text = translated
        if waiting_reveal and translated == null:
            request_source = ""
    if translated != null and translated != source:
        _rich_present(node, entry, translated)
    else:
        _rich_release(node, entry)
        if enabled and _valid(request_source) and translated == null and pending.size() < 4096:
            pending[request_source] = true
    entry.last = str(node.get(entry.property))
    entry.last_parsed = node.get_parsed_text()

func _font_u16(data, offset):
    return int(data[offset]) * 256 + int(data[offset + 1])

func _font_u32(data, offset):
    return int(data[offset]) * 16777216 + int(data[offset + 1]) * 65536 + int(data[offset + 2]) * 256 + int(data[offset + 3])

func _font_directory_valid(data):
    # Native font loaders may accept malformed data and silently use a fallback.
    # Bound and inspect the SFNT directory before replacing the working font.
    var size = data.size()
    if size < 12:
        return false
    var base = 0
    if _font_u32(data, 0) == 0x74746366:
        var faces = _font_u32(data, 8)
        if faces < 1 or faces > 4096 or 12 + faces * 4 > size:
            return false
        base = _font_u32(data, 12)
    if base < 0 or base + 12 > size:
        return false
    var magic = _font_u32(data, base)
    if not magic in [0x00010000, 0x4F54544F, 0x74727565]:
        return false
    var count = _font_u16(data, base + 4)
    if count < 1 or count > 256 or base + 12 + count * 16 > size:
        return false
    var tables = {}
    for index in range(count):
        var record = base + 12 + index * 16
        var tag = _font_u32(data, record)
        var offset = _font_u32(data, record + 8)
        var length = _font_u32(data, record + 12)
        if tables.has(tag) or offset > size or length > size - offset:
            return false
        tables[tag] = length
    # cmap, head, hhea, hmtx, maxp and name must contain real records.
    for tag in [0x636D6170, 0x68656164, 0x68686561, 0x686D7478, 0x6D617870, 0x6E616D65]:
        if int(tables.get(tag, 0)) < 4:
            return false
    return tables.has(0x676C7966) or tables.has(0x43464620) or tables.has(0x43464632)

func _load_font_file(path):
    if not path.get_extension().to_lower() in ["ttf", "otf", "ttc"]:
        return null
    var file = FileAccess.open(path, FileAccess.READ)
    if file == null:
        return null
    var size = file.get_length()
    if size < 12 or size > 33554432:
        file.close()
        return null
    var data = file.get_buffer(size)
    file.close()
    if data.size() != size or not _font_directory_valid(data):
        return null
    var selected = FontFile.new()
    if selected.load_dynamic_font(path) != OK:
        return null
    return selected

func _font_preferences(q):
    var mode = str(q.get("mode", "default"))
    var path = str(q.get("path", "")).replace("\\", "/")
    var family = str(q.get("family", ""))
    if mode not in ["default", "custom"]:
        return {"accepted":false,"error":"Unknown font preference mode.","restartRequired":false}
    var selected = null
    if mode == "custom":
        if path != "":
            selected = _load_font_file(path)
        elif family != "" and OS.get_system_fonts().has(family):
            selected = SystemFont.new()
            selected.font_names = [family]
            selected.allow_system_fallback = true
        if selected == null:
            return {"accepted":false,"error":"Cannot load the selected font. Choose a valid TTF, OTF or TTC file up to 32 MiB.","restartRequired":false}
    else:
        path = ""
        family = ""
    # Validation completed before touching any preference, text or owned font.
    var changed = mode != font_preference_mode or path != font_preference_path or family != font_preference_family
    var was_enabled = enabled
    if changed and was_enabled:
        enabled = false
        _refresh(true)
    if changed:
        custom_font = selected
        font_preference_mode = mode
        font_preference_path = path
        font_preference_family = family
    enabled = was_enabled
    if changed and was_enabled:
        _refresh(true)
    return {"accepted":true,"mode":mode,"enabled":enabled,"writebackPending":changed and enabled,"restorePending":changed and enabled and mode == "default","restartRequired":false}

func _prepare_font():
    var custom_path = OS.get_environment("FUSION_GAME_FONT_PATH").replace("\\", "/")
    var custom_family = OS.get_environment("FUSION_GAME_FONT_FAMILY")
    chinese_font = SystemFont.new()
    chinese_font.font_names = ["Microsoft YaHei", "Microsoft YaHei UI", "DengXian"]
    chinese_font.allow_system_fallback = true
    if custom_path != "" or custom_family != "":
        _font_preferences({"mode":"custom","path":custom_path,"family":custom_family})

func _font(node, entry, translated):
    if node is Label3D:
        if enabled and translated != "" and (custom_font != null or chinese_font != null):
            if not entry.has("world_font") or node.font != entry.world_font.owned:
                var original = node.font
                var replacement = FontVariation.new()
                replacement.base_font = custom_font if custom_font != null else (original if original != null else ThemeDB.fallback_font)
                replacement.fallbacks = [original if original != null else ThemeDB.fallback_font] if custom_font != null else [chinese_font]
                entry.world_font = {"original":original,"owned":replacement}
                node.font = replacement
        elif entry.has("world_font"):
            if node.font == entry.world_font.owned:
                node.font = entry.world_font.original
            entry.erase("world_font")
        return
    if not (node is Control or (node is Window and node.has_method("add_theme_font_override"))) or entry.property in ["tooltip_text", "hint_tooltip"]:
        return
    # Unchanged strings do not need another Unicode scan on every draw pass.
    if entry.get("font_text", null) != translated:
        entry.font_text = translated
        entry.font_cjk = false
        for i in range(translated.length()):
            var character = translated.unicode_at(i)
            if character >= 0x3000 and character <= 0x9fff or character >= 0xff00 and character <= 0xffef:
                entry.font_cjk = true
                break
    var names = ["normal_font", "bold_font", "italics_font", "bold_italics_font", "mono_font"] if node is RichTextLabel else ["font"]
    if enabled and (translated != "" if custom_font != null else (chinese_font != null and entry.get("font_cjk", false))):
        for name in names:
            if entry.fonts.has(name):
                var owned = entry.fonts[name]
                if node.get_theme_font(name) == owned.owned:
                    if not entry.get("font_style_dirty", false):
                        continue
                    # Restore our override briefly to resolve a changed inherited
                    # theme. A game's replacement is adopted, never overwritten.
                    if owned.original == null:
                        node.remove_theme_font_override(name)
                    else:
                        node.add_theme_font_override(name, owned.original)
                entry.fonts.erase(name)
            var old = node.get_theme_font(name)
            var original = old if node.has_theme_font_override(name) else null
            # Preserve the original Latin/numeric/icon face and its variation,
            # spacing and existing fallbacks. Only missing glyphs reach CJK.
            var replacement = old.duplicate() if old is FontVariation else FontVariation.new()
            if not old is FontVariation:
                replacement.base_font = old
            var fallbacks = replacement.fallbacks.duplicate()
            if custom_font != null:
                replacement.base_font = custom_font
                fallbacks.append(old)
            elif not fallbacks.has(chinese_font):
                fallbacks.append(chinese_font)
            replacement.fallbacks = fallbacks
            node.add_theme_font_override(name, replacement)
            entry.fonts[name] = {"original": original, "owned": replacement}
    else:
        for name in entry.fonts:
            var font = entry.fonts[name]
            if node.get_theme_font(name) == font.owned:
                if font.original == null:
                    node.remove_theme_font_override(name)
                else:
                    node.add_theme_font_override(name, font.original)
        entry.fonts.clear()
    # Installing our own override also emits theme_changed.
    entry.font_style_dirty = false

# Exact cache first, then reversible whole-line wrappers and explicit placeholders.
# Never fuzzy-match unrelated dialogue or infer a player name from game state.
var template_buckets = {}
var lookup_memo = {}
var lookup_dirty = true
var plain_cache = {}
var rich_tag_pattern = null
var rich_unknown_pattern = null
var rich_style_cache = {}
var rich_style_records = {}
var rich_style_pattern = null
var rich_color_pattern = null

# Cache identity includes all words, numbers, links and format boundaries. Only
# valid hexadecimal color values are paint parameters. Unsupported/complex BBCode
# stays on the original exact path instead of guessing its document semantics.
func _rich_style_document(value):
    if value.length() > 12000:
        return null
    if rich_style_pattern == null:
        rich_style_pattern = RegEx.new()
        rich_style_pattern.compile("\\[(/?)([a-z_]+)(?:=([^\\]\\r\\n]*))?\\]")
        rich_color_pattern = RegEx.new()
        rich_color_pattern.compile("^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")
    var matches = rich_style_pattern.search_all(value)
    if matches.size() > 256:
        return null
    var stack = []
    var parts = []
    var reduced = []
    var colors = []
    var reduced_colors = []
    var tags = []
    var reduced_tags = []
    var position = 0
    var total = 0
    var visible = 0
    var hidden = false
    var hidden_seen = false
    var visible_after_hidden = false
    var zero_count = 0
    for token_index in range(matches.size() + 1):
        var found = matches[token_index] if token_index < matches.size() else null
        var end = found.get_start() if found != null else value.length()
        var run = value.substr(position, end - position)
        parts.append(run)
        reduced.append(run)
        total += run.length()
        if run.length() > 0:
            if hidden:
                hidden_seen = true
            else:
                visible += run.length()
                visible_after_hidden = visible_after_hidden or hidden_seen
        if found == null:
            break
        position = found.get_end()
        var name = found.get_string(2)
        var closing = found.get_string(1) == "/"
        var raw = found.get_string()
        if name in ["lb", "rb"] and not closing and raw in ["[lb]", "[rb]"]:
            parts.append(raw)
            reduced.append(raw)
            total += 1
            if hidden:
                hidden_seen = true
            else:
                visible += 1
                visible_after_hidden = visible_after_hidden or hidden_seen
            continue
        if not name in ["b", "i", "u", "s", "color", "font", "font_size", "url", "center", "right", "left", "fill", "wave", "shake", "rainbow", "outline_size", "outline_color", "bgcolor", "fgcolor"]:
            return null
        var zero = false
        var paint = false
        var canonical = raw
        if closing:
            if stack.size() == 0 or stack.back().name != name:
                return null
            var popped = stack.pop_back()
            zero = popped.zero
            hidden = popped.hidden_before
        else:
            paint = name == "color" and rich_color_pattern.search(found.get_string(3)) != null
            if paint:
                canonical = "[color=#FFFFFFFF]"
                zero = raw.length() == 17 and found.get_string(3).right(2) == "00"
                colors.append(raw)
                if not zero:
                    reduced_colors.append(raw)
            stack.append({"name":name,"zero":zero,"hidden_before":hidden})
            if name == "color":
                hidden = zero
            if zero:
                zero_count += 1
        parts.append(canonical)
        tags.append(canonical)
        if not zero:
            reduced.append(canonical)
            reduced_tags.append(canonical)
    var closed = stack.size() == 0
    for opened in stack:
        if not opened.zero:
            return null
    var reveal = null
    if zero_count > 0 and hidden_seen and not visible_after_hidden:
        reveal = {"text":"".join(reduced),"colors":reduced_colors,"tags":reduced_tags,"closed":true}
    elif not closed:
        return null
    return {"text":"".join(parts),"colors":colors,"tags":tags,"closed":closed,"reveal":reveal,"visible":visible,"total":total}

# Only a document made entirely of separate, plain link labels is projected.
# Paint, hidden sentinels and whitespace remain in the live document. Link IDs,
# order, label text and numbers form its identity; arbitrary rich text falls back.
var rich_menu_records = {}
var rich_menu_cache = {}

func _rich_menu_document(value):
    if value.find("[url=") < 0 or value.length() > 12000:
        return null
    # Reuse the bounded, balanced BBCode lexer, not a permissive tag stripper.
    var validated = _rich_style_document(value)
    if validated == null or not validated.closed:
        return null
    var urls = []
    var words = []
    var spans = []
    var stack = []
    var hidden = false
    var in_link = false
    var position = 0
    for token in rich_style_pattern.search_all(value):
        var run = value.substr(position, token.get_start() - position)
        if in_link:
            if token.get_string() != "[/url]" or run.strip_edges() == "":
                return null
            var word = run.strip_edges()
            var start = position + run.find(word)
            words.append(word)
            spans.append([start, start + word.length()])
        elif not hidden and run.strip_edges() != "":
            return null
        var name = token.get_string(2)
        var closing = token.get_string(1) == "/"
        if not name in ["url", "color", "bgcolor", "fgcolor", "center", "left", "right"]:
            return null
        if closing:
            if stack.is_empty() or stack.back().name != name:
                return null
            hidden = stack.pop_back().hidden
            if name == "url":
                in_link = false
        else:
            stack.append({"name":name,"hidden":hidden})
            if name == "url":
                if hidden or token.get_string(3) == "" or urls.size() >= 128:
                    return null
                urls.append(token.get_string(3))
                in_link = true
            elif name == "color":
                var color = token.get_string(3).trim_prefix("#")
                if color.length() == 8 and color.is_valid_hex_number() and color.ends_with("00"):
                    hidden = true
        position = token.get_end()
    if not stack.is_empty() or value.substr(position).strip_edges() != "" or urls.is_empty() or words.size() != urls.size():
        return null
    var canonical = ""
    for i in range(words.size()):
        canonical += "[url=" + urls[i] + "]" + words[i] + "[/url]\n"
    return {"key":JSON.stringify([urls,words]),"canonical":canonical,"urls":urls,"words":words,"spans":spans}

func _rich_menu_index(values):
    # Sorting makes old conflicting hover variants choose the same wording on
    # every startup. A canonical entry (including a user edit) has precedence.
    var sources = values.keys()
    sources.sort()
    for source in sources:
        if source.find("[url=") < 0:
            continue
        var record = rich_menu_records.get(source)
        if record == null or record.value != values[source]:
            var original = _rich_menu_document(source)
            var translated = _rich_menu_document(values[source])
            record = {"value":values[source],"original":original,"translated":translated}
            if rich_menu_records.size() < 20000 or rich_menu_records.has(source):
                rich_menu_records[source] = record
        var original = record.original
        var translated = record.translated
        if original == null or translated == null or original.urls != translated.urls:
            continue
        if not rich_menu_cache.has(original.key) or source == original.canonical:
            rich_menu_cache[original.key] = translated.words

func _rich_menu_present_text(source, document, translated_words):
    if translated_words.size() != document.spans.size():
        return null
    var result = ""
    var position = 0
    for i in range(document.spans.size()):
        var span = document.spans[i]
        result += source.substr(position, span[0] - position) + translated_words[i]
        position = span[1]
    return result + source.substr(position)


func _rich_paint(text, document):
    var translated = _rich_style_document(text)
    if translated == null or translated.tags != document.tags or translated.colors.size() != document.colors.size():
        return null
    var output = ""
    var position = 0
    for color in document.colors:
        var at = text.find("[color=#FFFFFFFF]", position)
        if at < 0:
            return null
        output += text.substr(position, at - position) + color
        position = at + 17
    return output + text.substr(position)

func _rich_index_styles(values):
    for source in values:
        if source.find("[color=#") < 0:
            continue
        var record = rich_style_records.get(source)
        if record == null or record.value != values[source]:
            var original = _rich_style_document(source)
            var translated = _rich_style_document(values[source])
            record = {"value":values[source],"aliases":[]}
            if original != null and translated != null:
                if original.closed and translated.closed and original.tags == translated.tags:
                    record.aliases.append([original.text,translated.text])
                if original.reveal != null and translated.reveal != null and original.reveal.tags == translated.reveal.tags:
                    record.aliases.append([original.reveal.text,translated.reveal.text])
            # Provider batches must not repeatedly parse the whole startup cache.
            if rich_style_records.size() < 20000 or rich_style_records.has(source):
                rich_style_records[source] = record
        # An exact canonical entry from the host wins in _lookup. Old variants
        # retain one valid wording instead of switching along with their paint.
        for alias in record.aliases:
            if not rich_style_cache.has(alias[0]):
                rich_style_cache[alias[0]] = alias[1]

func _plain_rich_text(value, require_closed = true):
    # Lossless text-only aliases for ordinary emphasis/color wrappers. Complex
    # inline images/tables/custom effects are not guessed or reinterpreted.
    if value.find("[") < 0:
        return value
    if rich_tag_pattern == null:
        rich_tag_pattern = RegEx.new()
        rich_tag_pattern.compile("\\[(/?)(b|i|u|s|color|font|font_size|center|right|left|fill|wave|shake|rainbow|outline_size|outline_color|bgcolor|fgcolor)(?:=[^\\]\\r\\n]*)?\\]")
        rich_unknown_pattern = RegEx.new()
        rich_unknown_pattern.compile("\\[/?[A-Za-z_][^\\]]*\\]")
    var stack = []
    for match_tag in rich_tag_pattern.search_all(value):
        var name = match_tag.get_string(2)
        if match_tag.get_string(1) == "":
            stack.append(name)
        elif stack.size() == 0 or stack.pop_back() != name:
            return null
    if require_closed and stack.size() != 0:
        return null
    var plain = rich_tag_pattern.sub(value, "", true)
    if rich_unknown_pattern.search(plain.replace("[lb]", "").replace("[rb]", "")) != null:
        return null
    return plain.replace("[lb]", "[").replace("[rb]", "]")

# This is document-state inspection, not a cache alias. Godot permits an open
# formatting stack before append_text. Cache aliases still require closed tags.
func _rich_format_prefix(value):
    if value == "" or not value.begins_with("["):
        return ""
    if _plain_rich_text(value, false) != "":
        return ""
    var end = 0
    for match_tag in rich_tag_pattern.search_all(value):
        if match_tag.get_start() != end or match_tag.get_string(1) != "":
            break
        end = match_tag.get_end()
    return value if end == value.length() else ""

func _rich_escape_plain(value):
    var escaped = ""
    for c in value:
        escaped += "[lb]" if c == "[" else "[rb]" if c == "]" else c
    return escaped

func _regex_escape(value):
    var result = ""
    for i in range(value.length()):
        var c = value.substr(i, 1)
        result += ("\\" if c in ["\\", ".", "^", "$", "|", "?", "*", "+", "(", ")", "[", "]", "{", "}"] else "") + c
    return result

func _index_cache():
    template_buckets.clear()
    lookup_memo.clear()
    plain_cache.clear()
    lookup_dirty = false
    rich_style_cache.clear()
    rich_menu_cache.clear()
    for source in rich_menu_records.keys():
        if not cache.has(source) and not startup_exact.has(source):
            rich_menu_records.erase(source)
    _rich_menu_index(startup_exact)
    # Current canonical translations take precedence over inherited variants.
    _rich_menu_index(cache)
    for source in rich_style_records.keys():
        if not cache.has(source) and not startup_exact.has(source):
            rich_style_records.erase(source)
    _rich_index_styles(cache)
    _rich_index_styles(startup_exact)
    var ambiguous = {}
    for source in cache:
        if source.find("[") < 0:
            continue
        var plain = _plain_rich_text(source)
        var translated = _plain_rich_text(cache[source])
        if plain == null or translated == null or plain == source or cache.has(plain) or ambiguous.has(plain):
            continue
        if plain_cache.has(plain) and plain_cache[plain] != translated:
            plain_cache.erase(plain)
            ambiguous[plain] = true
        else:
            plain_cache[plain] = translated
    var candidates = cache.duplicate()
    for source in plain_cache:
        candidates[source] = plain_cache[source]
    var tokens = RegEx.new()
    tokens.compile("<<(?:var[ \\t]+[A-Za-z_][A-Za-z_0-9.: -]{0,120}|arg[ \\t]+[0-9]{1,3})>>|\\{[A-Za-z_][A-Za-z_0-9]*\\}|:[A-Za-z_][A-Za-z_0-9]*:")
    for source in candidates:
        var matches = tokens.search_all(source)
        if matches.size() == 0 or matches.size() > 8:
            continue
        var literal = tokens.sub(source, "", true)
        if literal.length() < 12:
            continue
        var parts = []
        var cursor = 0
        var placeholders = []
        var valid = true
        for found in matches:
            var token = found.get_string()
            if candidates[source].count(token) != source.count(token):
                valid = false
                break
            parts.append(source.substr(cursor, found.get_start() - cursor))
            placeholders.append(token)
            cursor = found.get_end()
        if not valid:
            continue
        parts.append(source.substr(cursor))
        var prefix = source.substr(0, min(4, matches[0].get_start()))
        if not template_buckets.has(prefix):
            template_buckets[prefix] = []
        template_buckets[prefix].append({"parts":parts,"tokens":placeholders,"text":candidates[source]})

func _match_template(candidate, value):
    if not value.begins_with(candidate.parts[0]):
        return null
    var position = candidate.parts[0].length()
    var values = {}
    for i in range(candidate.tokens.size()):
        var delimiter = candidate.parts[i + 1]
        var last = i == candidate.tokens.size() - 1
        if delimiter == "" and not last:
            return null
        var end = value.length() - delimiter.length() if last and value.ends_with(delimiter) else value.find(delimiter, position)
        if end <= position or end - position > 120:
            return null
        if not last:
            var second = value.find(delimiter, end + 1)
            if second >= 0 and second - position <= 120:
                return null
        var part = value.substr(position, end - position)
        var token = candidate.tokens[i]
        for c in ["\r", "\n", "<", ">", "[", "]", "{", "}"]:
            if part.find(c) >= 0:
                return null
        if values.has(token) and values[token] != part:
            return null
        values[token] = part
        position = end + delimiter.length()
    return values if position == value.length() else null

func _lookup(value, depth = 0):
    if cache.has(value):
        return cache[value]
    if depth == 0 and activation_exact.has(value):
        # Cache presence is not observation. Report this complete live source
        # once so later corrections, revocation and retries retain authority.
        if not activation_observed.has(value) and pending.size() < 4096:
            pending[value] = true
            activation_observed[value] = true
        return activation_exact[value]
    if depth == 0 and startup_exact.has(value):
        return startup_exact[value]
    if lookup_dirty:
        _index_cache()
    if plain_cache.has(value):
        return plain_cache[value]
    if lookup_memo.has(value):
        return lookup_memo[value]
    if depth > 8:
        return null
    var translated = null
    # Only peel a balanced tag which encloses the entire string. Internal tags
    # are sent for translation unchanged, because their new positions are unknown.
    var wrapper = RegEx.new()
    wrapper.compile("(?s)^([ \\t\\r\\n]*)\\[([A-Za-z_]+)(?:=[^\\]\\r\\n]*)?\\](.*)\\[/\\2\\]([ \\t\\r\\n]*)$")
    var match_tag = wrapper.search(value)
    if match_tag != null and match_tag.get_string(3).find("[/" + match_tag.get_string(2) + "]") < 0:
        var body = match_tag.get_string(3)
        var found = _lookup(body, depth + 1)
        if found != null:
            translated = value.substr(0, match_tag.get_start(3)) + found + value.substr(match_tag.get_end(3))
    var trimmed = value.strip_edges()
    if translated == null and trimmed != value and trimmed != "":
        var found = _lookup(trimmed, depth + 1)
        if found != null:
            var offset = value.find(trimmed)
            translated = value.substr(0, offset) + found + value.substr(offset + trimmed.length())
    if translated == null and value.find("\n") >= 0:
        var lines = value.split("\n", true)
        var ready = lines.size() <= 128
        var joined = []
        for line in lines:
            var found = _lookup(line, depth + 1) if line.strip_edges() != "" else line
            if found == null:
                ready = false
                break
            joined.append(found)
        if ready:
            translated = "\n".join(joined)
    if translated == null:
        var candidates = []
        for length in range(0, min(4, value.length()) + 1):
            candidates += template_buckets.get(value.substr(0, length), [])
        if candidates.size() <= 256:
            for candidate in candidates:
                var values = _match_template(candidate, value)
                if values == null:
                    continue
                var replacement = _fill_template(candidate.text, values)
                if translated != null and translated != replacement:
                    translated = null
                    break
                translated = replacement
    if lookup_memo.size() >= 4096:
        lookup_memo.clear()
    lookup_memo[value] = translated
    return translated

# Walk the untranslated template positions once. Inserted player names remain
# opaque data even when they happen to contain another placeholder's spelling.
func _fill_template(text, values):
    var result = ""
    var cursor = 0
    while cursor < text.length():
        var position = text.length()
        var next_token = ""
        for token in values:
            var found = text.find(token, cursor)
            if found >= 0 and found < position:
                position = found
                next_token = token
        result += text.substr(cursor, position - cursor)
        if next_token == "":
            break
        result += values[next_token]
        cursor = position + next_token.length()
    return result


func _refresh(all = false):
    if refreshing:
        return
    refreshing = true
    var stale = {}
    var start = Time.get_ticks_usec()
    var available = entries.size()
    for ignored in range(available if all else min(available, 128)):
        if not all and ignored > 0 and Time.get_ticks_usec() - start > 2000:
            break
        entry_cursor %= available
        var entry = entries[entry_cursor]
        entry_cursor += 1
        var node = entry.ref.get_ref()
        if node == null:
            stale[entry.id] = true
            continue
        # Pooled scenes can leave the tree without being freed. Keep their
        # identity for re-entry, but do not lay out or draw without a viewport.
        if not node.is_inside_tree() or node.get_viewport() == null:
            continue
        if not all and node is CanvasItem and not node.is_visible_in_tree():
            continue
        var current = str(node.get(entry.property))
        if node is RichTextLabel and entry.property == "text":
            var parsed = node.get_parsed_text()
            var property_plain = _plain_rich_text(current, false) if node.bbcode_enabled else current
            var appended = current == "" or (property_plain != null and property_plain != parsed) or (entry.has("last_parsed") and current == entry.last and parsed != entry.last_parsed)
            if current != entry.last:
                entry.rich_appended = false
            appended = appended or entry.get("rich_appended", false)
            entry.rich_appended = appended
            entry.rich_bbcode = node.bbcode_enabled and not appended
            entry.rich_prefix = _rich_format_prefix(current) if node.bbcode_enabled and appended else ""
            _rich_refresh(node, entry, parsed if appended else current)
            continue
        if current != entry.last:
            entry.source = current
        var result = entry.source
        if enabled and _valid(result):
            var localized = str(TranslationServer.translate(result))
            var translated = _lookup(localized)
            if translated == null and localized != result:
                translated = _lookup(result)
            if translated != null:
                result = translated
            elif pending.size() < 4096:
                pending[localized] = true
        if node is Label and entry.property == "text":
            if result != entry.source:
                _label_present(node, entry, result)
            else:
                _label_release(node, entry)
            entry.last = current
            continue
        if node is Button and entry.property == "text":
            _button_prepare(node, entry, result != entry.source)
        if result != current:
            node.set(entry.property, result)
            _redraw_viewport(node.get_viewport())
        _font(node, entry, result if result != entry.source else "")
        if node is Button and entry.property == "text" and result != entry.source:
            _button_fit(node, entry, result)
        entry.last = result
    if not stale.is_empty():
        for id in stale:
            known.erase(id)
        # One compaction after scene deletion, not O(n^2) erase per dead node.
        entries = entries.filter(func(entry):return not stale.has(entry.id))
    entry_refresh_max_ms = max(entry_refresh_max_ms, (Time.get_ticks_usec() - start) / 1000.0)
    _slots_refresh()
    refreshing = false

# A one-shot viewport may have already consumed its draw before text and font
# layout settle. Keep only changed, normally disabled viewports updating for
# three completed frames, then relinquish the update mode we temporarily own.
func _redraw_viewport(viewport):
    if viewport == null or viewport == get_tree().root or not viewport is SubViewport:
        return
    var id = viewport.get_instance_id()
    if dirty_viewports.has(id):
        dirty_viewports[id].frames = 3
    elif viewport.render_target_update_mode in [SubViewport.UPDATE_DISABLED, SubViewport.UPDATE_ONCE]:
        dirty_viewports[id] = {"ref":weakref(viewport),"frames":3,"original_mode":viewport.render_target_update_mode}
        viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS

func _finish_redraw():
    for id in dirty_viewports.keys():
        var entry = dirty_viewports[id]
        var viewport = entry.ref.get_ref()
        entry.frames -= 1
        if viewport == null or entry.frames <= 0:
            if viewport != null and viewport.render_target_update_mode == SubViewport.UPDATE_ALWAYS:
                viewport.render_target_update_mode = SubViewport.UPDATE_DISABLED
            dirty_viewports.erase(id)

func _schedule_draw():
    if enabled:
        call_deferred("_before_draw")

func _before_draw():
    if enabled:
        _refresh()

# Explicit modification authorization; translation alone never reads the clock.
var speed_authorized = false
var speed_owned = false
var speed_baseline = null
var speed_applied = 1.0
var speed_rate = 1.0
var speed_scale = null
var speed_state = "idle"
var speed_blocked = false
var speed_reason = ""
var bridge_poll_at = 0

func _speed_valid(value):
    return (typeof(value) == TYPE_INT or typeof(value) == TYPE_FLOAT) and not is_nan(float(value)) and not is_inf(float(value)) and float(value) >= 0.0

func _speed_same(a, b):
    if not _speed_valid(a) or not _speed_valid(b):
        return false
    if a == 0.0 or b == 0.0:
        return a == b
    return abs(a - b) <= 0.0000001 * max(abs(a), abs(b))

func _speed_unavailable(reason):
    speed_blocked = true
    speed_state = "unavailable"
    speed_reason = reason

func _speed_restore():
    # Cancel first; restoring never reapplies a request waiting for game resume.
    speed_rate = 1.0
    speed_blocked = false
    speed_state = "idle"
    speed_reason = ""
    if speed_owned:
        speed_scale = Engine.time_scale
        if not _speed_valid(speed_scale):
            _speed_unavailable("游戏返回了无效的时间倍率。")
            return
        if _speed_same(speed_scale, speed_applied):
            Engine.time_scale = speed_baseline
            speed_scale = Engine.time_scale
            if not _speed_same(speed_scale, speed_baseline):
                _speed_unavailable("游戏未确认恢复原来的时间倍率。")
                return
    else:
        speed_scale = null
    speed_owned = false
    speed_baseline = null

func _speed_observe(explicit = false):
    if not speed_authorized or speed_rate == 1.0 or (speed_blocked and not explicit):
        return
    speed_scale = Engine.time_scale
    if not _speed_valid(speed_scale):
        _speed_unavailable("游戏返回了无效的时间倍率。")
        return
    if speed_scale == 0.0:
        speed_owned = false
        speed_state = "paused"
        speed_reason = ""
        return
    if speed_owned and not _speed_same(speed_scale, speed_applied):
        speed_owned = false
    if not speed_owned:
        if explicit or speed_baseline == null:
            speed_baseline = speed_scale
        elif not _speed_same(speed_scale, speed_baseline):
            speed_state = "game-controlled"
            speed_reason = ""
            return
    var wanted = speed_baseline * speed_rate
    if not _speed_valid(wanted) or wanted <= 0.0:
        _speed_unavailable("请求的引擎时间倍率超出范围。")
        return
    if speed_owned and _speed_same(speed_scale, wanted):
        speed_state = "active"
        speed_reason = ""
        return
    var previous = speed_scale
    var previous_owned = speed_owned
    Engine.time_scale = wanted
    speed_scale = Engine.time_scale
    if not _speed_same(speed_scale, wanted):
        if speed_scale == 0.0:
            speed_owned = false
            speed_state = "paused"
            speed_reason = ""
            return
        # Revert this synchronous attempt, then wait for an explicit user retry.
        if _speed_valid(speed_scale) and speed_scale > 0.0 and not _speed_same(speed_scale, previous):
            Engine.time_scale = previous
            speed_scale = Engine.time_scale
        speed_owned = previous_owned and _speed_same(speed_scale, previous)
        _speed_unavailable("引擎未确认此倍率，请检查游戏状态后重新应用。")
        return
    speed_applied = speed_scale
    speed_owned = true
    speed_state = "active"
    speed_reason = ""

func _speed_status():
    var result = {"enabled": speed_authorized, "speed": speed_rate, "speedState": speed_state, "speedApplied": speed_state == "active" and speed_owned, "backend": "engine-time-scale", "reason": speed_reason}
    if _speed_valid(speed_scale):
        result["engineScale"] = speed_scale
    if speed_baseline != null:
        result["baselineScale"] = speed_baseline
    return result

func _speed_command(q):
    var op = q.get("op", "")
    if op == "speedEnable":
        speed_authorized = true
        return _speed_status()
    if op == "speedDisable":
        _speed_restore()
        speed_authorized = false
        return _speed_status()
    if not speed_authorized:
        return {"error": "请先开启修改。"}
    if q.has("rate"):
        var value = q.rate
        if (typeof(value) != TYPE_INT and typeof(value) != TYPE_FLOAT) or is_nan(float(value)) or is_inf(float(value)) or float(value) < 0.25 or float(value) > 100.0:
            return {"error": "倍率必须在 0.25 到 100 之间。"}
        var rate = float(value)
        if rate == 1.0:
            _speed_restore()
        else:
            speed_rate = rate
            speed_blocked = false
            _speed_observe(true)
    else:
        _speed_observe()
    return _speed_status()

func _command(q):
    if q.get("op", "") in ["speedEnable", "speedDisable", "gameSpeed"]:
        return _speed_command(q)
    match q.get("op", ""):
        "fontPreferences":
            return _font_preferences(q)
        "translationPrepare":
            _ensure_collection()
            var preserve = startup_loaded and q.get("preserveStartup", false) == true
            startup_loaded = false
            epoch += 1
            preparing = true
            staged_cache.clear()
            staged_exact.clear()
            staged_exact_bytes = 0
            staged_exact_ready = false
            activation_exact.clear()
            activation_observed.clear()
            cache.clear()
            if not preserve:
                enabled = false
                startup_exact.clear()
            lookup_dirty = true
            pending.clear()
            _prepare_catalog_language(str(q.get("sourceLanguage", "Auto")))
            return {"epoch": epoch, "exactStartupPrime": true}
        "translationEnable":
            if q.has("epoch") and int(q.epoch) != epoch:
                return {"epoch": epoch, "stale": true}
            _ensure_collection()
            if preparing:
                cache = staged_cache
                staged_cache = {}
                if staged_exact_ready:
                    activation_exact = staged_exact
                    activation_observed.clear()
                    startup_exact.clear()
                    staged_exact = {}
                    staged_exact_bytes = 0
                staged_exact_ready = false
                preparing = false
            lookup_dirty = true
            enabled = true
            _refresh(true)
            return {"epoch": epoch}
        "translationDisable":
            enabled = false
            startup_loaded = false
            startup_exact.clear()
            lookup_dirty = true
            preparing = false
            staged_cache.clear()
            staged_exact.clear()
            staged_exact_bytes = 0
            staged_exact_ready = false
            activation_exact.clear()
            activation_observed.clear()
            epoch += 1
            pending.clear()
            _refresh(true)
            return {"epoch": epoch}
        "translationCatalog":
            _ensure_collection()
            return _catalog_page(q.get("cursor", 0))
        "translationPoll":
            _refresh()
            var texts = []
            for text in pending.keys():
                if texts.size() >= 128:
                    break
                texts.append(text)
                pending.erase(text)
            return {"epoch": epoch, "texts": texts, "ahead": [], "activeTextLanguage": selected_language if selected_language == _locale_language(current_locale) else "", "entryRefreshMaxMs":entry_refresh_max_ms, "slotBindings":slot_rows, "slotRefreshMaxMs":slot_max_ms, "runtimeWarning":"本次运行部分列表文字达到读取容量上限" if slot_limit_hits > 0 else ""}
        "translationApply":
            var exact_prime = q.get("exactOnly", false) == true
            if exact_prime and (not preparing or q.get("prime", false) != true):
                return {"epoch": epoch, "stale": true, "applied": 0}
            if int(q.get("epoch", -1)) != epoch or (not enabled and not q.get("prime", false)):
                return {"stale": true, "applied": 0}
            var count = 0
            if exact_prime:
                if q.get("replaceExact", false) == true:
                    staged_exact.clear()
                    staged_exact_bytes = 0
                staged_exact_ready = true
            for pair in q.get("entries", []):
                if count >= 128:
                    break
                # Exact preparation stores structurally safe rows; display eligibility
                # still belongs to _valid at the actual control. An irrelevant
                # numeric/resource key must not reject an otherwise valid page.
                if typeof(pair) == TYPE_DICTIONARY and typeof(pair.get("source")) == TYPE_STRING and (exact_prime and pair.source.strip_edges() != "" and pair.source.length() <= 6000 or _valid(pair.source)) and typeof(pair.get("text")) == TYPE_STRING and pair.text.length() <= 12000:
                    if exact_prime:
                        if pair.text.strip_edges() == "" or _utf8(pair.source).has(0) or _utf8(pair.text).has(0):
                            continue
                        var size = _utf8(pair.source).size() + _utf8(pair.text).size()
                        if staged_exact.has(pair.source):
                            size -= _utf8(pair.source).size() + _utf8(staged_exact[pair.source]).size()
                        if (not staged_exact.has(pair.source) and staged_exact.size() >= 100000) or staged_exact_bytes + size > 33554432:
                            continue
                        staged_exact[pair.source] = pair.text
                        staged_exact_bytes += size
                    elif preparing and q.get("prime", false):
                        staged_cache[pair.source] = pair.text
                    else:
                        cache[pair.source] = pair.text
                    count += 1
            if count > 0:
                lookup_dirty = true
            _refresh(true)
            return {"epoch": epoch, "applied": count}
        "translationState":
            var snapshot = _catalog_page(0)
            return {"enabled": enabled, "epoch": epoch, "entries": cache.size(), "startupExactCache": startup_exact.size(), "activationExactCache": activation_exact.size(), "savedir": OS.get_user_data_dir(), "catalog": snapshot.total, "scanning": collection_started and snapshot.scanning, "skipped": snapshot.skipped, "warnings": snapshot.warnings, "locale": TranslationServer.get_locale(), "scan_max_ms": scan_max_ms, "scan_overruns": scan_overruns, "scan_thread": collection_started, "scene_resource_loads": 0, "sourceLanguage": snapshot.sourceLanguage, "selectedLanguage": snapshot.selectedLanguage, "skippedByLanguage": snapshot.skippedByLanguage}
    return null

func _exit_tree():
    _speed_restore()
    speed_authorized = false
    catalog_mutex.lock()
    scan_stop = true
    catalog_mutex.unlock()
    if scan_thread.is_started():
        scan_thread.wait_to_finish()
    enabled = false
    _refresh(true)
    for entry in dirty_viewports.values():
        var viewport = entry.ref.get_ref()
        if viewport != null and viewport.render_target_update_mode == SubViewport.UPDATE_ALWAYS:
            viewport.render_target_update_mode = entry.original_mode
    dirty_viewports.clear()
    entries.clear()
    known.clear()
    slot_groups.clear()
    slot_known.clear()
    slot_rows = 0
    cache.clear()
    startup_exact.clear()
    startup_loaded = false
    staged_cache.clear()
    staged_exact.clear()
    staged_exact_bytes = 0
    staged_exact_ready = false
    activation_exact.clear()
    activation_observed.clear()
    pending.clear()
    optimized_work.clear()
    json_work.clear()
    if directory_reader != null:
        directory_reader.list_dir_end()
        directory_reader = null

func _process(delta):
    var latest_locale = TranslationServer.get_locale()
    if latest_locale != current_locale:
        catalog_mutex.lock()
        current_locale = latest_locale
        language_revision += 1
        selected_catalog = []
        selected_metadata = []
        catalog_mutex.unlock()
        epoch += 1
        activation_observed.clear()
    if enabled:
        _refresh()
        call_deferred("_before_draw")
    if session == "":
        return
    # Communication and revocation use real time, including when the game pauses.
    if _ticks() < bridge_poll_at:
        return
    bridge_poll_at = _ticks() + 25
    _flush_writes()
    if _read(session + "/stop", 1024) == secret or ((enabled or speed_authorized) and _ticks() - last_request > 60000):
        _speed_restore()
        speed_authorized = false
        enabled = false
        epoch += 1
        _refresh(true)
        if _exists(session + "/stop"):
            set_process(false)
            return
    var q = _json(_read(session + "/request.json", 262144))
    if typeof(q) != TYPE_DICTIONARY or q.get("secret", "") != secret or int(q.get("id", 0)) <= last_id:
        _speed_observe()
        return
    last_id = int(q.id)
    last_request = _ticks()
    var result = _command(q)
    if not q.get("op", "") in ["speedEnable", "speedDisable", "gameSpeed"]:
        _speed_observe()
    _write("response.json", {"id": last_id, "ok": result != null, "result": result})

# Parse serialized data only. No ResourceLoader, script compilation, instancing,
# external textures, or third-party object setters are involved in this reader.
var binary_stream = null
var binary_bad = false
var binary_strings = []
var binary_real_size = 4
var binary_format = 0
var binary_items = 0

func _binary_skip(size):
    if size < 0 or binary_stream.get_position() + size > binary_stream.get_size():
        binary_bad = true
        return
    binary_stream.seek(binary_stream.get_position() + size)

func _binary_u32():
    if binary_bad or binary_stream.get_available_bytes() < 4:
        binary_bad = true
        return 0
    return binary_stream.get_u32()

func _binary_u64():
    var low = _binary_u32()
    var high = _binary_u32()
    if binary_stream.big_endian:
        return (low << 32) | high
    return low | (high << 32)

func _binary_count():
    var count = _binary_u32() & 0x7fffffff
    if count > 1000000:
        binary_bad = true
        return 0
    return count

func _binary_string(length = -1):
    if length == -1:
        length = _binary_u32()
    if binary_bad or length < 0 or length > 1048576 or length > binary_stream.get_available_bytes():
        binary_bad = true
        return ""
    if length == 0:
        return ""
    var bytes = binary_stream.get_data(length)[1]
    if bytes[bytes.size() - 1] == 0:
        bytes.resize(bytes.size() - 1)
    return bytes.get_string_from_utf8()

func _binary_name():
    var index = _binary_u32()
    if index & 0x80000000:
        return _binary_string(index & 0x7fffffff)
    if index >= binary_strings.size():
        binary_bad = true
        return ""
    return binary_strings[index]

func _binary_variant(depth = 0):
    binary_items += 1
    if binary_bad or depth > 48 or binary_items > 2000000 or (binary_items % 128 == 0 and _scan_cancelled()):
        binary_bad = true
        return null
    var type = _binary_u32()
    match type:
        1, 42, 43:
            return null
        2, 3, 23:
            return _binary_u32()
        40:
            return _binary_u64()
        5, 44:
            return _binary_string()
        4:
            _binary_skip(binary_real_size)
        41:
            _binary_skip(8)
        10, 11, 12, 13, 14, 15, 16, 17, 18, 50, 52:
            var counts = {10:2, 11:4, 12:3, 13:4, 14:4, 15:6, 16:9, 17:12, 18:6, 50:4, 52:16}
            _binary_skip(counts[type] * binary_real_size)
        20:
            _binary_skip(16)
        45, 46, 47, 51:
            var counts = {45:2, 46:4, 47:3, 51:4}
            _binary_skip(counts[type] * 4)
        22:
            if binary_stream.get_available_bytes() < 4:
                binary_bad = true
                return null
            var names = binary_stream.get_u16()
            var subnames = binary_stream.get_u16() & 0x7fff
            if binary_format < 3:
                subnames += 1
            for ignored in range(names + subnames):
                _binary_name()
        24:
            var object_type = _binary_u32()
            if object_type in [2, 3]:
                _binary_skip(4)
            elif object_type == 1:
                _binary_string()
                _binary_string()
            elif object_type != 0:
                binary_bad = true
        26:
            var result = {}
            var count = _binary_count()
            for ignored in range(count):
                var key = _binary_variant(depth + 1)
                var value = _binary_variant(depth + 1)
                if binary_bad:
                    return null
                if typeof(key) in [TYPE_STRING, TYPE_INT]:
                    result[key] = value
                elif typeof(key) == TYPE_ARRAY and key.size() == 2 and typeof(key[0]) == TYPE_STRING and typeof(key[1]) == TYPE_STRING:
                    # Godot 4.6 Translation uses [context, msgid] keys.
                    result[key] = value
            return result
        30:
            var result = []
            var count = _binary_count()
            for ignored in range(count):
                result.append(_binary_variant(depth + 1))
                if binary_bad:
                    return null
            return result
        31:
            var count = _binary_count()
            if count > binary_stream.get_available_bytes():
                binary_bad = true
                return null
            var result = binary_stream.get_data(count)[1]
            _binary_skip((4 - count % 4) % 4)
            return result
        32:
            var count = _binary_count()
            var result = []
            for ignored in range(count):
                result.append(_binary_u32())
                if binary_bad:
                    return null
            return result
        34:
            var count = _binary_count()
            var result = []
            for ignored in range(count):
                result.append(_binary_string())
                if binary_bad:
                    return null
            return result
        33, 35, 36, 37, 48, 49, 53:
            # Godot binary format 6 adds PackedVector4Array (four real_t values).
            # It has no text; consume its bounded payload before the next property.
            var sizes = {33:4, 35:binary_real_size*3, 36:16, 37:binary_real_size*2, 48:8, 49:8, 53:binary_real_size*4}
            _binary_skip(_binary_count() * sizes[type])
        _:
            binary_bad = true
    return null

func _binary_extract(path):
    var data = _read_bytes(path, 33554432)
    if data.size() < 24:
        _warning("二进制资源为空、不可读或超过 32 MB：" + path)
        return
    var stream = StreamPeerBuffer.new()
    stream.data_array = data
    var magic = stream.get_u32()
    if magic == 0x43435352: # RSCC block-compressed resource
        var mode = stream.get_u32()
        var block_size = stream.get_u32()
        var total = stream.get_u32()
        if mode > 4 or block_size < 1 or block_size > 1048576 or total > 33554432:
            _warning("压缩场景超出读取范围：" + path)
            return
        var count = int(total / block_size) + 1
        if count > 100000 or stream.get_available_bytes() < count * 4:
            _warning("压缩场景目录损坏：" + path)
            return
        var lengths = []
        for ignored in range(count):
            lengths.append(stream.get_u32())
        data = _bytes([])
        for index in range(count):
            var length = lengths[index]
            if length > stream.get_available_bytes():
                _warning("压缩场景块损坏：" + path)
                return
            var packed = stream.get_data(length)[1]
            var expected = total % block_size if index == count - 1 else block_size
            if expected == 0:
                continue
            var block = packed.decompress(expected, mode)
            if block.size() != expected:
                _warning("压缩场景无法解压：" + path)
                return
            data.append_array(block)
        stream.data_array = data
        stream.seek(0)
    elif magic != 0x43525352: # RSRC
        _warning("未识别的二进制资源格式：" + path)
        return
    binary_stream = stream
    binary_bad = false
    binary_items = 0
    binary_strings = []
    var big_endian = _binary_u32()
    var real64 = _binary_u32()
    stream.big_endian = big_endian != 0
    var major = _binary_u32()
    _binary_u32() # minor
    binary_format = _binary_u32()
    if not major in [3, 4] or binary_format < 1 or binary_format > (6 if major == 4 else 5):
        _warning("二进制资源版本暂未支持：" + path)
        return
    var resource_type = _binary_string()
    _binary_skip(8) # import metadata
    var flags = 0
    binary_real_size = 8 if real64 else 4
    if major >= 4:
        flags = _binary_u32()
        binary_real_size = 8 if flags & 4 else 4
        _binary_skip(8)
        if flags & 8:
            _binary_string()
        _binary_skip(44)
    else:
        _binary_skip(56)
    var count = _binary_count()
    for ignored in range(count):
        binary_strings.append(_binary_string())
        if binary_bad:
            break
    count = _binary_count()
    for ignored in range(count):
        _binary_string()
        _binary_string()
        if flags & 2:
            _binary_skip(8)
        if binary_bad:
            break
    var offsets = []
    count = _binary_count()
    for ignored in range(count):
        _binary_string()
        offsets.append(_binary_u64())
        if binary_bad:
            break
    # Read internal serialized resources as data, without resolving scripts or objects.
    if binary_bad or offsets.size() == 0:
        _warning("二进制资源索引无法读取：" + path)
        return
    for offset in offsets:
        if offset < 0 or offset >= stream.get_size():
            binary_bad = true
            break
        stream.seek(offset)
        var internal_type = _binary_string()
        var properties = {}
        count = _binary_count()
        for ignored in range(count):
            var key = _binary_name()
            var value = _binary_variant()
            properties[key] = value
            if binary_bad:
                break
        if binary_bad:
            break
        if internal_type == "PackedScene":
            _binary_scene(properties.get("_bundled", {}), path)
        elif properties.has("bucket_table") and properties.has("strings"):
            optimized_work = {"buckets":properties.bucket_table,"strings":properties.strings,"index":0,"row":0,"path":path,"locale":properties.get("locale", "")}
        elif internal_type in ["Translation", "OptimizedTranslation", "PHashTranslation"] and typeof(properties.get("messages")) == TYPE_DICTIONARY:
            for key in properties.messages:
                var value = properties.messages[key]
                if typeof(key) == TYPE_ARRAY and typeof(value) == TYPE_ARRAY:
                    for plural in value:
                        _catalog_add(plural, path, str(key), properties.get("locale", ""), "translation_plural")
                else:
                    _catalog_add(value, path, str(key), properties.get("locale", ""), "translation")
        elif internal_type == "Translation" and typeof(properties.get("messages")) == TYPE_ARRAY:
            # Godot's ordinary Translation serializes alternating key/value
            # strings; CSV-imported OptimizedTranslation uses buckets instead.
            var messages = properties.messages
            if messages.size() % 2 == 0:
                for at in range(0, messages.size(), 2):
                    if typeof(messages[at]) == TYPE_STRING and typeof(messages[at + 1]) == TYPE_STRING:
                        _catalog_add(messages[at + 1], path, messages[at], properties.get("locale", ""), "translation")
        else:
            json_work.append({"value":properties,"path":path,"key":str(offset),"depth":0,"accepted":false,"index":0})
    if binary_bad:
        _warning("二进制资源存在未支持或损坏的字段：" + path)
    binary_stream = null
    binary_strings = []

func _binary_scene(bundle, path):
    if typeof(bundle) != TYPE_DICTIONARY or not bundle.has("names") or not bundle.has("variants") or not bundle.has("nodes"):
        _warning("二进制场景没有可读取的节点表：" + path)
        return
    var names = bundle.names
    var values = bundle.variants
    var nodes = bundle.nodes
    var cursor = 0
    for index in range(min(int(bundle.get("node_count", 0)), 100000)):
        if cursor + 6 > nodes.size():
            _warning("二进制场景节点表不完整：" + path)
            return
        var node_name = int(nodes[cursor + 3]) & 0x3ffff
        var count = int(nodes[cursor + 5])
        cursor += 6
        if count < 0 or cursor + count * 2 + 1 > nodes.size():
            _warning("二进制场景属性表不完整：" + path)
            return
        for ignored in range(count):
            var property = int(nodes[cursor]) & 0x3fffffff
            var value = int(nodes[cursor + 1]) & 0x3fffffff
            cursor += 2
            if property >= names.size() or value >= values.size():
                continue
            var name = names[property]
            if name in ["text", "bbcode_text", "tooltip_text", "hint_tooltip", "placeholder_text"] or (name.begins_with("popup/item_") and name.ends_with("/text")):
                _catalog_add(values[value], path, str(index) + ":" + (names[node_name] if node_name < names.size() else "") + ":" + name, "", "scene_binary")
        var groups = int(nodes[cursor])
        cursor += 1 + groups
        if groups < 0 or cursor > nodes.size():
            _warning("二进制场景分组表不完整：" + path)
            return


# Copyright (c) 2006-2009, Salvatore Sanfilippo
# All rights reserved.
#
# Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
#
#     * Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
#     * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
#     * Neither the name of Smaz nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
#
# THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
const SMAZ = [
" ", "the", "e", "t", "a", "of", "o", "and", "i", "n", "s", "e ", "r", " th",
" t", "in", "he", "th", "h", "he ", "to", "\r\n", "l", "s ", "d", " a", "an",
"er", "c", " o", "d ", "on", " of", "re", "of ", "t ", ", ", "is", "u", "at",
"   ", "n ", "or", "which", "f", "m", "as", "it", "that", "\n", "was", "en",
"  ", " w", "es", " an", " i", "\r", "f ", "g", "p", "nd", " s", "nd ", "ed ",
"w", "ed", "http://", "for", "te", "ing", "y ", "The", " c", "ti", "r ", "his",
"st", " in", "ar", "nt", ",", " to", "y", "ng", " h", "with", "le", "al", "to ",
"b", "ou", "be", "were", " b", "se", "o ", "ent", "ha", "ng ", "their", "\"",
"hi", "from", " f", "in ", "de", "ion", "me", "v", ".", "ve", "all", "re ",
"ri", "ro", "is ", "co", "f t", "are", "ea", ". ", "her", " m", "er ", " p",
"es ", "by", "they", "di", "ra", "ic", "not", "s, ", "d t", "at ", "ce", "la",
"h ", "ne", "as ", "tio", "on ", "n t", "io", "we", " a ", "om", ", a", "s o",
"ur", "li", "ll", "ch", "had", "this", "e t", "g ", "e\r\n", " wh", "ere",
" co", "e o", "a ", "us", " d", "ss", "\n\r\n", "\r\n\r", "=\"", " be", " e",
"s a", "ma", "one", "t t", "or ", "but", "el", "so", "l ", "e s", "s,", "no",
"ter", " wa", "iv", "ho", "e a", " r", "hat", "s t", "ns", "ch ", "wh", "tr",
"ut", "/", "have", "ly ", "ta", " ha", " on", "tha", "-", " l", "ati", "en ",
"pe", " re", "there", "ass", "si", " fo", "wa", "ec", "our", "who", "its", "z",
"fo", "rs", ">", "ot", "un", "<", "im", "th ", "nc", "ate", "><", "ver", "ad",
" we", "ly", "ee", " n", "id", " cl", "ac", "il", "</", "rt", " wi", "div",
"e, ", " it", "whi", " ma", "ge", "x", "e c", "men", ".com"
]


extends Node

# Godot 3.5+ bridge. Godot 4 uses the separately compiled dialect.
# Text is looked up immediately before drawing, without changing the game's locale.
# Catalog reads serialized scenes without instantiating their game nodes.
var session = ""
var secret = ""
var enabled = false
var epoch = 0
var cache = {}
var staged_cache = {}
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
var current_locale = ""
var refreshing = false
var scan_thread = Thread.new()
var catalog_mutex = Mutex.new()
var scan_stop = false
var pending_writes = {}
var dirty_viewports = {}


func _ready():
    session = OS.get_environment("FUSION_GODOT_SESSION")
    secret = OS.get_environment("FUSION_GODOT_SECRET")
    if session == "" or secret == "":
        set_process(false)
        return
    pause_mode = Node.PAUSE_MODE_PROCESS
    process_priority = 2147483647
    last_request = _ticks()
    current_locale = TranslationServer.get_locale()
    var startup = _json(_read(session + "/startup.json", 33554432))
    if typeof(startup) == TYPE_DICTIONARY and startup.get("secret", "") == secret and typeof(startup.get("entries")) == TYPE_DICTIONARY:
        for source in startup.entries:
            var text = startup.entries[source]
            if _valid(source) and typeof(text) == TYPE_STRING and text.length() <= 12000:
                cache[source] = text
    enabled = OS.get_environment("FUSION_GODOT_TRANSLATION") == "1"
    _prepare_font()
    get_tree().connect("node_added", self, "_added")
    VisualServer.connect("frame_pre_draw", self, "_before_draw")
    VisualServer.connect("frame_post_draw", self, "_finish_redraw")
    _collect(get_tree().root)
    _refresh(true)
    # Inspect configured resources to identify their language before publishing one source-language catalogue.
    for setting in ProjectSettings.get_property_list():
        var key = str(setting.name)
        if key.begins_with("locale/translations") or key.begins_with("internationalization/locale/translations"):
            var value = ProjectSettings.get_setting(key)
            if typeof(value) != TYPE_STRING and value != null:
                for path in value:
                    _queue_resource(str(path))
    scan_thread.start(self, "_scan_worker")
    _write("hello.json", {"pid": OS.get_process_id(), "secret": secret, "protocol": 73, "version": Engine.get_version_info().string})

func _ticks():
    return OS.get_ticks_msec()

func _read(path, limit = 8388608):
    var f = File.new()
    if f.open(path, File.READ) != OK:
        return ""
    if f.get_len() > limit:
        f.close()
        return ""
    var value = f.get_as_text()
    f.close()
    return value

func _exists(path):
    return File.new().file_exists(path)

func _json(text):
    if text.strip_edges() == "":
        return null
    var parsed = JSON.parse(text)
    return parsed.result if parsed.error == OK else null

func _write(name, value):
    pending_writes[name] = {"text": JSON.print(value), "prepared": false}
    _flush_write(name)

func _flush_write(name):
    if not pending_writes.has(name):
        return
    var pending = pending_writes[name]
    var f = File.new()
    var path = session + "/" + name
    if not pending.prepared:
        if f.open(path + ".tmp", File.WRITE) != OK:
            return
        f.store_string(pending.text)
        var write_error = f.get_error()
        f.close()
        if write_error != OK:
            return
        pending.prepared = true
    var directory = Directory.new()
    if directory.file_exists(path):
        if directory.remove(path) != OK:
            return
    if directory.rename(path + ".tmp", path) == OK:
        pending_writes.erase(name)

func _flush_writes():
    # One non-blocking attempt per pending file per frame. A failed publication
    # retains its already-computed response; the command/epoch is not repeated.
    for name in pending_writes.keys():
        _flush_write(name)

func _directory_start(path):
    directory_reader = Directory.new()
    directory_path = path
    if directory_reader.open(path) != OK:
        directory_reader = null
        return
    directory_reader.list_dir_begin(true, false)

func _read_bytes(path, limit):
    var f = File.new()
    if f.open(path, File.READ) != OK:
        return _bytes([])
    var value = _bytes([])
    if f.get_len() <= limit:
        value = f.get_buffer(f.get_len())
    f.close()
    return value
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
        metadata.append(selected_metadata[i])
    var result = {"texts": texts, "metadata": metadata, "next": stop, "total": size, "skipped": catalog_skipped, "done": ready, "scanning": not ready, "warnings": catalog_warnings.duplicate(), "sourceLanguage": source_language, "selectedLanguage": selected_language if ready else "", "skippedByLanguage": language_excluded if ready else 0}
    catalog_mutex.unlock()
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
    var normalized = str(path).to_lower().replace("\\", "/").replace("-", "/").replace(".", "/")
    for token in normalized.split("/"):
        var language = _locale_language(token)
        if language != "":
            return language
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
        var code = value.ord_at(at)
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
    if mode == "Mixed":
        return "*"
    if mode != "Auto":
        return _locale_language(mode)
    var weights = {}
    var english_prose = 0
    var english_prose_letters = 0
    var english_labels = {}
    for row in catalog_metadata:
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
    for row in catalog_metadata:
        var matches = selected == "*" or row.language == selected or (row.language == "han" and selected in ["zh", "ja"])
        if not matches:
            excluded += 1
        elif not seen.has(row.source):
            seen[row.source] = true
            texts.append(row.source)
            metadata.append(row)
    catalog_mutex.lock()
    if revision == language_revision:
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
    return PoolByteArray(value)

func _utf8(value):
    return value.to_utf8()

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
    catalog_metadata.append({"source": value, "path": path, "key": key, "locale": locale, "kind": kind, "language": language})
    catalog_mutex.unlock()

func _queue_resource(path):
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
        count += 1
        if optimized_work.size() > 0:
            _optimized_step()
        elif json_work.size() > 0:
            _json_step()
        elif resources.size() > 0:
            var path = resources.pop_front()
            if path.get_extension().to_lower() in ["json", "gd", "tscn", "tres", "po"]:
                _extract_text(path)
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
                        _warning("编译脚本未反编译；其未登记文字将在运行时补译。")
                    elif extension in ["remap", "import"]:
                        var remap = ConfigFile.new()
                        if remap.load(path) == OK:
                            var mapped = str(remap.get_value("remap", "path", ""))
                            if mapped.begins_with("res://") and mapped.get_extension().to_lower() in ["scn", "tres", "res", "translation"]:
                                _queue_resource(mapped)
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
    messages.compile('(?s)(?:^|\\n)messages\\s*=\\s*(\\{(?:[^"{}]|"(?:[^"\\\\]|\\\\.)*")*\\})')
    var found = messages.search(source)
    if found != null:
        var values = _json(found.get_string(1))
        if typeof(values) == TYPE_DICTIONARY:
            for key in values:
                _catalog_add(values[key], path, str(key), locale, "translation_text")
    return true

func _file_size(path):
    var f = File.new()
    if f.open(path, File.READ) != OK:
        return -1
    var length = f.get_len()
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
                var language_keys = {}
                var strong_language = false
                var clear_map = true
                for candidate in task.keys:
                    var candidate_language = _locale_language(str(candidate))
                    if candidate_language != "" and typeof(data[candidate]) in [TYPE_STRING, TYPE_ARRAY, TYPE_DICTIONARY]:
                        language_keys[candidate_language] = true
                        if not candidate_language in ["id", "no"]:
                            strong_language = true
                    elif not str(candidate) in ["key", "context", "comment"]:
                        clear_map = false
                # Business objects with id/no/name are not language dictionaries.
                task.language_map = clear_map and strong_language and language_keys.size() >= 2
            child = task.keys[task.index]
            var child_locale = _locale_language(str(child))
            if child_locale != "" and task.language_map:
                locale = child_locale
                language_values = true
                accepted = true
            else:
                accepted = language_values or _text_field(str(child))
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
    if node == self or known.has(node.get_instance_id()) or entries.size() >= 20000:
        return
    var properties = []
    if node is Label or node is Button:
        properties.append("text")
    elif node is RichTextLabel:
        properties.append("bbcode_text" if node.bbcode_enabled and node.bbcode_text != "" else "text")
    elif node is LineEdit:
        properties.append("placeholder_text")
    if node is Control:
        properties.append("hint_tooltip")
    if properties.size() == 0:
        return
    known[node.get_instance_id()] = true
    for property in properties:
        var value = str(node.get(property))
        var entry = {"id": node.get_instance_id(), "ref": weakref(node), "property": property, "source": value, "last": value, "fonts": {}}
        entries.append(entry)

func _font_style_changed(entry):
    entry.font_style_dirty = true
    entry.rich_style_dirty = true

func _font_signature(font):
    if font is DynamicFont:
        return [font.font_data, font.size, font.outline_size, font.outline_color, font.use_filter, font.use_mipmaps, font.get_spacing(0), font.get_spacing(1), font.get_spacing(2), font.get_spacing(3), font.get_fallback_count()]
    return [font.get_height(), font.get_ascent()]

func _prepare_font():
    var path = OS.get_environment("WINDIR").replace("\\", "/") + "/Fonts/msyh.ttc"
    if _exists(path):
        chinese_font = DynamicFontData.new()
        chinese_font.font_path = path

func _font(node, entry, translated):
    if not node is Control or entry.property in ["tooltip_text", "hint_tooltip"]:
        return
    # Unchanged strings do not need another Unicode scan on every draw pass.
    if entry.get("font_text", null) != translated:
        entry.font_text = translated
        entry.font_cjk = false
        for i in range(translated.length()):
            var character = translated.ord_at(i)
            if character >= 0x3000 and character <= 0x9fff or character >= 0xff00 and character <= 0xffef:
                entry.font_cjk = true
                break
    var names = ["normal_font", "bold_font", "italics_font", "bold_italics_font"] if node is RichTextLabel else ["font"]
    if chinese_font != null and enabled and entry.get("font_cjk", false):
        for name in names:
            if entry.fonts.has(name):
                var owned = entry.fonts[name]
                if node.get_font(name) == owned.owned:
                    if not entry.get("font_style_dirty", false) and owned.signature == _font_signature(owned.source):
                        continue
                    # Restore our override briefly to resolve a changed inherited
                    # theme. A game's replacement is adopted, never overwritten.
                    if owned.original == null:
                        node.remove_font_override(name)
                    else:
                        node.add_font_override(name, owned.original)
                entry.fonts.erase(name)
            var old = node.get_font(name)
            var original = old if node.has_font_override(name) else null
            var replacement
            if old is DynamicFont:
                replacement = old.duplicate()
                replacement.add_fallback(chinese_font)
            else:
                # Godot 3 BitmapFont cannot accept a DynamicFont fallback.
                # Keep its line height instead of treating pixel height as size.
                replacement = DynamicFont.new()
                replacement.font_data = chinese_font
                replacement.size = max(1, int(old.get_height()))
                var ratio = old.get_height() / max(1.0, replacement.get_height())
                replacement.size = max(1, int(floor(replacement.size * ratio)))
            node.add_font_override(name, replacement)
            entry.fonts[name] = {"original": original, "owned": replacement, "source": old, "signature": _font_signature(old)}
    else:
        for name in entry.fonts:
            var font = entry.fonts[name]
            if node.get_font(name) == font.owned:
                if font.original == null:
                    node.remove_font_override(name)
                else:
                    node.add_font_override(name, font.original)
        entry.fonts.clear()
    # Installing our own override also emits theme_changed.
    entry.font_style_dirty = false

# Exact cache first, then reversible whole-line wrappers and explicit placeholders.
# Never fuzzy-match unrelated dialogue or infer a player name from game state.
var template_buckets = {}
var lookup_memo = {}
var lookup_dirty = true

func _regex_escape(value):
    var result = ""
    for i in range(value.length()):
        var c = value.substr(i, 1)
        result += ("\\" if c in ["\\", ".", "^", "$", "|", "?", "*", "+", "(", ")", "[", "]", "{", "}"] else "") + c
    return result

func _index_cache():
    template_buckets.clear()
    lookup_memo.clear()
    lookup_dirty = false
    var tokens = RegEx.new()
    tokens.compile("<<(?:var[ \\t]+[A-Za-z_][A-Za-z_0-9.: -]{0,120}|arg[ \\t]+[0-9]{1,3})>>|\\{[A-Za-z_][A-Za-z_0-9]*\\}|:[A-Za-z_][A-Za-z_0-9]*:")
    for source in cache:
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
            if cache[source].count(token) != source.count(token):
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
        template_buckets[prefix].append({"parts":parts,"tokens":placeholders,"text":cache[source]})

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
    if lookup_dirty:
        _index_cache()
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
        var joined = PoolStringArray()
        for line in lines:
            var found = _lookup(line, depth + 1) if line.strip_edges() != "" else line
            if found == null:
                ready = false
                break
            joined.append(found)
        if ready:
            translated = joined.join("\n")
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

# Insert captured game values once, without parsing the inserted data as tokens.
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
    var stale = []
    for entry in entries:
        var node = entry.ref.get_ref()
        if node == null:
            stale.append(entry)
            continue
        if not all and node is CanvasItem and not node.is_visible_in_tree():
            continue
        # Games can enable BBCode yet assign plain text, or start using BBCode
        # after node_added. Follow the populated source instead of an empty field.
        if node is RichTextLabel and entry.property in ["text", "bbcode_text"]:
            var property = "bbcode_text" if node.bbcode_enabled and node.bbcode_text != "" else "text"
            if entry.property != property:
                entry.property = property
                entry.source = str(node.get(property))
                entry.last = entry.source
        var current = str(node.get(entry.property))
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
        if result != current:
            node.set(entry.property, result)
            _redraw_viewport(node.get_viewport())
        _font(node, entry, result if result != entry.source else "")
        entry.last = result
    for entry in stale:
        known.erase(entry.id)
        entries.erase(entry)
    refreshing = false

# A one-shot viewport may have already consumed its draw before text and font
# layout settle. Keep only changed, normally disabled viewports updating for
# three completed frames, then relinquish the update mode we temporarily own.
func _redraw_viewport(viewport):
    if viewport == get_tree().root or not viewport is Viewport:
        return
    var id = viewport.get_instance_id()
    if dirty_viewports.has(id):
        dirty_viewports[id].frames = 3
    elif viewport.render_target_update_mode in [Viewport.UPDATE_DISABLED, Viewport.UPDATE_ONCE]:
        dirty_viewports[id] = {"ref":weakref(viewport),"frames":3,"original_mode":viewport.render_target_update_mode}
        viewport.render_target_update_mode = Viewport.UPDATE_ALWAYS

func _finish_redraw():
    for id in dirty_viewports.keys():
        var entry = dirty_viewports[id]
        var viewport = entry.ref.get_ref()
        entry.frames -= 1
        if viewport == null or entry.frames <= 0:
            if viewport != null and viewport.render_target_update_mode == Viewport.UPDATE_ALWAYS:
                viewport.render_target_update_mode = Viewport.UPDATE_DISABLED
            dirty_viewports.erase(id)

func _schedule_draw():
    if enabled:
        call_deferred("_before_draw")

func _before_draw():
    if enabled:
        _refresh()

func _command(q):
    match q.get("op", ""):
        "translationPrepare":
            epoch += 1
            preparing = true
            staged_cache.clear()
            pending.clear()
            _prepare_catalog_language(str(q.get("sourceLanguage", "Auto")))
            return {"epoch": epoch}
        "translationEnable":
            if preparing:
                cache = staged_cache
                staged_cache = {}
                preparing = false
            lookup_dirty = true
            enabled = true
            _refresh(true)
            return {"epoch": epoch}
        "translationDisable":
            enabled = false
            preparing = false
            staged_cache.clear()
            epoch += 1
            pending.clear()
            _refresh(true)
            return {"epoch": epoch}
        "translationCatalog":
            return _catalog_page(q.get("cursor", 0))
        "translationPoll":
            _refresh()
            var texts = []
            for text in pending.keys():
                if texts.size() >= 128:
                    break
                texts.append(text)
                pending.erase(text)
            return {"epoch": epoch, "texts": texts, "ahead": []}
        "translationApply":
            if int(q.get("epoch", -1)) != epoch or (not enabled and not q.get("prime", false)):
                return {"stale": true, "applied": 0}
            var count = 0
            for pair in q.get("entries", []):
                if count >= 128:
                    break
                if typeof(pair) == TYPE_DICTIONARY and _valid(pair.get("source", "")) and typeof(pair.get("text")) == TYPE_STRING and pair.text.length() <= 12000:
                    if preparing and q.get("prime", false):
                        staged_cache[pair.source] = pair.text
                    else:
                        cache[pair.source] = pair.text
                    count += 1
            if count > 0:
                lookup_dirty = true
            _refresh(true)
            return {"applied": count}
        "translationState":
            var snapshot = _catalog_page(0)
            return {"enabled": enabled, "epoch": epoch, "entries": cache.size(), "savedir": OS.get_user_data_dir(), "catalog": snapshot.total, "scanning": snapshot.scanning, "skipped": snapshot.skipped, "warnings": snapshot.warnings, "locale": TranslationServer.get_locale(), "scan_max_ms": scan_max_ms, "scan_overruns": scan_overruns, "scan_thread": true, "scene_resource_loads": 0, "sourceLanguage": snapshot.sourceLanguage, "selectedLanguage": snapshot.selectedLanguage, "skippedByLanguage": snapshot.skippedByLanguage}
    return null

func _exit_tree():
    catalog_mutex.lock()
    scan_stop = true
    catalog_mutex.unlock()
    if scan_thread.is_active():
        scan_thread.wait_to_finish()
    enabled = false
    _refresh(true)
    for entry in dirty_viewports.values():
        var viewport = entry.ref.get_ref()
        if viewport != null and viewport.render_target_update_mode == Viewport.UPDATE_ALWAYS:
            viewport.render_target_update_mode = entry.original_mode
    dirty_viewports.clear()
    entries.clear()
    known.clear()
    cache.clear()
    staged_cache.clear()
    pending.clear()
    optimized_work.clear()
    json_work.clear()
    if directory_reader != null:
        directory_reader.list_dir_end()
        directory_reader = null

func _process(delta):
    if enabled:
        _refresh()
        call_deferred("_before_draw")
    if session == "":
        return
    elapsed += delta
    if elapsed < 0.025:
        return
    elapsed = 0.0
    _flush_writes()
    if _read(session + "/stop", 1024) == secret or (enabled and _ticks() - last_request > 60000):
        enabled = false
        epoch += 1
        _refresh(true)
        if _exists(session + "/stop"):
            set_process(false)
            return
    var q = _json(_read(session + "/request.json", 262144))
    if typeof(q) != TYPE_DICTIONARY or q.get("secret", "") != secret or int(q.get("id", 0)) <= last_id:
        return
    last_id = int(q.id)
    last_request = _ticks()
    var result = _command(q)
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
    if binary_bad or depth > 48 or binary_items > 2000000:
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

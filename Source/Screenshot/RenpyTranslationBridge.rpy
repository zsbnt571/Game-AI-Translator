# Fusion's optional runtime bridge. No game script, variable, menu action or style is rewritten.
init 999 python hide:
    def _fusion_start():
        import os
        if not os.environ.get('FUSION_RENPY_PIPE') or not os.environ.get('FUSION_RENPY_SECRET'):
            return
        import renpy, threading, collections, weakref, json, ast, re, struct, time
        try:
            import builtins as native_builtins
        except ImportError:
            import __builtin__ as native_builtins
        # Store-level list is Ren'Py's rollback list, whereas Text receives both
        # native and rollback lists. Use their shared native base for type checks.
        list = native_builtins.list
        tuple = native_builtins.tuple
        dict = native_builtins.dict
        set = native_builtins.set
        # Ren'Py rewrites literals through these constructors even in python hide.
        # Translation caches and worker state must stay outside rollback/save data.
        __renpy__list__ = native_builtins.list
        __renpy__dict__ = native_builtins.dict
        __renpy__set__ = native_builtins.set
        try:
            string_type = basestring
        except NameError:
            string_type = str
        startup_translation = os.environ.get('FUSION_RENPY_TRANSLATION_START', '1') != '0'

        class Bridge(native_builtins.object):
            def __init__(self):
                self.lock = threading.RLock()
                self.ready = threading.Event()
                self.cache = {}
                self.startup_exact = {}
                self.staged_cache = {}
                self.preparing = False
                self.startup_loaded = False
                self.pending = collections.OrderedDict()
                self.changed = set()
                self.refresh_all = False
                # Existing translations are primed before the first frame. Missing
                # translations always leave the game's own language intact.
                self.enabled = startup_translation
                self.connected = True
                self.epoch = 0
                self.dependencies = weakref.WeakKeyDictionary()
                self.scopes = weakref.WeakKeyDictionary()
                self.pretranslated = weakref.WeakKeyDictionary()
                self.patterns = {}
                self.resolved = collections.OrderedDict()
                self.original_set_text = renpy.text.text.Text.set_text
                self.original_tokenize = renpy.text.text.Text.tokenize
                self.inline_fragments = set()
                self.cursor_next = 0
                self.cursor_state = None
                self.cursor_repairs = 0
                self.cursor_query = None
                self.cursor_query_source = 'pygame'
                self.cursor_history = []
                self.catalog = []
                self.catalog_set = set()
                self.catalog_skipped = 0
                self.source_language = 'Auto'
                self.active_text_language = getattr(renpy.game.preferences, 'language', None)
                self.language_excluded = set()
                self.nonoriginal_nodes = set()
                self.ahead = []
                self.last_current = None
                self.collect_catalog()
                self.previous_periodic = config.periodic_callback
                self.original_tags = renpy.text.text.Text.apply_custom_tags
                self.tags_static = isinstance(renpy.text.text.Text.__dict__.get('apply_custom_tags'), staticmethod)
                self.original_subsegment = renpy.text.text.TextSegment.subsegment
                self.font = os.path.join(os.environ.get('WINDIR', 'C:/Windows'), 'Fonts', 'msyh.ttc').replace('\\', '/')
                if not os.path.isfile(self.font):
                    self.font = None
                # Keep the existing Chinese/Japanese fallback first. Korean is
                # covered by Malgun when installed; never bundle system fonts.
                korean_font = os.path.join(os.environ.get('WINDIR', 'C:/Windows'), 'Fonts', 'malgun.ttf').replace('\\', '/')
                self.fallback_fonts = [font for font in (self.font, korean_font) if font and os.path.isfile(font)]
                self.glyph_fallback = os.environ.get('FUSION_RENPY_GLYPH_FALLBACK','1') != '0'
                self.font_maps = {}
                self.glyph_maps = {}
                self.load_startup_cache()

            def load_startup_cache(self):
                # The host publishes only this session's exact, language-filtered
                # translations before launch. No network or desktop wait occurs
                # in Ren'Py's init/render thread, and no entry enters save data.
                path = os.environ.get('FUSION_RENPY_STARTUP_FILE')
                if not startup_translation or not path:
                    return
                try:
                    with open(path, 'rb') as stream:
                        raw = stream.read(33554433)
                    if len(raw) > 33554432:
                        return
                    # Some shipped Ren'Py 7 runtimes omit the utf_8_sig codec.
                    # Plain UTF-8 is built in; consume only a leading byte BOM.
                    if raw.startswith(b'\xef\xbb\xbf'):
                        raw = raw[3:]
                    data = json.loads(raw.decode('utf-8'))
                    if not isinstance(data, dict) or data.get('secret') != os.environ.get('FUSION_RENPY_SECRET') or data.get('exactOnly') is not True:
                        return
                    entries = data.get('entries')
                    if not isinstance(entries, list) or len(entries) > 150000:
                        return
                    accepted = {}
                    for item in entries:
                        if not isinstance(item, dict):
                            continue
                        source, text = item.get('source'), item.get('text')
                        if self.valid(source) and self.valid_pair(source, text):
                            accepted[source] = text
                    self.startup_exact = accepted
                    # Templates are registered only by normal validated Apply.
                    # Startup entries match the complete original text exactly.
                    self.startup_loaded = True
                except (IOError, OSError, ValueError, TypeError, LookupError):
                    # A missing/corrupt snapshot keeps the game's original text.
                    return

            def valid(self, text):
                return isinstance(text, string_type) and 0 < len(text) <= 6000 and '\x00' not in text and any(c.isalpha() for c in text)

            def text_codes(self, text):
                # Match the host's token scanner: nested indices and quoted
                # brackets are legal, while doubled delimiters are literals.
                codes, i = [], 0
                while i < len(text):
                    opening = text[i]
                    if opening not in '[{':
                        i += 1
                        continue
                    end = i + 1
                    if end < len(text) and text[end] == opening:
                        end += 1
                    else:
                        closing = ']' if opening == '[' else '}'
                        quote, depth = None, 1
                        while end < len(text):
                            char = text[end]
                            if quote is not None:
                                if char == '\\':
                                    end += 2
                                    continue
                                if char == quote: quote = None
                            elif opening == '[' and char in ('\"', "'"):
                                quote = char
                            elif char == opening:
                                depth += 1
                            elif char == closing:
                                depth -= 1
                                if depth == 0: break
                            end += 1
                        if end >= len(text):
                            i += 1
                            continue
                        end += 1
                    codes.append(text[i:end])
                    i = end
                return codes

            def valid_pair(self, source, text):
                if not isinstance(text, string_type) or not text.strip() or len(text) > 12000 or '\x00' in text or u'⟦RPG' in text:
                    return False
                a, b = self.text_codes(source), self.text_codes(text)
                if sorted(a) != sorted(b): return False
                def control(code):
                    return not (code.startswith('[') and not code.startswith('[['))
                return list(filter(control, a)) == list(filter(control, b))

            def fragments(self, text):
                try:
                    return [v for k, v in renpy.text.text.textsupport.tokenize(text) if k == renpy.text.text.TEXT]
                except Exception:
                    return [text]

            def node_texts(self, node):
                if id(node) in self.nonoriginal_nodes:
                    return []
                if isinstance(node, renpy.ast.Say):
                    return [node.what]
                if isinstance(node, renpy.ast.Menu):
                    return [item[0] for item in node.items]
                if isinstance(node, renpy.ast.TranslateString):
                    return [node.old]
                if isinstance(node, renpy.ast.UserStatement):
                    # show/call screen arguments are not Say nodes. Read the parsed
                    # literals without calling ArgumentInfo.evaluate or the statement.
                    parsed = getattr(node, 'parsed', None)
                    if parsed and parsed[0] in (('show', 'screen'), ('call', 'screen')) and isinstance(parsed[1], dict):
                        args = parsed[1].get('arguments')
                        return [text for key, expr in getattr(args, 'arguments', ()) for text in self.expression_texts(expr)]
                return []

            def inline_literals(self, text):
                # Prefetch literal arguments in display interpolation, without
                # running its function or replacing variables in game state.
                # Runtime matching still decides which words are actually shown.
                for code in self.text_codes(text):
                    if not code.startswith('[') or code.startswith('[['): continue
                    try: tree = ast.parse(code[1:-1], mode='eval').body
                    except Exception: continue
                    for node in ast.walk(tree):
                        if not isinstance(node, ast.Call): continue
                        for arg in node.args:
                            try: value = ast.literal_eval(arg)
                            except Exception: continue
                            if self.valid(value) and len(value) > 1 and not re.search(r'[/\\\\#{}\[\]]', value):
                                yield value

            def walk_story(self, roots, limit):
                # Inspect graph links only: never evaluate conditions, calls or Python.
                todo = list(reversed(roots))
                seen = set()
                while todo and len(seen) < limit:
                    node = todo.pop()
                    if node is None or id(node) in seen:
                        continue
                    seen.add(id(node))
                    if id(node) in self.nonoriginal_nodes:
                        # Translated blocks can be outside /tl/. Their next link
                        # points into the translated body; only after is original.
                        todo.append(getattr(node, 'after', None))
                        continue
                    yield node
                    todo.append(getattr(node, 'next', None))
                    for item in reversed(getattr(node, 'entries', ())):
                        if isinstance(item, (tuple, list)) and len(item) > 1 and isinstance(item[1], list):
                            todo.extend(reversed(item[1]))
                    if isinstance(node, renpy.ast.Menu):
                        for item in reversed(node.items):
                            if item[2]: todo.extend(reversed(item[2]))
                    block = getattr(node, 'block', None)
                    if isinstance(block, list): todo.extend(reversed(block))
                    if isinstance(node, (renpy.ast.Jump, renpy.ast.Call)) and not getattr(node, 'expression', True):
                        target = getattr(node, 'target', getattr(node, 'label', None))
                        todo.append(renpy.game.script.namemap.get(target))

            def locale_language(self, key):
                if not isinstance(key, string_type): return ''
                value = key.strip().lower().replace('-', '_')
                aliases = {'english':'en','eng':'en','japanese':'ja','jp':'ja','jpn':'ja','chinese':'zh','simplifiedchinese':'zh','simplified_chinese':'zh','schinese':'zh','korean':'ko','kr':'ko','kor':'ko','french':'fr','german':'de','spanish':'es','portuguese':'pt','russian':'ru'}
                value = aliases.get(value, value)
                if not re.match(r'^[a-z]{2}(?:_(?:latn|cyrl|arab|hans|hant|jpan|kore|deva|hebr|thai|grek))?(?:_(?:[a-z]{2}|[0-9]{3}))?$', value): return ''
                value = value.split('_')[0]
                return value if value in ('en','ja','zh','ko','fr','de','es','pt','ru','it','pl','tr','uk','vi','th','id','ar','he','el','hi','nl','sv','fi','da','no','cs','hu') else ''

            def locale_values(self, tree):
                if not isinstance(tree, ast.Dict): return None
                entries = []
                for key, value in zip(tree.keys, tree.values):
                    try: name = ast.literal_eval(key)
                    except Exception: return None
                    language = self.locale_language(name)
                    if language: entries.append((language, value))
                    elif name not in ('key', 'context', 'comment'): return None
                languages = set(language for language, value in entries)
                if len(languages) < 2 or not languages.difference(('id','no')): return None
                mode = self.source_language
                selected = self.locale_language(self.active_text_language) or ('und-declared' if self.active_text_language is not None else self.locale_language(mode))
                if not selected:
                    selected = 'en' if 'en' in languages else ('ja' if 'ja' in languages else sorted(languages)[0])
                kept = []
                for language, value in entries:
                    if selected == '*' or language == selected: kept.append(value)
                    else:
                        for text in self.literal_values(value, False):
                            if self.valid(text): self.language_excluded.add(text)
                return kept

            def translation_source(self, tree):
                if not isinstance(tree, ast.Call): return None
                func = tree.func
                marker = isinstance(func, ast.Name) and func.id in ('_', '__', '___')
                native = isinstance(func, ast.Attribute) and isinstance(func.value, ast.Name) and func.value.id == 'renpy' and func.attr == 'translate_string'
                if not marker and not native: return None
                # Read the original literal only. In particular, ___ interpolation
                # and translate_string language selection are never executed.
                if tree.args: return tree.args[0]
                for keyword in tree.keywords:
                    if keyword.arg == 's': return keyword.value
                return None

            def literal_values(self, tree, select_language=True):
                # Only evaluate Python literals. Conditions, variables and function
                # calls are never executed while discovering unvisited text.
                try:
                    value = ast.literal_eval(tree)
                    if isinstance(value, string_type):
                        yield value
                        return
                except Exception:
                    pass
                translated = self.translation_source(tree)
                if translated is not None:
                    for text in self.literal_values(translated, select_language): yield text
                elif isinstance(tree, ast.IfExp):
                    for branch in (tree.body, tree.orelse):
                        for text in self.literal_values(branch, select_language): yield text
                elif isinstance(tree, ast.BinOp) and isinstance(tree.op, ast.Add):
                    left, right = list(self.literal_values(tree.left, select_language)), list(self.literal_values(tree.right, select_language))
                    if len(left) == len(right) == 1: yield left[0] + right[0]
                elif isinstance(tree, (ast.List, ast.Tuple, ast.Dict)):
                    localized = self.locale_values(tree) if select_language else None
                    children = localized if localized is not None else (tree.values if isinstance(tree, ast.Dict) else tree.elts)
                    for child in children:
                        for text in self.literal_values(child, select_language): yield text

            def expression_texts(self, expression):
                try: tree = ast.parse(expression, mode='eval').body
                except Exception: return
                for text in self.literal_values(tree): yield text

            def python_texts(self, code):
                source = getattr(code, 'source', None)
                if not isinstance(source, string_type): return
                try: tree = ast.parse(source, mode=getattr(code, 'mode', 'exec'))
                except Exception: return
                todo = [tree]
                while todo:
                    node = todo.pop()
                    # Do not revisit display calls inside a discarded language
                    # branch after an outer assignment has already been filtered.
                    localized = self.locale_values(node)
                    todo.extend(localized if localized is not None else ast.iter_child_nodes(node))
                    expressions = []
                    if isinstance(node, (ast.Assign, ast.Expression)):
                        expressions = [node.value if isinstance(node, ast.Assign) else node.body]
                    elif isinstance(node, ast.Call):
                        func = node.func
                        name = func.id if isinstance(func, ast.Name) else (func.attr if isinstance(func, ast.Attribute) and isinstance(func.value, ast.Name) and func.value.id == 'renpy' else '')
                        translated = self.translation_source(node)
                        if translated is not None: expressions = [translated]
                        elif name in ('notify', 'Text', 'text', 'textbutton', 'label'):
                            expressions = node.args[:1]
                        elif name == 'say': expressions = node.args[1:2]
                        if not expressions and name in ('notify', 'say'):
                            key = 'message' if name == 'notify' else 'what'
                            expressions = [keyword.value for keyword in node.keywords if keyword.arg == key]
                    for expression in expressions:
                        for text in self.literal_values(expression):
                            # Data assignments often contain asset paths/internal IDs.
                            # Display calls are explicit; other data must look like prose.
                            if isinstance(node, ast.Call) or ((any(c.isspace() for c in text) or any(c in text for c in u'。！？')) and not re.search(r'[/\\\\]|https?:', text)):
                                yield text

            def screen_texts(self, root):
                todo, seen = [root], set()
                while todo and len(seen) < 50000:
                    node = todo.pop()
                    if node is None or id(node) in seen: continue
                    seen.add(id(node))
                    displayable = getattr(node, 'displayable', None)
                    text_types = (renpy.text.text.Text, getattr(renpy.ui, '_textbutton', None), getattr(renpy.ui, '_label', None))
                    if displayable is not None and displayable in text_types:
                        for expression in getattr(node, 'positional', ())[:1]:
                            for text in self.expression_texts(expression): yield text
                    for key, expression in getattr(getattr(node, 'arguments', None), 'arguments', ()):
                        for text in self.expression_texts(expression): yield text
                    for text in self.python_texts(getattr(node, 'code', None)): yield text
                    todo.extend(getattr(node, 'children', ()) or ())
                    for entry in getattr(node, 'entries', ()):
                        if isinstance(entry, (tuple, list)) and len(entry) > 1: todo.append(entry[1])

            def collect_catalog(self):
                self.catalog = []
                self.catalog_set = set()
                self.catalog_skipped = 0
                self.language_excluded = set()
                self.nonoriginal_nodes = set()
                self.catalog_marked = False
                self.catalog_iter = self.catalog_steps()

            def catalog_steps(self):
                def add(text):
                    if not isinstance(text, string_type): return
                    if len(text) > 6000:
                        self.catalog_skipped += 1
                    elif self.valid(re.sub(r'\[[^\]]*\]|\{[^}]*\}', '', text)) and text not in self.catalog_set:
                        self.catalog_set.add(text)
                        self.catalog.append(text)
                script = renpy.game.script
                nodes = list(getattr(script, 'all_stmts', None) or script.namemap.values())
                translated_types = tuple(t for t in (getattr(renpy.ast, name, None) for name in ('Translate','TranslateSay','TranslateBlock','TranslateEarlyBlock','TranslatePython')) if t is not None)
                # all_stmts also contains translated children, even when their
                # filename is an ordinary script. Mark structural descendants
                # without following next/call/jump links into original statements.
                todo, active_nodes, marked = [], set(), set()
                active_language = self.active_text_language
                for node in nodes:
                    if isinstance(node, translated_types) and getattr(node, 'language', None) is not None:
                        todo.append((node, getattr(node, 'language', None) == active_language))
                    yield None
                while todo and len(marked) < 1000000:
                    node, is_active = todo.pop()
                    if node is None or id(node) in marked: continue
                    marked.add(id(node))
                    if is_active: active_nodes.add(id(node))
                    else: self.nonoriginal_nodes.add(id(node))
                    block = getattr(node, 'block', None)
                    if isinstance(block, (list, tuple)): todo.extend((child, is_active) for child in block)
                    for entry in getattr(node, 'entries', ()):
                        if isinstance(entry, (list, tuple)) and len(entry) > 1 and isinstance(entry[1], (list, tuple)):
                            todo.extend((child, is_active) for child in entry[1])
                    if isinstance(node, renpy.ast.Menu):
                        for item in node.items:
                            if len(item) > 2 and item[2]: todo.extend((child, is_active) for child in item[2])
                    yield None
                self.catalog_marked = True
                # The opening graph goes first. The remaining loaded scripts include
                # unvisited branches and compiled/archive scripts, without playing them.
                import itertools
                seen = set()
                extras = []
                for node in itertools.chain(self.walk_story([script.namemap.get('start')], 3000), nodes):
                    if id(node) in seen or id(node) in self.nonoriginal_nodes: continue
                    seen.add(id(node))
                    filename = getattr(node, 'filename', '').replace('\\', '/')
                    if active_language is not None and id(node) not in active_nodes: continue
                    if '/tl/' in '/' + filename and id(node) not in active_nodes: continue
                    for text in self.node_texts(node):
                        add(text)
                        for fragment in self.inline_literals(text): add(fragment)
                        yield None
                    extras.append(node)
                    yield None
                # Publish all ordinary dialogue/choices before expensive screen
                # expressions and Python data tables. They need no event execution.
                for node in extras:
                    filename = getattr(node, 'filename', '').replace('\\', '/')
                    if isinstance(node, renpy.ast.Screen):
                        for text in self.screen_texts(node.screen):
                            add(text)
                            yield None
                    if '/renpy/common/' not in '/' + filename and filename.rsplit('/', 1)[-1] not in ('zz_fusion_translation.rpy', 'zz_fusion_translation.rpyc'):
                        for text in self.python_texts(getattr(node, 'code', None)):
                            add(text)
                            yield None
                    yield None

            def advance_catalog(self, budget=0.004):
                # Only the game thread reads engine AST objects. Small batches
                # allow UI and the transport to start before a large scan ends.
                deadline = time.time() + budget
                with self.lock:
                    if self.catalog_iter is None: return
                    try:
                        for unused in range(65536):
                            next(self.catalog_iter)
                            if time.time() >= deadline: break
                    except StopIteration:
                        self.catalog_iter = None
                    except Exception:
                        self.catalog_skipped += 1
                        self.catalog_iter = None

            def register_template(self, source):
                # Character dialogue may interpolate before constructing Text. Match
                # the already-rendered values; never evaluate game expressions again.
                if '[' not in source: return
                literals, fields, literal = [], [], ''
                i = 0
                while i < len(source):
                    if source[i:i+2] == '[[':
                        literal += '['; i += 2; continue
                    if source[i] != '[':
                        literal += source[i]; i += 1; continue
                    start, depth, quote = i, 1, None
                    i += 1
                    while i < len(source) and depth:
                        c = source[i]
                        if quote:
                            if c == '\\': i += 2; continue
                            if c == quote: quote = None
                        elif c in ('"', "'"): quote = c
                        elif c == '[': depth += 1
                        elif c == ']': depth -= 1
                        i += 1
                    if depth: return
                    literals.append(literal); literal = ''
                    fields.append(source[start:i])
                literals.append(literal)
                if (not fields and literals[0] == source) or len(fields) > 16 or sum(len(t) for t in literals) < 4: return
                # Adjacent fields cannot be separated reliably after interpolation.
                if any(not t for t in literals[1:-1]): return
                pattern = re.escape(literals[0])
                for index, field in enumerate(fields):
                    if field in fields[:index]: pattern += '(?P=f%d)' % fields.index(field)
                    else: pattern += '(?P<f%d>.*?)' % index
                    pattern += re.escape(literals[index + 1])
                prefix = literals[0][:2] if len(literals[0]) >= 2 else ''
                # A short template ending in a variable can also swallow an entire
                # longer sentence. Prefer the match with the most literal context.
                specificity = sum(len(t) for t in literals)
                self.patterns.setdefault(prefix, {})[source] = (re.compile('^' + pattern + r'\Z', re.DOTALL), fields, specificity)

            def resolve(self, value, keys):
                if not self.enabled or not isinstance(value, string_type): return value
                if value in self.cache: return self.cache[value]
                if value in self.startup_exact: return self.startup_exact[value]
                if value in self.resolved:
                    source, result = self.resolved[value]
                    keys.add(source)
                    return result
                found = None
                best = -1
                ambiguous = False
                for prefix in (value[:2], ''):
                    for source, (pattern, fields, specificity) in self.patterns.get(prefix, {}).items():
                        if specificity < best: continue
                        match = pattern.match(value)
                        if match is None: continue
                        rendered = self.cache[source].replace('[[', '[')
                        # Replace fields in one pass so values containing bracket text
                        # are never mistaken for another interpolation expression.
                        names = list(collections.OrderedDict.fromkeys(fields))
                        substitutions = dict((field, match.group('f%d' % fields.index(field))) for field in names)
                        for field, value_part in substitutions.items():
                            if '{' in value_part and '(' in field and len(self.inline_fragments) < 4096:
                                self.inline_fragments.update(self.fragments(value_part))
                        expression = '|'.join(re.escape(field) for field in sorted(names, key=len, reverse=True))
                        if names: rendered = re.sub(expression, lambda m: substitutions[m.group(0)], rendered)
                        if specificity == best:
                            if found[1] != rendered: ambiguous = True
                            continue
                        best = specificity
                        ambiguous = False
                        found = (source, rendered)
                if found and not ambiguous:
                    keys.add(found[0])
                    if len(self.resolved) >= 2048: self.resolved.popitem(last=False)
                    self.resolved[value] = found
                    return found[1]
                return value

            def refresh_text(self, displayable, original, keys):
                # Text.text belongs to Ren'Py's say-screen identity and slow-text
                # checks. Invalidate only layout; translation happens in tokenize.
                displayable.dirty = True
                displayable.kill_layout()
                renpy.display.render.redraw(displayable, 0)
                return True

            def tokenize(self, displayable, text):
                if isinstance(displayable, renpy.display.behavior.Input) or getattr(displayable, 'tokenized', False) or self.layout_only(displayable):
                    return self.original_tokenize(displayable, text)
                keys = set(self.scopes.get(displayable, (None, set()))[1])
                mapped, prepared = [], set()
                with self.lock:
                    for value in text:
                        translated = self.resolve(value, keys)
                        mapped.append(translated)
                        if isinstance(value, string_type) and value != translated:
                            prepared.update(self.fragments(translated))
                    # Decorated values were produced by the game, not evaluated by
                    # Fusion. Translate their visible text before custom tags can
                    # turn animated characters into displayables.
                    prepared.difference_update(self.inline_fragments)
                self.pretranslated[displayable] = prepared
                self.dependencies[displayable] = keys
                return self.original_tokenize(displayable, mapped)

            def display_string(self, value):
                # Some Python 2 Chinese localizations hand Text a legacy byte
                # string (for example a character's already-substituted name).
                # Ren'Py decodes those as UTF-8 with replacement, losing the text
                # before font fallback can see it. Normalize only this display
                # value, with strict decoding/roundtrip; never change game data.
                if type(value) is not type(b'') or len(value)>32768:return value
                language=getattr(renpy.game.preferences,'language',None)
                if not isinstance(language,string_type) or language.lower().replace('-','_') not in ('chinese','schinese','simplified_chinese','simplified','zh','zh_cn','zh_hans'):return value
                try:value.decode('utf8');return value
                except UnicodeError:pass
                try:
                    try:
                        decoded=value.decode('gb18030');roundtrip=decoded.encode('gb18030')
                    except LookupError:
                        # Older Windows Ren'Py bundles omit CJK Python codecs.
                        # Windows provides strict GB18030 conversion without
                        # installing anything into the game's Python runtime.
                        if os.name!='nt':return value
                        import ctypes
                        api=ctypes.windll.kernel32
                        decode=api.MultiByteToWideChar
                        decode.argtypes=[ctypes.c_uint,ctypes.c_ulong,ctypes.c_char_p,ctypes.c_int,ctypes.c_wchar_p,ctypes.c_int]
                        decode.restype=ctypes.c_int
                        count=decode(54936,8,value,len(value),None,0)
                        if count<=0:return value
                        buffer=ctypes.create_unicode_buffer(count)
                        if decode(54936,8,value,len(value),buffer,count)!=count:return value
                        decoded=buffer[:count]
                        encode=api.WideCharToMultiByte
                        encode.argtypes=[ctypes.c_uint,ctypes.c_ulong,ctypes.c_wchar_p,ctypes.c_int,ctypes.c_char_p,ctypes.c_int,ctypes.c_void_p,ctypes.c_void_p]
                        encode.restype=ctypes.c_int
                        length=encode(54936,0,decoded,len(decoded),None,0,None,None)
                        if length<=0:return value
                        target=ctypes.create_string_buffer(length)
                        if encode(54936,0,decoded,len(decoded),target,length,None,None)!=length:return value
                        roundtrip=target.raw
                    if roundtrip==value and any(0x3400<=ord(c)<=0x9fff or 0x20000<=ord(c)<=0x323af for c in decoded):return decoded
                except Exception:pass
                return value

            def layout_only(self, displayable):
                # Games use fully transparent Text as a spacer behind image UI.
                # Translating that invisible string changes its height and moves
                # neighbouring sliders. Visible outlined text is still eligible.
                try:
                    color = displayable.style.color
                    if color is None or len(color) < 4 or color[3] != 0:
                        return False
                    for outline in displayable.style.outlines or ():
                        if outline[1] is not None and outline[1][3] != 0:
                            return False
                    return True
                except Exception:
                    return False

            def set_text(self, displayable, text, scope=None, substitute=False, update=True):
                if isinstance(displayable, renpy.display.behavior.Input) or getattr(displayable, 'tokenized', False) or getattr(displayable, 'locked', False) or self.layout_only(displayable):
                    self.scopes.pop(displayable, None)
                    self.pretranslated.pop(displayable, None)
                    self.dependencies.pop(displayable, None)
                    return self.original_set_text(displayable, text, scope, substitute, update)
                text=[self.display_string(value) for value in text] if isinstance(text,list) else self.display_string(text)
                result = self.original_set_text(displayable, text, scope, substitute, update)
                if update:
                    original = list(displayable.text)
                    source = text if isinstance(text, list) else [text]
                    keys = set(t for t in source + original if self.valid(t))
                    self.scopes[displayable] = (original, keys)
                return result

            def tags(self, displayable, tokens):
                if isinstance(displayable, renpy.display.behavior.Input) or self.layout_only(displayable):
                    return self.original_tags(tokens) if self.tags_static else self.original_tags(displayable, tokens)
                keys = set(self.dependencies.get(displayable, self.scopes.get(displayable, (None, set()))[1]))
                prepared = self.pretranslated.get(displayable, set())
                output = []
                with self.lock:
                    for kind, value in tokens:
                        if kind == renpy.text.text.TEXT and self.valid(value) and value not in prepared:
                            keys.add(value)
                            if self.enabled:
                                original = value
                                value = self.resolve(value, keys)
                                if original not in self.cache and original not in self.startup_exact and original not in self.resolved:
                                    if len(self.pending) < 512: self.pending[original] = True
                        output.append((kind, value))
                self.dependencies[displayable] = keys
                return self.original_tags(output) if self.tags_static else self.original_tags(displayable, output)

            def font_map(self, name):
                # Read only the small Unicode cmap table, including fonts inside
                # game archives and TTC face indices. No font/style is rewritten.
                if name in self.font_maps:return self.font_maps[name]
                result=None;stream=None
                try:
                    index=0;filename=name
                    if '@' in filename:
                        face,filename=filename.split('@',1);index=int(face)
                    try:stream=renpy.loader.load(filename)
                    except IOError:
                        if os.path.isabs(filename):stream=open(filename,'rb')
                        else:raise
                    def read_at(offset,length):
                        if offset<0 or length<0 or length>4194304:raise ValueError('font table bounds')
                        stream.seek(offset);value=stream.read(length)
                        if len(value)!=length:raise ValueError('short font table')
                        return value
                    header=read_at(0,12);base=0
                    if header[:4]==b'ttcf':
                        faces=struct.unpack_from('>I',header,8)[0]
                        if index<0 or index>=faces or faces>256:raise ValueError('font face bounds')
                        base=struct.unpack('>I',read_at(12+4*index,4))[0]
                        header=read_at(base,12)
                    count=struct.unpack_from('>H',header,4)[0]
                    if count>256:raise ValueError('font directory bounds')
                    tables=read_at(base+12,count*16);cmap=None
                    for pos in range(0,len(tables),16):
                        if tables[pos:pos+4]==b'cmap':
                            offset,length=struct.unpack_from('>II',tables,pos+8);cmap=read_at(offset,length);break
                    if cmap is None:raise ValueError('missing cmap')
                    count=struct.unpack_from('>H',cmap,2)[0]
                    if count>256:raise ValueError('cmap directory bounds')
                    result=[]
                    for pos in range(4,4+count*8,8):
                        platform,encoding,offset=struct.unpack_from('>HHI',cmap,pos)
                        if not (platform==0 or platform==3 and encoding in (1,10)):continue
                        fmt=struct.unpack_from('>H',cmap,offset)[0]
                        if fmt not in (4,12):continue
                        length=struct.unpack_from('>H' if fmt==4 else '>I',cmap,offset+(2 if fmt==4 else 4))[0]
                        table=cmap[offset:offset+length]
                        if len(table)!=length:raise ValueError('short cmap')
                        result.append((fmt,table))
                    if not result:result=None
                except Exception:result=None
                finally:
                    if stream is not None:
                        try:stream.close()
                        except Exception:pass
                if len(self.font_maps)>=64:self.font_maps.clear()
                self.font_maps[name]=result
                return result

            def has_glyph(self, segment, character, fallback=False, font=None):
                name=font or (self.font if fallback else segment.font)
                if not isinstance(name,string_type):return None
                bold,italic=segment.bold,segment.italic
                name,bold,italic=config.font_replacement_map.get((name,bold,italic),(name,bold,italic))
                image_font=renpy.text.font.image_fonts.get((name,segment.size,bold,italic))
                if image_font is not None:return character in image_font.chars
                key=(name,ord(character))
                if key not in self.glyph_maps:
                    if len(self.glyph_maps)>=32768:self.glyph_maps.clear()
                    self.glyph_maps[key]=self.font_has_glyph(name,character)
                return self.glyph_maps[key]

            def font_has_glyph(self, name, character):
                tables=self.font_map(name)
                if tables is None:return None
                code=ord(character)
                try:
                    for fmt,table in tables:
                        if fmt==12:
                            lo=0;hi=struct.unpack_from('>I',table,12)[0]
                            if 16+hi*12>len(table):continue
                            while lo<hi:
                                mid=(lo+hi)//2;start,end,glyph=struct.unpack_from('>III',table,16+mid*12)
                                if code<start:hi=mid
                                elif code>end:lo=mid+1
                                else:
                                    if glyph+code-start!=0:return True
                                    break
                        elif code<=65535:
                            count=struct.unpack_from('>H',table,6)[0]//2
                            for i in range(count):
                                end=struct.unpack_from('>H',table,14+2*i)[0]
                                if code>end:continue
                                start=struct.unpack_from('>H',table,16+2*count+2*i)[0]
                                if code<start:break
                                delta=struct.unpack_from('>h',table,16+4*count+2*i)[0]
                                address=16+6*count+2*i;offset=struct.unpack_from('>H',table,address)[0]
                                if not offset:
                                    if (code+delta)&65535!=0:return True
                                    break
                                glyph=struct.unpack_from('>H',table,address+offset+2*(code-start))[0]
                                if glyph!=0 and (glyph+delta)&65535!=0:return True
                                break
                except Exception:return None
                return False

            def subsegment(self, segment, text):
                # Local glyph fallback only. Never assign style.font or style.size:
                # Ren'Py shares styles between unrelated controls, including menu buttons.
                for original, value in self.original_subsegment(segment, text):
                    if not self.glyph_fallback or not self.fallback_fonts:
                        yield original, value
                        continue
                    def fallback_font(c):
                        n = ord(c)
                        # Preserve the original font when supported or coverage is
                        # unknown; fallback only for a confirmed missing glyph.
                        try:
                            if n < 32 or c.isspace() or self.has_glyph(original,c) is not False:
                                return None
                            for candidate in self.fallback_fonts:
                                if self.has_glyph(original,c,font=candidate) is True:
                                    return candidate
                        except Exception:pass
                        return None
                    start = 0
                    while start < len(value):
                        fallback = fallback_font(value[start])
                        end = start + 1
                        while end < len(value) and fallback_font(value[end]) == fallback:
                            end += 1
                        selected = original
                        if fallback:
                            selected = renpy.text.text.TextSegment(original)
                            selected.font = fallback
                        yield selected, value[start:end]
                        start = end

            def cursor_visible(self, mouse):
                if hasattr(mouse, 'get_visible'): return bool(mouse.get_visible())
                # pygame_sdl2 shipped with Ren'Py lacks pygame's get_visible.
                # Query the SDL already loaded by this game; -1 never changes it.
                if self.cursor_query is None:
                    self.cursor_query = False
                    if os.name == 'nt':
                        import ctypes
                        module = ctypes.windll.kernel32.GetModuleHandleW
                        module.argtypes = [ctypes.c_wchar_p]
                        module.restype = ctypes.c_void_p
                        for name in ('librenpython.dll', 'SDL2.dll', 'libSDL2.dll'):
                            handle = module(name)
                            if not handle: continue
                            try:
                                query = ctypes.CDLL(name, handle=handle).SDL_ShowCursor
                                query.argtypes = [ctypes.c_int]
                                query.restype = ctypes.c_int
                                self.cursor_query = query
                                self.cursor_query_source = 'loaded-sdl'
                                break
                            except (AttributeError, OSError): pass
                        if not self.cursor_query:
                            # Older bundled SDL builds do not export the query.
                            # Read Windows' cursor state; never alter ShowCursor's
                            # counter. Recovery below still requires game focus.
                            class Point(ctypes.Structure):
                                _fields_ = [('x', ctypes.c_long), ('y', ctypes.c_long)]
                            class CursorInfo(ctypes.Structure):
                                _fields_ = [('size', ctypes.c_uint), ('flags', ctypes.c_uint),
                                            ('cursor', ctypes.c_void_p), ('position', Point)]
                            get_info = ctypes.windll.user32.GetCursorInfo
                            get_info.argtypes = [ctypes.POINTER(CursorInfo)]
                            get_info.restype = ctypes.c_int
                            def system_cursor_query(ignored):
                                info = CursorInfo()
                                info.size = ctypes.sizeof(CursorInfo)
                                if not get_info(ctypes.byref(info)) or info.flags & 2: return -1
                                return 1 if info.flags & 1 else 0
                            self.cursor_query = system_cursor_query
                            self.cursor_query_source = 'windows'
                if self.cursor_query:
                    result = self.cursor_query(-1)
                    if result in (0, 1): return bool(result)
                return None

            def record_cursor(self, state):
                if state == self.cursor_state: return
                self.cursor_state = state
                self.cursor_history.append(state)
                self.cursor_history = self.cursor_history[-24:]
                path = os.environ.get('FUSION_RENPY_CURSOR_LOG')
                if path:
                    with open(path, 'wb') as stream:
                        stream.write(json.dumps({'current': state, 'recentStates': self.cursor_history}).encode('ascii'))

            def cursor_health(self):
                now = time.time()
                if now < self.cursor_next: return
                self.cursor_next = now + 1.0
                try:
                    interface = renpy.game.interface
                    mouse = renpy.display.core.pygame.mouse
                    intended = bool(interface.is_mouse_visible())
                    focused = bool(mouse.get_focused())
                    visible = self.cursor_visible(mouse)
                    recent = 0 <= renpy.display.core.get_time() - interface.mouse_event_time < 2
                    ordinary = config.mouse_displayable is None
                    # Repair only a stale system-cursor cache. Respect cutscenes,
                    # gamepad hiding, idle hiding and software cursors. The engine
                    # restores its own hardware cursor image when one is configured.
                    if intended and focused and recent and ordinary and interface.old_mouse is not False and visible is False:
                        interface.old_mouse = None
                        interface.update_mouse(None)
                        self.cursor_repairs += 1
                        visible = self.cursor_visible(mouse)
                    state = {'intended': intended, 'focused': focused, 'visible': visible,
                             'visibilitySource': self.cursor_query_source,
                             'recentMouse': recent, 'systemCursor': ordinary,
                             'fullscreen': bool(renpy.game.preferences.fullscreen), 'repairs': self.cursor_repairs,
                             'gameVisible': bool(getattr(renpy.store, 'mouse_visible', True)),
                             'interactionVisible': bool(getattr(interface, 'show_mouse', True)),
                             'video': bool(getattr(renpy.display.video, 'fullscreen', False)),
                             'lessMouse': bool(getattr(renpy.game, 'less_mouse', False))}
                    self.record_cursor(state)
                except Exception as error:
                    try: self.record_cursor({'error': type(error).__name__})
                    except Exception: pass

            def periodic(self):
                latest_language = getattr(renpy.game.preferences, 'language', None)
                if latest_language != self.active_text_language:
                    with self.lock:
                        self.active_text_language = latest_language
                        self.collect_catalog()
                        self.epoch += 1
                        self.ahead = []
                        self.last_current = None
                        self.refresh_all = True
                self.advance_catalog()
                self.cursor_health()
                if self.previous_periodic is not None:
                    self.previous_periodic()
                current = getattr(renpy.game.context(), 'current', None)
                if self.enabled and self.catalog_marked and current != self.last_current:
                    self.last_current = current
                    root = renpy.game.script.namemap.get(current)
                    texts = []
                    for node in self.walk_story([root], 800):
                        for text in self.node_texts(node):
                            if self.valid(text):
                                texts.append(text)
                                texts.extend(self.inline_literals(text))
                        if len(texts) >= 96: break
                    with self.lock: self.ahead = list(collections.OrderedDict.fromkeys(texts))[:96]
                with self.lock:
                    changed, all_text = self.changed, self.refresh_all
                    self.changed, self.refresh_all = set(), False
                if not changed and not all_text:
                    return
                for displayable, entry in list(self.scopes.items()):
                    if all_text or entry[1].intersection(changed):
                        self.refresh_text(displayable, entry[0], entry[1])
                for displayable, keys in list(self.dependencies.items()):
                    if all_text or keys.intersection(changed):
                        displayable.dirty = True
                        displayable.kill_layout()
                        renpy.display.render.redraw(displayable, 0)

            def command(self, request):
                if request.get('op', '').startswith('data') or request.get('op') in ('lifecycleState','prepareRestart'):
                    data = getattr(renpy, '_fusion_data', None)
                    if data is None:raise Exception('此游戏的修改数据尚未适配，游戏和翻译可继续使用。')
                    return data.request(request)
                with self.lock:
                    op = request.get('op')
                    if op == 'translationPrepare':
                        preserve = self.startup_loaded and request.get('preserveStartup') is True
                        self.startup_loaded = False
                        self.preparing = True
                        self.staged_cache.clear()
                        self.cache.clear()
                        if not preserve:
                            self.enabled = False
                            self.startup_exact.clear()
                        self.epoch += 1
                        self.patterns.clear()
                        self.resolved.clear()
                        self.pending.clear()
                        mode = request.get('sourceLanguage', 'Auto')
                        if mode not in ('Auto','English','Japanese','SimplifiedChinese','Korean','Mixed'): mode = 'Auto'
                        if mode != self.source_language:
                            self.source_language = mode
                            self.collect_catalog()
                        self.ahead = []
                        self.last_current = None
                        self.refresh_all = True
                        return {'epoch': self.epoch}
                    if op == 'translationEnable':
                        if self.preparing:
                            self.cache = self.staged_cache
                            self.staged_cache = {}
                            self.preparing = False
                            self.patterns.clear()
                            self.resolved.clear()
                            for source in self.cache:
                                self.register_template(source)
                        self.enabled = True
                        self.refresh_all = True
                        self.ready.set()
                        return {'epoch': self.epoch}
                    if op == 'translationDisable':
                        self.enabled = False
                        self.epoch += 1
                        self.startup_loaded = False
                        self.startup_exact.clear()
                        self.preparing = False
                        self.staged_cache.clear()
                        self.ready.set()
                        self.pending.clear()
                        self.refresh_all = True
                        return {'epoch': self.epoch}
                    if op == 'translationCatalog':
                        offset = max(0, min(len(self.catalog), int(request.get('cursor', 0))))
                        texts, size = [], 0
                        for text in self.catalog[offset:offset + 512]:
                            cost = len(json.dumps(text, ensure_ascii=True))
                            if texts and size + cost > 42000: break
                            texts.append(text)
                            size += cost
                        return {'texts': texts, 'next': offset + len(texts), 'total': len(self.catalog), 'scanning': self.catalog_iter is not None, 'skipped': self.catalog_skipped, 'sourceLanguage': self.source_language, 'skippedByLanguage': len(self.language_excluded)}
                    if op == 'translationPoll':
                        texts = list(self.pending.keys())[:128]
                        for text in texts:
                            self.pending.pop(text, None)
                        return {'epoch': self.epoch, 'texts': texts, 'ahead': self.ahead, 'activeTextLanguage': self.locale_language(self.active_text_language)}
                    if op == 'translationApply':
                        if request.get('epoch') != self.epoch:
                            return {'stale': True}
                        count = 0
                        for pair in request.get('entries', [])[:128]:
                            if not isinstance(pair, dict): continue
                            source, text = pair.get('source'), pair.get('text')
                            if self.valid(source) and self.valid_pair(source, text):
                                target = self.staged_cache if self.preparing and request.get('prime') else self.cache
                                if target.get(source) != text:
                                    target[source] = text
                                    if target is self.cache:
                                        self.register_template(source)
                                        self.resolved.clear()
                                        self.changed.add(source)
                                    # Character may already have substituted its variables
                                    # before Text was created. A new template can therefore
                                    # match visible text whose dependency key is the resolved form.
                                        if '[' in source: self.refresh_all = True
                                count += 1
                        return {'applied': count}
                    if op == 'translationState':
                        return {'enabled': self.enabled, 'entries': len(set(self.cache).union(self.startup_exact)), 'startupExactCache': len(self.startup_exact), 'savedir': config.savedir, 'version': renpy.version_only, 'pending': len(self.pending), 'catalog': len(self.catalog)}
                    raise Exception('Unknown translation request')

            def serve(self):
                pipe, secret = os.environ['FUSION_RENPY_PIPE'], os.environ['FUSION_RENPY_SECRET']
                while True:
                    self.serve_once(pipe, secret)
                    threading.Event().wait(1.0)
                    try:
                        path = os.environ.get('FUSION_RENPY_RECONNECT')
                        if path:
                            with open(path, 'rb') as stream: raw = stream.read(4097)
                            if len(raw) > 4096: continue
                            if raw.startswith(b'\xef\xbb\xbf'): raw = raw[3:]
                            data = json.loads(raw.decode('utf-8'))
                            if data.get('protocol') != 156 or data.get('pid') != os.getpid(): continue
                            if not re.match(r'^fusion-rpg-[a-f0-9]{32}$', data.get('pipe', '')) or not re.match(r'^[a-f0-9]{64}$', data.get('secret', '')): continue
                            pipe, secret = data['pipe'], data['secret']
                    except Exception:
                        pass

            def serve_once(self, pipe, secret):
                fd = None
                try:
                    fd = os.open('\\\\.\\pipe\\' + pipe, os.O_RDWR | os.O_BINARY)
                    def send(value):
                        data = (json.dumps(value, ensure_ascii=True) + '\n').encode('utf8')
                        while data:
                            written = os.write(fd, data)
                            if written <= 0:
                                raise IOError('Connection closed')
                            data = data[written:]
                    send({'hello': 1, 'secret': secret, 'pid': os.getpid(), 'reconnect': 156})
                    with self.lock: self.connected = True
                    pending = b''
                    while True:
                        while b'\n' not in pending and len(pending) <= 262144:
                            chunk = os.read(fd, 4096)
                            if not chunk:
                                raise IOError('Connection closed')
                            pending += chunk
                        if len(pending) > 262144:
                            break
                        line, pending = pending.split(b'\n', 1)
                        request = json.loads(line.decode('utf8'))
                        try:
                            response = {'id': request['id'], 'ok': True, 'result': self.command(request)}
                        except Exception as ex:
                            message = ex.args[0] if request.get('op','').startswith('data') and ex.args else 'Translation request failed'
                            response = {'id': request.get('id'), 'ok': False, 'error': message}
                        send(response)
                except Exception:
                    pass
                finally:
                    if fd is not None:
                        os.close(fd)
                    with self.lock:
                        was_active = self.connected or self.enabled
                        self.enabled = False
                        self.epoch += 1
                        self.startup_loaded = False
                        self.startup_exact.clear()
                        self.cache.clear()
                        self.preparing = False
                        self.staged_cache.clear()
                        self.patterns.clear()
                        self.resolved.clear()
                        self.pending.clear()
                        self.connected = False
                        self.refresh_all = self.refresh_all or was_active
                    self.ready.set()

        bridge = Bridge()
        class TextTags(native_builtins.object):
            # Ren'Py 8.5 also calls Text.apply_custom_tags(tokens) without an
            # instance (notifications, accessibility and tag filtering). Preserve
            # that original API; only instance rendering needs display dependencies.
            def __get__(self, displayable, owner):
                if displayable is None:
                    return bridge.original_tags
                def apply(tokens):
                    return bridge.tags(displayable, tokens)
                return apply
        def subsegment(self, s):
            return bridge.subsegment(self, s)
        def set_text(self, text, scope=None, substitute=False, update=True):
            return bridge.set_text(self, text, scope, substitute, update)
        def tokenize(self, text):
            return bridge.tokenize(self, text)
        renpy.text.text.Text.tokenize = tokenize
        renpy.text.text.Text.set_text = set_text
        renpy.text.text.Text.apply_custom_tags = TextTags()
        renpy.text.text.TextSegment.subsegment = subsegment
        if config.replace_text is None:
            config.replace_text = lambda text: text
        config.periodic_callback = bridge.periodic
        # Script ASTs are already loaded at init 999. Seed the first directory
        # page here, before connection, menus or live translation can run. Keep
        # the startup allowance bounded; later ticks continue larger catalogues.
        bridge.advance_catalog(0.04)
        worker = threading.Thread(target=bridge.serve)
        worker.daemon = True
        worker.start()
        # Cached exact translations were loaded locally before hooks were installed.
        # Newly encountered text keeps its original while the worker translates it;
        # never stall game initialization on a desktop/API handshake.
    _fusion_start()


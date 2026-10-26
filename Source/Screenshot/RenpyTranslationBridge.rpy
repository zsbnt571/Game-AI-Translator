# Fusion's optional runtime bridge. No game script, variable, menu action or style is rewritten.
init 999 python hide:
    def _fusion_start():
        import os
        if not os.environ.get('FUSION_RENPY_PIPE') or not os.environ.get('FUSION_RENPY_SECRET'):
            return
        import renpy, threading, collections, weakref, json, ast, re, struct, time, hashlib
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
            @staticmethod
            def stored_pairs_hash(pairs):
                # .NET ordinal order compares UTF-16 units, including astral
                # characters before BMP private-use characters. Bind exact UTF-8
                # bytes with little-endian lengths; this is not a display receipt.
                digest = hashlib.sha256()
                ordered = sorted(pairs, key=lambda pair: pair[0].encode('utf-16-be'))
                digest.update(struct.pack('<I', len(ordered)))
                for pair in ordered:
                    for value in pair:
                        data = value.encode('utf-8')
                        digest.update(struct.pack('<I', len(data)))
                        digest.update(data)
                return digest.hexdigest()

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
                self.catalog_requested = startup_translation
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
                # Explicit per-game selection only. Empty means the existing
                # original-font/missing-glyph behavior, including style metrics.
                self.custom_font = os.environ.get('FUSION_GAME_FONT_PATH', '').replace('\\', '/')
                if not self.custom_font or not os.path.isfile(self.custom_font):
                    self.custom_font = None
                self.font_maps = {}
                self.glyph_maps = {}
                self.font_pending = None
                self.font_connection = 0
                self.font_peer_check = None
                self.font_request_seconds = 5.0
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
                    # String translations have no child Say node. The selected
                    # language displays new; absent entries fall back to old.
                    language = getattr(node, 'language', None)
                    if language is not None:
                        return [node.new] if language == self.active_text_language else []
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

            def active_inline_literals(self, text):
                # Only a statically proven text-preserving decorator grants bulk
                # provenance. Unknown functions' arguments still wait for display.
                for code in self.text_codes(text):
                    if not code.startswith('[') or code.startswith('[['): continue
                    try: tree = ast.parse(code[1:-1], mode='eval').body
                    except Exception: continue
                    if not isinstance(tree, ast.Call) or not isinstance(tree.func, ast.Name) or tree.func.id not in self.text_decorators or not tree.args: continue
                    try: value = ast.literal_eval(tree.args[0])
                    except Exception: continue
                    if self.valid(value) and len(value) > 1 and not re.search(r'[/\\\\#{}\[\]]', value): yield value

            def decorator_preserves_text(self, function, known):
                # Abstract syntax only: no eval, function invocation, attribute
                # lookup or game variable reading. Every return must preserve its
                # first argument once, adding only formatting tags around it.
                marker = '\x01TEXT\x01'
                names = [getattr(arg, 'arg', getattr(arg, 'id', '')) for arg in function.args.args]
                if not names or function.decorator_list or getattr(function.args, 'vararg', None) or getattr(function.args, 'kwarg', None): return False
                if getattr(function.args, 'posonlyargs', None) or getattr(function.args, 'kwonlyargs', None): return False
                if len(names) > 16: return False
                if any(index > 2048 for index, unused in enumerate(ast.walk(function))): return False
                def literal(expression):
                    try: return ast.literal_eval(expression)
                    except Exception: return None
                def condition(expression, env):
                    if isinstance(expression, ast.Name): return expression.id in env or expression.id in ('True','False','None')
                    if isinstance(expression, (ast.Str, ast.Num)) or type(expression).__name__ in ('Constant','NameConstant'): return True
                    if isinstance(expression, (ast.Compare, ast.BoolOp, ast.UnaryOp)):
                        return all(condition(child, env) for child in ast.iter_child_nodes(expression) if not isinstance(child, (ast.cmpop, ast.boolop, ast.unaryop)))
                    if isinstance(expression, ast.Call) and isinstance(expression.func, ast.Name) and expression.func.id == 'isinstance' and len(expression.args) == 2 and not expression.keywords:
                        return 'isinstance' not in env and condition(expression.args[0], env) and isinstance(expression.args[1], ast.Name) and expression.args[1].id in ('str','int','float','bool') and expression.args[1].id not in env
                    return False
                def values(expression, env):
                    value = literal(expression)
                    if isinstance(value, string_type): return [value]
                    if isinstance(value, (int, float, bool)) or isinstance(expression, ast.Name) and expression.id in ('True','False','None') or type(expression).__name__ in ('Constant','NameConstant') and value is None: return ['FUSION_VALUE']
                    if isinstance(expression, ast.Name): return env.get(expression.id)
                    if isinstance(expression, ast.BinOp) and isinstance(expression.op, ast.Add):
                        left, right = values(expression.left, env), values(expression.right, env)
                        if left is None or right is None or len(left) * len(right) > 64: return None
                        result = [a + b for a in left for b in right]
                        return result if all(len(item) < 4096 for item in result) else None
                    if isinstance(expression, ast.IfExp) and condition(expression.test, env):
                        left, right = values(expression.body, env), values(expression.orelse, env)
                        return left + right if left is not None and right is not None else None
                    if isinstance(expression, ast.Call) and isinstance(expression.func, ast.Name) and not expression.keywords:
                        name = expression.func.id
                        if name in env: return None
                        if name == 'str' and len(expression.args) == 1: return values(expression.args[0], env)
                        if name in known and expression.args:
                            first = values(expression.args[0], env)
                            other = [values(arg, env) for arg in expression.args[1:]]
                            if first is not None and all(item is not None and all(marker not in s for s in item) for item in other):
                                return ['{fusion}' + s + '{/fusion}' for s in first]
                    return None
                returns = []
                def statements(body, states, depth=0):
                    if depth > 12: return None
                    for statement in body:
                        if isinstance(statement, ast.Expr) and isinstance(literal(statement.value), string_type): continue
                        upcoming = []
                        for env in states:
                            if isinstance(statement, ast.Assign) and len(statement.targets) == 1 and isinstance(statement.targets[0], ast.Name):
                                result = values(statement.value, env)
                                if result is None: return None
                                copy = dict(env); copy[statement.targets[0].id] = result; upcoming.append(copy)
                            elif isinstance(statement, ast.Return):
                                result = values(statement.value, env)
                                if result is None: return None
                                returns.extend(result)
                            elif isinstance(statement, ast.If) and condition(statement.test, env):
                                yes = statements(statement.body, [dict(env)], depth + 1)
                                no = statements(statement.orelse, [dict(env)], depth + 1)
                                if yes is None or no is None: return None
                                upcoming.extend(yes + no)
                            else: return None
                        states = upcoming
                        if len(states) > 64 or len(returns) > 512: return None
                    return states
                env = dict((name, [marker if index == 0 else 'FUSION_VALUE']) for index, name in enumerate(names))
                remaining = statements(function.body, [env])
                return remaining == [] and bool(returns) and all(re.sub(r'\{[^{}]*\}', '', value).strip() == marker for value in returns)

            def collect_text_decorators(self, nodes):
                functions, overwritten = {}, set()
                for node in nodes:
                    try:
                        filename = getattr(node, 'filename', '').replace('\\', '/')
                        if '/renpy/common/' in '/' + filename or '/tl/' in '/' + filename or id(node) in self.nonoriginal_nodes: continue
                        code = getattr(node, 'code', None)
                        source = getattr(code, 'source', None)
                        if not isinstance(source, string_type) or len(source) > 500000: continue
                        if getattr(node, 'store', 'store') not in ('store', None): continue
                        tree = ast.parse(source, mode=getattr(code, 'mode', 'exec'))
                        for statement in getattr(tree, 'body', ()) if isinstance(tree, ast.Module) else ():
                            if isinstance(statement, ast.FunctionDef):
                                if statement.name in functions: overwritten.add(statement.name)
                                if len(functions) < 1024: functions[statement.name] = statement
                            elif isinstance(statement, ast.Assign):
                                for target in statement.targets:
                                    if isinstance(target, ast.Name): overwritten.add(target.id)
                        # Rebinding under if/try/for still changes the module's
                        # symbol. Skip nested function/class scopes entirely;
                        # their local assignments cannot authorize global calls.
                        top_functions = set(id(statement) for statement in getattr(tree, 'body', ()) if isinstance(statement, ast.FunctionDef)) if isinstance(tree, ast.Module) else set()
                        pending = [tree]
                        while pending:
                            item = pending.pop()
                            if isinstance(item, (ast.FunctionDef, ast.ClassDef)):
                                if id(item) not in top_functions: overwritten.add(item.name)
                                continue
                            if isinstance(item, ast.Lambda): continue
                            if isinstance(item, ast.Name) and isinstance(item.ctx, (ast.Store, ast.Del)): overwritten.add(item.id)
                            if isinstance(item, (ast.Import, ast.ImportFrom)):
                                for alias in item.names: overwritten.add(alias.asname or alias.name.split('.')[0])
                            pending.extend(ast.iter_child_nodes(item))
                        if type(node).__name__ in ('Define', 'Default'): overwritten.add(getattr(node, 'varname', ''))
                    except Exception: pass
                    yield None
                known = set()
                self.character_constructor_trusted = 'Character' not in overwritten and 'Character' not in functions
                # Rebinding Python builtins invalidates the narrow proof.
                if not overwritten.union(functions).intersection(('str','isinstance','int','float','bool')):
                    for unused in range(8):
                        additions = set()
                        for name, function in functions.items():
                            if name not in overwritten and name not in known and self.decorator_preserves_text(function, known): additions.add(name)
                            yield None
                        if not additions: break
                        known.update(additions)
                        yield None
                self.text_decorators = known

            def walk_story(self, roots, limit):
                # Inspect graph links only: never evaluate conditions, calls or Python.
                todo = list(reversed(roots))
                seen = set()
                while todo and len(seen) < limit:
                    node = todo.pop()
                    if node is None or id(node) in seen:
                        continue
                    seen.add(id(node))
                    try:
                        if id(node) in self.nonoriginal_nodes:
                            # A covered original or an inactive translation must
                            # not lead the walk back into its excluded body.
                            todo.append(getattr(node, 'after', None))
                            continue
                        yield node
                        todo.append(getattr(node, 'next', None))
                        for item in reversed(getattr(node, 'entries', ()) or ()):
                            if isinstance(item, (tuple, list)) and len(item) > 1 and isinstance(item[1], (list, tuple)):
                                todo.extend(reversed(item[1]))
                        if isinstance(node, renpy.ast.Menu):
                            for item in reversed(node.items or ()):
                                if len(item) > 2 and item[2]: todo.extend(reversed(item[2]))
                        block = getattr(node, 'block', None)
                        if isinstance(block, (list, tuple)): todo.extend(reversed(block))
                        if isinstance(node, (renpy.ast.Jump, renpy.ast.Call)) and not getattr(node, 'expression', True):
                            target = getattr(node, 'target', getattr(node, 'label', None))
                            todo.append(renpy.game.script.namemap.get(target))
                    except Exception as error:
                        self.catalog_issue('story-links', node, error)

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

            def selected_string(self, source):
                # Match StringTranslator's lookup without calling translate(),
                # which can record unknown strings in the game's language data.
                if not isinstance(source, string_type): return source
                translator = getattr(renpy.game.script, 'translator', None)
                languages = getattr(translator, 'strings', None)
                selected = languages.get(self.active_text_language) if isinstance(languages, dict) else None
                translations = getattr(selected, 'translations', None)
                if not isinstance(translations, dict): return source
                result = translations.get(source)
                if result is None: result = translations.get(re.sub(r'\{\#.*?\}', '', source))
                return result if isinstance(result, string_type) else source

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

            def screen_texts(self, root, marked=False):
                todo, seen = [root], set()
                while todo and len(seen) < 50000:
                    node = todo.pop()
                    if node is None or id(node) in seen: continue
                    seen.add(id(node))
                    displayable = getattr(node, 'displayable', None)
                    text_types = (renpy.text.text.Text, getattr(renpy.ui, '_textbutton', None), getattr(renpy.ui, '_label', None))
                    if displayable is not None and displayable in text_types:
                        for expression in getattr(node, 'positional', ())[:1]:
                            for text in self.expression_texts(expression):
                                text = self.selected_string(text)
                                yield (text, True) if marked else text
                    for key, expression in getattr(getattr(node, 'arguments', None), 'arguments', ()):
                        for text in self.expression_texts(expression): yield (text, False) if marked else text
                    for text in self.python_texts(getattr(node, 'code', None)): yield (text, False) if marked else text
                    todo.extend(getattr(node, 'children', ()) or ())
                    for entry in getattr(node, 'entries', ()):
                        if isinstance(entry, (tuple, list)) and len(entry) > 1: todo.append(entry[1])
                if todo: self.catalog_issue('screen-limit', root, RuntimeError('screen traversal limit'))

            def common_ui_texts(self, code):
                # Common contains much more than UI. Read only explicit _()/Text
                # markers in its module-level statements, never a function body,
                # arbitrary assignments or a language table's unused values.
                source = getattr(code, 'source', None)
                if not isinstance(source, string_type): return
                try: tree = ast.parse(source, mode=getattr(code, 'mode', 'exec'))
                except Exception: return
                todo = [tree]
                while todo:
                    node = todo.pop()
                    if isinstance(node, (ast.FunctionDef, ast.ClassDef, ast.Lambda)): continue
                    localized = self.locale_values(node)
                    if localized is not None:
                        todo.extend(localized)
                        continue
                    marker = self.translation_source(node)
                    if marker is not None:
                        for text in self.literal_values(node): yield self.selected_string(text)
                        continue
                    if isinstance(node, ast.Call):
                        func = node.func
                        name = func.id if isinstance(func, ast.Name) else ''
                        if name in ('Text', 'text', 'textbutton', 'label', 'notify'):
                            for expression in node.args[:1]:
                                for text in self.literal_values(expression): yield self.selected_string(text)
                            continue
                    todo.extend(ast.iter_child_nodes(node))

            def display_names(self, node):
                # Character declarations are explicit names. Variables, custom
                # constructors and player-entered/interpolated names are not.
                code = getattr(node, 'code', None)
                if not self.character_constructor_trusted or getattr(node, 'store', 'store') not in ('store', None): return
                source = getattr(code, 'source', None)
                if not isinstance(source, string_type): return
                try: tree = ast.parse(source, mode=getattr(code, 'mode', 'exec'))
                except Exception: return
                declarations = []
                if isinstance(tree, ast.Expression) and type(node).__name__ == 'Define':
                    declarations.append((getattr(node, 'varname', ''), tree.body))
                elif isinstance(tree, ast.Module):
                    for statement in tree.body:
                        if isinstance(statement, ast.Assign) and len(statement.targets) == 1 and isinstance(statement.targets[0], ast.Name):
                            declarations.append((statement.targets[0].id, statement.value))
                for binding, expression in declarations:
                    if not isinstance(binding, string_type) or not re.match(r'^[A-Za-z_]\w{0,117}$', binding): continue
                    if not isinstance(expression, ast.Call) or not isinstance(expression.func, ast.Name) or expression.func.id != 'Character': continue
                    if any(keyword.arg == 'dynamic' and not (isinstance(keyword.value, ast.Name) and keyword.value.id == 'False' or getattr(keyword.value, 'value', None) is False) for keyword in expression.keywords): continue
                    value = expression.args[0] if expression.args else next((keyword.value for keyword in expression.keywords if keyword.arg == 'name'), None)
                    if value is None: continue
                    names = list(self.literal_values(value))
                    if len(names) != 1: continue
                    name = self.selected_string(names[0])
                    if not self.valid(name) or not 2 <= len(name) <= 80 or any(c in name for c in '[]\r\n\x00'): continue
                    filename = getattr(node, 'filename', '').replace('\\', '/')
                    if '/game/' in filename: filename = filename.split('/game/', 1)[1]
                    elif filename.startswith('game/'): filename = filename[5:]
                    if len(filename) > 512 or filename.startswith('/') or ':' in filename or any(part in ('','..','.') for part in filename.split('/')): filename = ''
                    yield {'source': name, 'kind': 'display-name', 'path': filename,
                        'key': 'character:' + binding, 'context': 'character-display-name'}

            def collect_catalog(self):
                self.catalog = []
                self.catalog_set = set()
                self.catalog_active_sources = set()
                self.catalog_name_origins = {}
                self.text_decorators = set()
                self.character_constructor_trusted = False
                self.catalog_skipped = 0
                self.language_excluded = set()
                self.nonoriginal_nodes = set()
                self.catalog_marked = False
                self.catalog_finished = False
                self.catalog_errors = []
                self.catalog_error_keys = set()
                self.catalog_error_count = 0
                self.dialogue_contexts = {}
                self.dialogue_roles = {}
                self.dialogue_speakers = set()
                self.dialogue_roles_ready = False
                self.catalog_iter = self.catalog_steps() if self.catalog_requested else None

            def catalog_issue(self, stage, node, error):
                # A malformed/custom node must not silently end the directory.
                # Keep diagnostics bounded and omit dialogue and Python contents.
                key = (stage, id(node), type(error).__name__)
                if key in self.catalog_error_keys: return
                self.catalog_error_keys.add(key)
                self.catalog_error_count += 1
                if len(self.catalog_errors) >= 32: return
                try: filename = getattr(node, 'filename', '')
                except Exception: filename = ''
                if not isinstance(filename, string_type): filename = ''
                self.catalog_errors.append({'stage': stage, 'nodeType': type(node).__name__,
                    'file': filename.replace('\\', '/')[-240:], 'error': type(error).__name__})

            def catalog_node_texts(self, node):
                try: return self.node_texts(node)
                except Exception as error:
                    self.catalog_issue('node-text', node, error)
                    return []

            def collect_dialogue_speakers(self, nodes):
                # A static, unique standard Character declaration is attribution,
                # never evidence of gender. Do not eval who/conditions or inspect
                # saves. Rebound/dynamic/custom bindings keep the existing path.
                declarations, writes, unsafe = {}, {}, False
                for node in nodes:
                    try:
                        filename = getattr(node, 'filename', '').replace('\\', '/')
                        if '/renpy/common/' in '/' + filename or '/tl/' in '/' + filename or id(node) in self.nonoriginal_nodes: continue
                        if filename.rsplit('/', 1)[-1] in ('zz_fusion_translation.rpy', 'zz_fusion_translation.rpyc'): continue
                        if getattr(node, 'store', 'store') not in ('store', None): continue
                        node_bindings = []
                        for origin in self.display_names(node):
                            binding = origin['key'][len('character:'):]
                            node_bindings.append(binding)
                            declarations[binding] = declarations.get(binding, 0) + 1
                        if type(node).__name__ in ('Define', 'Default'):
                            binding = getattr(node, 'varname', '')
                            writes[binding] = writes.get(binding, 0) + 1
                        code = getattr(node, 'code', None)
                        source = getattr(code, 'source', None)
                        if isinstance(source, string_type):
                            # Ren'Py wraps python hide in a function; its source
                            # still has exec grammar. Scan conservatively for
                            # possible writes instead of treating hide as an
                            # invalid Python parse mode or skipping the block.
                            mode = getattr(code, 'mode', 'exec')
                            tree = ast.parse(source, mode='exec' if mode == 'hide' else mode)
                            for item in ast.walk(tree):
                                if isinstance(item, ast.Name) and isinstance(item.ctx, (ast.Store, ast.Del)):
                                    writes[item.id] = writes.get(item.id, 0) + 1
                                elif isinstance(item, (ast.FunctionDef, ast.ClassDef)) or type(item).__name__ == 'AsyncFunctionDef':
                                    writes[item.name] = writes.get(item.name, 0) + 1
                                elif isinstance(item, (ast.Import, ast.ImportFrom)):
                                    for alias in item.names:
                                        name = alias.asname or alias.name.split('.')[0]
                                        writes[name] = writes.get(name, 0) + 1
                                        if name == '*': unsafe = True
                                elif isinstance(item, ast.Attribute) and isinstance(item.ctx, (ast.Store, ast.Del)):
                                    writes[item.attr] = writes.get(item.attr, 0) + 1
                                elif isinstance(item, ast.Call):
                                    name = item.func.id if isinstance(item.func, ast.Name) else ''
                                    if name in ('exec', 'execfile', 'eval', 'setattr', 'delattr', 'globals', 'locals', 'vars'): unsafe = True
                                    if name == 'Character':
                                        # Custom kinds/callbacks/dynamic arguments
                                        # cannot establish a fixed standard actor.
                                        safe = len(item.args) <= 1
                                        for keyword in item.keywords:
                                            if keyword.arg == 'name': continue
                                            if keyword.arg not in ('dynamic','color','who_color','what_color','who_style','what_style','window_style','image','voice_tag','who_prefix','who_suffix','what_prefix','what_suffix'):
                                                safe = False; break
                                            try: value = ast.literal_eval(keyword.value)
                                            except Exception: safe = False; break
                                            if keyword.arg == 'dynamic' and value is not False: safe = False; break
                                            if not isinstance(value, (string_type, bool, int, float, type(None))): safe = False; break
                                        if not safe:
                                            for binding in node_bindings: writes[binding] = writes.get(binding, 0) + 1
                                elif type(item).__name__ == 'Exec': unsafe = True
                                elif isinstance(item, ast.Subscript) and isinstance(item.ctx, (ast.Store, ast.Del)):
                                    owner = item.value
                                    if isinstance(owner, ast.Call) and isinstance(owner.func, ast.Name) and owner.func.id in ('globals', 'locals', 'vars'): unsafe = True
                                    if isinstance(owner, ast.Attribute) and owner.attr == '__dict__': unsafe = True
                    except Exception:
                        # Unreadable Python cannot prove immutable attribution.
                        unsafe = True
                    yield None
                self.dialogue_speakers = set(binding for binding in declarations
                    if declarations[binding] == 1 and writes.get(binding, 0) == 1) if not unsafe and self.character_constructor_trusted and 'Character' not in writes else set()

            def dialogue_speaker(self, node):
                who = getattr(node, 'who', None)
                if who is None: return 'narrator'
                if isinstance(who, string_type) and who in getattr(self, 'dialogue_speakers', set()): return who
                return None

            def build_dialogue_contexts(self, nodes):
                # Fixed AST neighbours, not traversal order or predicted branches.
                # Repeated strings and multiple incoming edges remain context-free.
                parents, counts = {}, {}
                for node in nodes:
                    try:
                        for text in self.catalog_node_texts(node):
                            if isinstance(text, string_type): counts[text] = counts.get(text, 0) + 1
                        successor = getattr(node, 'next', None)
                        if successor is not None:
                            key = id(successor)
                            parents[key] = node if key not in parents else None
                    except Exception as error:
                        self.catalog_issue('context-links', node, error)
                    yield None
                contexts, roles = {}, {}
                transparent = ('Show', 'Hide', 'With', 'Voice', 'Pass', 'Window', 'Translate', 'EndTranslate')
                for node in nodes:
                    try:
                        if not isinstance(node, renpy.ast.Say): continue
                        source = getattr(node, 'what', None)
                        if not isinstance(source, string_type) or not self.valid(source) or counts.get(source) != 1: continue
                        previous, previous_speakers, current, seen = [], [], node, set()
                        for step in range(32):
                            current = parents.get(id(current))
                            if current is None or id(current) in seen: break
                            seen.add(id(current))
                            if getattr(current, 'filename', '') != getattr(node, 'filename', ''): break
                            if isinstance(current, renpy.ast.Say):
                                text = getattr(current, 'what', None)
                                if not isinstance(text, string_type) or not self.valid(text) or len(text) > 600: break
                                previous.append(text)
                                previous_speakers.append(self.dialogue_speaker(current))
                                if len(previous) == 3: break
                            elif type(current).__name__ not in transparent: break
                        if previous and sum(len(text) for text in previous) <= 1200:
                            contexts[source] = list(reversed(previous))
                            speaker = self.dialogue_speaker(node)
                            # At least one earlier line must belong to this exact
                            # static binding. Unknown speakers are not merged.
                            if speaker and speaker != 'narrator' and speaker in previous_speakers:
                                bindings = {speaker: 'r0'}
                                def opaque(value):
                                    if value is None: return 'unknown'
                                    if value == 'narrator': return value
                                    if value not in bindings: bindings[value] = 'r' + str(len(bindings))
                                    return bindings[value]
                                roles[source] = {'contract': 'renpy-ast-speaker-v1', 'speaker': 'r0',
                                    'previousSpeakers': [opaque(value) for value in reversed(previous_speakers)]}
                    except Exception as error:
                        self.catalog_issue('context-neighbours', node, error)
                    yield None
                self.dialogue_contexts = contexts
                self.dialogue_roles = roles

            def context_fields(self, texts):
                rows, size, seen = [], 0, set()
                for source in texts:
                    if source in seen: continue
                    seen.add(source)
                    previous = self.dialogue_contexts.get(source)
                    if not previous: continue
                    row = {'source': source, 'unique': True, 'previous': previous}
                    attribution = getattr(self, 'dialogue_roles', {}).get(source)
                    if attribution and getattr(self, 'dialogue_roles_requested', False): row['speakerAttribution'] = attribution
                    cost = len(json.dumps(row, ensure_ascii=True))
                    if size + cost > 12000: continue
                    rows.append(row); size += cost
                    if len(rows) == 8: break
                result = {'dialogueContextContract': 'renpy-static-unique-1', 'dialogueContexts': rows}
                if getattr(self, 'dialogue_roles_requested', False):
                    result.update({'speakerAttributionContract': 'renpy-ast-speaker-v1',
                        'speakerAttributionReady': self.dialogue_roles_ready})
                return result

            def catalog_steps(self):
                def add(text, active_source=False):
                    if not isinstance(text, string_type): return
                    if len(text) > 6000:
                        self.catalog_skipped += 1
                    elif self.valid(re.sub(r'\[[^\]]*\]|\{[^}]*\}', '', text)):
                        if active_source: self.catalog_active_sources.add(text)
                        if text not in self.catalog_set:
                            self.catalog_set.add(text)
                            self.catalog.append(text)
                script = renpy.game.script
                nodes = list(getattr(script, 'all_stmts', None) or script.namemap.values())
                translated_types = tuple(t for t in (getattr(renpy.ast, name, None) for name in ('Translate','TranslateSay','TranslateBlock','TranslateEarlyBlock','TranslatePython','TranslateString')) if t is not None)
                # all_stmts also contains translated children, even when their
                # filename is an ordinary script. Mark structural descendants
                # without following next/call/jump links into original statements.
                todo, active_nodes, marked = [], set(), set()
                selected_roots = set()
                active_language = self.active_text_language
                # Ren'Py selects a translation for each identifier, then falls
                # back to that identifier's default block when the pack lacks it.
                # A saved language preference alone does not prove translated
                # text exists (partial packs and removed packs are both valid).
                translator = getattr(script, 'translator', None)
                language_translates = getattr(translator, 'language_translates', None)
                default_translates = getattr(translator, 'default_translates', None)
                if not isinstance(language_translates, dict):
                    language_translates = {}
                    for node in nodes:
                        try:
                            identifier = getattr(node, 'identifier', None)
                            language = getattr(node, 'language', None)
                            if identifier is not None and language is not None:
                                language_translates[(identifier, language)] = node
                        except Exception as error: self.catalog_issue('language-index', node, error)
                        yield None
                if not isinstance(default_translates, dict):
                    default_translates = {}
                    for node in nodes:
                        try:
                            identifier = getattr(node, 'identifier', None)
                            if identifier is not None and getattr(node, 'language', None) is None:
                                default_translates[identifier] = node
                        except Exception as error: self.catalog_issue('default-index', node, error)
                        yield None
                if active_language is not None:
                    for identifier, original in list(default_translates.items()):
                        try:
                            key = identifier.replace('.', '_') if isinstance(identifier, string_type) else identifier
                            selected = language_translates.get((key, active_language))
                            if selected is None:
                                alternate = getattr(original, 'alternate', None)
                                if alternate: selected = language_translates.get((key, alternate))
                            if selected is not None and selected is not original:
                                selected_roots.add(id(selected))
                                todo.append((original, False))
                                todo.append((selected, True))
                        except Exception as error: self.catalog_issue('translation-selection', original, error)
                        yield None
                for node in nodes:
                    try:
                        if isinstance(node, translated_types) and getattr(node, 'language', None) is not None:
                            todo.append((node, id(node) in selected_roots or getattr(node, 'language', None) == active_language))
                    except Exception as error: self.catalog_issue('translation-roots', node, error)
                    yield None
                while todo and len(marked) < 1000000:
                    node, is_active = todo.pop()
                    if node is None or id(node) in marked: continue
                    marked.add(id(node))
                    if is_active: active_nodes.add(id(node))
                    else: self.nonoriginal_nodes.add(id(node))
                    try:
                        block = getattr(node, 'block', None)
                        if isinstance(block, (list, tuple)): todo.extend((child, is_active) for child in block)
                        for entry in getattr(node, 'entries', ()) or ():
                            if isinstance(entry, (list, tuple)) and len(entry) > 1 and isinstance(entry[1], (list, tuple)):
                                todo.extend((child, is_active) for child in entry[1])
                        if isinstance(node, renpy.ast.Menu):
                            for item in node.items or ():
                                if len(item) > 2 and item[2]: todo.extend((child, is_active) for child in item[2])
                    except Exception as error: self.catalog_issue('translation-children', node, error)
                    yield None
                if todo: self.catalog_issue('translation-limit', None, RuntimeError('translation traversal limit'))
                self.catalog_marked = True
                for step in self.collect_text_decorators(nodes): yield None
                # The opening graph goes first. The remaining loaded scripts include
                # unvisited branches and compiled/archive scripts, without playing them.
                import itertools
                seen = set()
                extras = []
                for node in itertools.chain(self.walk_story([script.namemap.get('start')], 3000), nodes):
                    if id(node) in seen or id(node) in self.nonoriginal_nodes: continue
                    seen.add(id(node))
                    try:
                        filename = getattr(node, 'filename', '')
                        if not isinstance(filename, string_type): raise TypeError('node filename')
                        filename = filename.replace('\\', '/')
                    except Exception as error:
                        self.catalog_issue('node-source', node, error)
                        continue
                    if '/tl/' in '/' + filename and id(node) not in active_nodes: continue
                    # These are actual Say/Menu statements in the selected AST
                    # view. The engine's default language is None, not an ISO
                    # code; that does not make its original story an unused pack.
                    # Arbitrary Python/screen data and interpolation arguments
                    # remain unproven and wait for their own runtime evidence.
                    active_source = isinstance(node, (renpy.ast.Say, renpy.ast.Menu))
                    for text in self.catalog_node_texts(node):
                        if not isinstance(text, string_type): continue
                        add(text, active_source)
                        active_fragments = set(self.active_inline_literals(text)) if active_source else set()
                        for fragment in self.inline_literals(text): add(fragment, fragment in active_fragments)
                        yield None
                    extras.append(node)
                    yield None
                if getattr(self, 'dialogue_roles_requested', False):
                    for step in self.collect_dialogue_speakers(nodes): yield None
                else:
                    self.dialogue_speakers = set()
                for step in self.build_dialogue_contexts(extras): yield None
                self.dialogue_roles_ready = True
                # Publish all ordinary dialogue/choices before expensive screen
                # expressions and Python data tables. They need no event execution.
                for node in extras:
                    try:
                        filename = getattr(node, 'filename', '').replace('\\', '/')
                        if isinstance(node, renpy.ast.Screen):
                            for text, active_source in self.screen_texts(node.screen, True):
                                add(text, active_source)
                                yield None
                        if '/renpy/common/' in '/' + filename:
                            for text in self.common_ui_texts(getattr(node, 'code', None)):
                                add(text, True)
                                yield None
                        elif filename.rsplit('/', 1)[-1] not in ('zz_fusion_translation.rpy', 'zz_fusion_translation.rpyc'):
                            for text in self.python_texts(getattr(node, 'code', None)):
                                add(text)
                                yield None
                            for origin in self.display_names(node):
                                text = origin['source']
                                add(text, True)
                                if text in self.catalog_set and len(self.catalog_name_origins) < 10000:
                                    self.catalog_name_origins.setdefault(text, origin)
                                yield None
                    except Exception as error: self.catalog_issue('node-expressions', node, error)
                    yield None

            def advance_catalog(self, budget=0.004):
                if not self.catalog_requested or not (self.enabled or self.preparing): return
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
                        self.dialogue_roles_ready = True
                        self.catalog_finished = True
                        self.catalog_iter = None
                    except Exception as error:
                        self.catalog_issue('catalog-scan', None, error)
                        self.dialogue_roles_ready = True
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
                    if self.enabled and self.custom_font:
                        # Clone only this render segment. Styles, saves, font size,
                        # bold/italic and the game's reusable segment stay intact.
                        # Keep icon/unsupported glyph runs in their original face.
                        start = 0
                        while start < len(value):
                            use_custom = self.has_glyph(original, value[start], font=self.custom_font) is True
                            end = start + 1
                            while end < len(value) and (self.has_glyph(original, value[end], font=self.custom_font) is True) == use_custom:
                                end += 1
                            selected = original
                            if use_custom:
                                selected = renpy.text.text.TextSegment(original)
                                selected.font = self.custom_font
                            yield selected, value[start:end]
                            start = end
                        continue
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
                self.pump_font_preferences()
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
                if self.enabled:
                    self.cursor_health()
                if self.previous_periodic is not None:
                    self.previous_periodic()
                current = getattr(renpy.game.context(), 'current', None)
                if self.enabled and self.catalog_marked and current != self.last_current:
                    self.last_current = current
                    root = renpy.game.script.namemap.get(current)
                    texts = []
                    for node in self.walk_story([root], 800):
                        for text in self.catalog_node_texts(node):
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

            def cancel_font_request(self, job, message):
                with self.lock:
                    if job is None or job.get('result') is not None:
                        return
                    job['cancelled'] = True
                    job['error'] = message
                    if self.font_pending is job:
                        self.font_pending = None
                    job['done'].set()

            def font_request_current(self, job):
                if (job['cancelled'] or self.font_pending is not job or not self.connected
                    or job['connection'] != self.font_connection or time.time() >= job['deadline']):
                    return False
                try:
                    return self.font_peer_check is not None and self.font_peer_check()
                except Exception:
                    return False

            def request_font_preferences(self, request):
                # The pipe worker never touches the renderer's font/glyph caches.
                with self.lock:
                    if not self.connected or self.font_peer_check is None or not self.font_peer_check():
                        raise Exception('Font connection unavailable')
                    if self.font_pending is not None:
                        raise Exception('A font request is already pending')
                    job = {'request': dict(request), 'connection': self.font_connection,
                        'deadline': time.time() + self.font_request_seconds, 'done': threading.Event(),
                        'cancelled': False, 'processing': False, 'result': None, 'error': None}
                    self.font_pending = job
                while not job['done'].wait(0.05):
                    with self.lock:
                        if not self.font_request_current(job):
                            self.cancel_font_request(job, 'Font request expired or connection closed')
                if job['error'] is not None:
                    raise Exception(job['error'])
                return job['result']

            def load_custom_font(self, selected):
                # These named arguments match the shipped Ren'Py 7/8 font.py
                # APIs (9, 12 and 13 parameters); unknown variants fail closed.
                loader = renpy.text.font.get_font
                code = getattr(loader, '__code__', getattr(loader, 'func_code', None))
                if code is None:
                    raise Exception('RenPy font loader signature unavailable')
                names = code.co_varnames[:code.co_argcount]
                values = {'fn': selected, 'size': 24, 'bold': False, 'italics': False,
                    'outline': 0, 'antialias': True, 'vertical': False, 'hinting': True,
                    'scale': 1.0, 'shaper': 'freetype', 'instance': None, 'axis': None, 'features': None}
                if not names or names[0] != 'fn' or any(name not in values for name in names):
                    raise Exception('Unsupported RenPy font loader signature')
                face = loader(**dict((name, values[name]) for name in names))
                if face is None or not callable(getattr(face, 'glyphs', None)):
                    raise Exception('RenPy did not load a usable font face')
                face.glyphs(u'A\u0410\u4e2d')

            def pump_font_preferences(self):
                # Called only by Ren'Py's periodic main-thread callback. Load
                # first; commit only if the requesting connection still exists.
                with self.lock:
                    job = self.font_pending
                    if job is None or job['processing']:
                        return
                    if not self.font_request_current(job):
                        self.cancel_font_request(job, 'Font request expired or connection closed')
                        return
                    job['processing'] = True
                try:
                    request = job['request']
                    mode = request.get('mode')
                    if mode not in ('default', 'custom'):
                        raise Exception('Invalid font mode')
                    selected = None
                    if mode == 'custom':
                        selected = request.get('path', '').replace('\\', '/')
                        if not selected or not os.path.isabs(selected) or not os.path.isfile(selected):
                            raise Exception('Font file unavailable')
                        if os.path.getsize(selected) <= 0 or os.path.getsize(selected) > 33554432:
                            raise Exception('Font file exceeds size limit')
                        with open(selected, 'rb') as font_file:
                            if font_file.read(4) not in (b'\x00\x01\x00\x00', b'OTTO', b'ttcf', b'true'):
                                raise Exception('Unsupported font file')
                        self.load_custom_font(selected)
                    with self.lock:
                        if not self.font_request_current(job):
                            self.cancel_font_request(job, 'Font request expired or connection closed')
                            return
                        self.custom_font = selected
                        self.glyph_maps.clear()
                        if self.enabled:
                            self.refresh_all = True
                        job['result'] = {'accepted': True, 'mode': mode, 'enabled': self.enabled,
                            'fontLoaded': selected is not None, 'writebackPending': self.enabled,
                            'restorePending': self.enabled and mode == 'default', 'restartRequired': False}
                        self.font_pending = None
                        job['done'].set()
                except Exception as error:
                    self.cancel_font_request(job, 'Font load failed: ' + type(error).__name__)

            def command(self, request):
                if request.get('op') == 'fontPreferences':
                    return self.request_font_preferences(request)
                if request.get('op', '').startswith('data') or request.get('op') in ('lifecycleState','prepareRestart'):
                    data = getattr(renpy, '_fusion_data', None)
                    if data is None:raise Exception('此游戏的修改数据尚未适配，游戏和翻译可继续使用。')
                    return data.request(request)
                with self.lock:
                    op = request.get('op')
                    if op == 'translationPrepare':
                        roles_requested = request.get('speakerAttributionContract') == 'renpy-ast-speaker-v1'
                        newly_requested_roles = roles_requested and not getattr(self, 'dialogue_roles_requested', False)
                        self.dialogue_roles_requested = roles_requested
                        if not self.catalog_requested or newly_requested_roles:
                            self.catalog_requested = True
                            self.collect_catalog()
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
                        if not self.catalog_requested:
                            self.catalog_requested = True
                            self.collect_catalog()
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
                        if getattr(self, 'dialogue_roles_requested', False) and not self.dialogue_roles_ready:
                            # New role-aware hosts wait for attribution before
                            # dispatching early story pages. Legacy hosts and
                            # unaffected targets retain their existing paging.
                            response = {'texts': [], 'next': 0, 'total': 0, 'scanning': True, 'skipped': 0,
                                'epoch': self.epoch, 'catalogComplete': False, 'catalogErrorCount': self.catalog_error_count}
                            response.update(self.context_fields([]))
                            return response
                        offset = max(0, min(len(self.catalog), int(request.get('cursor', 0))))
                        texts, size = [], 0
                        for text in self.catalog[offset:offset + 512]:
                            cost = len(json.dumps(text, ensure_ascii=True))
                            if texts and size + cost > 42000: break
                            if texts and getattr(self, 'dialogue_roles_requested', False) and self.context_fields([text])['dialogueContexts']:
                                # A bounded context page must not omit the role
                                # of a source dispatched in that same page.
                                trial = self.context_fields(texts + [text])['dialogueContexts']
                                if not any(row['source'] == text for row in trial): break
                            texts.append(text)
                            size += cost
                        response = {'texts': texts, 'next': offset + len(texts), 'total': len(self.catalog), 'scanning': self.catalog_iter is not None, 'skipped': self.catalog_skipped, 'sourceLanguage': self.source_language, 'skippedByLanguage': len(self.language_excluded), 'epoch': self.epoch, 'activeSourceTexts': [text for text in texts if text in self.catalog_active_sources]}
                        response.update({'catalogComplete': self.catalog_finished and self.catalog_error_count == 0,
                            'catalogErrorCount': self.catalog_error_count, 'catalogErrors': list(self.catalog_errors)})
                        response.update({'nameOriginContract': 'fusion-display-names-1', 'nameOriginEngine': 'renpy',
                            'nameOrigins': [self.catalog_name_origins[text] for text in texts if text in self.catalog_name_origins]})
                        response.update(self.context_fields(texts))
                        return response
                    if op == 'translationPoll':
                        texts = list(self.pending.keys())[:128]
                        for text in texts:
                            self.pending.pop(text, None)
                        response = {'epoch': self.epoch, 'texts': texts, 'ahead': self.ahead, 'activeTextLanguage': self.locale_language(self.active_text_language)}
                        response.update(self.context_fields(texts + self.ahead))
                        return response
                    if op == 'translationApply':
                        if request.get('epoch') != self.epoch:
                            return {'stale': True}
                        count = 0
                        accepted_pairs = []
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
                                accepted_pairs.append((source, text))
                                count += 1
                        return {'applied': count, 'epoch': self.epoch, 'sourcePairsHash': self.stored_pairs_hash(accepted_pairs)}
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
                    peer_check = None
                    try:
                        import ctypes, msvcrt
                        peek = ctypes.windll.kernel32.PeekNamedPipe
                        peek.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32,
                            ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
                        peek.restype = ctypes.c_int
                        handle = msvcrt.get_osfhandle(fd)
                        peer_check = lambda: bool(peek(ctypes.c_void_p(handle), None, 0, None, None, None))
                    except Exception:
                        pass
                    def send(value):
                        data = (json.dumps(value, ensure_ascii=True) + '\n').encode('utf8')
                        while data:
                            written = os.write(fd, data)
                            if written <= 0:
                                raise IOError('Connection closed')
                            data = data[written:]
                    send({'hello': 1, 'secret': secret, 'pid': os.getpid(), 'reconnect': 156})
                    with self.lock:
                        self.connected = True
                        self.font_connection += 1
                        self.font_peer_check = peer_check
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
                            detailed = request.get('op','').startswith('data') or request.get('op') == 'fontPreferences'
                            message = ex.args[0] if detailed and ex.args else 'Translation request failed'
                            response = {'id': request.get('id'), 'ok': False, 'error': message}
                        send(response)
                except Exception:
                    pass
                finally:
                    if fd is not None:
                        os.close(fd)
                    data = getattr(renpy, '_fusion_data', None)
                    if data is not None:data.revoke()
                    with self.lock:
                        self.cancel_font_request(self.font_pending, 'Font connection closed')
                        self.font_peer_check = None
                        self.font_connection += 1
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


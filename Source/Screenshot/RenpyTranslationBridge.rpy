# Fusion's optional runtime bridge. No game script, variable, menu action or style is rewritten.
init 999 python hide:
    def _fusion_start():
        import os
        if not os.environ.get('FUSION_RENPY_PIPE') or not os.environ.get('FUSION_RENPY_SECRET'):
            return
        import renpy, threading, collections, weakref, json, ast, re, struct
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
                self.catalog = []
                self.catalog_set = set()
                self.catalog_skipped = 0
                self.source_language = 'Auto'
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
                self.glyph_fallback = os.environ.get('FUSION_RENPY_GLYPH_FALLBACK','1') != '0'
                self.font_maps = {}
                self.glyph_maps = {}

            def valid(self, text):
                return isinstance(text, string_type) and 1 < len(text) <= 6000 and any(c.isalpha() for c in text)

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
                aliases = {'english':'en','eng':'en','japanese':'ja','jp':'ja','jpn':'ja','chinese':'zh','simplifiedchinese':'zh','simplified_chinese':'zh','schinese':'zh','korean':'ko','kr':'ko','kor':'ko'}
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
                selected = '*' if mode == 'Mixed' else self.locale_language(mode)
                if mode == 'Auto':
                    selected = 'en' if 'en' in languages else ('ja' if 'ja' in languages else sorted(languages)[0])
                kept = []
                for language, value in entries:
                    if selected == '*' or language == selected: kept.append(value)
                    else:
                        for text in self.literal_values(value, False):
                            if self.valid(text): self.language_excluded.add(text)
                return kept

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
                if isinstance(tree, ast.Call) and isinstance(tree.func, ast.Name) and tree.func.id in ('_', '__') and len(tree.args) == 1:
                    for text in self.literal_values(tree.args[0], select_language): yield text
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
                        if name in ('_', '__', 'notify', 'Text', 'text', 'textbutton', 'label'):
                            expressions = node.args[:1]
                        elif name == 'say': expressions = node.args[1:2]
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
                todo = [node for node in nodes if isinstance(node, translated_types) and getattr(node, 'language', None) is not None]
                while todo and len(self.nonoriginal_nodes) < 1000000:
                    node = todo.pop()
                    if node is None or id(node) in self.nonoriginal_nodes: continue
                    self.nonoriginal_nodes.add(id(node))
                    block = getattr(node, 'block', None)
                    if isinstance(block, (list, tuple)): todo.extend(block)
                    for entry in getattr(node, 'entries', ()):
                        if isinstance(entry, (list, tuple)) and len(entry) > 1 and isinstance(entry[1], (list, tuple)): todo.extend(entry[1])
                    if isinstance(node, renpy.ast.Menu):
                        for item in node.items:
                            if len(item) > 2 and item[2]: todo.extend(item[2])
                # The opening graph goes first. The remaining loaded scripts include
                # unvisited branches and compiled/archive scripts, without playing them.
                opening = list(self.walk_story([script.namemap.get('start')], 3000))
                seen = set()
                for node in opening + sorted(nodes, key=lambda n: (getattr(n, 'filename', ''), getattr(n, 'linenumber', 0))):
                    if id(node) in seen or id(node) in self.nonoriginal_nodes: continue
                    seen.add(id(node))
                    filename = getattr(node, 'filename', '').replace('\\', '/')
                    if '/tl/' in '/' + filename: continue
                    for text in self.node_texts(node): add(text)
                    if isinstance(node, renpy.ast.Screen):
                        for text in self.screen_texts(node.screen): add(text)
                    if '/renpy/common/' not in '/' + filename and filename.rsplit('/', 1)[-1] not in ('zz_fusion_translation.rpy', 'zz_fusion_translation.rpyc'):
                        for text in self.python_texts(getattr(node, 'code', None)): add(text)

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
                with self.lock:
                    mapped = [self.resolve(t, keys) for t in original]
                ready = set()
                for before, after in zip(original, mapped):
                    if isinstance(before, string_type) and before != after:
                        ready.update(self.fragments(after))
                self.pretranslated[displayable] = ready
                changed = displayable.text != mapped
                displayable.text = mapped
                if changed:
                    displayable.dirty = True
                    renpy.display.render.redraw(displayable, 0)
                return changed

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

            def set_text(self, displayable, text, scope=None, substitute=False, update=True):
                if isinstance(displayable, renpy.display.behavior.Input) or getattr(displayable, 'tokenized', False) or getattr(displayable, 'locked', False):
                    return self.original_set_text(displayable, text, scope, substitute, update)
                text=[self.display_string(value) for value in text] if isinstance(text,list) else self.display_string(text)
                previous = getattr(displayable, 'text', None)
                entry = self.scopes.get(displayable)
                # Scope checks must compare original resolved text with original text,
                # not Chinese with English (which would force perpetual redraws).
                if entry is not None: displayable.text = entry[0]
                try:
                    result = self.original_set_text(displayable, text, scope, substitute, update)
                    resolved = displayable.text
                finally:
                    displayable.text = previous
                if update:
                    original = list(resolved)
                    source = text if isinstance(text, list) else [text]
                    keys = set(t for t in source + original if self.valid(t))
                    result = self.refresh_text(displayable, original, keys) or result
                    self.scopes[displayable] = (original, keys)
                return result

            def tags(self, displayable, tokens):
                # The engine first resolves its own interpolation, custom tags and filters.
                # Replace only final TEXT tokens; original templates and scope checks stay intact.
                result = self.original_tags(tokens) if self.tags_static else self.original_tags(displayable, tokens)
                if isinstance(displayable, renpy.display.behavior.Input):
                    return result
                keys = set(self.scopes.get(displayable, (None, set()))[1])
                prepared = self.pretranslated.get(displayable, set())
                output = []
                with self.lock:
                    for kind, value in result:
                        if kind == renpy.text.text.TEXT and self.valid(value) and value not in prepared:
                            keys.add(value)
                            if self.enabled:
                                original = value
                                value = self.resolve(value, keys)
                                if original not in self.cache and original not in self.resolved:
                                    if len(self.pending) < 512:
                                        self.pending[value] = True
                                    # Keep the original language until a real translation exists.
                        output.append((kind, value))
                self.dependencies[displayable] = keys
                return output

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

            def has_glyph(self, segment, character, fallback=False):
                name=self.font if fallback else segment.font
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
                    if not self.glyph_fallback or not self.font:
                        yield original, value
                        continue
                    def missing(c):
                        n = ord(c)
                        cjk=0x2e80 <= n <= 0x9fff or 0xf900 <= n <= 0xfaff or 0xff00 <= n <= 0xffef or 0x20000 <= n <= 0x323af
                        # Preserve the original font when supported or coverage is
                        # unknown; fallback only for a confirmed missing glyph.
                        try:return cjk and self.has_glyph(original,c) is False and self.has_glyph(original,c,True) is True
                        except Exception:return False
                    start = 0
                    while start < len(value):
                        fallback = missing(value[start])
                        end = start + 1
                        while end < len(value) and missing(value[end]) == fallback:
                            end += 1
                        selected = original
                        if fallback:
                            selected = renpy.text.text.TextSegment(original)
                            selected.font = self.font
                        yield selected, value[start:end]
                        start = end

            def periodic(self):
                if self.previous_periodic is not None:
                    self.previous_periodic()
                current = getattr(renpy.game.context(), 'current', None)
                if self.enabled and current != self.last_current:
                    self.last_current = current
                    root = renpy.game.script.namemap.get(current)
                    texts = []
                    for node in self.walk_story([root], 800):
                        texts.extend(t for t in self.node_texts(node) if self.valid(t))
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
                        # Keep rendering original text until all cached translations
                        # have been primed and the desktop explicitly enables them.
                        self.enabled = False
                        self.epoch += 1
                        self.cache.clear()
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
                        self.enabled = True
                        self.refresh_all = True
                        self.ready.set()
                        return {'epoch': self.epoch}
                    if op == 'translationDisable':
                        self.enabled = False
                        self.ready.set()
                        self.pending.clear()
                        self.refresh_all = True
                        return {'epoch': self.epoch}
                    if op == 'translationCatalog':
                        offset = max(0, min(len(self.catalog), int(request.get('cursor', 0))))
                        texts, size = [], 0
                        for text in self.catalog[offset:offset + 128]:
                            cost = len(json.dumps(text, ensure_ascii=True))
                            if texts and size + cost > 42000: break
                            texts.append(text)
                            size += cost
                        return {'texts': texts, 'next': offset + len(texts), 'total': len(self.catalog), 'skipped': self.catalog_skipped, 'sourceLanguage': self.source_language, 'skippedByLanguage': len(self.language_excluded)}
                    if op == 'translationPoll':
                        texts = list(self.pending.keys())[:128]
                        for text in texts:
                            self.pending.pop(text, None)
                        return {'epoch': self.epoch, 'texts': texts, 'ahead': self.ahead}
                    if op == 'translationApply':
                        if request.get('epoch') != self.epoch:
                            return {'stale': True}
                        count = 0
                        for pair in request.get('entries', [])[:128]:
                            source, text = pair.get('source'), pair.get('text')
                            if self.valid(source) and isinstance(text, string_type) and 0 < len(text) <= 12000:
                                if self.cache.get(source) != text:
                                    self.cache[source] = text
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
                        return {'enabled': self.enabled, 'entries': len(self.cache), 'savedir': config.savedir, 'version': renpy.version_only, 'pending': len(self.pending), 'catalog': len(self.catalog)}
                    raise Exception('Unknown translation request')

            def serve(self):
                fd = None
                try:
                    fd = os.open('\\\\.\\pipe\\' + os.environ['FUSION_RENPY_PIPE'], os.O_RDWR | os.O_BINARY)
                    def send(value):
                        data = (json.dumps(value, ensure_ascii=True) + '\n').encode('utf8')
                        while data:
                            written = os.write(fd, data)
                            if written <= 0:
                                raise IOError('Connection closed')
                            data = data[written:]
                    send({'hello': 1, 'secret': os.environ['FUSION_RENPY_SECRET'], 'pid': os.getpid()})
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
                        self.enabled = False
                        self.connected = False
                        self.refresh_all = True
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
        renpy.text.text.Text.set_text = set_text
        renpy.text.text.Text.apply_custom_tags = TextTags()
        renpy.text.text.TextSegment.subsegment = subsegment
        if config.replace_text is None:
            config.replace_text = lambda text: text
        config.periodic_callback = bridge.periodic
        worker = threading.Thread(target=bridge.serve)
        worker.daemon = True
        worker.start()
        # Prime existing translations before the first menu frame, not the whole story.
        # Slow preparation and a broken connection both preserve the original text.
        if startup_translation:
            bridge.ready.wait(12.0)
    _fusion_start()

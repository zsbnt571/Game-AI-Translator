# Optional, authenticated companion to the translation bridge. No eval or game methods.
init 998 python hide:
    def _fusion_data_start():
        import os
        if not os.environ.get('FUSION_RENPY_PIPE'): return
        import renpy, threading, collections, ast, re, json, math, time, dis, types
        # Ren'Py 7 may ship the Python-future "builtins" shim. Its str is
        # not the native byte-string type returned by Python 2 AST literals.
        try: import __builtin__ as n
        except ImportError: import builtins as n
        # Ren'Py's store replaces object/list/dict/set, and its compiler also
        # rewrites container literals through these constructor names. Bridge
        # state must never become rollback/save data, especially after load.
        list, dict, set, tuple = n.list, n.dict, n.set, n.tuple
        __renpy__list__ = n.list
        __renpy__dict__ = n.dict
        __renpy__set__ = n.set
        try: integer_types = (int, long)
        except NameError: integer_types = (int,)
        try: string_types = (basestring,)
        except NameError: string_types = (str,)

        class DataError(n.Exception): pass
        class Data(n.object):
            def __init__(self):
                self.queue = collections.deque()
                self.gate = threading.Lock()
                self.generation = 0
                self.serial = 0
                self.entries = n.dict()
                self.undo = n.list()
                self.roots = set()
                self.cg = set()
                self.replays = set()
                self.classes = set()
                self.wrappers = {}
                self.memberships = set()
                self.gallery_rules={}
                self.gallery_calls={}
                self.gallery_hooks={}
                self.quit_due = None
                self.inspect_script()
                self.custom_galleries(lambda *args: None)
                self.previous = config.periodic_callback
                config.periodic_callback = self.tick
                config.after_load_callbacks.append(self.reset)
                config.start_callbacks.append(self.reset)

            def reset(self):
                self.generation += 1
                self.entries.clear()
                self.undo[:] = []

            def inspect_script(self):
                def simple_field(function, setter=False):
                    args = function.args
                    if args.vararg or args.kwarg or args.defaults or getattr(args,'kwonlyargs',()): return None
                    names = [getattr(x,'arg',getattr(x,'id',None)) for x in args.args]
                    if len(names) != (2 if setter else 1) or len(function.body)!=1: return None
                    body = function.body[0]
                    if setter:
                        if not isinstance(body,ast.Assign) or len(body.targets)!=1 or not isinstance(body.value,ast.Name) or body.value.id!=names[1]:return None
                        target=body.targets[0]
                    else:
                        if not isinstance(body,ast.Return):return None
                        target=body.value
                    if isinstance(target,ast.Attribute) and isinstance(target.value,ast.Name) and target.value.id==names[0] and target.attr.startswith('_') and not target.attr.startswith('__'):return target.attr
                    return None
                def collection_path(expression):
                    try: node=ast.parse(expression,mode='eval').body
                    except Exception:return None
                    # A screen-local tab/index is deliberately not evaluated. The
                    # registered collection is inspected, across all of its groups.
                    if isinstance(node,ast.Subscript):
                        index=node.slice.value if isinstance(node.slice,getattr(ast,'Index',())) else node.slice
                        if not isinstance(index,(ast.Name,ast.Str,ast.Num)) and not (hasattr(ast,'Constant') and isinstance(index,ast.Constant)):return None
                        node=node.value
                    path=[]
                    while isinstance(node,ast.Attribute) and len(path)<3:
                        if node.attr.startswith('_'):return None
                        path.insert(0,node.attr);node=node.value
                    if not isinstance(node,ast.Name) or node.id.startswith('_'):return None
                    return tuple([node.id]+path)
                def parse(source, gallery=False, bindings=None):
                    if not isinstance(source, string_types) or len(source)>262144: return
                    try: tree = ast.parse(source)
                    except Exception: return
                    for index,node in enumerate(ast.walk(tree)):
                        if index>=20000:break
                        if isinstance(node, ast.ClassDef):
                            self.classes.add(node.name)
                            predicates={};mutators={}
                            for method in node.body:
                                if not isinstance(method,ast.FunctionDef) or method.decorator_list or len(method.body)!=1:continue
                                args=[getattr(x,'arg',getattr(x,'id',None)) for x in method.args.args]
                                if len(args)!=2 or method.args.defaults or method.args.vararg or method.args.kwarg:continue
                                body=method.body[0]
                                expr=body.value if isinstance(body,(ast.Return,ast.Expr)) else None
                                field=None
                                if isinstance(expr,ast.Compare) and len(expr.ops)==1 and isinstance(expr.ops[0],ast.In) and isinstance(expr.left,ast.Name) and expr.left.id==args[1]:
                                    target=expr.comparators[0]
                                    if isinstance(target,ast.Attribute) and isinstance(target.value,ast.Name) and target.value.id==args[0]:predicates[method.name]=target.attr
                                if isinstance(expr,ast.Call) and isinstance(expr.func,ast.Attribute) and expr.func.attr=='add' and len(expr.args)==1 and isinstance(expr.args[0],ast.Name) and expr.args[0].id==args[1]:
                                    target=expr.func.value
                                    if isinstance(target,ast.Attribute) and isinstance(target.value,ast.Name) and target.value.id==args[0]:mutators[method.name]=target.attr
                            for predicate,field in predicates.items():
                                for mutator,target in mutators.items():
                                    if field==target and not field.startswith('_'):self.gallery_rules[(node.name,predicate)]=(field,mutator)
                            getters,setters={},{}
                            for method in node.body:
                                if not isinstance(method,ast.FunctionDef):continue
                                for decorator in method.decorator_list:
                                    if isinstance(decorator,ast.Name) and decorator.id=='property':getters[method.name]=simple_field(method)
                                    if isinstance(decorator,ast.Attribute) and decorator.attr=='setter' and isinstance(decorator.value,ast.Name):setters[decorator.value.id]=simple_field(method,True)
                            for alias,field in getters.items():
                                if field and not alias.startswith('_') and setters.get(alias)==field and len(self.wrappers)<4096:self.wrappers[(node.name,alias)]=field
                        if isinstance(node, ast.Name) and isinstance(node.ctx, ast.Store): self.roots.add(node.id)
                        if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name) and node.value.id == 'persistent':
                            if re.search(r'gallery|unlock.*cg|cg.*unlock|replay', node.attr, re.I) and not re.search(r'notify|notification|tooltip|volume|sound|enabled',node.attr,re.I): self.cg.add(node.attr)
                        if gallery and bindings and isinstance(node,ast.Compare) and len(node.ops)==1 and isinstance(node.ops[0],(ast.In,ast.NotIn)):
                            target=node.comparators[0];left=node.left
                            if isinstance(target,ast.Attribute) and isinstance(target.value,ast.Name) and target.value.id=='persistent' and not target.attr.startswith('_'):
                                if isinstance(left,ast.Attribute) and isinstance(left.value,ast.Name) and left.value.id in bindings and not left.attr.startswith('_') and len(self.memberships)<128:
                                    self.memberships.add((bindings[left.value.id],left.attr,target.attr))
                        if isinstance(node, ast.Call) and node.args:
                            func=node.func
                            if isinstance(func,ast.Attribute) and isinstance(func.value,ast.Name) and len(node.args)==1:
                                try:literal=ast.literal_eval(node.args[0])
                                except Exception:literal=None
                                if type(literal) in (n.str,type(u'')) and 0<len(literal)<=200 and len(self.gallery_calls)<2000:self.gallery_calls.setdefault((func.value.id,func.attr),set()).add(literal)
                            name=func.id if isinstance(func,ast.Name) else getattr(func,'attr','')
                            if name=='Replay' or gallery and name=='seen_label':
                                try:label=ast.literal_eval(node.args[0])
                                except Exception:continue
                                if isinstance(label,string_types) and label in renpy.game.script.namemap:self.replays.add(label)
                for node in (getattr(renpy.game.script, 'all_stmts', None) or renpy.game.script.namemap.values()):
                    filename = getattr(node, 'filename', '').replace('\\', '/')
                    if '/common/' in filename or '/tl/' in filename or 'zz_fusion_translation' in filename: continue
                    gallery = bool(re.search(r'gallery|replay|recollection', filename, re.I))
                    if isinstance(node, (renpy.ast.Default, renpy.ast.Define)):
                        if getattr(node, 'store', 'store') == 'store': self.roots.add(node.varname)
                        elif node.store == 'store.persistent' and re.search(r'gallery|unlock.*cg|cg.*unlock|replay',node.varname,re.I) and not re.search(r'notify|notification|tooltip|volume|sound|enabled',node.varname,re.I):self.cg.add(node.varname)
                    parse(getattr(getattr(node, 'code', None), 'source', None), gallery)
                    if isinstance(node, renpy.ast.Screen):
                        todo, seen = [(node.screen,{})], set()
                        while todo and len(seen) < 30000:
                            sl,bindings = todo.pop()
                            if sl is None or id(sl) in seen: continue
                            seen.add(id(sl))
                            if type(sl).__name__=='SLFor':
                                collection=collection_path(getattr(sl,'expression',''))
                                variable=getattr(sl,'variable',None)
                                if collection and isinstance(variable,string_types):bindings=dict(bindings);bindings[variable]=collection
                            context = gallery or bool(re.search(r'gallery|replay|recollection', str(node.screen.name), re.I))
                            for key, value in getattr(sl, 'keyword', ()): parse(value, context, bindings)
                            for entry in getattr(sl, 'entries', ()):
                                if isinstance(entry, (n.list, n.tuple)) and len(entry) > 1:
                                    parse(entry[0], context, bindings); todo.append((entry[1],bindings))
                            todo.extend((child,bindings) for child in (getattr(sl, 'children', ()) or ()))

            def membership_function(self,function,field):
                if type(function) is not types.FunctionType:return False
                code=getattr(function,'__code__',getattr(function,'func_code',None))
                if code is None or code.co_argcount!=2 or code.co_freevars or code.co_cellvars or code.co_flags&12:return False
                try:
                    if hasattr(dis,'get_instructions'):
                        ops=[(i.opname,i.argval) for i in dis.get_instructions(function) if i.opname not in ('RESUME','CACHE','NOP','EXTENDED_ARG')]
                    else:
                        ops=[];offset=0;raw=code.co_code
                        while offset<len(raw):
                            op=ord(raw[offset]);offset+=1;arg=None
                            if op>=dis.HAVE_ARGUMENT:
                                arg=ord(raw[offset])|(ord(raw[offset+1])<<8);offset+=2
                                if op in dis.haslocal:arg=code.co_varnames[arg]
                                elif op in dis.hasname:arg=code.co_names[arg]
                                elif op in dis.hascompare:arg=dis.cmp_op[arg]
                                else:return False
                            ops.append((dis.opname[op],arg))
                    head=[('LOAD_FAST',code.co_varnames[1]),('LOAD_FAST',code.co_varnames[0]),('LOAD_ATTR',field)]
                    return ops in (head+[('CONTAINS_OP',0),('RETURN_VALUE',None)],head+[('COMPARE_OP','in'),('RETURN_VALUE',None)])
                except Exception:return False

            def gallery_key(self,path):return json.dumps(path[1:],ensure_ascii=True)

            def gallery_set(self,path):
                instance=vars(renpy.store).get(path[1]);attributes=self.fields(instance)
                value=attributes.get(path[2]) if attributes is not None else None
                if not isinstance(value,n.set) or type(value).__module__ not in ('builtins','__builtin__','renpy.revertable','renpy.python'):raise DataError('画廊数据结构已改变，请刷新后重试。')
                return value

            def gallery_override(self,path):
                mapping=vars(renpy.store.persistent).get('_fusion_gallery_overrides')
                return n.dict.get(mapping,self.gallery_key(path)) if self.safe_dict(mapping) else None

            def gallery_write(self,path,value,restore=None):
                target=self.gallery_set(path)
                mapping=vars(renpy.store.persistent).get('_fusion_gallery_overrides')
                if mapping is None:
                    mapping={};setattr(renpy.store.persistent,'_fusion_gallery_overrides',mapping)
                if not self.safe_dict(mapping):raise DataError('画廊覆盖记录格式无效。')
                native=restore[2] if restore is not None else value
                override=restore[3] if restore is not None else value
                # The game owns its native set; a reversible predicate override controls visibility.
                if override is None:n.dict.pop(mapping,self.gallery_key(path),None)
                else:n.dict.__setitem__(mapping,self.gallery_key(path),override)

            def custom_galleries(self,add):
                for (root,method),labels in sorted(self.gallery_calls.items()):
                    instance=vars(renpy.store).get(root)
                    if self.fields(instance) is None:continue
                    cls=type(instance)
                    for (class_name,predicate),(field,mutator) in sorted(self.gallery_rules.items()):
                        if cls.__name__!=class_name or mutator!=method:continue
                        original=cls.__dict__.get(predicate)
                        installed=self.gallery_hooks.get((cls,predicate))
                        if installed is not None:
                            if original is not installed:continue
                        elif self.membership_function(original,field):
                            def make_hook(original,field):
                                bridge=self
                                def effective(owner,name):
                                    # Exact string identifiers only; unrelated calls retain the game's behavior.
                                    if type(name) in (n.str,type(u'')):
                                        for candidate in set(x[0] for x in bridge.gallery_calls):
                                            if vars(renpy.store).get(candidate) is owner:
                                                override=bridge.gallery_override(['gallery',candidate,field,name])
                                                if type(override) is n.bool:return override
                                    return original(owner,name)
                                return effective
                            hook=make_hook(original,field);setattr(cls,predicate,hook);self.gallery_hooks[(cls,predicate)]=hook
                        else:continue
                        for label in sorted(labels):
                            path=['gallery',root,field,label]
                            try:value=self.read(path)
                            except DataError:continue
                            add(path,root+'.'+field+'['+json.dumps(label,ensure_ascii=False)+']',value,'cg','实际画廊条目 · 开关覆盖游戏自动解锁，可撤销')

            def kind(self, value):
                if type(value) is n.bool: return 'bool'
                if type(value) in integer_types and abs(value) <= 9007199254740991: return 'int'
                if type(value) is n.float and not math.isnan(value) and not math.isinf(value): return 'float'
                return None

            def fields(self, value):
                # Only script-defined data objects with ordinary attribute storage.
                t = type(value)
                if t.__name__ not in self.classes or not t.__module__.startswith('store'): return None
                if any('__getattribute__' in c.__dict__ or '__setattr__' in c.__dict__ for c in t.__mro__ if not (c.__name__=='object' and c.__module__ in ('builtins','__builtin__')) and not (c.__name__=='RevertableObject' and c.__module__.startswith('renpy.'))): return None
                try: return vars(value)
                except Exception: return None

            def simple_access(self, function, field, setter=False):
                # Inspect actual code too: a property may have been replaced after
                # its source was parsed. Neither accessor is ever invoked.
                if type(function) is not types.FunctionType:return False
                code=getattr(function,'__code__',getattr(function,'func_code',None))
                if code is None or code.co_argcount!=(2 if setter else 1) or code.co_freevars or code.co_cellvars or code.co_flags & 12:return False
                try:
                    if hasattr(dis,'get_instructions'):
                        instructions=[(i.opname,i.argval) for i in dis.get_instructions(function) if i.opname not in ('RESUME','CACHE','NOP','EXTENDED_ARG')]
                    else:
                        # Python 2.7 Ren'Py: one opcode, followed by a two-byte
                        # argument when HAVE_ARGUMENT applies. No code execution.
                        instructions=[];offset=0;raw=code.co_code
                        while offset<len(raw):
                            op=ord(raw[offset]);offset+=1;arg=None
                            if op>=dis.HAVE_ARGUMENT:
                                if offset+2>len(raw):return False
                                arg=ord(raw[offset])|(ord(raw[offset+1])<<8);offset+=2
                                if op in dis.haslocal:arg=code.co_varnames[arg]
                                elif op in dis.hasname:arg=code.co_names[arg]
                                elif op in dis.hasconst:arg=code.co_consts[arg]
                                else:return False
                            instructions.append((dis.opname[op],arg))
                    owner=code.co_varnames[0]
                    if not setter:return instructions==[('LOAD_FAST',owner),('LOAD_ATTR',field),('RETURN_VALUE',None)]
                    expected=[('LOAD_FAST',code.co_varnames[1]),('LOAD_FAST',owner),('STORE_ATTR',field)]
                    return instructions in (expected+[('LOAD_CONST',None),('RETURN_VALUE',None)],expected+[('RETURN_CONST',None)])
                except Exception:return False

            def wrapper_field(self, value, alias):
                fields=self.fields(value)
                field=self.wrappers.get((type(value).__name__,alias))
                if fields is None or not field or field not in fields or any(field in cls.__dict__ for cls in type(value).__mro__):return None
                descriptor=next((cls.__dict__[alias] for cls in type(value).__mro__ if alias in cls.__dict__),None)
                if type(descriptor) is not n.property or not self.simple_access(descriptor.fget,field) or not self.simple_access(descriptor.fset,field,True):return None
                return field if self.kind(fields[field]) in ('int','float') else None

            def safe_list(self, value):
                return isinstance(value,n.list) and type(value).__module__ in ('builtins','__builtin__','renpy.revertable','renpy.python')

            def safe_dict(self, value, registered=False):
                return isinstance(value,n.dict) and (type(value).__module__ in ('builtins','__builtin__','renpy.revertable','renpy.python') or registered and type(value) is collections.defaultdict)

            def dict_items(self, value):
                return n.dict.iteritems(value) if hasattr(n.dict,'iteritems') else n.dict.items(value)

            def member_list(self, key):
                value=vars(renpy.store.persistent).get(key)
                if not self.safe_list(value) or n.list.__len__(value)>2000:raise DataError('画廊记录类型不支持，或超过 2000 项读取上限。')
                values=n.list.__getitem__(value,slice(None))
                # A game's shared progress ledger can mix scene names with numeric
                # event identifiers. Preserve all inert native values in place;
                # never call user-defined equality/serialization on objects.
                def inert(item):
                    if item is None or type(item) in integer_types+(n.bool,):return True
                    if type(item) is n.float:return not math.isnan(item) and not math.isinf(item)
                    return type(item) in (n.str,type(u'')) and len(item)<=1024
                if not all(inert(item) for item in values):raise DataError('画廊记录包含对象或非有限数值，暂不修改此记录。')
                if sum(len(item) for item in values if type(item) in (n.str,type(u'')))>2097152:raise DataError('画廊记录文本超过读取上限。')
                return value

            def guard(self, path):
                if path[0]=='gallery':
                    value=self.gallery_set(path);return ('gallery',id(value),None,self.gallery_override(path))
                if path[0]=='member':
                    value=self.member_list(path[1]);return ('member',id(value),tuple(n.list.__getitem__(value,slice(None))))
                return id(self.read(path[:-1])) if path[0] in ('store','persistent') else None

            def read(self, path):
                if path[0]=='gallery':
                    override=self.gallery_override(path)
                    return override if type(override) is n.bool else n.set.__contains__(self.gallery_set(path),path[3])
                if path[0]=='image':return bool(renpy.exports.seen_image(path[1]))
                if path[0]=='label':return bool(renpy.exports.seen_label(path[1]))
                if path[0]=='member':return n.list.__contains__(self.member_list(path[1]),path[2])
                value = renpy.store if path[0] == 'store' else renpy.store.persistent
                for kind, key in path[1:]:
                    if kind == 'a': value = vars(value)[key]
                    elif kind == 'w':
                        field=self.wrapper_field(value,key)
                        if not field:raise DataError('数值属性已改变，请刷新列表。')
                        value=vars(value)[field]
                    elif kind == 'd': value = n.dict.__getitem__(value, key)
                    else: value = n.list.__getitem__(value, key)
                return value

            def write(self, path, value):
                if path[0]=='gallery':self.gallery_write(path,value);return
                if path[0]=='image':
                    (renpy.exports.mark_image_seen if value else renpy.exports.mark_image_unseen)(path[1]);return
                if path[0]=='label':
                    (renpy.exports.mark_label_seen if value else renpy.exports.mark_label_unseen)(path[1]);return
                if path[0]=='member':
                    # Persistent membership is changed in place. The list object,
                    # unrelated values, ordering and duplicates remain intact.
                    target=self.member_list(path[1]);n.list.__setitem__(target,slice(None),value);return
                parent = self.read(path[:-1]);kind, key = path[-1]
                if kind == 'a': setattr(parent, key, value)
                elif kind == 'w':
                    field=self.wrapper_field(parent,key)
                    if not field:raise DataError('数值属性已改变，请刷新列表。')
                    setattr(parent,field,value)
                else: parent[key] = value

            def snapshot(self):
                self.serial += 1
                self.entries.clear()
                rows, seen = [], set()
                cg_diagnostics=[];member_guards={}
                budget = [0]
                pending={};pending_roots=collections.deque();pending_count=[0]
                data_truncated=[False]
                cg_budget, cg_truncated = [0], [False]
                def add(path, label, value, group, hint):
                    if len(rows)>=15000:budget[0]=15001;return
                    kind = self.kind(value)
                    if kind is None and not (group == 'cg' and value is None): return
                    token = '%d:%d:%d' % (self.generation, self.serial, len(rows))
                    row = {'id': token, 'name': label, 'value': value, 'kind': kind or 'bool', 'group': group, 'hint': hint}
                    if path[0]=='member':
                        if path[1] not in member_guards:member_guards[path[1]]=self.guard(path)
                        parent=member_guards[path[1]]
                    else:parent = self.guard(path)
                    self.entries[token] = (path, value, row['kind'], parent)
                    rows.append(row)
                    return True
                def visit(value,path,label,depth):
                    root=path[1][1]
                    if root not in pending:pending[root]=collections.deque();pending_roots.append(root)
                    if len(pending[root])>=4096 or pending_count[0]>=120000:data_truncated[0]=True;return
                    pending[root].append((value,path,label,depth));pending_count[0]+=1
                def expand(value, path, label, depth):
                    budget[0] += 1
                    if depth>8:data_truncated[0]=True;return
                    if self.kind(value):
                        # Whole words plus camel-case boundaries; never game-name rules.
                        words = re.sub(r'([a-z0-9])([A-Z])', r'\1 \2', label)
                        words = set(re.findall(u'[^\\W\\d_]+', words.lower(), re.UNICODE))
                        money = bool(words.intersection([u'money',u'cash',u'gold',u'coin',u'coins',u'currency',u'wallet',u'credit',u'credits',u'ryo',u'balance',u'金钱',u'金幣',u'金币',u'货币',u'余额',u'所持金',u'お金',u'コイン',u'돈',u'골드',u'деньги',u'золото',u'dinero',u'monedas',u'argent',u'geld',u'dinheiro']))
                        items = bool(words.intersection([u'inventory',u'item',u'items',u'bag',u'potion',u'food',u'gift',u'ticket',u'keycard',u'weapon',u'armor',u'背包',u'物品',u'道具',u'薬',u'アイテム',u'持ち物',u'아이템',u'предметы',u'inventario',u'inventaire',u'inventar']))
                        group = 'money' if money and type(value) is not n.bool else 'items' if items else 'variables'
                        add(path, label, value, group, '当前进度 · 修改后请在游戏中存档')
                        return
                    if id(value) in seen: return
                    seen.add(id(value))
                    if self.safe_dict(value):
                        for index,(key, child) in enumerate(self.dict_items(value)):
                            if len(rows)>=15000:data_truncated[0]=True;break
                            if index>=2000:data_truncated[0]=True;break
                            if type(key) not in integer_types + (n.str, type(u'')) or isinstance(key, string_types) and (key.startswith('_') or len(key)>100): continue
                            visit(child, path+[('d',key)], label+'['+json.dumps(key,ensure_ascii=False)+']', depth+1)
                    elif self.safe_list(value):
                        if n.list.__len__(value)>2000:data_truncated[0]=True
                        for index, child in enumerate(n.list.__getitem__(value,slice(0,2000))):
                            if len(rows)>=15000:data_truncated[0]=True;break
                            visit(child,path+[('l',index)],label+'[%d]'%index,depth+1)
                    else:
                        fields = self.fields(value)
                        if fields is not None:
                            for key, child in fields.items():
                                if len(rows)>=15000:data_truncated[0]=True;break
                                if not key.startswith('_') and not any(key in cls.__dict__ for cls in type(value).__mro__):visit(child,path+[('a',key)],label+'.'+key,depth+1)
                            for class_name,alias in sorted(self.wrappers):
                                if len(rows)>=15000:data_truncated[0]=True;break
                                if class_name!=type(value).__name__:continue
                                field=self.wrapper_field(value,alias)
                                if field:visit(fields[field],path+[('w',alias)],label+'.'+alias,depth+1)
                # Verified CG rows come first, with their own bounded traversal.
                # A large live-variable tree must not hide an already known gallery.
                persistent = vars(renpy.store.persistent)
                self.custom_galleries(add)
                images=set()
                gallery_type=vars(renpy.store).get('Gallery')
                if isinstance(gallery_type,type):
                    for value in vars(renpy.store).values():
                        if type(value) is not gallery_type:continue
                        for button in vars(value).get('buttons',{}).values():
                            for entry in [button]+vars(button).get('images',[]):
                                for condition in vars(entry).get('conditions',[]):
                                    fields=vars(condition)
                                    for image in fields.get('images',[]):
                                        if isinstance(image,string_types):images.add(image)
                                    expression=fields.get('condition','')
                                    try:tree=ast.parse(expression,mode='eval')
                                    except Exception:continue
                                    for node in ast.walk(tree):
                                        if isinstance(node,ast.Attribute) and isinstance(node.value,ast.Name) and node.value.id=='persistent':self.cg.add(node.attr)
                for image in sorted(images):add(['image',image],'图片记录：'+image,self.read(['image',image]),'cg','标准画廊已看过标记 · 已保存到跨存档记录')
                for label in sorted(self.replays):add(['label',label],'回想记录：'+label,self.read(['label',label]),'cg','标准回想已看过标记 · 已保存到跨存档记录')
                for key in sorted(self.cg):
                    if key.startswith('_'):continue
                    value = persistent.get(key)
                    if value is None or type(value) is n.bool:
                        add(['persistent',('a',key)],'persistent.'+key,value,'cg','跨存档进度标记 · 不等同于画廊实际可见状态')
                membership_candidates=set();membership_rows=0
                for collection,field,key in sorted(self.memberships):
                    if cg_budget[0]>=5000:cg_truncated[0]=True;break
                    try:self.member_list(key)
                    except DataError as ex:
                        cg_diagnostics.append({'kind':'unsupportedLedger','path':'persistent.'+key,'detail':ex.args[0]});continue
                    value=vars(renpy.store).get(collection[0])
                    for attribute in collection[1:]:
                        attributes=self.fields(value)
                        value=attributes.get(attribute) if attributes is not None else None
                    if value is None:
                        cg_diagnostics.append({'kind':'collectionNotLoaded','path':'.'.join(collection),'detail':'画廊目录尚未加载。'});continue
                    todo,visited=[(value,0)],set()
                    while todo and cg_budget[0]<5000:
                        value,depth=todo.pop();cg_budget[0]+=1
                        if depth>4 or id(value) in visited:continue
                        visited.add(id(value))
                        if self.safe_dict(value,True):
                            # dict.items bypasses defaultdict factories and custom
                            # item access. Only bounded registered data is inspected.
                            for unused,child in self.dict_items(value):
                                if len(todo)>=2000:cg_truncated[0]=True;break
                                todo.append((child,depth+1))
                        elif self.safe_list(value):
                            available=2000-len(todo)
                            if n.list.__len__(value)>available:cg_truncated[0]=True
                            todo.extend((child,depth+1) for child in n.list.__getitem__(value,slice(0,available)))
                        else:
                            attributes=self.fields(value)
                            if attributes is None or any(field in cls.__dict__ for cls in type(value).__mro__):continue
                            label=attributes.get(field)
                            if type(label) not in (n.str,type(u'')) or not label or len(label)>200:continue
                            membership_candidates.add((key,label))
                    if todo:cg_truncated[0]=True
                for key,label in sorted(membership_candidates):
                    if label not in renpy.game.script.namemap:continue
                    path=['member',key,label]
                    if add(path,'persistent.'+key+'['+json.dumps(label,ensure_ascii=False)+']',self.read(path),'cg','画廊注册条目的跨存档记录 · 不会播放回想'):membership_rows+=1
                if not getattr(renpy.store,'main_menu',False):
                    for key in sorted(self.roots):
                        if len(rows)>=15000:data_truncated[0]=True;break
                        if key.startswith('_') or key in ('persistent','config','gui','style','preferences','renpy'):continue
                        if key in vars(renpy.store):visit(vars(renpy.store)[key],['store',('a',key)],key,0)
                while pending_roots and budget[0]<60000 and len(rows)<15000:
                    root=pending_roots.popleft();bucket=pending[root]
                    for unused in range(32):
                        if not bucket or budget[0]>=60000 or len(rows)>=15000:break
                        pending_count[0]-=1;expand(*bucket.popleft())
                    if bucket:pending_roots.append(root)
                    else:del pending[root]
                if pending_roots:data_truncated[0]=True
                cg_rows=sum(1 for row in rows if row['group']=='cg')
                cg_state='ready' if cg_rows else 'unsupported' if any(x['kind']=='unsupportedLedger' for x in cg_diagnostics) else 'notLoaded' if cg_diagnostics else 'notRecognized'
                if cg_truncated[0]:cg_diagnostics.append({'kind':'limitReached','path':'','detail':'部分画廊数据超过本次读取上限。'})
                main_menu=bool(getattr(renpy.store,'main_menu',False))
                return {'rows':rows,'generation':self.generation,'undo':len(self.undo),'savedir':config.savedir,'truncated':data_truncated[0] or cg_truncated[0],'mainMenu':main_menu,'dataState':'saveNotLoaded' if main_menu else 'ready','cgState':cg_state,'cgRows':cg_rows,'cgDiagnostics':cg_diagnostics,'cgMembershipCandidates':len(membership_candidates),'cgMembershipRows':membership_rows,'cgMembershipSkipped':len(membership_candidates)-membership_rows}

            def current(self, path):
                try:return self.read(path)
                except KeyError:
                    if path[0]=='persistent' and len(path)==2:return None
                    raise DataError('数据已改变，请刷新列表。')
                except (IndexError,TypeError,AttributeError):raise DataError('数据已改变，请刷新列表。')

            def journal(self, path, before, after):
                directory=os.environ.get('FUSION_RENPY_DATA_LOG')
                if not directory: raise DataError('没有可用的修改记录目录。')
                if not os.path.isdir(directory): os.makedirs(directory)
                with open(os.path.join(directory,'changes.jsonl'),'ab') as f:
                    f.write((json.dumps({'time':time.time(),'path':path,'before':before,'after':after},ensure_ascii=True)+'\n').encode('utf8'))
                    f.flush();os.fsync(f.fileno())

            def mutate(self, path, before, after, parent, restore=None):
                if path[0]=='store' and getattr(renpy.store,'main_menu',False): raise DataError('请先进入游戏或读取存档。')
                if parent is not None and self.guard(path)!=parent:raise DataError('数据对象或画廊记录已改变，请刷新后重试。')
                current=self.current(path)
                if type(current) is not type(before) or current!=before:raise DataError('游戏中的值已变化，请刷新后重试。')
                old_value,new_value=before,after
                if path[0]=='member':
                    old_value=list(parent[2])
                    if restore is not None:
                        if restore[0]!='member' or restore[1]!=parent[1] or (path[2] in restore[2])!=after:raise DataError('撤销记录与当前画廊不一致。')
                        new_value=list(restore[2])
                    elif after:new_value=old_value if path[2] in old_value else old_value+[path[2]]
                    else:new_value=[label for label in old_value if label!=path[2]]
                    if len(new_value)>2000:raise DataError('画廊记录已达到本次修改上限。')
                self.journal(path,old_value,new_value)
                self.write(path,new_value)
                if path[0]=='store':renpy.exports.retain_after_load()
                if path[0]!='store':
                    try:renpy.exports.save_persistent()
                    except Exception:
                        self.write(path,old_value)
                        raise DataError('解锁记录未能保存，已恢复内存中的原值。')
                renpy.exports.restart_interaction()
                return self.guard(path)

            def validate(self, entry, value):
                path,before,kind,parent=entry
                if kind=='bool' and type(value) is not n.bool and not (getattr(self,'_undoing',False) and value is None):raise DataError('此项只能开启或关闭。')
                if kind=='int' and type(value) not in integer_types:raise DataError('请输入整数。')
                if kind=='float' and type(value) not in integer_types+(n.float,):raise DataError('请输入数字。')
                if kind!='bool' and (self.kind(value) is None or abs(value)>9007199254740991):raise DataError('数值超出支持范围。')
                if path[0]=='store' and getattr(renpy.store,'main_menu',False):raise DataError('请先进入游戏或读取存档。')
                if parent is not None and self.guard(path)!=parent:raise DataError('数据对象或画廊记录已改变，请刷新后重试。')
                current=self.current(path)
                if type(current) is not type(before) or current!=before:raise DataError('游戏中的值已变化，请刷新后重试。')
                return n.float(value) if kind=='float' else value

            def transaction(self, changes, undo=False):
                # Validate every member against the same snapshot before changing
                # any item. Journal first; flush persistent data once per batch.
                planned=[];keys=set()
                for entry,value,restore in changes:
                    path,before,kind,parent=entry
                    key=json.dumps(path,ensure_ascii=True)
                    if key in keys:raise DataError('本批修改包含重复项目。')
                    keys.add(key);value=self.validate(entry,value)
                    planned.append((path,before,value,parent,restore))
                applied=[]
                try:
                    for path,before,value,parent,restore in planned:
                        # Another item in the same native membership list may
                        # already have changed; preserve its newly updated contents.
                        current_parent=self.guard(path)
                        old,new=before,value
                        if path[0]=='member':
                            old=list(current_parent[2])
                            if restore is not None:new=list(restore[2])
                            elif value:new=old if path[2] in old else old+[path[2]]
                            else:new=[x for x in old if x!=path[2]]
                            if len(new)>2000:raise DataError('画廊记录已达到修改上限。')
                        self.journal(path,old,new)
                        if path[0]=='gallery':self.gallery_write(path,new,restore)
                        else:self.write(path,new)
                        applied.append((path,old,new,before,value,parent))
                    if any(x[0][0]!='store' for x in applied):renpy.exports.save_persistent()
                    if any(x[0][0]=='store' for x in applied):renpy.exports.retain_after_load()
                except Exception:
                    for path,old,new,before,value,parent in reversed(applied):
                        if path[0]=='gallery':self.gallery_write(path,old,parent)
                        else:self.write(path,old)
                    try:
                        if any(x[0][0]!='store' for x in applied):renpy.exports.save_persistent()
                    except Exception:pass
                    raise DataError('本批修改未完成，已恢复原值，请刷新后重试。')
                record=[(path,before,value,self.generation,self.guard(path),parent) for path,old,new,before,value,parent in applied]
                renpy.exports.restart_interaction()
                return record

            def run(self, request):
                op=request.get('op')
                if op=='lifecycleState':return {'protocol':1,'normalQuit':True}
                if op=='prepareRestart':
                    # The authenticated caller confirms saving before requesting
                    # this normal engine exit. Let the response reach the pipe
                    # before raising Ren'Py's quit control-flow exception.
                    self.quit_due=time.time()+0.5
                    return {'accepted':True,'protocol':1}
                if op=='dataSnapshot':return self.snapshot()
                if op in ('dataSet','dataSetMany'):
                    items=request.get('changes') if op=='dataSetMany' else [request]
                    if not isinstance(items,(n.list,n.tuple)) or not 0<len(items)<=15000:raise DataError('本批修改数量无效。')
                    changes=[]
                    for item in items:
                        entry=self.entries.get(item.get('entry'))
                        if entry is None:raise DataError('列表已过期，请刷新后再修改。')
                        changes.append((entry,item.get('value'),None))
                    record=self.transaction(changes)
                    self.undo.append(record);self.undo=self.undo[-100:];self.entries.clear()
                    return {'changed':len(record),'value':items[0].get('value'),'undo':len(self.undo)}
                if op=='dataUndo':
                    if not self.undo:raise DataError('没有可撤销的修改。')
                    changes=[]
                    for path,before,after,generation,parent,original_parent in reversed(self.undo[-1]):
                        if generation!=self.generation:raise DataError('读档后不能撤销旧进度的修改。')
                        changes.append(((path,after,self.kind(after) or 'bool',parent),before,original_parent if path[0] in ('member','gallery') else None))
                    # None is the absence of a persistent record, valid for undo.
                    self._undoing=True
                    try:self.transaction(changes,True)
                    finally:self._undoing=False
                    self.undo.pop();self.entries.clear()
                    return {'undo':len(self.undo)}
                raise DataError('不支持此修改操作。')

            def request(self, request):
                item=n.dict(request=request,done=threading.Event(),cancel=False)
                with self.gate:self.queue.append(item)
                if not item['done'].wait(5.0):
                    with self.gate:item['cancel']=True
                    raise DataError('游戏正忙，未执行修改，请稍后重试。')
                if 'error' in item:raise DataError(item['error'])
                return item['result']

            def tick(self):
                if self.previous:self.previous()
                if self.quit_due is not None and time.time()>=self.quit_due:
                    self.quit_due=None
                    renpy.exports.quit(relaunch=False,status=0,save=False)
                with self.gate:
                    if not self.queue:return
                    item=self.queue.popleft()
                    if item['cancel']:return
                    # Execute only on the Ren'Py main thread. Keep the gate until
                    # completion so a timed-out pending request can never write later.
                    try:item['result']=self.run(item['request'])
                    except DataError as ex:item['error']=ex.args[0]
                    except Exception:item['error']='此数据结构暂不支持修改，请刷新后重试。'
                    finally:item['done'].set()
        renpy._fusion_data=Data()
    # A game's unusual data implementation must not prevent normal play or translation.
    try:
        _fusion_data_start()
    except Exception:
        import renpy
        renpy._fusion_data = None


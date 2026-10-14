"""Run only Fusion's pure AST reader against synthetic engine objects."""
import ast
import json
from pathlib import Path
import re
import sys
import textwrap
from types import SimpleNamespace

source = (Path(__file__).parents[1] / 'Source/Screenshot/RenpyTranslationBridge.rpy').read_text(encoding='utf-8-sig')
methods = []
for name in ('node_texts', 'collect_catalog', 'catalog_steps'):
    match = re.search(r'^            def ' + name + r'\(.*?(?=^            def |\Z)', source, re.M | re.S)
    methods.append(textwrap.indent(textwrap.dedent(match.group()), '    '))

class Node:
    def __init__(self, **values):
        self.filename = 'story.rpy'
        self.__dict__.update(values)
class Say(Node): pass
class Menu(Node): pass
class Translate(Node): pass
class TranslateString(Node): pass
class UserStatement(Node): pass
class Screen(Node): pass

engine = SimpleNamespace(ast=SimpleNamespace(Say=Say, Menu=Menu, Translate=Translate,
    TranslateString=TranslateString, UserStatement=UserStatement, Screen=Screen))
scope = dict(renpy=engine, string_type=str, re=re)
exec('class Reader:\n' + '\n'.join(methods), scope)
Reader = scope['Reader']
Reader.valid = lambda self, value: isinstance(value, str) and len(value) > 2
Reader.walk_story = lambda self, roots, limit: iter(roots)
Reader.inline_literals = lambda self, text: iter(['A computed argument remains unproven.'] if '[' in text else [])
Reader.screen_texts = lambda self, screen: iter(screen)
Reader.python_texts = lambda self, code: iter(code or [])
checks = []
def check(name, actual):
    checks.append(dict(name=name, passed=bool(actual)))
    if not actual: raise AssertionError(name)

original = Say(what='You should open the door, [player].')
mixed = Say(what='You can rest here. ここで休んでください。')
menu = Menu(items=[('Open the door', None, None), ('Wait here', None, None)])
french = Say(what='Vous pouvez ouvrir la porte.')
japanese = Say(what='この扉を開けてください。')
fr_block = Translate(language='french', block=[french])
ja_block = Translate(language='japanese', block=[japanese])
screen = Screen(screen=['This screen resource remains unproven.'])
data = Node(code=['This Python table remains unproven.'])
nodes = [original, mixed, menu, fr_block, french, ja_block, japanese, screen, data]
engine.game = SimpleNamespace(script=SimpleNamespace(all_stmts=nodes, namemap={'start': original}))
for language in (None, 'french', 'japanese'):
    reader = Reader()
    reader.active_text_language = language
    reader.collect_catalog()
    list(reader.catalog_iter)
    label = language or 'original'
    trusted = reader.catalog_active_sources
    if language is None:
        check(label + ': base dialogue and choices carry selected-AST provenance',
              trusted == {original.what, mixed.what, 'Open the door', 'Wait here'})
        check(label + ': inactive translated descendants excluded outside tl folder', french.what not in reader.catalog and japanese.what not in reader.catalog)
        check(label + ': dynamic and screen resources are not promoted', not trusted.intersection(screen.screen + data.code + ['A computed argument remains unproven.']))
    else:
        chosen = french if language == 'french' else japanese
        other = japanese if language == 'french' else french
        check(label + ': only active translated AST trusted', trusted == {chosen.what})
        check(label + ': original and inactive packs excluded', original.what not in reader.catalog and other.what not in reader.catalog)
    reader.active_text_language = None if language else 'japanese'
    reader.collect_catalog()
    check(label + ': rebuild clears previous provenance immediately', not reader.catalog_active_sources)

out = Path(sys.argv[1])
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(json.dumps({'passed': True, 'checks': checks, 'executedGameCode': False}, indent=2), encoding='utf-8')
print('PASS', len(checks), '/', len(checks))

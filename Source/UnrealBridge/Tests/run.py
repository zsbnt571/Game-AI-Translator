"""Production Lua under neutral mocks. No engine, game, translation provider or native DLL."""
from pathlib import Path
import hashlib, json, os, sys, tempfile
sys.dont_write_bytecode = True
if os.getenv('FUSION_LUA_PYTHON_PATH'): sys.path.insert(0, os.environ['FUSION_LUA_PYTHON_PATH'])
from lupa.lua54 import LuaRuntime

HERE = Path(__file__).resolve().parent
SOURCE = HERE.parent
OUTPUT = Path(sys.argv[2]) if len(sys.argv)>2 else Path(tempfile.mkdtemp(prefix='fusion-unreal-regression-'))
OUTPUT.mkdir(parents=True, exist_ok=True)
if len(sys.argv) > 1:
    SOURCE = Path(sys.argv[1]).resolve()
checks = []

def require(condition, message):
    if not condition:
        raise AssertionError(message)

def check(name, fn):
    try:
        fn()
        checks.append({'name': name, 'passed': True})
    except Exception as error:
        checks.append({'name': name, 'passed': False, 'detail': str(error)})

def case(cache=None, entries=None, initial=None, tables=True, reference=False, identity=False, query_shape=False, display_sources=None):
    folder = Path(tempfile.mkdtemp(prefix='case-', dir=OUTPUT))
    (folder / 'startup.json').write_text(json.dumps({'secret': 'a' * 64, 'entries': cache or {}}, ensure_ascii=False), encoding='utf-8')
    if entries is not None:
        (folder / 'catalog.json').write_text(json.dumps({'protocol': 76, 'entries': entries, 'displaySources': display_sources or [], 'skipped': 0}, ensure_ascii=False), encoding='utf-8')
    lua = LuaRuntime(unpack_returned_tuples=True, register_eval=False, register_builtins=False)
    for name, value in {'FIXTURE_FOLDER': str(folder), 'FIXTURE_MAIN': str(SOURCE / 'main.lua'),
                        'FIXTURE_TABLES': tables, 'FIXTURE_REFERENCE': reference, 'FIXTURE_IDENTITY': identity,
                        'FIXTURE_QUERY_SHAPE': query_shape,
                        'FIXTURE_INITIAL_DISPLAYS': lua.table_from(initial or {})}.items():
        setattr(lua.globals(), name, value)
    lua.execute((HERE / 'fixture.lua').read_text(encoding='utf-8'))
    hello = json.loads((folder / 'hello.json').read_text(encoding='utf-8'))
    require('error' not in hello, f'hello failed: {hello.get("error")}')
    return lua, lua.globals()._FIXTURE, folder

def request(fx, folder, number, operation, **extra):
    fx.advance(.21)
    (folder / 'request.json').write_text(json.dumps({'secret': 'a' * 64, 'id': number, 'op': operation, **extra}, ensure_ascii=False), encoding='utf-8')
    fx.loop(); fx.pump()
    return json.loads((folder / f'response-{number}.json').read_text(encoding='utf-8'))

def static_entry(source='Continue', key='Continue', **extra):
    return {'ns': 'Neutral.Menu', 'key': key, 'source': source, 'culture': 'en', **extra}

def writes(fx, ns):
    return [row for row in fx.writes.values() if row.ns == ns]

def startup_identity():
    entry = static_entry()
    _, fx, folder = case({'Continue': '继续'}, [entry], {'Neutral.Menu\0Continue': 'Continue'}, tables=False)
    require(fx.display['Neutral.Menu\0Continue'] == '继续', 'startup cache not applied to proven catalog identity')
    require(fx.setTextCalls == 0 and fx.originalBindingIntact(), 'widget bindings changed')
    result = request(fx, folder, 1, 'translationDisable')
    require(result['ok'] and fx.display['Neutral.Menu\0Continue'] == 'Continue', 'startup original not restored')
check('startup_cache_exact_catalog_identity_and_restore', startup_identity)

def startup_prepare_enable():
    entry = static_entry()
    _, fx, folder = case({'Continue': '继续'}, [entry], {'Neutral.Menu\0Continue': 'Continue'}, tables=False)
    # Connection.PrimeStartup adds retainStartup once, independently of the
    # queue's preserveStartup flag; it consumes it only after prepare is acked.
    prepare = request(fx, folder, 1, 'translationPrepare', preserveStartup=True, retainStartup=True)
    enabled = request(fx, folder, 2, 'translationEnable')
    require(prepare['ok'] and enabled['ok'] and fx.display['Neutral.Menu\0Continue'] == '继续', 'queue prepare/enable discarded primed cache')
    # A later profile prepare must restore and clear that old cache.
    prepare = request(fx, folder, 3, 'translationPrepare', preserveStartup=True, retainStartup=False)
    enabled = request(fx, folder, 4, 'translationEnable')
    require(prepare['ok'] and enabled['ok'] and fx.display['Neutral.Menu\0Continue'] == 'Continue', 'single-use startup flag leaked the previous profile cache')
check('queue_prepare_enable_preserves_startup_once_without_prime_request', startup_prepare_enable)

def cached_reset():
    _, fx, folder = case({'Pants': '裤子'})
    require(fx.pants() == '裤子', 'fixture did not prime a real identity')
    fx.external('Pants')
    result = request(fx, folder, 1, 'translationPoll')
    require(result['ok'] and fx.pants() == '裤子', 'known non-Blueprint identity stranded after game reset')
    require(fx.setTextCalls == 0 and fx.originalBindingIntact(), 'refresh replaced game binding')
    require(request(fx, folder, 2, 'translationDisable')['ok'] and fx.pants() == 'Pants', 'refresh broke restoration')
check('non_blueprint_cached_translation_reapplies_after_game_reset', cached_reset)

def protected_reset():
    _, fx, folder = case({'Pants': '裤子'})
    fx.external('Third party text')
    request(fx, folder, 1, 'translationPoll')
    request(fx, folder, 2, 'translationDisable')
    require(fx.pants() == 'Third party text', 'cached refresh overwrote newer foreign localization')
check('cached_refresh_preserves_foreign_text', protected_reset)

def changed_runtime_source():
    _, fx, folder = case({'Pants': '裤子'}, identity=True)
    require(fx.pants() == '裤子', 'primed identity missing')
    fx.widgetSource = 'Different original under reused key'
    request(fx, folder, 1, 'translationPoll')
    before = len(writes(fx, 'Clothes'))
    result = request(fx, folder, 2, 'translationDisable')
    require(result['ok'] and len(writes(fx, 'Clothes')) == before and fx.pants() == '裤子', 'stale source restored over reidentified game text')
check('runtime_source_change_relinquishes_stale_restore_ownership', changed_runtime_source)

def duplicate_conflict():
    a = static_entry(); b = static_entry('Different source')
    _, fx, folder = case({'Continue': '继续', 'Different source': '其他'}, [a, b], {'Neutral.Menu\0Continue': 'Continue'}, tables=False)
    result = request(fx, folder, 1, 'translationPoll')
    require(not writes(fx, 'Neutral.Menu'), 'conflicting namespace/key sources were registered')
    require(result['result']['runtime']['integration']['conflictingIdentities'] == 1, 'conflict not diagnosed')
check('all_catalog_provenance_rejects_conflicting_source_identity', duplicate_conflict)

def exact_duplicate():
    e = static_entry()
    _, fx, folder = case({'Continue': '继续'}, [e, e], {'Neutral.Menu\0Continue': 'Continue'}, tables=False)
    result = request(fx, folder, 1, 'translationInspect')
    require(result['result']['total'] == 1 and len(writes(fx, 'Neutral.Menu')) == 1, 'duplicate identity scheduled duplicate writes')
check('exact_duplicate_catalog_identity_is_coalesced', exact_duplicate)

def invalid_identity():
    entries = [static_entry(key='bad\0key'), static_entry(key=''), static_entry(source='bad\0source'), static_entry(ns='bad\0ns')]
    _, fx, folder = case({'bad\0source': '错误'}, entries, tables=False)
    result = request(fx, folder, 1, 'translationInspect')
    require(result['result']['total'] == 0 and result['result']['runtime']['integration']['invalidIdentities'] == 4, 'malformed identity entered engine operations')
check('nul_and_empty_identity_fields_rejected', invalid_identity)

def same_source_distinct_ids():
    a = static_entry(); b = static_entry(key='Foreign')
    _, fx, folder = case({'Continue': '继续'}, [a, b], {'Neutral.Menu\0Continue': 'Continue', 'Neutral.Menu\0Foreign': '已有汉化'}, tables=False)
    require(fx.display['Neutral.Menu\0Continue'] == '继续' and fx.display['Neutral.Menu\0Foreign'] == '已有汉化', 'source cache guessed other identity ownership')
    request(fx, folder, 1, 'translationDisable')
    require(fx.display['Neutral.Menu\0Foreign'] == '已有汉化', 'restore overwrote protected duplicate source')
check('same_source_cache_keeps_distinct_identity_ownership', same_source_distinct_ids)

def unproven_candidate():
    entry = static_entry(provenance='blueprintLocalizedCandidate')
    _, fx, folder = case({'Continue': '继续'}, [entry], tables=False)
    request(fx, folder, 1, 'translationPoll')
    require(not writes(fx, 'Neutral.Menu'), 'unloaded candidate manufactured a native identity')
    fx.display['Neutral.Menu\0Continue'] = 'Continue'
    request(fx, folder, 2, 'translationPoll')
    require(fx.display['Neutral.Menu\0Continue'] == '继续', 'later exact identity did not reuse cache')
check('candidate_waits_for_exact_live_identity', unproven_candidate)

def unsupported_candidate():
    entry = static_entry(provenance='blueprintLocalizedCandidate')
    _, fx, folder = case({'Continue': '继续'}, [entry], {'Neutral.Menu\0Continue': 'Continue'}, tables=False, reference=True)
    request(fx, folder, 1, 'translationPoll')
    require(not writes(fx, 'Neutral.Menu'), 'unsupported query bypassed candidate guard')
check('unsupported_native_query_does_not_enable_candidate_write', unsupported_candidate)

def confirmed_counts():
    _, fx, folder = case()
    first = request(fx, folder, 1, 'translationApply', epoch=0, entries=[{'source': 'Pants', 'text': '裤子'}])
    second = request(fx, folder, 2, 'translationApply', epoch=0, entries=[{'source': 'Pants', 'text': '裤子'}])
    require(first['result']['applied'] == 1 and second['result']['applied'] == 0, 'unchanged or merely queued write counted applied')
check('apply_count_requires_new_confirmed_write', confirmed_counts)

def no_op_write():
    _, fx, folder = case(); fx.ignoreApply = True
    result = request(fx, folder, 1, 'translationApply', epoch=0, entries=[{'source': 'Pants', 'text': '裤子'}])
    require(not result['ok'] and fx.pants() == 'Pants', 'ignored native write reported success')
check('native_noop_cannot_report_success', no_op_write)

def late_table_key():
    lua, fx, folder = case({'Pants': '裤子', 'Hat': '帽子'})
    fx.tableKeys = lua.eval("{{get=function()return {ToString=function()return 'Pants'end}end},{get=function()return {ToString=function()return 'Hat'end}end}}")
    fx.tableSources = lua.table_from({'Pants': 'Pants', 'Hat': 'Hat'})
    fx.display['Clothes\0Hat'] = 'Hat'
    fx.advance(6)
    for _ in range(30): fx.loop()
    require(fx.display['Clothes\0Hat'] == '帽子', 'new key in an already registered string table was never read')
    require(request(fx, folder, 1, 'translationDisable')['ok'] and fx.display['Clothes\0Hat'] == 'Hat', 'late table key cannot restore')
check('registered_table_late_keys_refresh_and_reuse_cache', late_table_key)

def inactive_culture():
    lua, fx, folder = case({'Pants': '裤子'})
    fx.setCulture('fr', lua.table_from({'Clothes\0Pants': 'French value'}))
    before = len(fx.writes)
    result = request(fx, folder, 1, 'translationDisable')
    require(result['ok'] and result.get('pendingRestorations',0)>0 and len(fx.writes) == before, 'inactive culture restoration was falsely confirmed or foreign display changed')
    require(request(fx, folder, 2, 'translationEnable')['ok'], 'inactive record blocked active-culture enable')
    require(request(fx, folder, 3, 'translationDisable')['ok'], 'current-language disable was blocked')
    fx.setCulture('en')
    for _ in range(50): fx.loop()
    require(fx.pants() == 'Pants' and not fx.cancelled, 'reactivated culture did not finish pending restoration')
check('inactive_culture_retains_ownership_without_blocking_current_language', inactive_culture)


def culture_return_resumes():
    lua, fx, folder = case({'Pants': '裤子'})
    require(fx.pants() == '裤子', 'translation did not start')
    fx.setCulture('fr', lua.table_from({'Clothes\0Pants': 'French value'}))
    for _ in range(20): fx.advance(.21); fx.loop(); fx.pump()
    pending = request(fx, folder, 1, 'translationPoll')
    require(pending['ok'], 'language transition blocked polling')
    require(fx.pants() == 'French value', 'inactive culture overwritten')
    fx.setCulture('en')
    for _ in range(50): fx.advance(.21); fx.loop(); fx.pump()
    resumed = request(fx, folder, 2, 'translationPoll')
    require(resumed['ok'] and fx.pants() == '裤子', 'returning to original language did not resume cache')
    require(not fx.cancelled, 'recoverable switch cancelled the bridge')
    require(request(fx, folder, 3, 'translationDisable')['ok'] and fx.pants() == 'Pants', 'resumed translation lost restoration')
check('language_switch_and_return_automatically_resume_translation', culture_return_resumes)

def active_language_progress():
    entries=[static_entry(),static_entry('先へ進む','NativeJa',culture='ja',localized={'en':'Continue','ja':'先へ進む'})]
    lua,fx,folder=case({'Continue':'继续','先へ進む':'向前走'},entries,{'Neutral.Menu\0Continue':'Continue','Neutral.Menu\0NativeJa':'Continue'},tables=False)
    require(fx.display['Neutral.Menu\0Continue']=='继续','English fixture was not translated')
    require(fx.display['Neutral.Menu\0NativeJa']=='继续','declared English locale did not use its own source')
    fx.setCulture('ja',lua.table_from({'Neutral.Menu\0Continue':'別の表示','Neutral.Menu\0NativeJa':'先へ進む'}))
    result=request(fx,folder,1,'translationPoll')
    require(result['ok'] and fx.display['Neutral.Menu\0NativeJa']=='向前走','new native language did not become eligible')
    require(fx.display['Neutral.Menu\0Continue']=='別の表示','foreign active localization was overwritten')
    require(request(fx,folder,2,'translationDisable')['ok'],'disable required returning to English')
    require(fx.display['Neutral.Menu\0NativeJa']=='先へ進む','Japanese original was not restored')
    require(request(fx,folder,3,'translationPrepare')['ok'],'prepare in Japanese was blocked')
    enable=request(fx,folder,4,'translationEnable');epoch=enable['result']['epoch']
    request(fx,folder,5,'translationApply',epoch=epoch,entries=[{'source':'先へ進む','text':'向前走'}])
    require(fx.display['Neutral.Menu\0NativeJa']=='向前走','could not re-enable in Japanese')
    request(fx,folder,6,'translationDisable')
    fx.setCulture('en');request(fx,folder,7,'translationPoll')
    require(fx.display['Neutral.Menu\0Continue']=='Continue','inactive English ownership was lost')
    require(not fx.cancelled,'language switches closed bridge')
check('english_to_japanese_disable_reenable_and_return_with_culture_ownership',active_language_progress)

def immediate_language_disable():
    entries=[static_entry(),static_entry('先へ進む','NativeJa',culture='ja',localized={'en':'Continue','ja':'先へ進む'})]
    lua,fx,folder=case({'Continue':'继续','先へ進む':'向前走'},entries,{'Neutral.Menu\0Continue':'Continue','Neutral.Menu\0NativeJa':'Continue'},tables=False)
    fx.setCulture('ja',lua.table_from({'Neutral.Menu\0Continue':'別の表示','Neutral.Menu\0NativeJa':'先へ進む'}))
    # No poll or maintenance callback between language change and disable.
    require(request(fx,folder,1,'translationDisable')['ok'],'immediate disable failed')
    require(fx.display['Neutral.Menu\0NativeJa']=='先へ進む','disable applied a translation first')
    require(request(fx,folder,2,'translationEnable')['ok'],'immediate re-enable failed')
    require(fx.display['Neutral.Menu\0NativeJa']=='向前走','new culture was lost by immediate disable')
    for i in range(4):
        fx.setCulture('en');request(fx,folder,3+i*3,'translationPoll')
        fx.setCulture('ja');request(fx,folder,4+i*3,'translationDisable')
        request(fx,folder,5+i*3,'translationEnable')
        require(fx.display['Neutral.Menu\0NativeJa']=='向前走','repeated switch stranded current language')
    request(fx,folder,20,'translationDisable');fx.setCulture('en');request(fx,folder,21,'translationPoll')
    require(fx.display['Neutral.Menu\0Continue']=='Continue','repeated switches lost original')
check('immediate_disable_and_repeated_language_switches_keep_new_culture',immediate_language_disable)

def declared_localization_switch():
    entry=static_entry(localized={'en':'Continue','ja':'続ける','zh-CN':'继续游戏'})
    lua,fx,folder=case({'Continue':'继续'},[entry],{'Neutral.Menu\0Continue':'Continue'},tables=False)
    fx.setCulture('ja',lua.table_from({'Neutral.Menu\0Continue':'続ける'}))
    polled=request(fx,folder,1,'translationPoll');epoch=polled['result']['epoch']
    require(polled['ok'] and fx.display['Neutral.Menu\0Continue']=='続ける','English cache was incorrectly reused as Japanese source')
    catalog=request(fx,folder,2,'translationCatalog',cursor=0)
    require('続ける' in catalog['result']['texts'],'current localized Japanese was not admitted')
    late=request(fx,folder,3,'translationApply',epoch=epoch,entries=[{'source':'Continue','text':'英文迟到结果'}])
    require(late['ok'] and fx.display['Neutral.Menu\0Continue']=='続ける','late English result overwrote Japanese')
    result=request(fx,folder,4,'translationApply',epoch=epoch,entries=[{'source':'続ける','text':'接着玩'}])
    require(result['ok'] and fx.display['Neutral.Menu\0Continue']=='接着玩','declared Japanese localization not translated')
    require(request(fx,folder,5,'translationDisable')['ok'] and fx.display['Neutral.Menu\0Continue']=='続ける','Japanese localized original not restored')
    request(fx,folder,6,'translationEnable')
    require(fx.display['Neutral.Menu\0Continue']=='接着玩','could not re-enable while staying Japanese')
    fx.setCulture('zh-CN',lua.table_from({'Neutral.Menu\0Continue':'继续游戏'}));request(fx,folder,7,'translationPoll')
    require(fx.display['Neutral.Menu\0Continue']=='继续游戏','existing Chinese locale was changed')
    request(fx,folder,8,'translationDisable');fx.setCulture('en');request(fx,folder,9,'translationPoll')
    require(fx.display['Neutral.Menu\0Continue']=='Continue','old English original not restored after localized switch')
check('declared_english_to_japanese_locale_admission_stale_result_restore_and_chinese_protection',declared_localization_switch)

def localized_han_and_third_party():
    entry=static_entry('Settings','Settings',localized={'en':'Settings','ja':'設定'})
    lua,fx,folder=case({'Settings':'选项','設定':'设置'},[entry],{'Neutral.Menu\0Settings':'Settings'},tables=False)
    fx.setCulture('ja-JP',lua.table_from({'Neutral.Menu\0Settings':'設定'}))
    request(fx,folder,1,'translationPoll')
    require(fx.display['Neutral.Menu\0Settings']=='设置','Han-only Japanese regional locale was incorrectly protected as Chinese')
    fx.display['Neutral.Menu\0Settings']='外部の変更'
    request(fx,folder,2,'translationPoll');request(fx,folder,3,'translationDisable')
    require(fx.display['Neutral.Menu\0Settings']=='外部の変更','third-party current locale change was overwritten')
check('declared_han_only_japanese_regional_locale_and_later_external_edit',localized_han_and_third_party)

def localized_immediate_prepare():
    entry=static_entry(localized={'en':'Continue','ja':'続ける'})
    lua,fx,folder=case({'Continue':'继续'},[entry],{'Neutral.Menu\0Continue':'Continue'},tables=False)
    fx.setCulture('ja',lua.table_from({'Neutral.Menu\0Continue':'続ける'}))
    require(request(fx,folder,1,'translationPrepare')['ok'],'prepare immediately after switching failed')
    epoch=request(fx,folder,2,'translationEnable')['result']['epoch']
    result=request(fx,folder,3,'translationApply',epoch=epoch,entries=[{'source':'続ける','text':'接着玩'}])
    require(result['ok'] and fx.display['Neutral.Menu\0Continue']=='接着玩','immediate prepare missed localized admission')
check('prepare_immediately_after_locale_change_keeps_localized_source',localized_immediate_prepare)

def capture_not_write():
    _, fx, folder = case({'Unidentified label': '未知'}, tables=False)
    # Default visible Pants has no catalog or registered table identity here.
    result = request(fx, folder, 1, 'translationPoll')
    require(result['result']['texts'] == [] and result['result']['readOnlyTexts'] == ['Pants'], 'display capture became an invented write identity')
    require(fx.setTextCalls == 0, 'capture modified a widget')
check('raw_display_capture_does_not_authorize_writeback', capture_not_write)

def autonomous_cache_reuse():
    _, fx, folder = case({'Pants': '裤子'})
    fx.external('Pants')
    # No host poll or translation request follows the game resetting a label.
    fx.advance(.25)
    for _ in range(12): fx.loop()
    require(fx.pants() == '裤子', 'cached locres/table text waited for another IPC poll')
    require(fx.setTextCalls == 0 and fx.originalBindingIntact(), 'maintenance changed a binding')
    require(request(fx, folder, 1, 'translationDisable')['ok'] and fx.pants() == 'Pants', 'autonomous reuse lost recovery')
check('game_thread_reuses_delivered_cache_without_host_poll', autonomous_cache_reuse)

def autonomous_foreign_protection():
    _, fx, folder = case({'Pants': '裤子'})
    fx.external('New game or mod localization')
    fx.advance(.25)
    for _ in range(12): fx.loop()
    require(fx.pants() == 'New game or mod localization', 'maintenance overwrote foreign text')
    request(fx, folder, 1, 'translationDisable')
    require(fx.pants() == 'New game or mod localization', 'maintenance restore overwrote foreign text')
check('autonomous_cache_reuse_preserves_foreign_changes', autonomous_foreign_protection)

def incremental_fonts():
    entries = [static_entry(source=f'Item {i}', key=f'Item{i}') for i in range(100)]
    initial = {f'Neutral.Menu\0Item{i}': f'Item {i}' for i in range(100)}
    _, fx, folder = case({}, entries, initial, tables=False)
    before = fx.forcedFontUpdates or 0
    fx.advance(.21)
    (folder / 'request.json').write_text(json.dumps({'secret': 'a' * 64, 'id': 1,
        'op': 'translationApply', 'epoch': 0,
        'entries': [{'source': f'Item {i}', 'text': f'项目 {i}'} for i in range(100)]}, ensure_ascii=False), encoding='utf-8')
    fx.loop()
    require(fx.isBusy(), 'fixture must leave an unfinished native batch')
    require(any(fx.display[f'Neutral.Menu\0Item{i}'] == f'项目 {i}' for i in range(100)), 'fixture must have native writes')
    require((fx.forcedFontUpdates or 0) > before, 'font pass waited until the whole native batch ended')
    fx.pump()
    result=json.loads((folder / 'response-1.json').read_text(encoding='utf-8'))
    require(result['ok'] and result['result']['applied'] == 100, 'chunked batch failed or miscounted')
    require(request(fx, folder, 2, 'translationDisable')['ok'], 'chunked font work broke restoration')
check('font_opportunity_precedes_yield_during_large_apply_batch', incremental_fonts)

def cache_before_discovery():
    _, fx, folder = case({'Pants': '裤子'})
    fx.external('Pants')
    before=len(writes(fx, 'Clothes'))
    fx.keySnapshotCost=.5
    fx.advance(6)
    for _ in range(12): fx.loop()
    repair=writes(fx, 'Clothes')[before:]
    require(len(repair) == 1 and repair[0].text == '裤子', 'cached repair not made once')
    require(repair[0].event < fx.lastKeySnapshotEvent, 'background table snapshot delayed known visible cache repair')
    require(fx.setTextCalls == 0 and fx.originalBindingIntact(), 'priority repair changed bindings')
check('cached_visible_identity_precedes_background_table_snapshot', cache_before_discovery)

def incremental_font_failure():
    entries=[static_entry(source=f'Item {i}',key=f'Item{i}') for i in range(100)]
    initial={f'Neutral.Menu\0Item{i}': f'Item {i}' for i in range(100)}
    _, fx, folder=case({},entries,initial,tables=False)
    fx.fontUpdateError=True
    result=request(fx,folder,1,'translationApply',epoch=0,
        entries=[{'source':f'Item {i}','text':f'项目 {i}'} for i in range(100)])
    require(not result['ok'], 'failed incremental font stage was acknowledged as a complete batch')
    require(all(fx.display[k] == v for k,v in initial.items()), 'partial text batch did not restore after font stage failure')
check('incremental_font_exception_rolls_back_partial_text_batch', incremental_font_failure)

def observed_real_failure():
    entry = static_entry('Pants', key='Pants', provenance='blueprintLocalizedCandidate')
    _, fx, folder = case({'Pants': '裤子'}, [entry], tables=False, query_shape=True)
    response = request(fx, folder, 1, 'translationPoll')['result']
    runtime = response['runtime']
    require(runtime['textApi']['metadata']['FindTextInLocalizationTable']['layout'] == 'out-middle-4', 'FLife2 schema not exercised')
    require(runtime['textApi']['results']['FindTextInLocalizationTable'] == 'native-out-argument-not-consumed', 'unsafe native layout no longer blocked')
    require(runtime['identityDetail'] == 'identity-not-returned' and not runtime['identityReplacement'], 'lost FText identity became writable')
    require('Pants' in response['readOnlyTexts'], 'current text did not reach read-only route during actual failed identity combination')
    require(runtime['visibleRead'] and runtime['currentObserved'] > 0, 'actual read capability not reported')
    require(not writes(fx, 'Neutral.Menu') and fx.setTextCalls == 0, 'read-only text guessed a replacement identity')
    catalog = request(fx, folder, 2, 'translationCatalog', offset=0, limit=10)['result']
    require('Pants' in catalog['texts'], 'safe catalog disappeared when runtime identity failed')
    stopped = request(fx, folder, 3, 'translationDisable')
    require(stopped['ok'] and fx.pants() == 'Pants', 'read-only fallback modified original game text')
check('FLife2_actual_query_and_identity_failures_keep_catalog_and_current_readonly_text_without_write', observed_real_failure)

def native_candidate_barrier():
    entry=static_entry(provenance='nativeLocalizedCandidate')
    _,fx,folder=case({'Continue':'继续'},[entry],tables=False)
    request(fx,folder,1,'translationPoll')
    require(not writes(fx,'Neutral.Menu'),'native literal manufactured an identity')
    fx.display['Neutral.Menu\0Continue']='Continue'
    request(fx,folder,2,'translationPoll')
    require(fx.display['Neutral.Menu\0Continue']=='继续','exact live native identity not translated')
    require(request(fx,folder,3,'translationDisable')['ok'] and fx.display['Neutral.Menu\0Continue']=='Continue','native literal did not restore')
    _,fx,folder=case({'Continue':'继续'},[entry],{'Neutral.Menu\0Continue':'Different source'},tables=False)
    request(fx,folder,1,'translationPoll')
    require(not writes(fx,'Neutral.Menu'),'mismatched native identity overwritten')
check('native_literal_candidates_require_existing_matching_identity_and_restore',native_candidate_barrier)

def prepared_and_visible_english():
    entry=static_entry(source='ドア',key='Door')
    _,fx,folder=case({'Door':'门'},[entry],{'Neutral.Menu\0Door':'Door'},tables=False)
    b=fx.addControl('/Engine/Transient.Game.DoorLabel','Door',True)
    fx.advance(.3);fx.pump()
    catalog=request(fx,folder,1,'translationCatalog',offset=0,limit=100)['result']['texts']
    require('Door' in catalog and b.writes==0,'hidden typed property did not prepare safely')
    b.visible=True
    fx.advance(.3);fx.pump()
    require(b.source=='门' and b.writes==1,'foreign native display blocked independent live English caption')
    require(fx.display['Neutral.Menu\0Door']=='Door' and not writes(fx,'Neutral.Menu'),'protected native identity was changed')
    require(request(fx,folder,2,'translationDisable')['ok'] and b.source=='Door','caption did not restore original English')
check('hidden_preparation_and_live_english_keep_native_identity_protection_separate',prepared_and_visible_english)

def prepared_accepts_delivered_cache():
    _,fx,folder=case({},[],tables=False)
    b=fx.addControl('/Engine/Transient.Game.FutureChoice','Stardust Orpheus',True)
    fx.advance(.3);fx.pump()
    delivered=request(fx,folder,1,'translationApply',epoch=0,entries=[{'source':'Stardust Orpheus','text':'星尘俄耳甫斯'}])
    require(delivered['ok'] and b.writes==0,'preparation wrote a hidden widget')
    b.visible=True;fx.advance(.3);fx.pump()
    require(b.source=='星尘俄耳甫斯','prepared translation waited for another request')
    request(fx,folder,2,'translationDisable')
    require(b.source=='Stardust Orpheus','prepared control failed restoration')
check('pretranslated_hidden_control_reuses_cache_on_visibility_without_request',prepared_accepts_delivered_cache)

lua = LuaRuntime(unpack_returned_tuples=True, register_eval=False, register_builtins=False)
lua.globals().RUNTIME_SOURCE = (SOURCE / 'runtime.lua').read_text(encoding='utf-8-sig')
snapshot_checks = lua.execute((HERE / 'snapshot_fixture.lua').read_text(encoding='utf-8'))
for item in snapshot_checks.values():
    checks.append({'name': 'snapshot:' + item['name'], 'passed': bool(item['passed']), 'detail': item['detail']})

lua = LuaRuntime(unpack_returned_tuples=True, register_eval=False, register_builtins=False)
lua.globals().TEXT_API = lua.execute((SOURCE / 'text_api.lua').read_text(encoding='utf-8-sig'))
alias = lua.execute((HERE / 'text_api_fixture.lua').read_text(encoding='utf-8'))
for item in alias.values():
    checks.append({'name': 'text_schema:' + item['name'], 'passed': bool(item['passed']), 'detail': item['detail']})

lua = LuaRuntime(unpack_returned_tuples=True, register_eval=False, register_builtins=False)
lua.globals().FONTS_SOURCE = (SOURCE / 'fonts.lua').read_text(encoding='utf-8-sig')
font_checks = lua.execute((HERE / 'fonts_fixture.lua').read_text(encoding='utf-8'))
for item in font_checks.values():
    checks.append({'name': 'font:' + item['name'], 'passed': bool(item['passed']), 'detail': item['detail']})

lua = LuaRuntime(unpack_returned_tuples=True, register_eval=False, register_builtins=False)
lua.globals().MODULE = lua.execute((SOURCE / 'widget_text.lua').read_text(encoding='utf-8-sig'))
lua.globals().STREAM = lua.execute((SOURCE / 'text_stream.lua').read_text(encoding='utf-8-sig'))
widget_checks = lua.execute((HERE / 'widget_fixture.lua').read_text(encoding='utf-8'))
for item in widget_checks.values():
    checks.append({'name': 'widget:' + item['name'], 'passed': bool(item['passed']), 'detail': item['detail']})

def display_only_preparation():
    source='After thinking for a while, I wrote my reply.'
    _,fx,folder=case({source:'想了一会儿后，我写下了回复。'},[],tables=False,display_sources=[source,source,'bad\0source'])
    cat=request(fx,folder,1,'translationCatalog')['result']['texts']
    require(list(cat).count(source)==1 and 'bad\0source' not in cat,'display sources were not bounded/coalesced')
    require(not writes(fx,'Neutral.Menu'),'display-only source manufactured an identity')
    b=fx.addControl('/Engine/Transient.Game.Dialogue',source,False)
    fx.advance(.3);fx.pump()
    require(b.source=='想了一会儿后，我写下了回复。','prepared full dialogue did not reuse cache')
    require(request(fx,folder,2,'translationDisable')['ok'] and b.source==source,'display-only original not restored')
check('display_only_catalog_prepares_without_native_identity_and_restores_live_control',display_only_preparation)

lua = LuaRuntime(unpack_returned_tuples=True, register_eval=False, register_builtins=False)
lua.globals().STREAM = lua.execute((SOURCE / 'text_stream.lua').read_text(encoding='utf-8-sig'))
for item in lua.execute((HERE / 'stream_fixture.lua').read_text(encoding='utf-8')).values():
    checks.append({'name': 'stream:' + item['name'], 'passed': bool(item['passed']), 'detail': item['detail']})

def source_pair_receipt():
    _,fx,folder=case({},[],tables=False)
    fx.addControl('/Engine/Transient.Game.Pending','A newly observed complete dialogue.',True)
    fx.advance(.3);fx.pump()
    result=request(fx,folder,1,'translationApply',epoch=0,entries=[{'source':'A newly observed complete dialogue.','text':'新出现的完整对话。'}])['result']
    require(result['stored']==1 and result['applied']==0,'source acceptance was confused with native identity writes')
    stale=request(fx,folder,2,'translationApply',epoch=999,entries=[{'source':'Unknown text','text':'错误'}])['result']
    require(stale.get('stale') and stale.get('stored',0)==0,'stale cache update accepted')
check('unreal_source_pair_receipts_are_independent_of_native_identity_counts',source_pair_receipt)

def startup_typewriter_source():
    source='After thinking for a while, I wrote my reply.'
    target='想了一会儿后，我写下了回复。'
    _,fx,folder=case({source:target},[],tables=False)
    b=fx.addControl('/Engine/Transient.Game.Dialogue',source[:12],False)
    fx.advance(.3);fx.pump()
    b.source=source[:24]
    fx.advance(.3);fx.pump()
    require(b.source!=source[:24] and target.startswith(b.source),'cached full sentence was not indexed for runtime reveal')
check('startup_complete_source_is_available_for_a_new_typewriter_control',startup_typewriter_source)

lua=LuaRuntime(unpack_returned_tuples=True,register_eval=False,register_builtins=False)
lua.globals().SLATE=lua.execute((SOURCE/'slate.lua').read_text(encoding='utf-8-sig'))
for item in lua.execute((HERE/'slate_fixture.lua').read_text(encoding='utf-8')).values():
    checks.append({'name':'slate:'+item['name'],'passed':bool(item['passed'])})

report = {'passed': sum(c['passed'] for c in checks), 'total': len(checks), 'checks': checks,
          'scope': 'Neutral Lua contracts only; no native ABI, game, API, rendering or first-frame proof.',
          'sha256': {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in SOURCE.glob('*.lua')}}
name = 'results.json'
(OUTPUT / name).write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f"{report['passed']}/{report['total']} passed")
for c in checks:
    if not c['passed']: print('FAIL', c['name'], c.get('detail', ''))
raise SystemExit(0 if report['passed'] == report['total'] else 1)


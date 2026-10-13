#include "RuntimeApi.h"
#include "json.hpp"
#include <atomic>
#include <condition_variable>
#include <deque>
#include <memory>
#include <set>
#include <chrono>
#include <stdexcept>
using Json=nlohmann::json;
using Clock=std::chrono::steady_clock;
static std::atomic<bool> ready=false,disconnectPending=false;
static std::atomic<int> started=0;
static bool enabled=false,preparing=false,restoringDisconnect=false;
static int epoch=1,confirmed=0,failed=0,captured=0,blocked=0;
static uint64_t unchangedTextChecks=0,textOverwrites=0,fontChanges=0,fontAssignments=0;
#include "MainThreadWake.h"
static std::map<std::string,std::string> translations,staged,startupTranslations;
static std::deque<std::string> visible;
static std::set<std::string> queued;
static uint32_t legacyFont=0,pathFont=0,tmpFont=0;
static std::set<std::string> legacyCoverage,tmpCoverage;
static uint64_t tmpGlyphRevision=0,tmpAtlasRepairs=0;
static std::string secret,pipeName;
static std::wstring Env(const wchar_t* key){DWORD n=GetEnvironmentVariableW(key,nullptr,0);if(!n||n>32768)return {};std::wstring v(n,L'\0');GetEnvironmentVariableW(key,v.data(),n);v.resize(n-1);return v;}
static std::string Utf8(const std::wstring& w){int n=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,w.data(),(int)w.size(),nullptr,0,nullptr,nullptr);std::string s(n,'\0');WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,w.data(),(int)w.size(),s.data(),n,nullptr,nullptr);return s;}
static Json ReadJson(const std::wstring& path,size_t limit){std::ifstream f(path,std::ios::binary|std::ios::ate);if(!f)throw std::runtime_error("File unavailable");auto n=f.tellg();if(n<0||(uint64_t)n>limit)throw std::runtime_error("File limit exceeded");std::string s((size_t)n,'\0');f.seekg(0);f.read(s.data(),s.size());return Json::parse(s);}
static bool Eligible(const std::string& s){if(s.empty()||s.size()>24000||s.find('\0')!=std::string::npos)return false;int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.data(),(int)s.size(),nullptr,0);if(n<=0||n>6000)return false;std::wstring text(n,L'\0');MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.data(),(int)s.size(),text.data(),n);std::vector<WORD> types(n);if(!GetStringTypeW(CT_CTYPE1,text.data(),n,types.data()))return false;for(auto type:types)if(type&C1_ALPHA)return true;return false;}
static bool Bool(void* value){return value&&*(bool*)il2cpp_object_unbox(value);}
static void* findObjects=nullptr,*arrayGet=nullptr,*objectAlive=nullptr,*instanceId=nullptr,*canvasMethod=nullptr,*quitMethod=nullptr;
struct Family { void* klass=nullptr,*type=nullptr,*get=nullptr,*set=nullptr,*getFont=nullptr,*setFont=nullptr,*getMaterial=nullptr,*setMaterial=nullptr,*active=nullptr;bool tmp=false;uint32_t cursor=0; };
static std::vector<Family> families;
struct Root {uint32_t id=0;explicit Root(void* value){if(value)id=il2cpp_gchandle_new(value,false);}~Root(){if(id)il2cpp_gchandle_free(id);}};
#include "NativeTextFit.h"
#include "NativeLatency.h"
#include "NativeLegacyLayout.h"
struct Binding { DisplayLatencyMark latency; TextFitLease fit; LegacyLayoutLease legacy; int key=0; uint32_t weak=0,oldFont=0,oldMaterial=0;size_t family=0;std::string source,applied,glyphText,glyphMode;void* ownedFont=nullptr,*ownedMaterial=nullptr,*checkedFont=nullptr;bool fontBlocked=false,active=false,shown=false,glyphReady=false;uint64_t glyphRevision=0;ULONGLONG retryAt=0,queueAt=0,fitAt=0,fontCheckAt=0; };
static std::map<int,Binding> bindings;
static std::map<std::string,std::set<int>> sourceBindings;
static std::deque<int> priorityRefresh;
static std::set<int> priorityQueued;
static void Prioritize(int key){if(priorityQueued.size()<8192&&priorityQueued.insert(key).second)priorityRefresh.push_back(key);}
static void Unindex(Binding& b){auto it=sourceBindings.find(b.source);if(it!=sourceBindings.end()){it->second.erase(b.key);if(it->second.empty())sourceBindings.erase(it);}}
static void SourceChanged(Binding& b,const std::string& text){Unindex(b);b.source=text;b.latency={};if(Eligible(text))b.latency.Begin(GetTickCount64());if(Eligible(text))sourceBindings[text].insert(b.key);}

static bool Alive(void* target){void* args[]={target};return target&&Bool(Invoke(objectAlive,nullptr,args));}

static void Release(Binding& b){Unindex(b);priorityQueued.erase(b.key);if(b.weak)il2cpp_gchandle_free(b.weak);if(b.oldFont)il2cpp_gchandle_free(b.oldFont);if(b.oldMaterial)il2cpp_gchandle_free(b.oldMaterial);b={};}
static void Restore(Binding& b,void* target){auto& f=families[b.family];b.glyphReady=false;b.checkedFont=nullptr;b.glyphText.clear();b.glyphMode.clear();RestoreFit(b.fit,target,f.klass);RestoreLegacy(b.legacy,target,f.klass);if(!b.applied.empty()){
 auto now=Text(Invoke(f.get,target));if(now==b.applied){void* args[]={il2cpp_string_new(b.source.c_str())};Invoke(f.set,target,args);if(Text(Invoke(f.get,target))!=b.source)return;}b.applied.clear();}
 if(b.ownedFont){auto current=Invoke(f.getFont,target);auto material=f.getMaterial?Invoke(f.getMaterial,target):nullptr;auto restoreMaterial=material==b.ownedMaterial&&b.oldMaterial?il2cpp_gchandle_get_target(b.oldMaterial):material;Root held(restoreMaterial);if(current==b.ownedFont){auto original=b.oldFont?il2cpp_gchandle_get_target(b.oldFont):nullptr;if(original&&!Alive(original))return;void* args[]={original};Invoke(f.setFont,target,args);if(Invoke(f.getFont,target)!=original)return;}if(restoreMaterial&&f.setMaterial&&Alive(restoreMaterial)){void* args[]={restoreMaterial};Invoke(f.setMaterial,target,args);if(Invoke(f.getMaterial,target)!=restoreMaterial)return;}b.ownedFont=nullptr;b.ownedMaterial=nullptr;}
 if(b.oldMaterial){il2cpp_gchandle_free(b.oldMaterial);b.oldMaterial=0;}
 if(b.oldFont){il2cpp_gchandle_free(b.oldFont);b.oldFont=0;}}
#include "NativeFontLifetime.h"
static void* EnsureFont(bool tmp){
 auto klass=Class("UnityEngine","Font");if(!klass)return nullptr;
 auto& handle=tmp?pathFont:legacyFont;DropDeadFont(handle,tmp);if(tmp)DropDeadFont(tmpFont,true);
 if(!handle){
  auto font=il2cpp_object_new(klass);if(!font)return nullptr;Root heldFont(font);void* result=nullptr;
  if(tmp){
   auto create=ExactMethod(klass,"Internal_CreateFontFromPath",{"UnityEngine.Font","System.String"});
   wchar_t windows[MAX_PATH];UINT n=GetWindowsDirectoryW(windows,MAX_PATH);if(!n||n>=MAX_PATH)return nullptr;
   std::wstring selected;for(auto name:{L"msyh.ttc",L"simhei.ttf",L"simsun.ttc"}){auto path=std::wstring(windows)+L"\\Fonts\\"+name;if(GetFileAttributesW(path.c_str())!=INVALID_FILE_ATTRIBUTES){selected=path;break;}}
   if(selected.empty())return nullptr;auto path=il2cpp_string_new(Utf8(selected).c_str());Root heldPath(path);void* args[]={font,path};
   if(create){if(!FontCall(create,nullptr,args,result))return nullptr;}
   else {auto native=reinterpret_cast<void(*)(void*,void*)>(il2cpp_resolve_icall("UnityEngine.Font::Internal_CreateFontFromPath(UnityEngine.Font,System.String)"));if(!native||!FontInitializer([&]{native(font,path);}))return nullptr;}
  }else{
   auto create=ExactMethod(klass,"Internal_CreateDynamicFont",{"UnityEngine.Font","System.String[]","System.Int32"});
   auto set=ExactMethod(Class("System","Array"),"SetValue",{"System.Object","System.Int32"});if(!set)return nullptr;
   auto array=il2cpp_array_new(Class("System","String"),3);if(!array)return nullptr;Root heldNames(array);int i=0;
   for(auto name:{"Microsoft YaHei","SimHei","Arial"}){auto value=il2cpp_string_new(name);Root heldName(value);void* args[]={value,&i};if(!FontCall(set,array,args,result))return nullptr;i++;}
   int size=32;void* args[]={font,array,&size};
   if(create){if(!FontCall(create,nullptr,args,result))return nullptr;}
   else {auto native=reinterpret_cast<void(*)(void*,void*,int)>(il2cpp_resolve_icall("UnityEngine.Font::Internal_CreateDynamicFont(UnityEngine.Font,System.String[],System.Int32)"));if(!native||!FontInitializer([&]{native(font,array,size);}))return nullptr;}
  }
  if(!RetainGeneratedFont(font))return nullptr;
  // Publish a handle only after initialization and native-lifetime retention.
  handle=il2cpp_gchandle_new(font,false);if(!handle)return nullptr;
 }
 auto font=il2cpp_gchandle_get_target(handle);if(!tmp)return font;
 if(!tmpFont){
  auto klass=Class("TMPro","TMP_FontAsset");void* args[]={font};void* made=nullptr;
  if(!FontCall(ExactMethod(klass,"CreateFontAsset",{"UnityEngine.Font"}),nullptr,args,made)||!made)return nullptr;Root held(made);
  if(!RetainGeneratedFont(made))return nullptr;
  bool allow=true;void* grow[]={&allow};void* result=nullptr;FontCall(Method(klass,"set_isMultiAtlasTexturesEnabled",1),made,grow,result);
  tmpFont=il2cpp_gchandle_new(made,false);
 }
 return il2cpp_gchandle_get_target(tmpFont);
}
static bool Covers(void* font,bool tmp,const std::string& text,bool* repaired=nullptr){auto& cache=tmp?tmpCoverage:legacyCoverage;if(repaired)*repaired=false;if(!Alive(font))return false;if(!tmp&&cache.count(text))return true;bool covered=false;
 // TMP's character tables and atlas can be cleared without replacing the font.
 // A historical positive is evidence of a reset, never current glyph proof.
 // PrepareGlyphs already paces stable checks to once per 500 ms per binding.
 if(tmp){auto klass=Class("TMPro","TMP_FontAsset");auto check=ExactMethod(klass,"HasCharacters",{"System.String"});auto value=il2cpp_string_new(text.c_str());Root held(value);void* args[]={value};covered=Bool(Invoke(check,font,args));if(!covered){if(cache.count(text)){cache.clear();tmpGlyphRevision++;}void* missing=nullptr;bool include=true;void* add[]={args[0],&missing,&include};Invoke(ExactMethod(klass,"TryAddCharacters",{"System.String","System.String&","System.Boolean"}),font,add);covered=Bool(Invoke(check,font,args));if(covered){tmpAtlasRepairs++;if(repaired)*repaired=true;}}}
 else {auto has=ExactMethod(Class("UnityEngine","Font"),"HasCharacter",{"System.Int32"});auto s=il2cpp_string_new(text.c_str());Root held(s);auto chars=il2cpp_string_chars(s);int n=il2cpp_string_length(s);covered=has!=nullptr;for(int i=0;i<n&&covered;i++){int cp=chars[i];if(cp==10||cp==13||cp==9)continue;if(cp>=0xd800&&cp<=0xdbff&&i+1<n){int lo=chars[++i];cp=0x10000+((cp-0xd800)<<10)+(lo-0xdc00);}void* args[]={&cp};void* result=nullptr;covered=FontCall(has,font,args,result)&&Bool(result);}}
 if(covered&&cache.size()<150000)cache.insert(text);return covered;
}
#include "NativeFontFallback.h"
static bool PrepareGlyphs(Binding& b,void* target,const std::string& text,bool& repaired){
 auto& f=families[b.family];auto prior=Invoke(f.getFont,target);auto now=GetTickCount64();
 repaired=false;
 if(b.checkedFont&&b.checkedFont!=prior)fontChanges++;
 if(b.glyphReady&&b.checkedFont==prior&&Alive(prior)&&b.glyphText==text&&(!f.tmp||b.glyphRevision==tmpGlyphRevision)&&now<b.fontCheckAt)return true;
 bool wasReady=b.glyphReady;auto previous=b.checkedFont;
 b.checkedFont=prior;b.glyphText=text;b.glyphReady=false;b.fontCheckAt=now+500;
 if(f.tmp){
  auto required=IntroducedGlyphs(b.source,text);
  if(HasGlyphs(prior,required)){b.glyphReady=true;b.glyphMode="original";}
  else {
    auto font=EnsureFont(true);auto before=fallbackAdds;bool atlasRepaired=false;
    if(font&&Covers(font,true,required,&atlasRepaired)&&AttachFallback(prior,font)){
     b.glyphReady=true;b.glyphMode="fallback";repaired=atlasRepaired||fallbackAdds!=before;
   }
  }
  // Never alternate a game's theme font and our replacement font. A missing
  // fallback API remains visible as a glyph-support failure, not a write storm.
  if(!b.glyphReady){b.glyphMode="unsupported";return false;}
   repaired=repaired||!wasReady||previous!=prior||b.glyphRevision!=tmpGlyphRevision;b.glyphRevision=tmpGlyphRevision;return true;
 }
 auto font=EnsureFont(false);if(!font||!Covers(font,false,text))return false;
 // Legacy UI.Text has no TMP fallback table. Retain its existing font lease
 // across repeated source writes; restoring and reacquiring it dirties layout.
 if(prior!=font){
  if(b.oldFont)il2cpp_gchandle_free(b.oldFont);
  b.oldFont=prior?il2cpp_gchandle_new(prior,false):0;
  void* args[]={font};Invoke(f.setFont,target,args);fontAssignments++;
  if(Invoke(f.getFont,target)!=font)return false;
  repaired=true;
 }
 b.ownedFont=font;b.checkedFont=font;b.glyphMode="component";b.glyphReady=true;return true;
}
static const std::string* Lookup(const std::string& s){auto t=translations.find(s);if(t!=translations.end())return &t->second;auto early=startupTranslations.find(s);return early==startupTranslations.end()?nullptr:&early->second;}
static void Queue(const std::string& s){if(Eligible(s)&&visible.size()<4096&&queued.insert(s).second)visible.push_back(s);}
static void Refresh(Binding& b,void* target){
 auto& f=families[b.family];bool wasActive=b.active;b.active=!f.active||Bool(Invoke(f.active,target));b.shown=false;
 if(!enabled){Restore(b,target);return;}if(!b.active)return;
 auto now=GetTickCount64();if(!wasActive&&Eligible(b.source))b.latency.Begin(now);auto text=Text(Invoke(f.get,target));
 if(!b.applied.empty()&&text!=b.applied){
  textOverwrites++;
  // A source writer invalidates only the text receipt, not the font/size lease.
  // Rebuilding those leases on every source rewrite causes unrelated UI dirties.
  b.applied.clear();
 }
 if(b.applied.empty()&&b.source!=text){
  RestoreFit(b.fit,target,f.klass);RestoreLegacy(b.legacy,target,f.klass);SourceChanged(b,text);b.retryAt=0;b.fontBlocked=false;b.queueAt=0;b.glyphReady=false;
  if(Eligible(text)){captured++;Queue(text);b.queueAt=now+15000;}
 }
 auto hit=Lookup(b.source);
 if(!hit){
  if(b.ownedFont)Restore(b,target);
  if(now>=b.queueAt){Queue(b.source);b.queueAt=now+15000;}
  return;
 }
 CacheReady(b.latency,now);
 if(now<b.retryAt)return;
 bool repaired=false;
 if(!f.tmp)CaptureLegacy(b.legacy,target,f.klass,b.source,!b.ownedFont||Invoke(f.getFont,target)!=b.ownedFont);
 if(!PrepareGlyphs(b,target,*hit,repaired)){FontReady(b.latency,false,GetTickCount64());Restore(b,target);b.fontBlocked=true;b.retryAt=now+1000;return;}
 FontReady(b.latency,true,GetTickCount64());b.fontBlocked=false;
 auto display=*hit;
 if(!f.tmp){
  if(repaired||!wasActive||b.legacy.input!=*hit||now>=b.fitAt){b.fitAt=now+500;display=PrepareLegacyLayout(b.legacy,target,f.klass,b.ownedFont,*hit);}
  else if(!b.legacy.display.empty())display=b.legacy.display;
 }
 if(f.tmp&&(repaired||b.fit.text!=*hit||now>=b.fitAt)){b.fitAt=now+500;FitText(b.fit,target,f.klass,*hit,repaired);}
 // A font repair is not a new translation and must not rewrite an unchanged
 // string. A component readback is also not proof of its rendered glyphs.
 if(text==display){
  unchangedTextChecks++;if(repaired&&f.tmp)InvalidateGlyphs(target,f.klass);
  b.applied=display;b.shown=true;ReadbackReady(b.latency,GetTickCount64());return;
 }
 auto value=il2cpp_string_new(display.c_str());Root held(value);void* args[]={value};Invoke(f.set,target,args);
 if(Text(Invoke(f.get,target))==display){b.applied=display;b.shown=true;confirmed++;ReadbackReady(b.latency,GetTickCount64());}
 else{failed++;Restore(b,target);b.retryAt=now+10000;}
}
static size_t familyCursor=0;static ULONGLONG discoverAt=0;static int refreshCursor=0;static double maxTickMs=0,maxDiscoveryMs=0;
static uint64_t discoveryLimited=0;
static uint32_t discoveryArray=0,discoveryRemaining=0,discoveryLength=0;
static size_t discoveryFamily=0;static ULONGLONG discoveryStarted=0;
static uint64_t discoverySnapshots=0,discoveryExpirations=0;
static void ReleaseDiscovery(){if(discoveryArray)il2cpp_gchandle_free(discoveryArray);discoveryArray=0;discoveryRemaining=0;discoveryLength=0;}
static void Discover(){
 if(families.empty())return;auto began=Clock::now();auto now=GetTickCount64();
 // Keep at most one rooted engine snapshot for at most two seconds. Continue
 // its bounded chunks on normal pumps rather than allocating/sorting a new
 // scene-wide snapshot and waiting 200 ms for every 128 objects.
 if(discoveryArray&&now-discoveryStarted>=2000){ReleaseDiscovery();discoveryExpirations++;}
 if(!discoveryArray){
  if(now<discoverAt)return;discoverAt=now+200;discoveryFamily=familyCursor++%families.size();
  auto& f=families[discoveryFamily];void* args[]={f.type};auto array=Invoke(findObjects,nullptr,args);
  discoverySnapshots++;
  if(!array)return;discoveryArray=il2cpp_gchandle_new(array,false);discoveryLength=il2cpp_array_length(array);discoveryRemaining=discoveryLength;discoveryStarted=now;
  if(!discoveryArray||!discoveryLength){ReleaseDiscovery();return;}
 }
 auto array=il2cpp_gchandle_get_target(discoveryArray);if(!array){ReleaseDiscovery();return;}
 auto& f=families[discoveryFamily];
 for(uint32_t visited=0;discoveryRemaining&&visited<128;visited++){
  if(visited&&Clock::now()-began>std::chrono::milliseconds(2))break;
  f.cursor%=discoveryLength;int index=(int)f.cursor++;discoveryRemaining--;void* a[]={&index};auto target=Invoke(arrayGet,array,a);
  if(!Alive(target))continue;auto id=Invoke(instanceId,target);if(!id)continue;int key=*(int*)il2cpp_object_unbox(id);auto existing=bindings.find(key);
  if(existing!=bindings.end()&&il2cpp_gchandle_get_target(existing->second.weak)!=target){Release(existing->second);bindings.erase(existing);}
  if(!bindings.count(key)){
   if(bindings.size()>=8192){discoveryLimited++;continue;}Binding b;b.key=key;b.weak=il2cpp_gchandle_new_weakref(target,false);b.family=discoveryFamily;
   auto inserted=bindings.emplace(key,std::move(b));
   // A ready cached label need not queue behind thousands of newly found,
   // untranslated controls. This work shares the same discovery time budget.
   if(Lookup(Text(Invoke(f.get,target))))Refresh(inserted.first->second,target);else Prioritize(key);
  }
 }
 if(!discoveryRemaining)ReleaseDiscovery();
 maxDiscoveryMs=std::max(maxDiscoveryMs,std::chrono::duration<double,std::milli>(Clock::now()-began).count());
}
static int PendingRestore(){int n=0;for(auto& p:bindings)if(!p.second.applied.empty()||p.second.ownedFont||p.second.fit.owned>0||LegacyPending(p.second.legacy))n++;return n;}
static void RefreshOne(std::map<int,Binding>::iterator it){auto& b=it->second;auto target=il2cpp_gchandle_get_target(b.weak);if(!Alive(target)){Release(b);bindings.erase(it);}else Refresh(b,target);}
static void RefreshSlice(){
 auto begin=Clock::now();int count=0;
 // Cache arrivals are serviced ahead of old inactive scene objects. Leave a
 // reservation for the rolling pass so sustained arrivals cannot starve it.
 while(!priorityRefresh.empty()&&count<32){int key=priorityRefresh.front();priorityRefresh.pop_front();priorityQueued.erase(key);auto it=bindings.find(key);if(it!=bindings.end()){RefreshOne(it);count++;}if(Clock::now()-begin>std::chrono::milliseconds(1))break;}
 int remaining=(int)std::min(bindings.size(),size_t(64-count));while(!bindings.empty()&&remaining-->0){auto it=bindings.upper_bound(refreshCursor);if(it==bindings.end())it=bindings.begin();refreshCursor=it->first;RefreshOne(it);if(Clock::now()-begin>std::chrono::milliseconds(2))break;}
}
static void ObserveTextObject(void* target){
 if(!Alive(target))return;auto klass=il2cpp_object_get_class(target);size_t fi=families.size();
 for(int depth=0;klass&&depth<32&&fi==families.size();depth++,klass=il2cpp_class_get_parent(klass))for(size_t i=0;i<families.size();i++)if(klass==families[i].klass){fi=i;break;}
 if(fi==families.size())return;auto value=Invoke(instanceId,target);if(!value)return;int key=*reinterpret_cast<int*>(il2cpp_object_unbox(value));auto it=bindings.find(key);
 if(it!=bindings.end()&&il2cpp_gchandle_get_target(it->second.weak)!=target){Release(it->second);bindings.erase(it);it=bindings.end();}
 if(it==bindings.end()){if(bindings.size()>=8192)return;Binding b;b.key=key;b.weak=il2cpp_gchandle_new_weakref(target,false);b.family=fi;it=bindings.emplace(key,std::move(b)).first;}
 // A setter/enable can run inside Unity's graphics or font-update traversal.
 // Capture identity here; perform mutations at the next safe pump.
 Prioritize(key);
}
#include "NativeTextHooks.h"
#include "NativeUiDiagnostics.h"
static Json LatencyProgress(){
 uint64_t cache=0,font=0,control=0,oldest=0;auto now=GetTickCount64();
 if(enabled)for(auto& p:bindings){auto& b=p.second;auto& m=b.latency;if(!b.active||!m.started||m.done)continue;oldest=std::max(oldest,(uint64_t)(now-m.observedAt));if(!m.cached)cache++;else if(m.fontBlocked)font++;else control++;}
 return {{"observedToCacheSamples",observedToCache.samples},{"observedToCacheLastMs",observedToCache.lastMs},{"observedToCacheMaxMs",observedToCache.maxMs},{"cacheToReadbackSamples",cacheToReadback.samples},{"cacheToReadbackLastMs",cacheToReadback.lastMs},{"cacheToReadbackMaxMs",cacheToReadback.maxMs},{"glyphWaitSamples",glyphWait.samples},{"glyphWaitLastMs",glyphWait.lastMs},{"glyphWaitMaxMs",glyphWait.maxMs},{"waitingForCache",cache},{"waitingForFont",font},{"waitingForReadback",control},{"oldestObservedMs",oldest},{"maxDiscoveryMs",maxDiscoveryMs},{"discoverySnapshots",discoverySnapshots},{"lastPumpAgeMs",now-lastPumpAt.load()}};
}
static Json Progress(){int shown=0,waiting=0,fonts=0;blocked=0;for(auto& p:bindings){auto& b=p.second;if(b.ownedFont)fonts++;if(!b.active)continue;if(b.fontBlocked)blocked++;if(b.shown)shown++;else if(enabled&&Lookup(b.source))waiting++;}
 return {{"latency",LatencyProgress()},{"latencySemantics","monotonic-first-active-observation-cache-glyph-and-control-readback-not-pixels"},{"epoch",epoch},{"enabled",enabled},{"bindings",bindings.size()},{"bindingLimit",8192},{"discoveryLimited",discoveryLimited},{"runtimeWarning",discoveryLimited?"本次运行部分文字控件扫描达到容量上限；场景变化后继续读取":""},{"mainThreadWake",wakeHook!=nullptr},{"wakeTicks",wakeTicks},{"renderTicks",renderTicks},{"textChangeHooks",installedTextHooks},{"enableHooks",installedEnableHooks},{"compiledPreRender",renderOriginal!=nullptr},{"priorityPending",priorityRefresh.size()},{"lastPumpAgeMs",GetTickCount64()-lastPumpAt.load()},{"countsSemantics","last-budgeted-control-readback-not-visible-glyphs"},{"rawCaptureCount",captured},{"rawCaptureCountSemantics","lifetime-component-source-transitions"},{"writebacksConfirmed",confirmed},{"writebacksFailed",failed},{"writebackPending",waiting},{"lastVerifiedBindings",shown},{"activeRenderedBindings",shown},{"activeCachedWaitingBindings",waiting},{"fontBlockedBindings",blocked},{"fontLifetimeResets",fontLifetimeResets},{"fontManagedFailures",fontManagedFailures},{"fontsOwned",fonts},{"fontStatus",blocked?"glyph-support-unavailable":"ready"},{"writebackCountSemantics","successful-text-set-and-readback-including-repeat-sources"},{"sameTextWritesSkipped",unchangedTextChecks},{"sourceOverwrites",textOverwrites},{"observedFontChanges",fontChanges},{"componentFontAssignments",fontAssignments},{"fallbackFontAttachments",fallbackAdds},{"fallbackFontRemovals",fallbackRemoves},{"fallbackFontErrors",fallbackErrors},{"fallbackFontLists",fallbackLeases.size()}};}
static Json Handle(const Json& request){std::string op=request.value("op","");
 if(op=="translationPrepare"){if(!request.value("preserveStartup",false))startupTranslations.clear();epoch++;preparing=true;staged.clear();visible.clear();queued.clear();return {{"epoch",epoch}};}
 if(op=="translationEnable"){if(preparing){translations.swap(staged);staged.clear();preparing=false;}enabled=true;discoverAt=0;for(auto& p:bindings){p.second.retryAt=0;Queue(p.second.source);p.second.queueAt=GetTickCount64()+15000;}return {{"epoch",epoch},{"enabled",true}};}
 if(op=="translationDisable"){if(enabled||preparing)epoch++;enabled=false;ReleaseDiscovery();preparing=false;staged.clear();RefreshSlice();int n=PendingRestore();if(n==0)RemoveFallbacksSlice();n+=(int)fallbackLeases.size();return {{"epoch",epoch},{"enabled",false},{"restored",n==0},{"restorePending",n}};}
 if(op=="translationApply"){if(request.value("epoch",-1)!=epoch)throw std::runtime_error("Translation session changed; stale result rejected.");auto entries=request.at("entries");if(!entries.is_array()||entries.size()>128)throw std::runtime_error("Invalid translation batch");bool prime=request.value("prime",false);if(prime&&!preparing)throw std::runtime_error("No prepared session");auto& dest=prime?staged:translations;if(dest.size()+entries.size()>150000)throw std::runtime_error("Cache limit exceeded");
  for(auto& row:entries){auto s=row.at("source").get<std::string>();auto t=row.at("text").get<std::string>();if(!Eligible(s)||t.empty()||t.size()>96000)throw std::runtime_error("Invalid translation");}
  for(auto& row:entries){auto source=row.at("source").get<std::string>();dest[source]=row.at("text").get<std::string>();if(!prime){auto at=sourceBindings.find(source);if(at!=sourceBindings.end())for(int key:at->second){auto binding=bindings.find(key);if(binding!=bindings.end()){binding->second.retryAt=0;CacheReady(binding->second.latency,GetTickCount64());}Prioritize(key);}}}return {{"epoch",epoch},{"stored",entries.size()},{"applied",entries.size()},{"writebackConfirmed",false}};}
 if(op=="translationPoll"){Json result=Progress();result["texts"]=Json::array();result["ahead"]=Json::array();while(!visible.empty()&&result["texts"].size()<128){auto s=std::move(visible.front());visible.pop_front();queued.erase(s);result["texts"].push_back(s);}return result;}
 if(op=="translationCatalog")return {{"total",0},{"next",0},{"skipped",0},{"texts",Json::array()}};
 if(op=="snapshot")return {{"generation",epoch},{"rows",Json::array()},{"state","translationOnly"}};
 if(op=="translationDiagnostics"){auto result=Progress();auto app=Class("UnityEngine","Application");for(auto key:{"companyName","productName","persistentDataPath","unityVersion"})result[key]=Text(Invoke(Method(app,(std::string("get_")+key).c_str(),0)));result["hookMode"]="native-runtime-api";result["preRenderAvailable"]=renderOriginal!=nullptr||renderTicks>0;result["legacyLayouts"]=legacyLayouts;result["legacyAspectCorrections"]=legacyAspectCorrections;result["legacyLayoutUnavailable"]=legacyLayoutUnavailable;result["maxTickMs"]=maxTickMs;result["maxDiscoveryMs"]=maxDiscoveryMs;result["cache"]=translations.size();result["startupExactCache"]=startupTranslations.size();DiagnosticBindings(request,result);return result;}
 throw std::runtime_error("This bridge provides translation only");}
struct Request {Json data,response;std::mutex lock;std::condition_variable wake;int state=0;}; // queued, started, completed, cancelled
static std::mutex queueLock;static std::deque<std::shared_ptr<Request>> requests;
static bool Initialize(){auto object=Class("UnityEngine","Object"),array=Class("System","Array");if(!object||!array)return false;findObjects=ExactMethod(object,"FindObjectsOfType",{"System.Type"});arrayGet=ExactMethod(array,"GetValue",{"System.Int32"});objectAlive=Method(object,"op_Implicit",1);instanceId=Method(object,"GetInstanceID",0);if(!findObjects||!arrayGet||!objectAlive||!instanceId)return false;
 for(auto pair:std::vector<std::pair<const char*,const char*>>{{"UnityEngine.UI","Text"},{"TMPro","TextMeshProUGUI"},{"TMPro","TextMeshPro"}}){auto c=Class(pair.first,pair.second);if(!c)continue;Family f;f.klass=c;f.type=il2cpp_type_get_object(il2cpp_class_get_type(c));f.get=Method(c,"get_text",0);f.set=Method(c,"set_text",1);f.getFont=Method(c,"get_font",0);f.setFont=Method(c,"set_font",1);f.getMaterial=Method(c,"get_fontSharedMaterial",0);f.setMaterial=Method(c,"set_fontSharedMaterial",1);f.active=Method(c,"get_isActiveAndEnabled",0);f.tmp=std::string(pair.first)=="TMPro";if(f.type&&f.get&&f.set&&f.getFont&&f.setFont)families.push_back(f);}
 quitMethod=Method(Class("UnityEngine","Application"),"Internal_ApplicationQuit",0);canvasMethod=Method(Class("UnityEngine","Canvas"),"SendWillRenderCanvases",0);Log("ready families="+std::to_string(families.size())+" preRender="+std::to_string(canvasMethod!=nullptr));return !families.empty();}
static void Tick(){auto begin=Clock::now();if(!ready){if(!Initialize())return;ready=true;InstallTextHooks();StartWake();}if(disconnectPending.exchange(false)){restoringDisconnect=true;enabled=false;preparing=false;staged.clear();epoch++;}
 std::shared_ptr<Request> request;{std::lock_guard<std::mutex> guard(queueLock);if(!requests.empty()){request=requests.front();requests.pop_front();}}
 if(request){std::unique_lock<std::mutex> guard(request->lock);if(request->state==0){request->state=1;guard.unlock();Json response;int id=request->data.value("id",0);try{response={{"id",id},{"ok",true},{"result",Handle(request->data)}};}catch(const std::exception& e){response={{"id",id},{"ok",false},{"error",e.what()}};}guard.lock();request->response=std::move(response);request->state=2;guard.unlock();request->wake.notify_one();}}
 if(enabled)Discover();else ReleaseDiscovery();RefreshSlice();if(!enabled&&PendingRestore()==0){bool restored=RemoveFallbacksSlice();if(restoringDisconnect&&restored){restoringDisconnect=false;wakeFinished=true;Log("disconnect restoration completed");}}maxTickMs=std::max(maxTickMs,std::chrono::duration<double,std::milli>(Clock::now()-begin).count());}
static bool UiRebuilding(){
 static void* graphics=nullptr,*layout=nullptr;
 if(!graphics||!layout){auto klass=Class("UnityEngine.UI","CanvasUpdateRegistry");graphics=Method(klass,"IsRebuildingGraphics",0);layout=Method(klass,"IsRebuildingLayout",0);}
 return Bool(Invoke(graphics))||Bool(Invoke(layout));
}
static void Pump(){if(inside||runtimeStopping||GetCurrentThreadId()!=config.mainThread)return;inside=true;
 try{if(!ready||!UiRebuilding()){lastPumpAt=GetTickCount64();nextTick=lastPumpAt+16;Tick();}}
 catch(const std::exception& e){Log(std::string("runtime ")+e.what());}inside=false;}
static thread_local int invokeDepth=0;
static void* HookInvoke(void* method,void* instance,void** args,void** exception){
 if(quitMethod&&method==quitMethod)runtimeStopping=true;
 struct InvocationScope{InvocationScope(){invokeDepth++;}~InvocationScope(){invokeDepth--;}};
 bool outer=invokeDepth==0,pre=outer&&!inside&&!runtimeStopping&&!renderOriginal&&method==canvasMethod&&GetCurrentThreadId()==config.mainThread;
 if(pre){renderTicks++;nextTick=GetTickCount64()+16;Pump();}
 void* result;{InvocationScope scope;result=originalInvoke(method,instance,args,exception);}
 // Nested callbacks may return while Unity is still enumerating a canvas or
 // font-update list. Only the outermost invocation may service queued work.
 if(outer&&!pre&&!inside&&GetCurrentThreadId()==config.mainThread&&GetTickCount64()>=nextTick){nextTick=GetTickCount64()+16;Pump();}
 return result;
}
static bool WriteLine(HANDLE pipe,const Json& obj){auto line=obj.dump()+"\n";DWORD written=0;return line.size()<8*1024*1024&&WriteFile(pipe,line.data(),(DWORD)line.size(),&written,nullptr)&&written==line.size();}
static void LogTelemetry(const Json& response){
 // Worker-side I/O only. No dialogue, credentials or object addresses in this
 // bounded session trace; readback and glyph-support counters stay separate.
 static ULONGLONG due=0;static int lines=0;
 auto now=GetTickCount64();if(now<due||lines>=720||!response.contains("result"))return;
 const auto& result=response["result"];if(!result.contains("sameTextWritesSkipped"))return;
 due=now+5000;lines++;Json row={{"sessionMs",now}};
 for(auto name:{"bindings","writebacksConfirmed","writebacksFailed","sameTextWritesSkipped","sourceOverwrites","observedFontChanges","componentFontAssignments","fallbackFontAttachments","fallbackFontRemovals","fallbackFontErrors","fallbackFontLists","fontBlockedBindings","fontLifetimeResets","fontManagedFailures","activeCachedWaitingBindings","priorityPending","lastPumpAgeMs","mainThreadWake","textChangeHooks","compiledPreRender"})if(result.contains(name))row[name]=result[name];
 if(result.contains("latency"))row["latency"]=result["latency"];
 Log("text-stability "+row.dump());
}
static DWORD WINAPI Worker(void*){try{
 auto p=Env(L"FUSION_UNITY_PIPE"),s=Env(L"FUSION_UNITY_SECRET");if(p.empty()||p.size()>128||s.size()!=64)return 1;pipeName=Utf8(p);secret=Utf8(s);enabled=Env(L"FUSION_UNITY_TRANSLATION_START")==L"1";
 auto startup=Env(L"FUSION_UNITY_STARTUP_FILE");if(enabled&&!startup.empty()){auto doc=ReadJson(startup,32*1024*1024);if(doc.at("secret")!=secret||!doc.at("entries").is_array()||doc.at("entries").size()>150000)throw std::runtime_error("Startup cache validation failed");for(auto& row:doc.at("entries")){auto src=row.at("source").get<std::string>(),text=row.at("text").get<std::string>();if(Eligible(src)&&!text.empty()&&text.size()<=96000)startupTranslations[src]=text;}}
 for(int i=0;i<12000;i++){assembly=GetModuleHandleW(L"GameAssembly.dll");if(assembly)break;Sleep(5);}if(!assembly)throw std::runtime_error("Runtime module timeout");
 std::ifstream map(std::wstring(config.root)+L"\\api-map.tsv",std::ios::binary);if(!map)throw std::runtime_error("API map unavailable");std::string line;int count=0;while(std::getline(map,line)){if(line.size()>256||++count>300)throw std::runtime_error("Invalid API map");auto t=line.find('\t');if(t==std::string::npos)throw std::runtime_error("Invalid API mapping");aliases[line.substr(0,t)]=line.substr(t+1);}
 if(!BindApis())throw std::runtime_error("Runtime API unavailable");if(MH_Initialize()!=MH_OK||MH_CreateHook((void*)il2cpp_runtime_invoke,(void*)HookInvoke,(void**)&originalInvoke)!=MH_OK||MH_EnableHook((void*)il2cpp_runtime_invoke)!=MH_OK)throw std::runtime_error("Runtime callback could not be installed");
 for(int i=0;i<1800&&!ready;i++)Sleep(100);if(!ready)throw std::runtime_error("Runtime did not initialize");
 HANDLE pipe=INVALID_HANDLE_VALUE;auto path=L"\\\\.\\pipe\\"+p;for(int i=0;i<100&&pipe==INVALID_HANDLE_VALUE;i++){pipe=CreateFileW(path.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr);if(pipe==INVALID_HANDLE_VALUE)Sleep(50);}if(pipe==INVALID_HANDLE_VALUE)throw std::runtime_error("Desktop pipe unavailable");
 if(!WriteLine(pipe,{{"hello",1},{"pid",GetCurrentProcessId()},{"secret",secret},{"engine","unity"},{"protocol",1}})){CloseHandle(pipe);throw std::runtime_error("Handshake failed");}
 std::string buffer;char bytes[8192];DWORD n;bool running=true;while(running&&ReadFile(pipe,bytes,sizeof(bytes),&n,nullptr)&&n){buffer.append(bytes,n);if(buffer.size()>1024*1024)break;size_t end;while((end=buffer.find('\n'))!=std::string::npos){auto part=buffer.substr(0,end);buffer.erase(0,end+1);auto request=std::make_shared<Request>();try{request->data=Json::parse(part);if(!request->data.is_object()||!request->data.at("id").is_number_integer())throw std::runtime_error("Invalid request");}catch(...){running=false;break;}
  {std::lock_guard<std::mutex> guard(queueLock);requests.push_back(request);}std::unique_lock<std::mutex> lock(request->lock);bool done=request->wake.wait_for(lock,std::chrono::seconds(2),[&]{return request->state==2;});if(!done&&request->state==0){request->state=3;request->response={{"id",request->data.at("id")},{"ok",false},{"error","Unity main thread is paused; request was not started."},{"errorCode","unity-main-thread-paused"},{"recoverable",true}};}
  else if(!done)request->wake.wait(lock,[&]{return request->state==2;});auto response=request->response;bool cancelled=request->state==3;lock.unlock();if(cancelled){std::lock_guard<std::mutex> guard(queueLock);auto it=std::find(requests.begin(),requests.end(),request);if(it!=requests.end())requests.erase(it);}LogTelemetry(response);if(!WriteLine(pipe,response)){running=false;break;}}}
 CloseHandle(pipe);disconnectPending=true;Log("desktop disconnected; restoration scheduled");return 0;
 }catch(const std::exception& e){Log(std::string("stopped: ")+e.what());disconnectPending=true;return 2;}}
extern "C" __declspec(dllexport) DWORD WINAPI FusionStart(Bootstrap* bootstrap){if(!bootstrap||bootstrap->size!=sizeof(Bootstrap)||!bootstrap->mainThread||bootstrap->root[1023]||started.exchange(1))return 1;config=*bootstrap;auto thread=CreateThread(nullptr,0,Worker,nullptr,0,nullptr);if(!thread)return 2;CloseHandle(thread);return 0;}
BOOL WINAPI DllMain(HINSTANCE module,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(module);return TRUE;}


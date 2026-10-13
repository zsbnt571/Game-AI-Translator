// Optional native Slate adapter. Resolve engine functions from a matching local
// PDB; never use a game's addresses, class names, text labels or saved data.
// Only display/measurement scopes substitute borrowed strings. FText storage,
// input, delegates and gameplay callers keep the original value.
#include <windows.h>
#include <dbghelp.h>
#include <string>
#include <vector>
#include <unordered_map>
#include <deque>
#include <mutex>
#include <atomic>
#include <memory>
#include <fstream>
#include <filesystem>
#include "MinHook.h"
#include "json.hpp"
using nlohmann::json;
namespace fs=std::filesystem;
enum {SymTagFunction=5,SymTagUDT=11,SymTagFunctionType=13,SymTagPointerType=14,SymTagBaseType=16};
struct FStringView {const wchar_t* data;int count,capacity;};
struct OwnedString {
 std::wstring value;FStringView view;
 explicit OwnedString(std::wstring s):value(std::move(s)),view{value.c_str(),(int)value.size()+1,(int)value.size()+1}{}
};
using Map=std::unordered_map<std::wstring,std::shared_ptr<OwnedString>>;
static std::shared_ptr<const Map> cache=std::make_shared<Map>();
// Engine calls may retain a const FString reference. Retired entries are not
// freed until process teardown; refuse growth beyond this explicit budget.
static std::vector<std::shared_ptr<OwnedString>> retained;
static size_t retainedUnits=0;
static std::wstring folder;
static std::string secret;
static std::atomic<bool> active{false},started{false};
static std::atomic<unsigned long long> displayHits{0},measureHits{0},captures{0},scopes{0},refreshes{0};
static std::atomic<unsigned> errors{0};
static std::mutex telemetry;
static std::unordered_map<std::wstring,ULONGLONG> visible;
struct Snapshot {std::wstring original,display;bool changed=false;};
static thread_local unsigned depth=0;
static thread_local std::unordered_map<void*,Snapshot> snapshots;
struct Scope {Scope(){++depth;++scopes;}~Scope(){--depth;}};
using Text=const FStringView*(__fastcall*)(const void*);
// UE4 FVector2D has constructors: MSVC returns it through the hidden second
// argument, even though its storage is only eight bytes.
using Measure=void*(__fastcall*)(void*,void*,float);
using Paint=int(__fastcall*)(void*,const void*,const void*,const void*,void*,int,const void*,bool);
using Equal=bool(__fastcall*)(void*,const void*);
using FontMeasure=void*(__fastcall*)(void*,void*,const void*,const void*,float);
static Text originalText;
static Measure originalMeasure;
static Paint originalPaint;
static Measure originalRichMeasure;
static Paint originalRichPaint;
static bool richTextScopes=false;
static Equal originalIdentical,originalEqual;
static FontMeasure originalStringMeasure,originalTextMeasure;
static bool Copy(const FStringView* view,wchar_t* data,int* size) {
 __try {
  if(!view||view->count<2||view->count>6001||view->capacity<view->count||view->capacity>1048576||!view->data)return false;
  int n=view->count-1;if(view->data[n])return false;
  for(int i=0;i<n;i++)if(!view->data[i]||(view->data[i]<32&&view->data[i]!=9&&view->data[i]!=10&&view->data[i]!=13))return false;
  memcpy(data,view->data,n*sizeof(wchar_t));*size=n;return true;
 }__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
static std::wstring ReadText(const FStringView* v){wchar_t buffer[6001];int n=0;return Copy(v,buffer,&n)?std::wstring(buffer,n):std::wstring();}
static std::wstring Wide(const std::string& v){int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,v.data(),(int)v.size(),nullptr,0);std::wstring s(n,0);if(n)MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,v.data(),(int)v.size(),s.data(),n);return s;}
static std::string Utf8(const std::wstring& v){int n=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,v.data(),(int)v.size(),nullptr,0,nullptr,nullptr);std::string s(n,0);if(n)WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,v.data(),(int)v.size(),s.data(),n,nullptr,nullptr);return s;}
static std::shared_ptr<OwnedString> Resolve(const std::wstring& source){
 if(!active.load(std::memory_order_acquire)||source.empty())return {};
 auto current=std::atomic_load(&cache);auto i=current->find(source);return i==current->end()?nullptr:i->second;
}
static void Observe(const std::wstring& s){
 if(s.empty()||!active.load(std::memory_order_relaxed))return;
 std::unique_lock<std::mutex> l(telemetry,std::try_to_lock);if(!l)return;
 if(visible.size()<4096||visible.count(s)){visible[s]=GetTickCount64();++captures;}
}
static const FStringView* __fastcall ToString(const void* text){
 auto raw=originalText(text);if(!depth)return raw;
 try{auto s=ReadText(raw);Observe(s);auto found=Resolve(s);if(found){++displayHits;return &found->view;}}
 catch(...){++errors;}return raw;
}
static void* __fastcall MeasureString(void* self,void* result,const void* text,const void* font,float scale){
 if(depth)try{auto source=ReadText((const FStringView*)text);Observe(source);auto found=Resolve(source);if(found){++measureHits;return originalStringMeasure(self,result,&found->view,font,scale);}}catch(...){++errors;}
 return originalStringMeasure(self,result,text,font,scale);
}
static void* __fastcall MeasureText(void* self,void* result,const void* text,const void* font,float scale){
 // Simple text uses a font-measure overload that may inline FText::ToString.
 // Substitute through the public FString overload only inside a text widget.
 if(depth)try{auto source=ReadText(originalText(text));Observe(source);auto found=Resolve(source);if(found){++measureHits;return originalStringMeasure(self,result,&found->view,font,scale);}}catch(...){++errors;}
 return originalTextMeasure(self,result,text,font,scale);
}
static void* __fastcall Compute(void* self,void* result,float scale){Scope scope;return originalMeasure(self,result,scale);}
static int __fastcall OnPaint(void* self,const void* args,const void* geometry,const void* rect,void* elements,int layer,const void* style,bool parent){Scope scope;return originalPaint(self,args,geometry,rect,elements,layer,style,parent);}
static void* __fastcall RichCompute(void* self,void* result,float scale){Scope scope;return originalRichMeasure(self,result,scale);}
static int __fastcall RichPaint(void* self,const void* args,const void* geometry,const void* rect,void* elements,int layer,const void* style,bool parent){Scope scope;return originalRichPaint(self,args,geometry,rect,elements,layer,style,parent);}
static bool __fastcall Identical(void* self,const void* text){
 bool same=originalIdentical(self,text);if(!depth)return same;
 try{
  auto s=ReadText(originalText(text));if(s.empty())return same;Observe(s);
  auto found=Resolve(s);auto display=found?found->value:s;
  if(snapshots.size()>=4096&&!snapshots.count(self))snapshots.clear();
  auto& previous=snapshots[self];
  // A cache arrival or disable invalidates this display snapshot exactly once.
  // Ordinary unchanged frames retain the engine's own cached layout.
  previous.changed=previous.original!=s||previous.display!=display;
  previous.original=std::move(s);previous.display=std::move(display);
  if(previous.changed){++refreshes;return false;}
 }catch(...){++errors;}return same;
}
static bool __fastcall DisplayEqual(void* self,const void* text){
 if(depth){auto i=snapshots.find(self);if(i!=snapshots.end()&&i->second.changed){i->second.changed=false;return false;}}
 return originalEqual(self,text);
}
static std::wstring Env(const wchar_t* name){DWORD n=GetEnvironmentVariableW(name,nullptr,0);if(n<2||n>32768)return {};std::wstring s(n,0);n=GetEnvironmentVariableW(name,s.data(),n);s.resize(n);return s;}
static json Read(const wchar_t* name,size_t limit){auto p=fs::path(folder)/name;std::error_code e;auto n=fs::file_size(p,e);if(e||!n||n>limit)return {};std::ifstream f(p,std::ios::binary);std::string s((size_t)n,0);if(!f.read(s.data(),s.size()))return {};return json::parse(s,nullptr,false);}
static bool Write(const json& value){auto p=fs::path(folder)/L"slate-status.json",t=p;t+=L".tmp";auto data=value.dump(-1,' ',false,json::error_handler_t::replace);{std::ofstream f(t,std::ios::binary|std::ios::trunc);if(!f.write(data.data(),data.size()))return false;}return MoveFileExW(t.c_str(),p.c_str(),MOVEFILE_REPLACE_EXISTING)!=FALSE;}
static unsigned Put(Map& map,const json& entries){
 unsigned count=0;if(!entries.is_object())return 0;
 for(auto i=entries.begin();i!=entries.end();++i){if(!i.value().is_string()||i.key().size()>24000)continue;
  auto raw=i.value().get<std::string>();if(raw.empty()||raw.size()>48000)continue;
  auto s=Wide(i.key()),t=Wide(raw);if(s.empty()||t.empty()||s.find(L'\0')!=s.npos||t.find(L'\0')!=t.npos)continue;
  auto prior=map.find(s);if(prior!=map.end()&&prior->second->value==t){++count;continue;}
  if(retained.size()>=100000||retainedUnits+t.size()>16*1024*1024)continue;
  auto owned=std::make_shared<OwnedString>(std::move(t));retainedUnits+=owned->value.size();retained.push_back(owned);map[s]=owned;++count;
 }return count;
}
static bool Function(HANDLE process,DWORD64 base,const char* name,ULONG parameters,ULONG returnTag,ULONG returnBytes,void** result){
 alignas(SYMBOL_INFO) unsigned char buf[sizeof(SYMBOL_INFO)+1024]={};auto s=(SYMBOL_INFO*)buf;s->SizeOfStruct=sizeof(SYMBOL_INFO);s->MaxNameLen=1024;
 if(!SymFromName(process,name,s)||s->ModBase!=base||s->Tag!=SymTagFunction||s->Size<14)return false;
 ULONG tag=0,count=0,ret=0;ULONG64 length=0;
 if(!SymGetTypeInfo(process,base,s->TypeIndex,TI_GET_SYMTAG,&tag)||tag!=SymTagFunctionType||
    !SymGetTypeInfo(process,base,s->TypeIndex,TI_GET_COUNT,&count)||count!=parameters||
    !SymGetTypeInfo(process,base,s->TypeIndex,TI_GET_TYPE,&ret)||
    !SymGetTypeInfo(process,base,ret,TI_GET_SYMTAG,&tag)||tag!=returnTag||
    !SymGetTypeInfo(process,base,ret,TI_GET_LENGTH,&length)||length!=returnBytes)return false;
 *result=(void*)s->Address;return true;
}
static bool NamedType(HANDLE process,DWORD64 base,ULONG type,const wchar_t* expected,ULONG64 bytes){
 ULONG tag=0;ULONG64 length=0;PWSTR name=nullptr;
 if(!SymGetTypeInfo(process,base,type,TI_GET_SYMTAG,&tag)||tag!=SymTagUDT||!SymGetTypeInfo(process,base,type,TI_GET_LENGTH,&length)||length!=bytes||!SymGetTypeInfo(process,base,type,TI_GET_SYMNAME,&name))return false;
 bool match=wcscmp(name,expected)==0;LocalFree(name);return match;
}
struct OverloadSearch {HANDLE process;DWORD64 base;const wchar_t* argument;ULONG64 size;void* address=nullptr;unsigned count=0;};
static BOOL CALLBACK FindMeasure(PSYMBOL_INFO symbol,ULONG,void* context){
 auto& q=*(OverloadSearch*)context;ULONG count=0,ret=0,children=0;
 if(symbol->Tag!=SymTagFunction||symbol->Size<14||symbol->ModBase!=q.base||
 !SymGetTypeInfo(q.process,q.base,symbol->TypeIndex,TI_GET_COUNT,&count)||count!=4||
 !SymGetTypeInfo(q.process,q.base,symbol->TypeIndex,TI_GET_TYPE,&ret)||!NamedType(q.process,q.base,ret,L"FVector2D",8)||
 !SymGetTypeInfo(q.process,q.base,symbol->TypeIndex,TI_GET_CHILDRENCOUNT,&children)||children!=3)return TRUE;
 struct {ULONG Count,Start,ids[3];} ids{3,0,{}};
 if(!SymGetTypeInfo(q.process,q.base,symbol->TypeIndex,TI_FINDCHILDREN,&ids))return TRUE;
 for(unsigned i=0;i<3;i++){
  ULONG type=0,tag=0,related=0;ULONG64 bytes=0;
  if(!SymGetTypeInfo(q.process,q.base,ids.ids[i],TI_GET_TYPE,&type)||!SymGetTypeInfo(q.process,q.base,type,TI_GET_SYMTAG,&tag)||!SymGetTypeInfo(q.process,q.base,type,TI_GET_LENGTH,&bytes))return TRUE;
  if(i<2){if(tag!=SymTagPointerType||bytes!=8||!SymGetTypeInfo(q.process,q.base,type,TI_GET_TYPE,&related)||!NamedType(q.process,q.base,related,i==0?q.argument:L"FSlateFontInfo",i==0?q.size:88))return TRUE;}
  else {ULONG basic=0;if(tag!=SymTagBaseType||bytes!=4||!SymGetTypeInfo(q.process,q.base,type,TI_GET_BASETYPE,&basic)||basic!=8)return TRUE;}
 }
 q.address=(void*)symbol->Address;++q.count;return TRUE;
}
static bool FontOverload(HANDLE process,DWORD64 base,const wchar_t* argument,ULONG64 size,void** result){
 OverloadSearch q{process,base,argument,size};
 if(!SymEnumSymbols(process,base,"FSlateFontMeasure::Measure",FindMeasure,&q)||q.count!=1)return false;
 *result=q.address;return true;
}
static bool Install(){
 if(Env(L"FUSION_UNREAL_ENGINE")!=L"4.27")return false;
 wchar_t name[32768];if(!GetModuleFileNameW(nullptr,name,32768))return false;
 auto executable=fs::path(name),pdb=executable;pdb.replace_extension(L".pdb");if(!fs::exists(pdb))return false;
 // A distinct symbol-session handle avoids modifying another component's
 // symbol table. Exact PDB matching and a local-only path are mandatory.
 HANDLE symbols=CreateEventW(nullptr,TRUE,FALSE,nullptr);if(!symbols)return false;
 auto old=SymGetOptions();SymSetOptions(SYMOPT_UNDNAME|SYMOPT_LOAD_LINES|SYMOPT_EXACT_SYMBOLS|SYMOPT_FAIL_CRITICAL_ERRORS|SYMOPT_NO_PROMPTS|SYMOPT_IGNORE_NT_SYMPATH);
 bool ready=SymInitializeW(symbols,executable.parent_path().c_str(),FALSE)!=FALSE;
 auto base=ready?SymLoadModuleExW(symbols,nullptr,name,nullptr,(DWORD64)GetModuleHandleW(nullptr),0,nullptr,0):0;
 void *text=nullptr,*measure=nullptr,*paint=nullptr,*identical=nullptr,*equal=nullptr,*stringMeasure=nullptr,*textMeasure=nullptr;
 void *richMeasure=nullptr,*richPaint=nullptr;
 ready=base&&Function(symbols,base,"FText::ToString",1,SymTagPointerType,8,&text)&&
  Function(symbols,base,"STextBlock::ComputeDesiredSize",2,SymTagUDT,8,&measure)&&
  Function(symbols,base,"STextBlock::OnPaint",8,SymTagBaseType,4,&paint)&&
  Function(symbols,base,"FTextSnapshot::IdenticalTo",2,SymTagBaseType,1,&identical)&&
  Function(symbols,base,"FTextSnapshot::IsDisplayStringEqualTo",2,SymTagBaseType,1,&equal)&&
  FontOverload(symbols,base,L"FString",16,&stringMeasure)&&FontOverload(symbols,base,L"FText",24,&textMeasure);
 // Rich-text widgets have their own layout and paint scopes. Require both
 // named functions and the same locally verified UE4.27 return ABI; a missing
 // optional scope never grants broad process-wide text replacement.
 richTextScopes=ready&&Function(symbols,base,"SRichTextBlock::ComputeDesiredSize",2,SymTagUDT,8,&richMeasure)&&
  Function(symbols,base,"SRichTextBlock::OnPaint",8,SymTagBaseType,4,&richPaint)&&richMeasure!=measure&&richPaint!=paint;
 if(base||ready)SymCleanup(symbols);else SymCleanup(symbols);SymSetOptions(old);CloseHandle(symbols);
 if(!ready)return false;
 if(MH_Initialize()!=MH_OK)return false;
 struct Hook{void* address;void* callback;void** original;};
 std::vector<Hook> hooks={{text,(void*)ToString,(void**)&originalText},{measure,(void*)Compute,(void**)&originalMeasure},{paint,(void*)OnPaint,(void**)&originalPaint},{identical,(void*)Identical,(void**)&originalIdentical},{equal,(void*)DisplayEqual,(void**)&originalEqual},{stringMeasure,(void*)MeasureString,(void**)&originalStringMeasure},{textMeasure,(void*)MeasureText,(void**)&originalTextMeasure}};
 if(richTextScopes){hooks.push_back({richMeasure,(void*)RichCompute,(void**)&originalRichMeasure});hooks.push_back({richPaint,(void*)RichPaint,(void**)&originalRichPaint});}
 for(auto& hook:hooks)if(MH_CreateHook(hook.address,hook.callback,hook.original)!=MH_OK){MH_Uninitialize();return false;}
 if(MH_EnableHook(MH_ALL_HOOKS)!=MH_OK){MH_Uninitialize();return false;}return true;
}
static DWORD WINAPI Worker(void*){
 try{
  if(!Install()){Write({{"secret",secret},{"state","unavailable-local-symbols-or-abi"}});return 0;}
  auto initial=Read(L"startup.json",32*1024*1024);auto map=std::make_shared<Map>();
  if(initial.is_object()&&initial.value("secret","")==secret){if(initial.contains("entries"))Put(*map,initial["entries"]);if(initial.contains("displayEntries"))Put(*map,initial["displayEntries"]);}
  std::atomic_store(&cache,std::shared_ptr<const Map>(map));active=false;
  unsigned id=0,stored=0;ULONGLONG lastHeartbeat=GetTickCount64();
  for(;;){
   auto command=L"slate-command-"+std::to_wstring(id+1)+L".json";
   auto request=Read(command.c_str(),262144);
   if(request.is_object()&&request.value("secret","")==secret&&request.value("id",0u)>id){
    auto next=std::make_shared<Map>(request.value("reset",false)?Map():*std::atomic_load(&cache));
    stored=request.contains("entries")?Put(*next,request["entries"]):0;
    std::atomic_store(&cache,std::shared_ptr<const Map>(next));active=request.value("enabled",false);id=request.value("id",0u);lastHeartbeat=GetTickCount64();
    std::error_code e;fs::remove(fs::path(folder)/command,e);
   }
   // Lua sends a heartbeat while the owning translation session is alive.
   if(GetTickCount64()-lastHeartbeat>5000)active=false;
   json sources=json::array();
   {std::lock_guard<std::mutex> l(telemetry);auto now=GetTickCount64();for(auto i=visible.begin();i!=visible.end();)if(now-i->second>3000)i=visible.erase(i);else{if(sources.size()<256)sources.push_back(Utf8(i->first));++i;}}
   Write({{"secret",secret},{"state","ready"},{"id",id},{"stored",stored},{"enabled",active.load()},{"entries",std::atomic_load(&cache)->size()},
    {"richTextScopes",richTextScopes},{"scopes",scopes.load()},{"captures",captures.load()},{"displayHits",displayHits.load()},{"measureHits",measureHits.load()},{"layoutRefreshes",refreshes.load()},{"errors",errors.load()},{"texts",sources}});
   Sleep(100);
  }
 }catch(...){active=false;Write({{"secret",secret},{"state","failed"}});}return 0;
}
extern "C" __declspec(dllexport) int luaopen_FusionSlate(void*){
 if(started.exchange(true))return 0;
 folder=Env(L"FUSION_UNREAL_SESSION");secret=Utf8(Env(L"FUSION_UNREAL_SECRET"));if(folder.empty()||secret.size()!=64)return 0;
 auto thread=CreateThread(nullptr,0,Worker,nullptr,0,nullptr);if(thread)CloseHandle(thread);return 0;
}
BOOL WINAPI DllMain(HINSTANCE,DWORD,LPVOID){return TRUE;}

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <string>
#include <vector>
#include <map>
#include <fstream>
#include <mutex>
#include <algorithm>
#include "MinHook.h"

// Research bridge: no raw metadata layouts, no game methods and no background
// Unity calls. Bootstrap is only supplied by the isolated-copy test launcher.
struct Bootstrap { DWORD size,mainThread; wchar_t root[1024]; };
static Bootstrap config{};
static std::mutex logLock;
static std::map<std::string,std::string> aliases;
static HMODULE assembly;
static thread_local bool inside=false;
static ULONGLONG nextTick=0;
static int scans=0;
static std::map<std::string,std::string> translations;
static uint32_t dynamicFont=0,tmpFont=0;
static void Log(const std::string& s){std::lock_guard<std::mutex> g(logLock);std::ofstream f(std::wstring(config.root)+L"\\native-probe.log",std::ios::app|std::ios::binary);f<<s<<"\n";}
#define API(ret,name,...) static ret (*name)(__VA_ARGS__)
API(void*,il2cpp_domain_get);
API(void**,il2cpp_domain_get_assemblies,void*,size_t*);
API(void*,il2cpp_domain_assembly_open,void*,const char*);
API(void*,il2cpp_get_corlib);
API(void*,il2cpp_assembly_get_image,void*);
API(const char*,il2cpp_image_get_name,void*);
API(void*,il2cpp_class_from_name,void*,const char*,const char*);
API(void*,il2cpp_class_get_method_from_name,void*,const char*,int);
API(void*,il2cpp_class_get_methods,void*,void**);
API(void*,il2cpp_class_get_parent,void*);
API(void*,il2cpp_class_get_type,void*);
API(const char*,il2cpp_class_get_name,void*);
API(const char*,il2cpp_class_get_namespace,void*);
API(void*,il2cpp_type_get_object,void*);
API(char*,il2cpp_type_get_name,void*);
API(void,il2cpp_free,void*);
API(const char*,il2cpp_method_get_name,void*);
API(void*,il2cpp_method_get_param,void*,uint32_t);
API(uint32_t,il2cpp_method_get_param_count,void*);
API(void*,il2cpp_object_get_class,void*);
API(void*,il2cpp_object_new,void*);
API(void*,il2cpp_resolve_icall,const char*);
API(void*,il2cpp_object_unbox,void*);
API(void*,il2cpp_runtime_invoke,void*,void*,void**,void**);
API(void*,il2cpp_string_new,const char*);
API(int,il2cpp_string_length,void*);
API(const wchar_t*,il2cpp_string_chars,void*);
API(uint32_t,il2cpp_array_length,void*);
API(void*,il2cpp_array_new,void*,size_t);
API(uint32_t,il2cpp_gchandle_new,void*,bool);
API(void*,il2cpp_gchandle_get_target,uint32_t);
API(void,il2cpp_gchandle_free,uint32_t);
static decltype(il2cpp_runtime_invoke) originalInvoke;
static bool Resolve(void** p,const char* n){auto it=aliases.find(n);const char* symbol=it==aliases.end()?n:it->second.c_str();*p=(void*)GetProcAddress(assembly,symbol);if(!*p)Log(std::string("missing ")+n);return *p!=nullptr;}
static std::string Text(void* s){if(!s)return "";int n=il2cpp_string_length(s);if(n<0||n>16000)return "[length rejected]";const wchar_t* chars=il2cpp_string_chars(s);int len=WideCharToMultiByte(CP_UTF8,0,chars,n,nullptr,0,nullptr,nullptr);std::string result(len,'\0');WideCharToMultiByte(CP_UTF8,0,chars,n,result.data(),len,nullptr,nullptr);return result;}
static void* Invoke(void* m,void* o=nullptr,void** args=nullptr){if(!m)return nullptr;void* exception=nullptr;auto result=originalInvoke(m,o,args,&exception);if(exception){Log(std::string("exception ")+il2cpp_method_get_name(m));return nullptr;}return result;}
static void* Class(const char* ns,const char* name){auto domain=il2cpp_domain_get();if(!domain)return nullptr;auto corlib=il2cpp_get_corlib();if(corlib){auto c=il2cpp_class_from_name(corlib,ns,name);if(c)return c;}for(const char* image:{"UnityEngine.CoreModule","UnityEngine.UI","Unity.TextMeshPro","UnityEngine.TextRenderingModule","UnityEngine"}){auto a=il2cpp_domain_assembly_open(domain,image);if(a){auto c=il2cpp_class_from_name(il2cpp_assembly_get_image(a),ns,name);if(c)return c;}}return nullptr;}
static void* Method(void* c,const char* name,int count){for(int depth=0;c&&depth<32;depth++,c=il2cpp_class_get_parent(c)){auto m=il2cpp_class_get_method_from_name(c,name,count);if(m)return m;}return nullptr;}
static void* ExactMethod(void* c,const char* name,const std::vector<std::string>& types){if(!c)return nullptr;void* iter=nullptr;void* m;int n=0;while((m=il2cpp_class_get_methods(c,&iter))&&n++<5000){if(std::string(il2cpp_method_get_name(m))!=name||il2cpp_method_get_param_count(m)!=types.size())continue;bool matches=true;for(size_t i=0;i<types.size();i++){char* type=il2cpp_type_get_name(il2cpp_method_get_param(m,(uint32_t)i));if(!type||types[i]!=type)matches=false;if(type)il2cpp_free(type);}if(matches)return m;}return nullptr;}
static void ListMethods(void* klass){if(!klass)return;void* it=nullptr;void* m;int n=0;while((m=il2cpp_class_get_methods(klass,&it))&&n++<5000){std::string name=il2cpp_method_get_name(m);if(name.find("CreateFont")==std::string::npos&&name.find("Characters")==std::string::npos&&name.find("CreateDynamic")==std::string::npos)continue;std::string s=std::string(il2cpp_class_get_name(klass))+"::"+name+"(";for(uint32_t i=0;i<il2cpp_method_get_param_count(m);i++){char* t=il2cpp_type_get_name(il2cpp_method_get_param(m,i));s+=(i?",":"")+std::string(t?t:"");if(t)il2cpp_free(t);}Log(s+")");}}
static bool PrepareFont(void* target,void* klass,const std::string& text,bool tmp){
 if(!dynamicFont){auto font=Class("UnityEngine","Font");auto value=il2cpp_object_new(font);uint32_t held=value?il2cpp_gchandle_new(value,false):0;
  if(value){auto fromPath=(void(*)(void*,void*))il2cpp_resolve_icall("UnityEngine.Font::Internal_CreateFontFromPath(UnityEngine.Font,System.String)");if(tmp&&fromPath){fromPath(value,il2cpp_string_new("C:\\Windows\\Fonts\\msyh.ttc"));Log("font path initializer");}else{
   auto create=(void(*)(void*,void*,int))il2cpp_resolve_icall("UnityEngine.Font::Internal_CreateDynamicFont(UnityEngine.Font,System.String[],System.Int32)");
   if(create){auto names=il2cpp_array_new(Class("System","String"),1);auto handle=il2cpp_gchandle_new(names,false);int index=0;void* args[]={il2cpp_string_new("Microsoft YaHei"),&index};Invoke(ExactMethod(Class("System","Array"),"SetValue",{"System.Object","System.Int32"}),names,args);create(value,names,32);il2cpp_gchandle_free(handle);Log("dynamic font initializer");}
  }dynamicFont=held;}
  Log("dynamicFont="+std::to_string(dynamicFont)+" name="+Text(Invoke(Method(font,"get_name",0),value)));}
 if(!dynamicFont)return false;
 void* desired=il2cpp_gchandle_get_target(dynamicFont);
 if(tmp){auto fontClass=Class("TMPro","TMP_FontAsset");if(!tmpFont){void* args[]={desired};auto created=Invoke(ExactMethod(fontClass,"CreateFontAsset",{"UnityEngine.Font"}),nullptr,args);if(created)tmpFont=il2cpp_gchandle_new(created,false);Log("tmpFont="+std::to_string(tmpFont));}if(!tmpFont)return false;desired=il2cpp_gchandle_get_target(tmpFont);
  void* checkArgs[]={il2cpp_string_new(text.c_str())};auto result=Invoke(ExactMethod(fontClass,"HasCharacters",{"System.String"}),desired,checkArgs);bool covered=result&&*(bool*)il2cpp_object_unbox(result);
  if(!covered){auto add=ExactMethod(fontClass,"TryAddCharacters",{"System.String","System.String&","System.Boolean"});void* missing=nullptr;bool include=true;void* args[]={checkArgs[0],&missing,&include};Invoke(add,desired,args);result=Invoke(ExactMethod(fontClass,"HasCharacters",{"System.String"}),desired,checkArgs);covered=result&&*(bool*)il2cpp_object_unbox(result);Log("missing="+Text(missing));}
  Log("glyphs="+std::to_string(covered));if(!covered)return false;
 }else{
  auto fontClass=Class("UnityEngine","Font");auto has=(bool(*)(void*,int))il2cpp_resolve_icall("UnityEngine.Font::HasCharacter(System.Int32)");Log("legacy-character-api="+std::to_string((uintptr_t)has)+" latin="+std::to_string(has&&has(desired,65)));void* s=il2cpp_string_new(text.c_str());const wchar_t* chars=il2cpp_string_chars(s);bool covered=has!=nullptr;for(int i=0;i<il2cpp_string_length(s)&&covered;i++){int code=chars[i];covered=has(desired,code);}Log("glyphs="+std::to_string(covered));if(!covered)return false;
 }
 auto set=Method(klass,"set_font",1);void* args[]={desired};Invoke(set,target,args);auto actual=Invoke(Method(klass,"get_font",0),target);return actual==desired;
}
static void Tick(){
 auto app=Class("UnityEngine","Application");auto obj=Class("UnityEngine","Object");auto resources=Class("UnityEngine","Resources");auto array=Class("System","Array");
 if(!app||!obj||!resources||!array)return;
 if(scans==0){
  auto savedir=Text(Invoke(Method(app,"get_persistentDataPath",0)));Log("savedir="+savedir);
  if(savedir.find("FusionTest124_")==std::string::npos){Log("STOP save isolation not confirmed");nextTick=~0ULL;return;}
  Log("runtime core and UI classes resolved");
  ListMethods(Class("UnityEngine","Font"));ListMethods(Class("TMPro","TMP_FontAsset"));
 }
 auto getValue=ExactMethod(array,"GetValue",{"System.Int32"});
 auto find=ExactMethod(obj,"FindObjectsOfType",{"System.Type"});
 Log("scan="+std::to_string(scans)+" find="+std::to_string((uintptr_t)find)+" getValue="+std::to_string((uintptr_t)getValue));
 for(auto pair:std::vector<std::pair<const char*,const char*>>{{"UnityEngine.UI","Text"},{"TMPro","TextMeshProUGUI"},{"TMPro","TextMeshPro"}}){
  auto klass=Class(pair.first,pair.second);if(!klass){Log(std::string("no-class ")+pair.second);continue;}
  auto type=il2cpp_type_get_object(il2cpp_class_get_type(klass));void* args[]={type};auto objects=Invoke(find,nullptr,args);if(!objects)continue;
  auto count=il2cpp_array_length(objects);Log(std::string(pair.second)+" objects="+std::to_string(count));if(count>10000)continue;
  uint32_t held=il2cpp_gchandle_new(objects,false);
  auto getter=Method(klass,"get_text",0);auto setter=ExactMethod(klass,"set_text",{"System.String"});if(!setter)setter=Method(klass,"set_text",1);
  auto valueOf=Method(obj,"op_Implicit",1);
  for(uint32_t i=0;i<std::min(count,128u);i++){
   int index=(int)i;void* a[]={&index};auto target=Invoke(getValue,il2cpp_gchandle_get_target(held),a);if(!target)continue;
   void* aliveArgs[]={target};auto alive=Invoke(valueOf,nullptr,aliveArgs);if(alive&&!*(bool*)il2cpp_object_unbox(alive))continue;
   auto text=Text(Invoke(getter,target));if(text.empty())continue;
   Log(std::string("text ")+pair.second+" "+text);
   auto translation=translations.find(text);if(scans>=1&&translation!=translations.end()){
    auto originalFont=Invoke(Method(klass,"get_font",0),target);uint32_t fontRoot=originalFont?il2cpp_gchandle_new(originalFont,false):0;
    if(PrepareFont(target,klass,translation->second,std::string(pair.first)=="TMPro")){
     void* args[]={il2cpp_string_new(translation->second.c_str())};Invoke(setter,target,args);Log("writeback="+std::to_string(Text(Invoke(getter,target))==translation->second));
     void* undo[]={il2cpp_string_new(text.c_str())};Invoke(setter,target,undo);void* oldFont[]={fontRoot?il2cpp_gchandle_get_target(fontRoot):nullptr};Invoke(Method(klass,"set_font",1),target,oldFont);Log("restored="+std::to_string(Text(Invoke(getter,target))==text&&Invoke(Method(klass,"get_font",0),target)==oldFont[0]));
    }else Log("font preparation failed");
    if(fontRoot)il2cpp_gchandle_free(fontRoot);
   }
  }
  il2cpp_gchandle_free(held);
 }
 scans++;
}
static void* HookInvoke(void* method,void* instance,void** args,void** exception){
 auto result=originalInvoke(method,instance,args,exception);
 if(!inside&&GetCurrentThreadId()==config.mainThread&&GetTickCount64()>=nextTick&&scans<12){inside=true;nextTick=GetTickCount64()+3000;Tick();inside=false;}
 return result;
}
static DWORD WINAPI Worker(void*){
 for(int i=0;i<12000;i++){assembly=GetModuleHandleW(L"GameAssembly.dll");if(assembly)break;Sleep(5);}if(!assembly){Log("STOP module timeout");return 1;}
 std::ifstream map(std::wstring(config.root)+L"\\api-map.tsv");std::string line;while(std::getline(map,line)){auto t=line.find('\t');if(t!=std::string::npos)aliases[line.substr(0,t)]=line.substr(t+1);}
 std::ifstream cache(std::wstring(config.root)+L"\\probe-translations.tsv");while(std::getline(cache,line)){auto t=line.find('\t');if(t!=std::string::npos)translations[line.substr(0,t)]=line.substr(t+1);}
 bool ok=true;
#define BIND(n) ok=Resolve((void**)&n,#n)&&ok
 BIND(il2cpp_domain_get);BIND(il2cpp_domain_assembly_open);BIND(il2cpp_get_corlib);BIND(il2cpp_assembly_get_image);BIND(il2cpp_image_get_name);BIND(il2cpp_class_from_name);BIND(il2cpp_class_get_method_from_name);BIND(il2cpp_class_get_methods);BIND(il2cpp_class_get_parent);BIND(il2cpp_class_get_type);BIND(il2cpp_class_get_name);BIND(il2cpp_class_get_namespace);BIND(il2cpp_type_get_object);BIND(il2cpp_type_get_name);BIND(il2cpp_free);BIND(il2cpp_method_get_name);BIND(il2cpp_method_get_param);BIND(il2cpp_method_get_param_count);BIND(il2cpp_object_get_class);BIND(il2cpp_object_unbox);BIND(il2cpp_runtime_invoke);BIND(il2cpp_string_new);BIND(il2cpp_string_length);BIND(il2cpp_string_chars);BIND(il2cpp_array_length);BIND(il2cpp_gchandle_new);BIND(il2cpp_gchandle_get_target);BIND(il2cpp_gchandle_free);
 BIND(il2cpp_object_new);BIND(il2cpp_resolve_icall);BIND(il2cpp_array_new);
 if(!ok)return 2;
 if(MH_Initialize()!=MH_OK||MH_CreateHook((void*)il2cpp_runtime_invoke,(void*)HookInvoke,(void**)&originalInvoke)!=MH_OK||MH_EnableHook((void*)il2cpp_runtime_invoke)!=MH_OK){Log("STOP hook failure");return 3;}
 Log("native API connected; mainThread="+std::to_string(config.mainThread));return 0;
}
extern "C" __declspec(dllexport) DWORD WINAPI FusionStart(Bootstrap* bootstrap){if(!bootstrap||bootstrap->size!=sizeof(Bootstrap)||!bootstrap->mainThread||bootstrap->root[1023])return 1;config=*bootstrap;Log("bootstrap");auto t=CreateThread(nullptr,0,Worker,nullptr,0,nullptr);if(t)CloseHandle(t);return t?0:2;}
BOOL WINAPI DllMain(HINSTANCE module,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(module);return TRUE;}

#pragma once
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

// Only exported IL2CPP APIs; all managed calls run on the game main thread.
struct Bootstrap { DWORD size,mainThread; wchar_t root[1024]; };
static Bootstrap config{};
static std::mutex logLock;
static std::map<std::string,std::string> aliases;
static HMODULE assembly;
static thread_local bool inside=false;
static ULONGLONG nextTick=0;



static void Log(const std::string& s){std::lock_guard<std::mutex> g(logLock);std::ofstream f(std::wstring(config.root)+L"\\runtime.log",std::ios::app|std::ios::binary);f<<s<<"\n";}
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
API(uint32_t,il2cpp_gchandle_new_weakref,void*,bool);
API(void*,il2cpp_gchandle_get_target,uint32_t);
API(void,il2cpp_gchandle_free,uint32_t);
static decltype(il2cpp_runtime_invoke) originalInvoke;
static bool Resolve(void** p,const char* n){auto it=aliases.find(n);const char* symbol=it==aliases.end()?n:it->second.c_str();*p=(void*)GetProcAddress(assembly,symbol);if(!*p)Log(std::string("missing ")+n);return *p!=nullptr;}
static std::string Text(void* s){if(!s)return "";int n=il2cpp_string_length(s);if(n<0||n>16000)return "[length rejected]";const wchar_t* chars=il2cpp_string_chars(s);int len=WideCharToMultiByte(CP_UTF8,0,chars,n,nullptr,0,nullptr,nullptr);std::string result(len,'\0');WideCharToMultiByte(CP_UTF8,0,chars,n,result.data(),len,nullptr,nullptr);return result;}
static void* Invoke(void* m,void* o=nullptr,void** args=nullptr){if(!m)return nullptr;void* exception=nullptr;auto result=originalInvoke(m,o,args,&exception);if(exception){Log(std::string("exception ")+il2cpp_method_get_name(m));return nullptr;}return result;}
static void* Class(const char* ns,const char* name){auto domain=il2cpp_domain_get();if(!domain)return nullptr;auto corlib=il2cpp_get_corlib();if(corlib){auto c=il2cpp_class_from_name(corlib,ns,name);if(c)return c;}for(const char* image:{"UnityEngine.CoreModule","UnityEngine.UIModule","UnityEngine.UI","Unity.TextMeshPro","UnityEngine.TextRenderingModule","UnityEngine"}){auto a=il2cpp_domain_assembly_open(domain,image);if(a){auto c=il2cpp_class_from_name(il2cpp_assembly_get_image(a),ns,name);if(c)return c;}}return nullptr;}
static void* Method(void* c,const char* name,int count){for(int depth=0;c&&depth<32;depth++,c=il2cpp_class_get_parent(c)){auto m=il2cpp_class_get_method_from_name(c,name,count);if(m)return m;}return nullptr;}
static void* ExactMethod(void* c,const char* name,const std::vector<std::string>& types){if(!c)return nullptr;void* iter=nullptr;void* m;int n=0;while((m=il2cpp_class_get_methods(c,&iter))&&n++<5000){if(std::string(il2cpp_method_get_name(m))!=name||il2cpp_method_get_param_count(m)!=types.size())continue;bool matches=true;for(size_t i=0;i<types.size();i++){char* type=il2cpp_type_get_name(il2cpp_method_get_param(m,(uint32_t)i));if(!type||types[i]!=type)matches=false;if(type)il2cpp_free(type);}if(matches)return m;}return nullptr;}

static bool BindApis(){
 bool ok=true;
#define BIND(n) ok=Resolve((void**)&n,#n)&&ok
 BIND(il2cpp_domain_get);BIND(il2cpp_domain_assembly_open);BIND(il2cpp_get_corlib);BIND(il2cpp_assembly_get_image);BIND(il2cpp_image_get_name);BIND(il2cpp_class_from_name);BIND(il2cpp_class_get_method_from_name);BIND(il2cpp_class_get_methods);BIND(il2cpp_class_get_parent);BIND(il2cpp_class_get_type);BIND(il2cpp_class_get_name);BIND(il2cpp_class_get_namespace);BIND(il2cpp_type_get_object);BIND(il2cpp_type_get_name);BIND(il2cpp_free);BIND(il2cpp_method_get_name);BIND(il2cpp_method_get_param);BIND(il2cpp_method_get_param_count);BIND(il2cpp_object_get_class);BIND(il2cpp_object_unbox);BIND(il2cpp_runtime_invoke);BIND(il2cpp_string_new);BIND(il2cpp_string_length);BIND(il2cpp_string_chars);BIND(il2cpp_array_length);BIND(il2cpp_gchandle_new);BIND(il2cpp_gchandle_get_target);BIND(il2cpp_gchandle_free);
 BIND(il2cpp_object_new);BIND(il2cpp_resolve_icall);BIND(il2cpp_array_new);
 BIND(il2cpp_gchandle_new_weakref);
 return ok;
}

#pragma once

// Keep a game's primary TMP font/material in place. Theme and hover code can
// reassign those assets every frame; swapping the component font fights that
// writer and briefly renders Chinese with a Latin-only atlas.
// These are in-memory, reversible list edits through exported runtime APIs.
struct FallbackLease {
 uint32_t list=0,font=0;
 bool created=false;
};
static std::vector<FallbackLease> fallbackLeases;
static uint64_t fallbackAdds=0,fallbackRemoves=0,fallbackErrors=0;
static ULONGLONG fallbackCleanupRetryAt=0;
static void* (*fallbackReturnType)(void*)=nullptr;
static void* (*fallbackClassFromType)(void*)=nullptr;
static bool fallbackApisResolved=false;

static std::set<uint32_t> GlyphSet(const std::string& text){
 int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),(int)text.size(),nullptr,0);
 if(n<=0)return {};std::wstring wide(n,L'\0');MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),(int)text.size(),wide.data(),n);
 std::set<uint32_t> result;
 for(int i=0;i<n;i++){
  uint32_t cp=wide[i];if(cp>=0xd800&&cp<=0xdbff&&i+1<n){auto low=wide[++i];cp=0x10000+((cp-0xd800)<<10)+(low-0xdc00);}
  // Line breaks and join/variation controls do not require standalone glyphs.
  if(cp==9||cp==10||cp==13||cp==0x200b||cp==0x200c||cp==0x200d||cp==0xfe0e||cp==0xfe0f||cp==0x2060||cp==0xfeff)continue;
  result.insert(cp);
 }
 return result;
}
static std::string IntroducedGlyphs(const std::string& source,const std::string& translated){
 auto original=GlyphSet(source);auto desired=GlyphSet(translated);std::wstring needed;
 for(auto cp:desired){
  // Existing symbols may be supplied by the game's sprite assets or other
  // font fallbacks. They must not hold a whole translated sentence in English
  // merely because our supplementary CJK font does not contain that symbol.
  if(original.count(cp))continue;
  if(cp<=0xffff)needed.push_back((wchar_t)cp);
  else{cp-=0x10000;needed.push_back((wchar_t)(0xd800+(cp>>10)));needed.push_back((wchar_t)(0xdc00+(cp&1023)));}
 }
 return Utf8(needed);
}

static bool TryCall(void* method,void* object,void** args,void*& result){
 result=nullptr;if(!method)return false;
 void* exception=nullptr;result=originalInvoke(method,object,args,&exception);
 return !exception;
}
static void ResolveFallbackApis(){
 if(fallbackApisResolved)return;fallbackApisResolved=true;
 auto symbol=[](const char* name){auto it=aliases.find(name);return GetProcAddress(assembly,it==aliases.end()?name:it->second.c_str());};
 fallbackReturnType=reinterpret_cast<decltype(fallbackReturnType)>(symbol("il2cpp_method_get_return_type"));
 fallbackClassFromType=reinterpret_cast<decltype(fallbackClassFromType)>(symbol("il2cpp_class_from_type"));
}
static bool HasGlyphs(void* font,const std::string& text){
 if(text.empty())return true;
 if(!font)return false;
 auto method=ExactMethod(Class("TMPro","TMP_FontAsset"),"HasCharacters",{"System.String"});
 auto value=il2cpp_string_new(text.c_str());Root held(value);void* args[]={value};void* result=nullptr;
 return TryCall(method,font,args,result)&&Bool(result);
}
static void DropFallbackLease(FallbackLease& lease){
 if(lease.list)il2cpp_gchandle_free(lease.list);
 if(lease.font)il2cpp_gchandle_free(lease.font);
 lease={};
}
static bool AttachFallback(void* font,void* fallback){
 if(!font||!fallback||font==fallback)return false;
 auto klass=Class("TMPro","TMP_FontAsset");
 auto get=Method(klass,"get_fallbackFontAssetTable",0),set=Method(klass,"set_fallbackFontAssetTable",1);
 void* list=nullptr;
 if(!TryCall(get,font,nullptr,list))return false;
 bool created=false;
 if(!list){
  // AOT may strip an unused generic constructor; failure leaves the original
  // property untouched and lets the caller report unsupported glyph support.
  ResolveFallbackApis();if(!set||!fallbackReturnType||!fallbackClassFromType)return false;
  auto type=fallbackReturnType(get);auto listClass=type?fallbackClassFromType(type):nullptr;
  auto ctor=Method(listClass,".ctor",0);if(!ctor)return false;
  list=il2cpp_object_new(listClass);if(!list)return false;Root held(list);void* ignored=nullptr;
  if(!TryCall(ctor,list,nullptr,ignored))return false;
  // Assign only after construction and API validation below.
  created=true;
 }
 Root heldList(list);
 auto listClass=il2cpp_object_get_class(list);
 auto contains=Method(listClass,"Contains",1),add=Method(listClass,"Add",1),remove=Method(listClass,"Remove",1);
 auto count=Method(listClass,"get_Count",0);void* result=nullptr;void* value[]={fallback};
 if(!contains||!add||!remove||!count||!TryCall(contains,list,value,result))return false;
 if(Bool(result))return true;
 // No strong references to abandoned scene font assets or their lists.
 for(auto it=fallbackLeases.begin();it!=fallbackLeases.end();){
  if(!il2cpp_gchandle_get_target(it->list)){DropFallbackLease(*it);it=fallbackLeases.erase(it);}else ++it;
 }
 auto existing=std::find_if(fallbackLeases.begin(),fallbackLeases.end(),[&](const FallbackLease& lease){return il2cpp_gchandle_get_target(lease.list)==list;});
 if(existing==fallbackLeases.end()&&fallbackLeases.size()>=256)return false;
 if(!TryCall(add,list,value,result)||!TryCall(contains,list,value,result)||!Bool(result)){fallbackErrors++;return false;}
 if(created){
  void* arg[]={list};void* assigned=nullptr;
  if(!TryCall(set,font,arg,result)||!TryCall(get,font,nullptr,assigned)||assigned!=list){
   TryCall(remove,list,value,result);fallbackErrors++;return false;
  }
 }
 if(existing==fallbackLeases.end())fallbackLeases.push_back({il2cpp_gchandle_new_weakref(list,false),created?il2cpp_gchandle_new_weakref(font,false):0,created});
 fallbackAdds++;return true;
}
static bool RemoveFallbacksSlice(){
 if(GetTickCount64()<fallbackCleanupRetryAt)return fallbackLeases.empty();
 auto began=Clock::now();auto fallback=tmpFont?il2cpp_gchandle_get_target(tmpFont):nullptr;int count=0;
 while(!fallbackLeases.empty()&&count++<8){
  auto& lease=fallbackLeases.back();auto list=il2cpp_gchandle_get_target(lease.list);Root heldList(list);void* result=nullptr;
  if(list&&fallback){
   auto klass=il2cpp_object_get_class(list);void* args[]={fallback};
   // Remove our entry only, preserving other plugins' and the game's entries.
   if(!TryCall(Method(klass,"Remove",1),list,args,result)){fallbackErrors++;fallbackCleanupRetryAt=GetTickCount64()+1000;return false;}
   if(Bool(result))fallbackRemoves++;
   if(lease.created){
    auto font=il2cpp_gchandle_get_target(lease.font);Root heldFont(font);void* size=nullptr;
    if(font&&Alive(font)&&TryCall(Method(klass,"get_Count",0),list,nullptr,size)&&size&&*reinterpret_cast<int*>(il2cpp_object_unbox(size))==0){
     auto fontClass=Class("TMPro","TMP_FontAsset");void* current=nullptr;
     if(TryCall(Method(fontClass,"get_fallbackFontAssetTable",0),font,nullptr,current)&&current==list){void* empty[]={nullptr};TryCall(Method(fontClass,"set_fallbackFontAssetTable",1),font,empty,result);}
    }
   }
  }
  DropFallbackLease(lease);fallbackLeases.pop_back();
  if(Clock::now()-began>std::chrono::milliseconds(1))break;
 }
 return fallbackLeases.empty();
}
static void InvalidateGlyphs(void* target,void* klass){
 // Reparse only after newly installing support for an already translated
 // label. No text setter, rectangle change, input flag change or layout rebuild.
 bool dirty=true;void* args[]={&dirty};void* ignored=nullptr;
 TryCall(Method(klass,"set_havePropertiesChanged",1),target,args,ignored);
 TryCall(Method(klass,"SetVerticesDirty",0),target,nullptr,ignored);
}

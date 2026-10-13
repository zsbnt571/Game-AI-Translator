#pragma once

// A GC handle preserves the managed wrapper, not the Unity native asset.
// Retain only our generated assets across UnloadUnusedAssets; never change
// the flags or lifetime of an authored game font, material or scene object.
static uint64_t fontLifetimeResets=0,fontManagedFailures=0;
static bool FontCall(void* method,void* object,void** args,void*& result){
 result=nullptr;if(!method)return false;
 void* exception=nullptr;result=originalInvoke(method,object,args,&exception);
 if(exception){fontManagedFailures++;return false;}return true;
}
template<class Initialize> static bool FontInitializer(Initialize&& initialize){
 // Some AOT players strip the managed dynamic/path constructors while keeping
 // Unity's registered native initializers. Confine their C++ exceptions to
 // this unpublished-font operation. /EHsc intentionally does not catch SEH,
 // access violations, abort, or faults elsewhere in the game.
 try{initialize();return true;}catch(...){fontManagedFailures++;return false;}
}
static bool RetainGeneratedFont(void* font){
 if(!Alive(font))return false;
 auto klass=Class("UnityEngine","Object");auto get=Method(klass,"get_hideFlags",0),set=Method(klass,"set_hideFlags",1);
 void* result=nullptr;if(!get||!set||!FontCall(get,font,nullptr,result)||!result)return false;
 constexpr int dontUnloadUnusedAsset=32;
 int flags=*reinterpret_cast<int*>(il2cpp_object_unbox(result))|dontUnloadUnusedAsset;
 void* args[]={&flags};if(!FontCall(set,font,args,result)||!FontCall(get,font,nullptr,result)||!result)return false;
 return (*reinterpret_cast<int*>(il2cpp_object_unbox(result))&dontUnloadUnusedAsset)!=0;
}
static void DropDeadFont(uint32_t& handle,bool tmp){
 if(!handle||Alive(il2cpp_gchandle_get_target(handle)))return;
 il2cpp_gchandle_free(handle);handle=0;fontLifetimeResets++;
 if(tmp){tmpCoverage.clear();tmpGlyphRevision++;}else legacyCoverage.clear();
}

#pragma once

// Resolve a compiled entry through the runtime's reflection contract. Do not
// read MethodInfo memory offsets: their layout varies across Unity releases.
static void* (*methodObject)(void*,void*)=nullptr;
static void* (*returnType)(void*)=nullptr;
static void* CompiledEntry(void* method,void* declaring){
 if(!method||!methodObject||!returnType)return nullptr;
 char* name=il2cpp_type_get_name(returnType(method));
 bool isVoid=name&&std::string(name)=="System.Void";if(name)il2cpp_free(name);
 if(!isVoid)return nullptr;
 auto reflection=methodObject(method,declaring);if(!reflection)return nullptr;Root rooted(reflection);
 auto handle=Invoke(Method(il2cpp_object_get_class(reflection),"get_MethodHandle",0),reflection);
 if(!handle)return nullptr;Root held(handle);
 auto pointer=Invoke(Method(il2cpp_object_get_class(handle),"GetFunctionPointer",0),il2cpp_object_unbox(handle));
 if(!pointer)return nullptr;
 auto entry=*reinterpret_cast<void**>(il2cpp_object_unbox(pointer));
 MEMORY_BASIC_INFORMATION info{};
 if(!entry||!VirtualQuery(entry,&info,sizeof(info))||info.State!=MEM_COMMIT||info.AllocationBase!=assembly
    ||!(info.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY))
    ||(info.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return nullptr;
 return entry;
}
static void* textOriginal[3]{};
static void* enableOriginal[3]{};
static void* renderOriginal=nullptr;
static void* quitOriginal=nullptr;
static int installedTextHooks=0,installedEnableHooks=0;
static void ObserveTextObject(void* target);
template<int Slot> static void TextChanged(void* self,void* text,void* method){
 auto original=reinterpret_cast<void(*)(void*,void*,void*)>(textOriginal[Slot]);
 original(self,text,method);
 if(!ready||!enabled||runtimeStopping||inside||GetCurrentThreadId()!=config.mainThread)return;
 inside=true;try{ObserveTextObject(self);}catch(const std::exception& e){Log(std::string("text callback ")+e.what());}inside=false;
}
template<int Slot> static void TextEnabled(void* self,void* method){
 auto original=reinterpret_cast<void(*)(void*,void*)>(enableOriginal[Slot]);original(self,method);
 if(!ready||!enabled||runtimeStopping||inside||GetCurrentThreadId()!=config.mainThread)return;
 inside=true;try{ObserveTextObject(self);}catch(const std::exception& e){Log(std::string("enable callback ")+e.what());}inside=false;
}
static void BeforeCanvas(void* method){
 if(!inside&&!runtimeStopping&&GetCurrentThreadId()==config.mainThread){renderTicks++;Pump();}
 reinterpret_cast<void(*)(void*)>(renderOriginal)(method);
}
static void BeforeQuit(void* method){
 runtimeStopping=true;
 reinterpret_cast<void(*)(void*)>(quitOriginal)(method);
}
static bool InstallEntry(void* entry,void* hook,void** original){
 if(!entry||MH_CreateHook(entry,hook,original)!=MH_OK)return false;
 if(MH_EnableHook(entry)==MH_OK)return true;
 MH_RemoveHook(entry);*original=nullptr;return false;
}
static void InstallTextHooks(){
 auto symbol=[&](const char* name){auto it=aliases.find(name);return GetProcAddress(assembly,it==aliases.end()?name:it->second.c_str());};
 methodObject=reinterpret_cast<decltype(methodObject)>(symbol("il2cpp_method_get_object"));
 returnType=reinterpret_cast<decltype(returnType)>(symbol("il2cpp_method_get_return_type"));
 if(!methodObject||!returnType){Log("compiled text callbacks unavailable; using main-thread wake");return;}
 void* setters[]={reinterpret_cast<void*>(TextChanged<0>),reinterpret_cast<void*>(TextChanged<1>),reinterpret_cast<void*>(TextChanged<2>)};
 void* enables[]={reinterpret_cast<void*>(TextEnabled<0>),reinterpret_cast<void*>(TextEnabled<1>),reinterpret_cast<void*>(TextEnabled<2>)};
 std::set<void*> hooked;
 for(auto& family:families){
  auto setter=CompiledEntry(family.set,family.klass);
  if(setter&&!hooked.count(setter)&&installedTextHooks<3&&InstallEntry(setter,setters[installedTextHooks],&textOriginal[installedTextHooks])){hooked.insert(setter);installedTextHooks++;}
  auto enable=CompiledEntry(Method(family.klass,"OnEnable",0),family.klass);
  if(enable&&!hooked.count(enable)&&installedEnableHooks<3&&InstallEntry(enable,enables[installedEnableHooks],&enableOriginal[installedEnableHooks])){hooked.insert(enable);installedEnableHooks++;}
 }
 InstallEntry(CompiledEntry(canvasMethod,Class("UnityEngine","Canvas")),reinterpret_cast<void*>(BeforeCanvas),&renderOriginal);
 InstallEntry(CompiledEntry(quitMethod,Class("UnityEngine","Application")),reinterpret_cast<void*>(BeforeQuit),&quitOriginal);
 Log("compiled callbacks text="+std::to_string(installedTextHooks)+" enable="+std::to_string(installedEnableHooks)+" render="+std::to_string(renderOriginal!=nullptr));
}

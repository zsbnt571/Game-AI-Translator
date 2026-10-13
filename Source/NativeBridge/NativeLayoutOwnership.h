#pragma once

// Same ownership boundary as the managed bridge: inspect only public Unity
// layout components, not arbitrary game getters, siblings or scene names.
struct NativeLayoutApi {void* type=nullptr,*enabled=nullptr,*ignore=nullptr,*preferredWidth=nullptr,*preferredHeight=nullptr,*horizontalFit=nullptr,*verticalFit=nullptr,*controlWidth=nullptr,*controlHeight=nullptr;};
static NativeLayoutApi& LayoutApi(const char* name){
 static std::map<std::string,NativeLayoutApi> known;
 auto at=known.find(name);if(at!=known.end())return at->second;
 NativeLayoutApi api;auto c=Class("UnityEngine.UI",name);
 // Built-in IL2CPP metadata lives for the domain. Resolve once rather than
 // enumerating public method signatures at every ancestor on every review.
 if(c){api.type=il2cpp_type_get_object(il2cpp_class_get_type(c));api.enabled=Method(c,"get_enabled",0);if(!api.enabled)api.enabled=Method(c,"get_isActiveAndEnabled",0);
  api.ignore=Method(c,"get_ignoreLayout",0);api.preferredWidth=Method(c,"get_preferredWidth",0);api.preferredHeight=Method(c,"get_preferredHeight",0);
  api.horizontalFit=Method(c,"get_horizontalFit",0);api.verticalFit=Method(c,"get_verticalFit",0);api.controlWidth=Method(c,"get_childControlWidth",0);api.controlHeight=Method(c,"get_childControlHeight",0);}
 return known.emplace(name,api).first->second;
}
static void* EnabledLayoutComponent(void* transform,const char* name){
 auto& api=LayoutApi(name);if(!api.type)return nullptr;
 static auto get=ExactMethod(Class("UnityEngine","Component"),"GetComponent",{"System.Type"});
 void* args[]={api.type};auto component=Invoke(get,transform,args);
 if(!component)return nullptr;
 auto state=Invoke(api.enabled,component);return state&&!Bool(state)?nullptr:component;
}
static bool NativeLayoutOwnsTextSize(void* transform){
 int open=3;auto node=transform;
 for(int depth=0;node&&open&&depth<8;depth++){
  Root held(node);
  if(auto element=EnabledLayoutComponent(node,"LayoutElement")){
   auto& api=LayoutApi("LayoutElement");
   if(Bool(Invoke(api.ignore,element)))return false;
   auto width=Invoke(api.preferredWidth,element),height=Invoke(api.preferredHeight,element);
   if(width&&Scalar(width)>=0)open&=~1;if(height&&Scalar(height)>=0)open&=~2;
  }
  if(auto fitter=EnabledLayoutComponent(node,"ContentSizeFitter")){
   auto& api=LayoutApi("ContentSizeFitter");
   auto width=Invoke(api.horizontalFit,fitter),height=Invoke(api.verticalFit,fitter);
   if(((open&1)&&width&&*reinterpret_cast<int*>(il2cpp_object_unbox(width))>0)||((open&2)&&height&&*reinterpret_cast<int*>(il2cpp_object_unbox(height))>0))return true;
  }
  if(!open)return false;
  static auto getParent=Method(Class("UnityEngine","Transform"),"get_parent",0);
  auto parent=Invoke(getParent,node);if(!parent||parent==node)return false;
  const char* groupType="HorizontalLayoutGroup";auto group=EnabledLayoutComponent(parent,groupType);if(!group){groupType="VerticalLayoutGroup";group=EnabledLayoutComponent(parent,groupType);}
  if(!group)return false;
  auto& api=LayoutApi(groupType);
  auto width=Invoke(api.controlWidth,group),height=Invoke(api.controlHeight,group);
  if(width&&!Bool(width))open&=~1;if(height&&!Bool(height))open&=~2;
  node=parent;
 }
 return false;
}

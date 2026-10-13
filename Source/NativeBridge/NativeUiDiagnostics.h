#pragma once

// Read only the documented UI state, on demand, at a bounded number of
// ancestors. Never invoke a game script getter or change an input property.
static Json UiState(void* target){
 Json result=Json::object();auto klass=il2cpp_object_get_class(target);
 auto boolean=[&](Json& row,void* object,const char* method,const char* key){
  auto c=il2cpp_object_get_class(object);auto value=Invoke(Method(c,method,0),object);
  if(value)row[key]=Bool(value);
 };
 boolean(result,target,"get_raycastTarget","raycastTarget");
 boolean(result,target,"get_isActiveAndEnabled","active");
 auto transform=Invoke(Method(klass,"get_rectTransform",0),target);
 if(!transform)return result;
 auto transformClass=il2cpp_object_get_class(transform);
 auto rect=Invoke(Method(transformClass,"get_rect",0),transform);
 if(rect){auto c=il2cpp_object_get_class(rect);auto value=il2cpp_object_unbox(rect);
  for(auto name:{"x","y","width","height"}){auto number=Invoke(Method(c,(std::string("get_")+name).c_str(),0),value);if(number)result["rect"][name]=Scalar(number);}}
 auto component=Class("UnityEngine","Component");auto get=ExactMethod(component,"GetComponent",{"System.Type"});
 auto started=Clock::now();result["ancestors"]=Json::array();
 for(int depth=0;transform&&depth<6&&Clock::now()-started<std::chrono::milliseconds(1);depth++){
  Json row={{"depth",depth}};
  for(auto name:{"Selectable","CanvasGroup"}){
   auto typeClass=Class(std::string(name)=="Selectable"?"UnityEngine.UI":"UnityEngine",name);if(!typeClass)continue;
   auto type=il2cpp_type_get_object(il2cpp_class_get_type(typeClass));void* args[]={type};auto ui=Invoke(get,transform,args);
   if(!ui)continue;Json state=Json::object();
   boolean(state,ui,"get_interactable","interactable");boolean(state,ui,"get_blocksRaycasts","blocksRaycasts");
   boolean(state,ui,"get_ignoreParentGroups","ignoreParentGroups");boolean(state,ui,"get_isActiveAndEnabled","active");
   auto alpha=Invoke(Method(typeClass,"get_alpha",0),ui);if(alpha)state["alpha"]=Scalar(alpha);
   row[name]=state;
  }
  if(row.size()>1)result["ancestors"].push_back(row);
  auto next=Invoke(Method(il2cpp_object_get_class(transform),"get_parent",0),transform);
  if(next==transform)break;transform=next;
 }
 return result;
}

static void DiagnosticBindings(const Json& request,Json& result){
 std::set<std::string> sources;
 if(request.contains("sources")){
  const auto& selected=request.at("sources");if(!selected.is_array()||selected.size()>32)throw std::runtime_error("Invalid diagnostic sources");
  for(const auto& source:selected){auto value=source.get<std::string>();if(!Eligible(value))throw std::runtime_error("Invalid diagnostic source");sources.insert(value);}
 }
 result["displayed"]=Json::array();result["observed"]=Json::array();result["diagnosticTruncated"]=false;
 auto began=Clock::now();int visited=0;
 for(auto& pair:bindings){
  auto& binding=pair.second;if(!sources.empty()&&!sources.count(binding.source))continue;
  if(sources.empty()&&!binding.active)continue;
  if(visited++>=128||Clock::now()-began>std::chrono::milliseconds(3)){result["diagnosticTruncated"]=true;break;}
  auto target=il2cpp_gchandle_get_target(binding.weak);if(!Alive(target))continue;
  auto& family=families[binding.family];auto actual=Text(Invoke(family.get,target));
  Json row={{"source",binding.source},{"text",actual},{"fontBlocked",binding.fontBlocked},{"glyphSupport",binding.glyphMode},
    {"cached",Lookup(binding.source)!=nullptr},{"active",!family.active||Bool(Invoke(family.active,target))},
    {"ui",UiState(target)}};
  if(result["observed"].size()<64)result["observed"].push_back(row);
  if(!binding.applied.empty()&&actual==binding.applied&&result["displayed"].size()<64)result["displayed"].push_back(row);
 }
}

#pragma once
// Explicit framework contracts only. Framework assemblies are inspected through
// the running player's exported metadata API; no assembly is loaded by Fusion.
static constexpr const char* characterContract="fusion-character-runtime-1";
struct NativeCharacterRow {std::string requestId,identity,source,name,framework,text;int actorId=0;bool fallback=false;ULONGLONG deadline=0;};
static bool characterRuntime=false;static uint64_t characterSerial=0;
static int characterScanCursor=0,characterPollCursor=0;
static std::map<int,NativeCharacterRow> characterRows;
static std::map<std::string,std::vector<std::string>> characterFacts;
static void ForgetCharacter(int key){characterRows.erase(key);}
static void ResetCharacters(bool active){characterRuntime=active;characterRows.clear();characterFacts.clear();characterScanCursor=characterPollCursor=0;}
static void* FrameworkClass(const char* ns,const char* name){
 struct Entry{void* klass=nullptr;ULONGLONG retry=0;};static std::map<std::string,Entry> cache;
 auto& e=cache[std::string(ns)+"."+name];if(e.klass)return e.klass;auto now=GetTickCount64();if(now<e.retry)return nullptr;e.retry=now+5000;
 if(!il2cpp_domain_get_assemblies)return nullptr;size_t count=0;auto list=il2cpp_domain_get_assemblies(il2cpp_domain_get(),&count);if(!list||count>512)return nullptr;
 for(size_t i=0;i<count;i++){auto image=il2cpp_assembly_get_image(list[i]);if(image){auto c=il2cpp_class_from_name(image,ns,name);if(c){e.klass=c;break;}}}return e.klass;
}
static bool NativeInstance(void* value,void* wanted){if(!value||!wanted)return false;auto klass=il2cpp_object_get_class(value);for(int d=0;klass&&d<32;d++,klass=il2cpp_class_get_parent(klass))if(klass==wanted)return true;return false;}
static void* NativeField(void* klass,const char* name,const char* expected){
 if(!klass||!il2cpp_class_get_field_from_name||!il2cpp_field_get_type||!il2cpp_field_get_value)return nullptr;
 auto field=il2cpp_class_get_field_from_name(klass,name);if(!field)return nullptr;auto type=il2cpp_type_get_name(il2cpp_field_get_type(field));bool valid=type&&std::string(type)==expected;if(type)il2cpp_free(type);return valid?field:nullptr;
}
static void* NativeReference(void* klass,void* target,const char* name,const char* expected){
 if(!NativeInstance(target,klass))return nullptr;auto getter=Method(klass,(std::string("get_")+name).c_str(),0);if(getter)return Invoke(getter,target);
 auto field=NativeField(klass,name,expected);void* value=nullptr;if(field)il2cpp_field_get_value(target,field,&value);return value;
}
static bool NativeBool(void* klass,void* target,const char* name,bool& value){
 if(!NativeInstance(target,klass))return false;auto getter=Method(klass,(std::string("get_")+name).c_str(),0);if(getter){auto result=Invoke(getter,target);if(!result)return false;value=Bool(result);return true;}
 auto field=NativeField(klass,name,"System.Boolean");if(!field)return false;il2cpp_field_get_value(target,field,&value);return true;
}
static bool NativeInt(void* klass,void* target,const char* name,int& value){
 if(!NativeInstance(target,klass))return false;auto getter=Method(klass,(std::string("get_")+name).c_str(),0);if(getter){auto result=Invoke(getter,target);if(!result)return false;value=*static_cast<int*>(il2cpp_object_unbox(result));return true;}
 auto field=NativeField(klass,name,"System.Int32");if(!field)return false;il2cpp_field_get_value(target,field,&value);return true;
}
static bool NativeActive(void* target){if(!Alive(target))return false;auto method=Method(il2cpp_object_get_class(target),"get_isActiveAndEnabled",0);auto value=Invoke(method,target);return value&&Bool(value);}
static void* CharacterParent(void* target,void* klass){if(!klass)return nullptr;auto type=il2cpp_type_get_object(il2cpp_class_get_type(klass));void* args[]={type};return type?Invoke(ExactMethod(Class("UnityEngine","Component"),"GetComponentInParent",{"System.Type"}),target,args):nullptr;}
static bool CharacterComplete(Binding& b,void* target){if(!families[b.family].tmp)return true;int max=0;return NativeInt(Class("TMPro","TMP_Text"),target,"maxVisibleCharacters",max)&&max>=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,b.source.data(),(int)b.source.size(),nullptr,0);}
static bool NativeCharacterIdentity(Binding& b,void* target,NativeCharacterRow& row){
 if(!NativeActive(target)||!CharacterComplete(b,target))return false;
 auto ntext=FrameworkClass("Naninovel.UI","RevealableText");
 if(NativeInstance(target,ntext)){
  auto full=Invoke(Method(ntext,"get_Text",0),target),progress=Invoke(Method(ntext,"get_RevealProgress",0),target);
  if(Text(full)!=b.source||!progress)return false;float p=*static_cast<float*>(il2cpp_object_unbox(progress));if(!std::isfinite(p)||p<0.999999f)return false;
  auto pclass=FrameworkClass("Naninovel.UI","RevealableTextPrinterPanel"),panel=CharacterParent(target,pclass);if(!NativeActive(panel))return false;
  if(Invoke(Method(pclass,"get_RevealableText",0),panel)!=target)return false;
  auto actor=Text(Invoke(Method(pclass,"get_AuthorId",0),panel));if(actor.empty()||actor.size()>96)return false;
  for(size_t i=0;i<actor.size();i++){char c=actor[i];bool alpha=(c>='A'&&c<='Z')||(c>='a'&&c<='z')||c=='_';if(!alpha&&(i==0||!((c>='0'&&c<='9')||c=='.'||c=='-')))return false;}
  row.identity="naninovel:"+actor;row.name=actor;row.framework="naninovel";return true;
 }
 auto pclass=FrameworkClass("PixelCrushers.DialogueSystem","StandardUISubtitlePanel"),panel=CharacterParent(target,pclass);if(!NativeActive(panel))return false;
 bool value=false;if(!NativeBool(pclass,panel,"accumulateText",value)||value||!NativeBool(pclass,panel,"addSpeakerName",value)||value)return false;
 if(NativeBool(pclass,panel,"delayTypewriterUntilOpen",value)&&value&&(!NativeBool(pclass,panel,"hasFocus",value)||!value))return false;
 auto fieldClass=FrameworkClass("PixelCrushers","UITextField");auto field=NativeReference(pclass,panel,"subtitleText","PixelCrushers.UITextField");if(!field)return false;
 auto ui=NativeReference(fieldClass,field,"uiText","UnityEngine.UI.Text"),tmp=NativeReference(fieldClass,field,"textMeshProUGUI","TMPro.TextMeshProUGUI");
 if(Alive(ui)&&Alive(tmp))return false;if((Alive(ui)?ui:tmp)!=target)return false;
 auto subtitleClass=FrameworkClass("PixelCrushers.DialogueSystem","Subtitle"),formatClass=FrameworkClass("PixelCrushers.DialogueSystem","FormattedText"),infoClass=FrameworkClass("PixelCrushers.DialogueSystem","CharacterInfo");
 auto subtitle=NativeReference(pclass,panel,"currentSubtitle","PixelCrushers.DialogueSystem.Subtitle"),format=NativeReference(subtitleClass,subtitle,"formattedText","PixelCrushers.DialogueSystem.FormattedText");
 if(Text(NativeReference(formatClass,format,"text","System.String"))!=b.source)return false;
 auto getTypewriter=Method(pclass,"GetTypewriter",0);if(!getTypewriter)return false;void* effect=nullptr;if(!FontCall(getTypewriter,panel,nullptr,effect))return false;
 if(effect){auto effectClass=FrameworkClass("PixelCrushers.DialogueSystem","AbstractTypewriterEffect");if(!NativeBool(effectClass,effect,"isPlaying",value)||value)return false;}
 auto info=NativeReference(subtitleClass,subtitle,"speakerInfo","PixelCrushers.DialogueSystem.CharacterInfo");int actor=0;if(!NativeInt(infoClass,info,"id",actor)||actor<=0)return false;
 row.actorId=actor;row.identity="unityds:actor:"+std::to_string(actor);row.framework="unityds";row.name=std::to_string(actor);return true;
}
static NativeCharacterRow* CharacterFor(Binding& b,void* target){
 if(!characterRuntime||!enabled)return nullptr;NativeCharacterRow next;
 if(!Eligible(b.source)||!NativeCharacterIdentity(b,target,next)){ForgetCharacter(b.key);return nullptr;}
 auto old=characterRows.find(b.key);if(old!=characterRows.end()&&old->second.source==b.source&&old->second.identity==next.identity)return &old->second;
 if(old==characterRows.end()&&characterRows.size()>=32)return nullptr;
 next.source=b.source;next.requestId="n"+std::to_string(++characterSerial);next.deadline=GetTickCount64()+3000;
 return &(characterRows[b.key]=std::move(next));
}
static const std::string* CharacterLookup(Binding& b,void* target){auto row=CharacterFor(b,target);if(row&&!row->text.empty())return &row->text;return Lookup(b.source);}
static bool CharacterWaiting(Binding& b,void* target){auto row=CharacterFor(b,target);return row&&!row->fallback&&(!row->text.empty()||GetTickCount64()<=row->deadline);}
static void PollCharacters(const Json& request,Json& response){
 if(!characterRuntime)return;
 std::vector<int> previous;for(auto& pair:characterRows)previous.push_back(pair.first);
 for(int key:previous){auto found=bindings.find(key);if(found==bindings.end()){ForgetCharacter(key);continue;}auto target=il2cpp_gchandle_get_target(found->second.weak);if(Alive(target))CharacterFor(found->second,target);else ForgetCharacter(key);}
 for(size_t i=0;i<std::min(size_t(128),bindings.size());i++){auto it=bindings.upper_bound(characterScanCursor);if(it==bindings.end())it=bindings.begin();characterScanCursor=it->first;auto target=il2cpp_gchandle_get_target(it->second.weak);if(Alive(target))CharacterFor(it->second,target);}
 auto accepted=Json::array();auto replies=request.find("characterResults");
 if(request.value("characterRuntimeContract","")==characterContract&&replies!=request.end()&&replies->is_array())for(size_t i=0;i<std::min(size_t(16),replies->size());i++){
  auto& reply=(*replies)[i];if(!reply.is_object()||!reply.contains("requestId")||!reply["requestId"].is_string()||!reply.contains("source")||!reply["source"].is_string()||!reply.contains("characterId")||!reply["characterId"].is_string())continue;
  for(auto& pair:characterRows){auto& r=pair.second;if(r.requestId!=reply["requestId"]||r.source!=reply["source"]||r.identity!=reply["characterId"])continue;
   if(reply.contains("pending")&&reply["pending"]==true)r.deadline=GetTickCount64()+3000;
   else if(reply.contains("fallback")&&reply["fallback"]==true){r.text.clear();r.fallback=true;Prioritize(pair.first);}
   else if(reply.contains("text")&&reply["text"].is_string()){auto text=reply["text"].get<std::string>();if(text.empty()||text.size()>96000||text.find('\0')!=std::string::npos)continue;r.text=text;r.fallback=false;Prioritize(pair.first);}
   else continue;accepted.push_back(r.requestId);break;
  }
 }
 auto rows=Json::array();for(size_t i=0;i<std::min(size_t(16),characterRows.size());i++){auto it=characterRows.upper_bound(characterPollCursor);if(it==characterRows.end())it=characterRows.begin();characterPollCursor=it->first;auto& r=it->second;rows.push_back({{"requestId",r.requestId},{"characterId",r.identity},{"source",r.source},{"displayName",r.name},{"framework",r.framework},{"nativeActorId",r.actorId},{"evidence",Json::array()}});}
 response["characterRuntimeContract"]=characterContract;response["characterRequests"]=rows;response["characterAccepted"]=accepted;
}

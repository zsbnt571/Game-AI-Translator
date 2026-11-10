#pragma once
#include <bcrypt.h>
#pragma comment(lib,"bcrypt.lib")
// Font-only leases are separate from translation leases and caches. Candidate
// fonts are initialized before selection commits; up to eight pairs are retained
// for this process so a still-referenced asset is never destroyed underneath UI.
struct CustomNativeFont {std::string path,family,fingerprint;uint32_t font=0,tmp=0;};
struct CustomNativeLease {uint32_t original=0,material=0;void* owned=nullptr,*ownedMaterial=nullptr;int revision=0;std::string checkedText;ULONGLONG checkAt=0;};
static std::vector<CustomNativeFont> customNativeFonts;static int customSelection=-1,customFontRevision=0;
static bool customIndependent=false;static std::map<int,CustomNativeLease> customFontLeases;
static std::set<int> customFontDeferred;
static bool NativeCustomSelected(){return customSelection>=0&&customSelection<(int)customNativeFonts.size();}
static void ForgetCustomFont(int key){customFontDeferred.erase(key);auto it=customFontLeases.find(key);if(it==customFontLeases.end())return;if(it->second.original)il2cpp_gchandle_free(it->second.original);if(it->second.material)il2cpp_gchandle_free(it->second.material);customFontLeases.erase(it);}
static bool RestoreNativeCustom(Binding& b,void* target){
 auto it=customFontLeases.find(b.key);if(it==customFontLeases.end())return true;auto& l=it->second;auto& f=families[b.family];auto current=Invoke(f.getFont,target);
 if(current==l.owned){auto original=il2cpp_gchandle_get_target(l.original);if(!Alive(original))return false;auto material=f.getMaterial?Invoke(f.getMaterial,target):nullptr;auto wanted=material==l.ownedMaterial&&l.material?il2cpp_gchandle_get_target(l.material):material;Root held(wanted);void* args[]={original};void* ignored=nullptr;
  if(!FontCall(f.setFont,target,args,ignored)||Invoke(f.getFont,target)!=original)return false;
  if(f.setMaterial&&Alive(wanted)){void* m[]={wanted};l.owned=original;l.ownedMaterial=Invoke(f.getMaterial,target);if(!FontCall(f.setMaterial,target,m,ignored)||Invoke(f.getMaterial,target)!=wanted)return false;}
 }
 ForgetCustomFont(b.key);return true;
}
static bool CustomGlyphs(void* font,bool tmp,const std::string& text){
 if(!Alive(font))return false;if(text.empty())return true;
 if(tmp){auto value=il2cpp_string_new(text.c_str());Root held(value);void* args[]={value};void* result=nullptr;auto klass=Class("TMPro","TMP_FontAsset");
  if(FontCall(ExactMethod(klass,"HasCharacters",{"System.String"}),font,args,result)&&Bool(result))return true;
  void* missing=nullptr;bool include=true;void* add[]={value,&missing,&include};FontCall(ExactMethod(klass,"TryAddCharacters",{"System.String","System.String&","System.Boolean"}),font,add,result);
  return FontCall(ExactMethod(klass,"HasCharacters",{"System.String"}),font,args,result)&&Bool(result);
 }
 auto has=ExactMethod(Class("UnityEngine","Font"),"HasCharacter",{"System.Int32"});if(!has)return false;for(auto cp:GlyphSet(text)){int code=(int)cp;void* args[]={&code};void* result=nullptr;if(!FontCall(has,font,args,result)||!Bool(result))return false;}return true;
}
static bool ApplyNativeCustom(Binding& b,void* target,const std::string& text){
 if(!NativeCustomSelected()||!Alive(target))return false;auto& f=families[b.family];auto& selected=customNativeFonts[customSelection];auto font=il2cpp_gchandle_get_target(f.tmp?selected.tmp:selected.font);
 auto prior=Invoke(f.getFont,target);auto found=customFontLeases.find(b.key);auto now=GetTickCount64();
 if(found!=customFontLeases.end()&&found->second.owned==prior&&prior==font&&Alive(font)&&found->second.revision==customFontRevision&&found->second.checkedText==text&&now<found->second.checkAt)return true;
 if(!CustomGlyphs(font,f.tmp,text)){RestoreNativeCustom(b,target);customFontDeferred.insert(b.key);return false;}
 if(found!=customFontLeases.end()&&found->second.owned==prior&&prior==font){found->second.revision=customFontRevision;found->second.checkedText=text;found->second.checkAt=now+500;customFontDeferred.erase(b.key);return true;}
 if(found!=customFontLeases.end()&&found->second.owned!=prior)ForgetCustomFont(b.key);
 bool hadLease=customFontLeases.count(b.key)!=0;
 if(!Alive(prior))return false;auto material=f.getMaterial?Invoke(f.getMaterial,target):nullptr;
 auto& lease=customFontLeases[b.key];if(!lease.original){lease.original=il2cpp_gchandle_new(prior,false);lease.material=material?il2cpp_gchandle_new(material,false):0;}
 if(!lease.original||(material&&!lease.material)){ForgetCustomFont(b.key);customFontDeferred.insert(b.key);return false;}
 auto previous=lease;lease.owned=font;void* args[]={font};void* ignored=nullptr;bool written=FontCall(f.setFont,target,args,ignored);auto actual=Invoke(f.getFont,target);lease.ownedMaterial=f.getMaterial?Invoke(f.getMaterial,target):nullptr;
 if(!written||actual!=font){
  // A setter may throw after assigning. Keep a provisional ownership receipt
  // and attempt to restore the last confirmed face, including its material.
  if(actual==font){void* rollback[]={prior};FontCall(f.setFont,target,rollback,ignored);actual=Invoke(f.getFont,target);
   if(actual==prior&&f.setMaterial&&Alive(material)){void* m[]={material};FontCall(f.setMaterial,target,m,ignored);}
  }
  if(actual==prior){bool materialRestored=!f.getMaterial||Invoke(f.getMaterial,target)==material;if(materialRestored){if(hadLease)lease=previous;else ForgetCustomFont(b.key);}else{lease.owned=prior;lease.ownedMaterial=Invoke(f.getMaterial,target);}}
  else if(actual&&actual!=font)ForgetCustomFont(b.key);
  customFontDeferred.insert(b.key);return false;
 }
 lease.owned=font;lease.ownedMaterial=f.getMaterial?Invoke(f.getMaterial,target):nullptr;lease.revision=customFontRevision;lease.checkedText=text;lease.checkAt=now+500;customFontDeferred.erase(b.key);return true;
}
static void DisposeCustomCandidate(void* font){if(!Alive(font))return;void* args[]={font};void* ignored=nullptr;FontCall(ExactMethod(Class("UnityEngine","Object"),"Destroy",{"UnityEngine.Object"}),nullptr,args,ignored);}
static bool NativeFontFamilyAvailable(const std::string& family){
 auto array=Invoke(Method(Class("UnityEngine","Font"),"GetOSInstalledFontNames",0));if(!array)return false;Root held(array);auto n=il2cpp_array_length(array);if(n>10000)return false;
 for(uint32_t i=0;i<n;i++){int index=(int)i;void* args[]={&index};if(_stricmp(Text(Invoke(arrayGet,array,args)).c_str(),family.c_str())==0)return true;}return false;
}
static std::wstring FontWide(const std::string& value){int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),(int)value.size(),nullptr,0);if(n<=0)return {};std::wstring w(n,L'\0');MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),(int)value.size(),w.data(),n);return w;}
static std::string NativeFontHash(const std::string& path){
 if(path.empty())return {};std::ifstream file(FontWide(path),std::ios::binary);if(!file)throw std::runtime_error("Font file unavailable.");
 BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;std::string result;
 try{if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0||BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)<0)throw std::runtime_error("Font fingerprint unavailable.");
  std::vector<unsigned char> buffer(65536);size_t total=0;while(file){file.read((char*)buffer.data(),buffer.size());auto count=file.gcount();if(count<=0)break;total+=(size_t)count;if(total>32*1024*1024||BCryptHashData(hash,buffer.data(),(ULONG)count,0)<0)throw std::runtime_error("Font fingerprint failed.");}
  unsigned char digest[32];if(!file.eof()||BCryptFinishHash(hash,digest,32,0)<0)throw std::runtime_error("Font fingerprint failed.");const char hex[]="0123456789abcdef";for(auto c:digest){result+=hex[c>>4];result+=hex[c&15];}
 }catch(...){if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);throw;}
 BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(algorithm,0);return result;
}
static CustomNativeFont BuildCustomNativeFont(const std::string& path,const std::string& family){
 auto klass=Class("UnityEngine","Font");if(!klass||!ExactMethod(Class("UnityEngine","Object"),"Destroy",{"UnityEngine.Object"}))throw std::runtime_error("This Unity runtime cannot safely prepare a custom font.");
 auto font=il2cpp_object_new(klass);if(!font)throw std::runtime_error("Custom font allocation failed.");Root root(font),tmpRoot(nullptr);void* tmp=nullptr;void* result=nullptr;
 try{
  if(!path.empty()){
   auto value=il2cpp_string_new(path.c_str());Root held(value);void* args[]={font,value};auto create=ExactMethod(klass,"Internal_CreateFontFromPath",{"UnityEngine.Font","System.String"});
   bool loaded=false;if(create)loaded=FontCall(create,nullptr,args,result);else{auto native=reinterpret_cast<void(*)(void*,void*)>(il2cpp_resolve_icall("UnityEngine.Font::Internal_CreateFontFromPath(UnityEngine.Font,System.String)"));loaded=native&&FontInitializer([&]{native(font,value);});}
   if(!loaded||!Alive(font))throw std::runtime_error("Unity rejected the selected font file.");
  }else{
   if(!NativeFontFamilyAvailable(family))throw std::runtime_error("The selected family is not installed in this Unity runtime.");
   auto names=il2cpp_array_new(Class("System","String"),1);Root held(names);auto value=il2cpp_string_new(family.c_str());Root name(value);int index=0,size=32;void* set[]={value,&index};
   if(!names||!FontCall(ExactMethod(Class("System","Array"),"SetValue",{"System.Object","System.Int32"}),names,set,result))throw std::runtime_error("Font family array unavailable.");
   void* args[]={font,names,&size};auto create=ExactMethod(klass,"Internal_CreateDynamicFont",{"UnityEngine.Font","System.String[]","System.Int32"});bool loaded=false;
   if(create)loaded=FontCall(create,nullptr,args,result);else{auto native=reinterpret_cast<void(*)(void*,void*,int)>(il2cpp_resolve_icall("UnityEngine.Font::Internal_CreateDynamicFont(UnityEngine.Font,System.String[],System.Int32)"));loaded=native&&FontInitializer([&]{native(font,names,size);});}
   if(!loaded||!Alive(font))throw std::runtime_error("Unity rejected the selected font family.");
  }
  bool legacyNeeded=false,tmpNeeded=false;for(auto& f:families){legacyNeeded|=!f.tmp;tmpNeeded|=f.tmp;}
  if(legacyNeeded&&!CustomGlyphs(font,false,"Aa0"))throw std::runtime_error("The selected font lacks a usable legacy Unity font face.");
  if(tmpNeeded){void* args[]={font};if(!FontCall(ExactMethod(Class("TMPro","TMP_FontAsset"),"CreateFontAsset",{"UnityEngine.Font"}),nullptr,args,tmp)||!Alive(tmp))throw std::runtime_error("This TMP version cannot create a selected font atlas.");
   tmpRoot.id=il2cpp_gchandle_new(tmp,false);if(!tmpRoot.id)throw std::runtime_error("TMP font could not be rooted.");bool allow=true;void* grow[]={&allow};FontCall(Method(Class("TMPro","TMP_FontAsset"),"set_isMultiAtlasTexturesEnabled",1),tmp,grow,result);
   if(!CustomGlyphs(tmp,true,"Aa0")||!RetainGeneratedFont(tmp))throw std::runtime_error("The selected TMP font could not be validated.");
  }
  if(!RetainGeneratedFont(font))throw std::runtime_error("Custom font lifetime could not be retained.");
  CustomNativeFont next;next.path=path;next.family=family;next.font=il2cpp_gchandle_new(font,false);next.tmp=tmp?il2cpp_gchandle_new(tmp,false):0;
  if(!next.font||(tmp&&!next.tmp)){if(next.font)il2cpp_gchandle_free(next.font);if(next.tmp)il2cpp_gchandle_free(next.tmp);throw std::runtime_error("Font lifetime handle unavailable.");}return next;
 }catch(...){DisposeCustomCandidate(tmp);DisposeCustomCandidate(font);throw;}
}
static Json ApplyNativeFontPreferences(const Json& request){
 auto mode=request.value("mode","");if(mode!="custom"&&mode!="default")throw std::runtime_error("Invalid font mode.");auto path=request.value("path",""),family=request.value("family","");bool independent=request.value("independent",false);int next=-1;
 if(mode=="custom"){
  auto safe=[](const std::string& s){return std::none_of(s.begin(),s.end(),[](unsigned char c){return c<32||c==127;});};
  if(path.size()>32768||family.size()>1024||!safe(path)||!safe(family)||(path.empty()&&family.empty()))throw std::runtime_error("Invalid custom font selection.");
  if(!path.empty()){auto wide=FontWide(path);auto dot=wide.find_last_of(L'.');auto extension=dot==std::wstring::npos?L"":wide.substr(dot);bool absolute=wide.size()>2&&wide[1]==L':'&&(wide[2]==L'\\'||wide[2]==L'/')||wide.size()>2&&wide[0]==L'\\'&&wide[1]==L'\\';WIN32_FILE_ATTRIBUTE_DATA info{};
   if(!absolute||(_wcsicmp(extension.c_str(),L".ttf")&&_wcsicmp(extension.c_str(),L".otf"))||!GetFileAttributesExW(wide.c_str(),GetFileExInfoStandard,&info)||info.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY||info.nFileSizeHigh||info.nFileSizeLow<12||info.nFileSizeLow>32*1024*1024)throw std::runtime_error("Select an available absolute TTF/OTF file up to 32 MiB.");}
  auto fingerprint=NativeFontHash(path);
  for(size_t i=0;i<customNativeFonts.size();i++)if(customNativeFonts[i].path==path&&customNativeFonts[i].family==family&&customNativeFonts[i].fingerprint==fingerprint&&Alive(il2cpp_gchandle_get_target(customNativeFonts[i].font))&&(!customNativeFonts[i].tmp||Alive(il2cpp_gchandle_get_target(customNativeFonts[i].tmp)))){next=(int)i;break;}
  if(next<0){if(customNativeFonts.size()>=8)throw std::runtime_error("Eight custom font selections are retained for this session; restart before adding another.");auto candidate=BuildCustomNativeFont(path,family);if(NativeFontHash(path)!=fingerprint){DisposeCustomCandidate(il2cpp_gchandle_get_target(candidate.tmp));DisposeCustomCandidate(il2cpp_gchandle_get_target(candidate.font));if(candidate.tmp)il2cpp_gchandle_free(candidate.tmp);il2cpp_gchandle_free(candidate.font);throw std::runtime_error("Font file changed while loading.");}candidate.fingerprint=fingerprint;customNativeFonts.push_back(candidate);next=(int)customNativeFonts.size()-1;}
 }
 if(next!=customSelection){customSelection=next;customFontRevision++;}customIndependent=independent;customFontDeferred.clear();
 for(auto& pair:bindings){pair.second.retryAt=0;pair.second.glyphReady=false;Prioritize(pair.first);}
 return {{"accepted",true},{"mode",mode},{"enabled",enabled},{"independent",customIndependent},{"fontLoaded",next>=0},{"revision",customFontRevision},{"restartRequired",false},{"writebackPending",!bindings.empty()},{"restorePending",next<0&&!customFontLeases.empty()},{"deferredComponents",next>=0?bindings.size():0},{"sessionFontLimit",8}};
}

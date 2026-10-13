#pragma once
#include "NativeLegacyFlow.h"

// Native UI.Text uses an integer size and the legacy word breaker. It must not
// enter the TMP float-size path. Work only on our OS font and fixed allocations.
struct LegacyNumberLease {int original=0,owned=0;bool held=false,relinquished=false;};
struct LegacyLayoutLease {
 LegacyNumberLease size,best;
 bool captured=false,geometry=false,geometryRelinquished=false,scaleHeld=false,deltaHeld=false;
 float originalScale=0,ownedScale=0,originalDelta=0,ownedDelta=0;
 float sourceSize=0,width=0,height=0,spacing=0;
 int style=-1;void* measuredFont=nullptr;std::string input,display;
};
static uint64_t legacyLayouts=0,legacyLayoutUnavailable=0,legacyAspectCorrections=0;
static bool LegacyInt(void* target,void* klass,const char* name,int& value){
 auto result=Invoke(Method(klass,(std::string("get_")+name).c_str(),0),target);
 if(!result)return false;auto c=il2cpp_object_get_class(result);auto n=std::string(il2cpp_class_get_name(c));
 if(n=="Boolean"){value=Bool(result)?1:0;return true;}
 if(n!="Int32"&&n!="FontStyle"&&n!="VerticalWrapMode"&&n!="HorizontalWrapMode")return false;
 value=*static_cast<int*>(il2cpp_object_unbox(result));return true;
}
static bool LegacySet(LegacyNumberLease& l,void* target,void* klass,const char* name,int desired,bool boolean=false){
 if(l.relinquished)return false;int current;if(!LegacyInt(target,klass,name,current))return false;
 if(l.held&&current!=l.owned){l.held=false;l.relinquished=true;return false;}
 if(current==desired)return true;
 auto set=Method(klass,(std::string("set_")+name).c_str(),1);if(!set)return false;
 // Retain a restoration receipt even if the setter succeeds but readback fails.
 if(!l.held)l.original=current;l.owned=desired;l.held=true;
 bool flag=desired!=0;void* args[]={boolean?(void*)&flag:(void*)&desired};Invoke(set,target,args);
 int after;if(!LegacyInt(target,klass,name,after)||after!=desired)return false;
 return true;
}
static bool LegacyVector(void* target,void* klass,const char* name,float* values,int count){
 auto value=Invoke(Method(klass,(std::string("get_")+name).c_str(),0),target);if(!value)return false;
 auto type=std::string(il2cpp_class_get_name(il2cpp_object_get_class(value)));
 if(type!=(count==2?"Vector2":count==3?"Vector3":"Rect"))return false;
 auto data=static_cast<float*>(il2cpp_object_unbox(value));for(int i=0;i<count;i++){if(!std::isfinite(data[i]))return false;values[i]=data[i];}return true;
}
static bool LegacySetVector(void* target,void* klass,const char* name,float* values){
 auto set=Method(klass,(std::string("set_")+name).c_str(),1);if(!set)return false;void* args[]={values};Invoke(set,target,args);
 float after[3];int count=std::string(name)=="localScale"?3:2;if(!LegacyVector(target,klass,name,after,count))return false;
 for(int i=0;i<count;i++)if(std::abs(after[i]-values[i])>.00001f)return false;return true;
}
static bool LegacyPending(const LegacyLayoutLease& l){return l.size.held||l.best.held||l.scaleHeld||l.deltaHeld;}
static void RestoreLegacyGeometry(LegacyLayoutLease& l,void* target,void* klass){
 if(!l.scaleHeld&&!l.deltaHeld){l.geometry=false;return;}
 auto tr=Invoke(Method(klass,"get_rectTransform",0),target);if(!tr)return;auto tc=il2cpp_object_get_class(tr);
 // Each property is restored independently. A game animation may replace one
 // axis without taking ownership of the other compensated property.
 if(l.deltaHeld){float delta[2];if(LegacyVector(tr,tc,"sizeDelta",delta,2)){
  if(std::abs(delta[1]-l.ownedDelta)>.01f)l.deltaHeld=false;
  else{delta[1]=l.originalDelta;if(LegacySetVector(tr,tc,"sizeDelta",delta))l.deltaHeld=false;}
 }}
 if(l.scaleHeld){float scale[3];if(LegacyVector(tr,tc,"localScale",scale,3)){
  if(std::abs(scale[1]-l.ownedScale)>.00001f)l.scaleHeld=false;
  else{scale[1]=l.originalScale;if(LegacySetVector(tr,tc,"localScale",scale))l.scaleHeld=false;}
 }}
 l.geometry=l.scaleHeld||l.deltaHeld;
}
static void RestoreLegacy(LegacyLayoutLease& l,void* target,void* klass){
 for(auto row:{std::make_pair(&l.size,"fontSize"),std::make_pair(&l.best,"resizeTextForBestFit")}){
  auto& lease=*row.first;int current;
  if(lease.held&&LegacyInt(target,klass,row.second,current)){
   if(current!=lease.owned){lease.held=false;continue;}
   bool flag=lease.original!=0;void* args[]={row.first==&l.best?(void*)&flag:(void*)&lease.original};Invoke(Method(klass,(std::string("set_")+row.second).c_str(),1),target,args);
   if(LegacyInt(target,klass,row.second,current)&&current==lease.original)lease.held=false;
  }
 }
 RestoreLegacyGeometry(l,target,klass);
 if(!LegacyPending(l))l={};
}
static void CaptureLegacy(LegacyLayoutLease& l,void* target,void* klass,const std::string& source,bool originalFont=true){
 if(l.captured)return;l.captured=true;
 int size=0,max=0,best=0;LegacyInt(target,klass,"fontSize",size);LegacyInt(target,klass,"resizeTextMaxSize",max);LegacyInt(target,klass,"resizeTextForBestFit",best);
 l.sourceSize=(float)(best?std::max(size,max):size);
 // A reused control may still hold the last translated generation. Reusing
 // that line height as a new source ceiling would shrink successive sentences.
 if(!originalFont||(source.size()<80&&source.find('\n')==std::string::npos))return;
 // Use the already rendered source line to avoid enlarging a CJK paragraph to
 // the game's often enormous nominal best-fit size. No renderer mutation.
 auto generator=Invoke(Method(klass,"get_cachedTextGenerator",0),target);if(!generator)return;
 auto list=Invoke(Method(il2cpp_object_get_class(generator),"get_lines",0),generator);if(!list)return;Root held(list);
 auto lc=il2cpp_object_get_class(list);auto count=Invoke(Method(lc,"get_Count",0),list);if(!count||*static_cast<int*>(il2cpp_object_unbox(count))<1)return;
 int zero=0;void* args[]={&zero};auto line=Invoke(Method(lc,"get_Item",1),list,args);if(!line||std::string(il2cpp_class_get_name(il2cpp_object_get_class(line)))!="UILineInfo")return;
 // UILineInfo is Unity's public sequential value type: startCharIdx, height.
 int pixels=static_cast<int*>(il2cpp_object_unbox(line))[1];float ppu=Scalar(Invoke(Method(klass,"get_pixelsPerUnit",0),target));
 if(best&&pixels>0&&ppu>0&&std::isfinite(ppu))l.sourceSize=std::min(l.sourceSize,std::max(1.f,pixels/ppu/1.25f));
}
static bool LegacyMeasure(const std::wstring& face,const std::wstring& text,int size,int style,std::vector<float>& advances,float& lineHeight){
 if(size<1||size>512||text.size()>6000||face.empty()||face.size()>=LF_FACESIZE)return false;
 struct Metrics {HDC dc=nullptr;HFONT font=nullptr;HGDIOBJ old=nullptr;~Metrics(){if(dc&&old)SelectObject(dc,old);if(font)DeleteObject(font);if(dc)DeleteDC(dc);}};
 static std::map<std::pair<std::wstring,int>,std::unique_ptr<Metrics>> cache;
 auto key=std::make_pair(face,style);auto found=cache.find(key);
 if(found==cache.end()){
  if(cache.size()>=16)return false;auto m=std::make_unique<Metrics>();m->dc=CreateCompatibleDC(nullptr);if(!m->dc)return false;
  m->font=CreateFontW(-128,0,0,0,(style&1)?FW_BOLD:FW_NORMAL,(style&2)?TRUE:FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,DEFAULT_QUALITY,DEFAULT_PITCH,face.c_str());
  if(!m->font)return false;m->old=SelectObject(m->dc,m->font);found=cache.emplace(key,std::move(m)).first;
 }
 auto dc=found->second->dc;TEXTMETRICW metric{};std::vector<int> widths(text.size());SIZE extent{};
 bool ok=GetTextMetricsW(dc,&metric)&&GetTextExtentExPointW(dc,text.c_str(),(int)text.size(),INT_MAX,nullptr,widths.data(),&extent);
 if(ok){float scale=size/128.f;advances.resize(text.size());int previous=0;for(size_t i=0;i<text.size();i++){advances[i]=scale*std::max(0,widths[i]-previous);previous=widths[i];if(text[i]==L'\r'||text[i]==L'\n')advances[i]=0;}lineHeight=std::max(size*1.35f,scale*(metric.tmHeight+metric.tmExternalLeading));}
 return ok;
}
static std::string PrepareLegacyLayout(LegacyLayoutLease& l,void* target,void* klass,void* font,const std::string& text){
 if(!HanText(text)){RestoreLegacy(l,target,klass);return text;}auto tr=Invoke(Method(klass,"get_rectTransform",0),target);if(!tr)return text;auto tc=il2cpp_object_get_class(tr);
 int overflow=1;if(!LegacyInt(target,klass,"verticalOverflow",overflow)||overflow!=0||NativeLayoutOwnsTextSize(tr)){RestoreLegacy(l,target,klass);return text;}
 float rect[4],local[3],world[3],delta[2];if(!LegacyVector(tr,tc,"rect",rect,4)||rect[2]<=1||rect[3]<=1)return text;
 if(l.geometry&&LegacyVector(tr,tc,"localScale",local,3)&&LegacyVector(tr,tc,"sizeDelta",delta,2)&&
   (std::abs(local[1]-l.ownedScale)>.00001f||std::abs(delta[1]-l.ownedDelta)>.01f)){
  RestoreLegacyGeometry(l,target,klass);l.geometryRelinquished=true;l.input.clear();LegacyVector(tr,tc,"rect",rect,4);
 }
 // Correct only a leaf text node. Inverse height compensation preserves every
 // rendered corner, pivot, position and ancestor button's clickable rectangle.
 auto children=Invoke(Method(tc,"get_childCount",0),tr);
 if(!l.geometry&&!l.geometryRelinquished&&children&&*static_cast<int*>(il2cpp_object_unbox(children))==0&&
    !EnabledLayoutComponent(tr,"Selectable")&&!EnabledLayoutComponent(tr,"AspectRatioFitter")&&
    LegacyVector(tr,tc,"localScale",local,3)&&LegacyVector(tr,tc,"lossyScale",world,3)&&LegacyVector(tr,tc,"sizeDelta",delta,2)&&std::abs(world[1])>.00001f){
  float ratio=std::abs(world[0]/world[1]);
  if(ratio>1.12f&&ratio<=2.5f){
   l.originalScale=local[1];l.originalDelta=delta[1];local[1]*=ratio;delta[1]+=rect[3]*(1/ratio-1);
   l.ownedScale=local[1];l.ownedDelta=delta[1];l.deltaHeld=true;
   if(LegacySetVector(tr,tc,"sizeDelta",delta)){
    l.scaleHeld=true;
    if(LegacySetVector(tr,tc,"localScale",local)){l.geometry=true;legacyAspectCorrections++;LegacyVector(tr,tc,"rect",rect,4);}
    else {RestoreLegacyGeometry(l,target,klass);l.geometryRelinquished=true;}
   }else {RestoreLegacyGeometry(l,target,klass);l.geometryRelinquished=true;}
  }
 }
 int size=0,style=0,best=0;if(!LegacyInt(target,klass,"fontSize",size)||!LegacyInt(target,klass,"fontStyle",style)||!LegacyInt(target,klass,"resizeTextForBestFit",best))return text;
 if(l.size.relinquished||l.best.relinquished)return text;
 if((l.size.held&&size!=l.size.owned)||(l.best.held&&best!=l.best.owned)){l.size.relinquished=l.best.relinquished=true;return text;}
 float spacing=Scalar(Invoke(Method(klass,"get_lineSpacing",0),target));if(!std::isfinite(spacing)||spacing<=0)spacing=1;
 if(l.input==text&&l.width==rect[2]&&l.height==rect[3]&&l.style==style&&l.spacing==spacing&&l.measuredFont==font)return l.display.empty()?text:l.display;
 // Rich-text size/material tags are authored metric instructions. Leave those
 // buffers with Unity until a measured rich-run implementation is available.
 if(text.find('<')!=std::string::npos||text.find('\t')!=std::string::npos||text.size()>18000){legacyLayoutUnavailable++;return text;}
 auto name=Text(Invoke(Method(Class("UnityEngine","Object"),"get_name",0),font));
 auto wide=[](const std::string& value){int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),(int)value.size(),nullptr,0);std::wstring w(n,L'\0');if(n)MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),(int)value.size(),w.data(),n);return w;};
 auto content=wide(text),face=wide(name);if(content.empty())return text;
 int ceiling=std::clamp((int)std::floor(l.sourceSize>0?l.sourceSize:size),1,512),chosen=1;
 LegacyFlow::Result chosenFlow;
 // The source size is a ceiling, never a reason to exceed a small row. Fixed
 // integer candidates prevent the legacy engine from rasterizing hundreds of
 // unused best-fit sizes into the shared CJK atlas.
 int low=1,high=ceiling;
 for(int iteration=0;low<=high&&iteration<10;iteration++){
  int mid=(low+high)/2;std::vector<float> advances;float line=0;
  if(!LegacyMeasure(face,content,mid,style,advances,line)){legacyLayoutUnavailable++;return text;}
  auto flow=LegacyFlow::Wrap(content,advances,rect[2]*.97f);
  float demand=line+(flow.lines-1)*line*std::max(1.f,spacing);
  if(flow.valid&&flow.width<=rect[2]&&demand<=rect[3]*.95f){chosen=mid;chosenFlow=std::move(flow);low=mid+1;}else high=mid-1;
 }
 if(!chosenFlow.valid){legacyLayoutUnavailable++;return text;}
 if(!LegacySet(l.size,target,klass,"fontSize",chosen)||!LegacySet(l.best,target,klass,"resizeTextForBestFit",0,true))return text;
 l.input=text;l.width=rect[2];l.height=rect[3];l.style=style;l.spacing=spacing;l.measuredFont=font;l.display=Utf8(chosenFlow.text);legacyLayouts++;return l.display;
}

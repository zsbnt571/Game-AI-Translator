#pragma once
#include <cmath>

struct TextFitLease {float original=0,owned=0,width=0,height=0;std::string text;bool relinquished=false;};
static float Scalar(void* value){return value?*reinterpret_cast<float*>(il2cpp_object_unbox(value)):0;}
#include "NativeLayoutOwnership.h"
static bool HanText(const std::string& text){
 auto s=il2cpp_string_new(text.c_str());if(!s)return false;
 int n=il2cpp_string_length(s);auto chars=il2cpp_string_chars(s);
 for(int i=0;i<n;i++)if(chars[i]>=0x2e80&&chars[i]<=0x9fff)return true;
 return false;
}
static void RestoreFit(TextFitLease& lease,void* target,void* klass){
 if(lease.owned>0){auto get=Method(klass,"get_fontSize",0);auto value=Invoke(get,target);if(!value)return;auto current=Scalar(value);if(!std::isfinite(current))return;if(std::abs(current-lease.owned)<0.01f){void* a[]={&lease.original};Invoke(Method(klass,"set_fontSize",1),target,a);auto restored=Invoke(get,target);if(!restored||!std::isfinite(Scalar(restored))||std::abs(Scalar(restored)-lease.original)>=0.01f)return;}}
 lease={};
}
static void FitText(TextFitLease& lease,void* target,void* klass,const std::string& text,bool fontRepaired=false){
 // Text's authored rectangle, anchors, parent layout and hit targets stay intact.
 // Size only a fixed TMP UI label; a fitter or game auto-size owns its own layout.
 if(!klass||std::string(il2cpp_class_get_name(klass))!="TextMeshProUGUI")return;
 auto getSize=Method(klass,"get_fontSize",0),setSize=Method(klass,"set_fontSize",1);
 auto transform=Invoke(Method(klass,"get_rectTransform",0),target);
 if(!transform||!getSize||!setSize||!HanText(text))return;
 float current=Scalar(Invoke(getSize,target));if(!std::isfinite(current)||current<=0)return;
 if(lease.owned>0&&std::abs(current-lease.owned)>0.01f){lease.owned=0;lease.relinquished=true;}
 if(lease.relinquished)return;
 if(Bool(Invoke(Method(klass,"get_enableAutoSizing",0),target))||Bool(Invoke(Method(klass,"get_autoSizeTextContainer",0),target))||NativeLayoutOwnsTextSize(transform)){RestoreFit(lease,target,klass);return;}
 auto rect=Invoke(Method(il2cpp_object_get_class(transform),"get_rect",0),transform);if(!rect)return;
 auto rc=il2cpp_object_get_class(rect);auto data=il2cpp_object_unbox(rect);
 float width=Scalar(Invoke(Method(rc,"get_width",0),data));float height=Scalar(Invoke(Method(rc,"get_height",0),data));
 if(!std::isfinite(width)||!std::isfinite(height)||width<=1||height<=1)return;
 bool contextChanged=fontRepaired||lease.text!=text||lease.width!=width||lease.height!=height;
 lease.text=text;lease.width=width;lease.height=height;
 if(!lease.owned)lease.original=current;
 if(contextChanged&&lease.owned&&current!=lease.original){void* a[]={&lease.original};Invoke(setSize,target,a);current=Scalar(Invoke(getSize,target));if(std::abs(current-lease.original)>0.01f){lease.owned=0;lease.relinquished=true;return;}lease.owned=0;}
 // Text and rectangle alone do not identify font metrics, margins or spacing.
 // Measure the current size on each paced call, without resetting a stable
 // fitted label to its original size and dirtying layout every half second.
 auto measure=ExactMethod(Class("TMPro","TMP_Text"),"GetPreferredValues",{"System.String","System.Single","System.Single"});if(!measure)return;
 auto value=il2cpp_string_new(text.c_str());Root rooted(value);float tall=1000000.0f;
 for(int iteration=0;iteration<3;iteration++){
  void* a[]={value,&width,&tall};auto preferred=Invoke(measure,target,a);if(!preferred)break;
  auto c=il2cpp_object_get_class(preferred);
  if(std::string(il2cpp_class_get_namespace(c))!="UnityEngine"||std::string(il2cpp_class_get_name(c))!="Vector2")break;
  // Vector2 is the public two-float value type, not an opaque runtime object.
  auto pair=reinterpret_cast<float*>(il2cpp_object_unbox(preferred));float neededWidth=pair[0],neededHeight=pair[1];
  if(!std::isfinite(neededWidth)||!std::isfinite(neededHeight)||neededWidth<0||neededHeight<=0)break;
  float ratio=std::max(neededWidth/width,neededHeight*1.05f/height);
  if(ratio<=1.0f)break;
  float next=std::max(std::min(8.0f,lease.original),current*std::min(0.95f,1.0f/ratio));
  if(next>=current-0.01f)break;void* sizeArgs[]={&next};Invoke(setSize,target,sizeArgs);
  current=Scalar(Invoke(getSize,target));if(!std::isfinite(current)||std::abs(current-next)>0.01f){lease.owned=0;lease.relinquished=true;break;}lease.owned=current;
 }
}

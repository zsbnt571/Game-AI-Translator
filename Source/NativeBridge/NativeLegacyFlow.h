#pragma once
#include <cmath>
#include <set>

// A display-only line buffer. Cache keys and provider translations remain exact.
namespace LegacyFlow {
static bool Cjk(wchar_t c){return (c>=0x2e80&&c<=0x9fff)||(c>=0xac00&&c<=0xd7af)||(c>=0xf900&&c<=0xfaff);}
static bool Closing(wchar_t c){return std::wstring(L"，。！？、；：)]}”’》」』】％%…").find(c)!=std::wstring::npos;}
static bool Opening(wchar_t c){return std::wstring(L"([{“‘《「『【").find(c)!=std::wstring::npos;}
static bool Latin(wchar_t c){return (c>=L'0'&&c<=L'9')||(c>=L'A'&&c<=L'Z')||(c>=L'a'&&c<=L'z')||c==L'\'';}
static bool Combining(wchar_t c){return (c>=0x300&&c<=0x36f)||(c>=0xfe00&&c<=0xfe0f)||(c>=0xdc00&&c<=0xdfff);}
struct Result {std::wstring text;float width=0;int lines=0;bool valid=false;};
static Result Wrap(const std::wstring& text,const std::vector<float>& advance,float width){
 Result r;if(text.empty()||text.size()!=advance.size()||text.size()>6000||!std::isfinite(width)||width<=1)return r;
 size_t start=0;bool trailingBreak=false;
 while(start<text.size()&&r.lines<512){
  if(text[start]==L'\r'||text[start]==L'\n'){
   if(text[start]==L'\r'&&start+1<text.size()&&text[start+1]==L'\n')start++;
   r.text+=L'\n';r.lines++;start++;trailingBreak=true;continue;
  }
  size_t end=start,lastBreak=start;float used=0;
  while(end<text.size()&&text[end]!=L'\n'&&text[end]!=L'\r'){
   float next=advance[end];if(!std::isfinite(next)||next<0)return {};
   if(used+next>width&&end>start)break;
   used+=next;end++;
   if(end<text.size()&&!Combining(text[end])&&!Closing(text[end])&&!Opening(text[end-1])&&
      (text[end-1]==L' '||Cjk(text[end-1])||Cjk(text[end])||Closing(text[end-1])))lastBreak=end;
  }
  if(end<text.size()&&text[end]!=L'\r'&&text[end]!=L'\n'){
   if(lastBreak>start)end=lastBreak;
   while(end<text.size()&&Combining(text[end]))end++;
   if(end<=start)return {};
   used=0;for(size_t i=start;i<end;i++)used+=advance[i];
  }
  r.text.append(text,start,end-start);r.width=std::max(r.width,used);r.lines++;trailingBreak=false;start=end;
  if(start<text.size()){
   if(text[start]==L'\r'||text[start]==L'\n'){
    if(text[start]==L'\r'&&start+1<text.size()&&text[start+1]==L'\n')start++;
    start++;
   }else while(start<text.size()&&text[start]==L' ')start++;
   r.text+=L'\n';trailingBreak=true;
  }
 }
 if(trailingBreak)r.lines++;
 r.valid=start==text.size();return r;
}
}

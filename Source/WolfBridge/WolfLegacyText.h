#pragma once
#include <windows.h>
#include <array>
#include <atomic>
#include <mutex>
#include <string>
#include <unordered_map>

// WOLF 2 parses two-byte Shift-JIS characters before the Unicode font renderer.
// Reserve Shift-JIS user characters for unrepresentable BMP letters, then resolve
// them at the renderer's Unicode conversion boundary, before glyph-cache lookup.
// The mapping is scoped to translated layout calls; no game string/data is saved.
namespace WolfLegacyText {
inline std::array<std::atomic<wchar_t>,1880> characters{};
inline std::unordered_map<wchar_t,unsigned> assigned;
inline std::mutex gate;
inline bool Decode(const std::string& raw,std::string& text) {
    int n=MultiByteToWideChar(932,MB_ERR_INVALID_CHARS,raw.data(),(int)raw.size(),nullptr,0);if(n<=0)return false;
    std::wstring wide(n,0);MultiByteToWideChar(932,MB_ERR_INVALID_CHARS,raw.data(),(int)raw.size(),wide.data(),n);
    n=WideCharToMultiByte(CP_UTF8,0,wide.data(),n,nullptr,0,nullptr,nullptr);if(n<=0)return false;
    text.resize(n);return WideCharToMultiByte(CP_UTF8,0,wide.data(),(int)wide.size(),text.data(),n,nullptr,nullptr)==n;
}
inline bool Encode(const std::string& text,std::string& raw) {
    int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),(int)text.size(),nullptr,0);if(n<=0||n>24000)return false;
    std::wstring wide(n,0);MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),(int)text.size(),wide.data(),n);
    std::string result;result.reserve(n*2);std::lock_guard<std::mutex> lock(gate);
    for(wchar_t c:wide){
        // Private-use text and surrogate pairs cannot share the alias namespace.
        if(c>=0xd800&&c<=0xf8ff)return false;
        char bytes[4];BOOL fallback=FALSE;wchar_t check=0;
        int count=WideCharToMultiByte(932,WC_NO_BEST_FIT_CHARS,&c,1,bytes,4,nullptr,&fallback);
        if(count>0&&!fallback&&MultiByteToWideChar(932,MB_ERR_INVALID_CHARS,bytes,count,&check,1)==1&&check==c){result.append(bytes,count);continue;}
        unsigned index;auto found=assigned.find(c);
        if(found==assigned.end()){if(assigned.size()>=characters.size())return false;index=(unsigned)assigned.size();assigned.emplace(c,index);characters[index].store(c,std::memory_order_release);}else index=found->second;
        unsigned trail=index%188+0x40;if(trail>=0x7f)++trail;
        result+=(char)(0xf0+index/188);result+=(char)trail;
    }
    raw=std::move(result);return true;
}
inline unsigned Resolve(void* destination,int bytes) {
    if(bytes<2||bytes>1024*1024)return 0;unsigned count=0;
    __try {auto wide=(wchar_t*)destination;for(int i=0;i<bytes/2&&wide[i];++i){unsigned c=wide[i];if(c>=0xe000&&c<0xe000+characters.size()){wchar_t target=characters[c-0xe000].load(std::memory_order_acquire);if(target){wide[i]=target;++count;}}}}
    __except(EXCEPTION_EXECUTE_HANDLER){return 0;}return count;
}
}

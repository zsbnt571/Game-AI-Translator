#pragma once
#include <windows.h>
#include <string>
#include <mutex>
#include <unordered_map>
#include <atomic>

namespace WolfFont {
// Positive LOGFONT height means cell height, not glyph/em height. Noto CJK
// has different internal leading from Japanese game fonts. Reusing a positive
// height made only the missing Chinese characters roughly 30% smaller.
inline bool Description(HDC dc,LOGFONTW& result){
    HFONT current=(HFONT)GetCurrentObject(dc,OBJ_FONT);TEXTMETRICW metrics={};
    if(GetObjectW(current,sizeof(result),&result)!=sizeof(result)||!GetTextMetricsW(dc,&metrics))return false;
    LONG em=metrics.tmHeight-metrics.tmInternalLeading;
    if(em<=0||em>4096)return false;
    result.lfHeight=-em;
    wcscpy_s(result.lfFaceName,L"Noto Sans CJK SC");result.lfCharSet=GB2312_CHARSET;
    return true;
}
class Renderer {
    std::mutex gate;
    std::unordered_map<std::string,HFONT> fonts;
    std::atomic<unsigned> applied{0};
    struct Selection {
        HDC dc;HGDIOBJ original;
        ~Selection(){if(original&&original!=HGDI_ERROR)SelectObject(dc,original);}
    };
public:
    unsigned Count()const{return applied.load(std::memory_order_relaxed);}
    ~Renderer(){for(auto& entry:fonts)DeleteObject(entry.second);}
    DWORD Draw(decltype(&GetGlyphOutlineW) original,bool translated,HDC dc,UINT ch,UINT format,
        LPGLYPHMETRICS metrics,DWORD count,LPVOID buffer,const MAT2* matrix) noexcept {
        try{
            if(!translated||(format&GGO_GLYPH_INDEX)||ch<0x2e80||ch>0xffef)
                return original(dc,ch,format,metrics,count,buffer,matrix);
            wchar_t character=(wchar_t)ch;WORD index=0;
            if(GetGlyphIndicesW(dc,&character,1,&index,GGI_MARK_NONEXISTING_GLYPHS)==GDI_ERROR||index!=0xffff)
                return original(dc,ch,format,metrics,count,buffer,matrix);
            LOGFONTW lf={};if(!Description(dc,lf))return original(dc,ch,format,metrics,count,buffer,matrix);
            std::string key((const char*)&lf,sizeof(lf));HFONT fallback=nullptr;
            {std::unique_lock<std::mutex> lock(gate,std::try_to_lock);if(lock){
                auto found=fonts.find(key);if(found!=fonts.end())fallback=found->second;
                else if(fonts.size()<128){fallback=CreateFontIndirectW(&lf);if(fallback)fonts.emplace(std::move(key),fallback);}
            }}
            if(!fallback)return original(dc,ch,format,metrics,count,buffer,matrix);
            Selection selection{dc,SelectObject(dc,fallback)};
            if(!selection.original||selection.original==HGDI_ERROR)return original(dc,ch,format,metrics,count,buffer,matrix);
            DWORD result=original(dc,ch,format,metrics,count,buffer,matrix);
            if(result!=GDI_ERROR)applied.fetch_add(1,std::memory_order_relaxed);
            return result;
        }catch(...){return original(dc,ch,format,metrics,count,buffer,matrix);}
    }
};
}

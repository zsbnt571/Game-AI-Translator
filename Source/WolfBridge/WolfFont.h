#pragma once
#include <windows.h>
#include <string>
#include <mutex>
#include <unordered_map>
#include <atomic>
#include <memory>
#include <functional>
#include "WolfFontSelection.h"

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
    std::shared_ptr<StartupSelection> selection=std::make_shared<StartupSelection>();
    std::mutex drawingGate;
    std::mutex gate;
    std::unordered_map<std::string,HFONT> fonts;
    std::atomic<unsigned> applied{0};
    std::atomic<unsigned> customApplied{0},customFailed{0};
    struct Selection {
        HDC dc;HGDIOBJ original;
        ~Selection(){if(original&&original!=HGDI_ERROR)SelectObject(dc,original);}
    };
public:
    unsigned Count()const{return applied.load(std::memory_order_relaxed);}
    unsigned CustomCount()const{return customApplied.load(std::memory_order_relaxed);}
    unsigned CustomFailures()const{return customFailed.load(std::memory_order_relaxed);}
    bool CustomActive()const{return std::atomic_load(&selection)->Active();}
    bool CustomFailed()const{auto current=std::atomic_load(&selection);return current->Requested()&&!current->Active();}
    bool Configure(const std::wstring& path,const std::wstring& face,const std::wstring& hash,bool failed=false){return selection->Configure(path,face,hash,failed);}
    // This is an engine transaction, not a transport-level setter. The caller
    // must run at a verified engine safe point and clear EVERY live DxLib font
    // cache through its native API before returning true. A GDI cache flush,
    // text-layout refresh or successful file load cannot satisfy this contract.
    bool ReplaceAfterCacheClear(const std::wstring& path,const std::wstring& face,const std::wstring& hash,
        const std::function<bool()>& clearEngineCaches){
        if(!clearEngineCaches)return false;
        auto candidate=std::make_shared<StartupSelection>();
        if(!candidate->Configure(path,face,hash))return false;
        std::lock_guard<std::mutex> drawing(drawingGate);
        // drawingGate excludes every Draw while old handles and the previous
        // private font registration retire. Repeated switches do not accumulate
        // registrations and restoring the default never exhausts a quota.
        try{if(!clearEngineCaches())return false;}catch(...){return false;}
        {std::lock_guard<std::mutex> lock(gate);for(auto& entry:fonts)DeleteObject(entry.second);fonts.clear();}
        std::atomic_store(&selection,std::move(candidate));
        return true;
    }
    ~Renderer(){for(auto& entry:fonts)DeleteObject(entry.second);}
    DWORD Draw(decltype(&GetGlyphOutlineW) original,bool translated,HDC dc,UINT ch,UINT format,
        LPGLYPHMETRICS metrics,DWORD count,LPVOID buffer,const MAT2* matrix) noexcept {
        try{
            std::lock_guard<std::mutex> drawing(drawingGate);
            auto snapshot=std::atomic_load(&selection);const auto& custom=*snapshot;
            if(custom.Active()&&!(format&GGO_GLYPH_INDEX)&&ch>=32&&ch<=0xffef&&!(ch>=0xd800&&ch<=0xdfff)){
                LOGFONTW lf={};if(Description(dc,lf)){
                    wcscpy_s(lf.lfFaceName,custom.Family().c_str());lf.lfCharSet=DEFAULT_CHARSET;
                    std::string key("custom:");key.append((const char*)&lf,sizeof(lf));HFONT selected=nullptr;
                    {std::unique_lock<std::mutex> lock(gate,std::try_to_lock);if(lock){
                        auto found=fonts.find(key);if(found!=fonts.end())selected=found->second;
                        else if(fonts.size()<128){
                            HFONT created=CreateFontIndirectW(&lf);
                            if(created){bool exact=false;{Selection probe{dc,SelectObject(dc,created)};exact=probe.original&&probe.original!=HGDI_ERROR&&custom.Matches(dc);}
                                if(exact){selected=created;fonts.emplace(std::move(key),created);}else DeleteObject(created);}
                        }
                    }}
                    if(selected){
                        Selection selection{dc,SelectObject(dc,selected)};wchar_t character=(wchar_t)ch;WORD index=0;
                        if(selection.original&&selection.original!=HGDI_ERROR&&GetGlyphIndicesW(dc,&character,1,&index,GGI_MARK_NONEXISTING_GLYPHS)!=GDI_ERROR&&index!=0&&index!=0xffff){
                            DWORD result=original(dc,ch,format,metrics,count,buffer,matrix);
                            if(result!=GDI_ERROR){customApplied.fetch_add(1,std::memory_order_relaxed);return result;}
                        }
                    }
                    customFailed.fetch_add(1,std::memory_order_relaxed);
                }
            }
            // The bundled face also has Cyrillic, Greek and accented Latin.
            // Only supplement a missing Unicode glyph; preserve all glyphs
            // already supplied by the game, including native Chinese text.
            // GetGlyphOutlineW consumes one UTF-16 code unit, not a scalar or
            // shaped sequence, so never reinterpret a surrogate or glyph id.
            if(!translated||(format&GGO_GLYPH_INDEX)||ch<0xa0||ch>0xffef||(ch>=0xd800&&ch<=0xdfff))
                return original(dc,ch,format,metrics,count,buffer,matrix);
            wchar_t character=(wchar_t)ch;WORD index=0;
            // Some symbol faces report .notdef (index zero) rather than
            // 0xffff for an absent Unicode character.
            if(GetGlyphIndicesW(dc,&character,1,&index,GGI_MARK_NONEXISTING_GLYPHS)==GDI_ERROR||(index!=0xffff&&index!=0))
                return original(dc,ch,format,metrics,count,buffer,matrix);
            LOGFONTW lf={};if(!Description(dc,lf))return original(dc,ch,format,metrics,count,buffer,matrix);
            std::string key((const char*)&lf,sizeof(lf));HFONT fallback=nullptr;
            {std::unique_lock<std::mutex> lock(gate,std::try_to_lock);if(lock){
                auto found=fonts.find(key);if(found!=fonts.end())fallback=found->second;
                else if(fonts.size()<128){fallback=CreateFontIndirectW(&lf);if(fallback)fonts.emplace(std::move(key),fallback);}
            }}
            if(!fallback)return original(dc,ch,format,metrics,count,buffer,matrix);
            {
                Selection selection{dc,SelectObject(dc,fallback)};
                if(!selection.original||selection.original==HGDI_ERROR)return original(dc,ch,format,metrics,count,buffer,matrix);
                // A successful font handle is not evidence that this face
                // contains the character. Do not replace .notdef with another
                // .notdef or commit a failed bitmap/metrics request.
                WORD fallbackIndex=0;
                if(GetGlyphIndicesW(dc,&character,1,&fallbackIndex,GGI_MARK_NONEXISTING_GLYPHS)!=GDI_ERROR&&fallbackIndex!=0xffff&&fallbackIndex!=0){
                    DWORD result=original(dc,ch,format,metrics,count,buffer,matrix);
                    if(result!=GDI_ERROR){applied.fetch_add(1,std::memory_order_relaxed);return result;}
                }
            }
            // Selection restores the exact original HDC font before retrying.
            // Font support must not turn an otherwise drawable result into an
            // error, nor leave the fallback selected for the next game glyph.
            return original(dc,ch,format,metrics,count,buffer,matrix);
        }catch(...){return original(dc,ch,format,metrics,count,buffer,matrix);}
    }
};
}

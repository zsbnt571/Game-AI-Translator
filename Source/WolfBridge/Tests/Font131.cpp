#include <windows.h>
#include <fstream>
#include <iostream>
#include <vector>
#include <filesystem>
#include "json.hpp"
#include "../WolfFont.h"

using nlohmann::json;
static HFONT expectedOriginal=nullptr;
static int injectedCalls=0,restoredCalls=0;
static DWORD WINAPI FailFallback(HDC dc,UINT ch,UINT format,LPGLYPHMETRICS metrics,DWORD count,LPVOID buffer,const MAT2* matrix){
    if(GetCurrentObject(dc,OBJ_FONT)!=expectedOriginal){injectedCalls++;return GDI_ERROR;}
    restoredCalls++;return GetGlyphOutlineW(dc,ch,format,metrics,count,buffer,matrix);
}
static WORD Index(HDC dc,wchar_t ch){WORD result=0xffff;return GetGlyphIndicesW(dc,&ch,1,&result,GGI_MARK_NONEXISTING_GLYPHS)==GDI_ERROR?0xffff:result;}
struct Raster {DWORD size=GDI_ERROR;GLYPHMETRICS metrics={};std::vector<BYTE> pixels;};
static MAT2 identity={{0,1},{0,0},{0,0},{0,1}};
static Raster Draw(HDC dc,UINT ch,WolfFont::Renderer* renderer=nullptr,bool enabled=true,UINT format=GGO_GRAY8_BITMAP){
    Raster r;auto call=[&](DWORD size,LPVOID data){return renderer?renderer->Draw(GetGlyphOutlineW,enabled,dc,ch,format,&r.metrics,size,data,&identity):GetGlyphOutlineW(dc,ch,format,&r.metrics,size,data,&identity);};
    r.size=call(0,nullptr);if(r.size!=GDI_ERROR&&r.size<1024*1024){r.pixels.resize(r.size);if(r.size&&call(r.size,r.pixels.data())==GDI_ERROR)r.size=GDI_ERROR;}return r;
}
static bool Equal(const Raster& a,const Raster& b){return a.size==b.size&&memcmp(&a.metrics,&b.metrics,sizeof(a.metrics))==0&&a.pixels==b.pixels;}
int wmain(int argc,wchar_t** argv){
    if(argc!=3)return 2;
    SetPriorityClass(GetCurrentProcess(),BELOW_NORMAL_PRIORITY_CLASS);
    json real=json::array(),controlled=json::array();int failed=0;
    auto check=[&](const std::string& name,bool ok,bool simulated=false){(simulated?controlled:real).push_back({{"name",name},{"passed",ok}});if(!ok){failed++;std::cout<<"FAIL "<<name<<"\n";}};
    int registered=AddFontResourceExW(argv[1],FR_PRIVATE,nullptr);check("Register bundled fallback in this process only",registered>0);
    HDC dc=CreateCompatibleDC(nullptr);HFONT sparse=CreateFontW(28,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,SYMBOL_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,ANTIALIASED_QUALITY,DEFAULT_PITCH,L"Marlett");
    HGDIOBJ previous=SelectObject(dc,sparse);expectedOriginal=sparse;
    check("Neutral GDI memory DC and sparse source font available",dc&&sparse&&previous&&previous!=HGDI_ERROR);
    LOGFONTW description={};check("Fallback measures original em height",WolfFont::Description(dc,description)&&description.lfHeight<0);
    HFONT bundled=CreateFontIndirectW(&description);
    {
        WolfFont::Renderer renderer;
        struct Sample{const char* name;wchar_t character;};
        for(auto sample:std::vector<Sample>{{"Cyrillic",L'\x0416'},{"Greek",L'\x03a9'},{"French",L'\x00e9'},{"German",L'\x00df'},{"Spanish",L'\x00f1'},{"Chinese",L'\x4e2d'},{"Japanese",L'\x8a9e'},{"Korean",L'\xd55c'}}){
            std::string label=sample.name;
            std::cout<<label<<" original-index="<<Index(dc,sample.character)<<"\n";
            check(label+" fixture is genuinely missing from original font",Index(dc,sample.character)==0xffff||Index(dc,sample.character)==0);
            auto original=Draw(dc,sample.character);
            auto disabled=Draw(dc,sample.character,&renderer,false);
            check(label+" translation disabled keeps original glyph and metrics",Equal(original,disabled));
            auto translated=Draw(dc,sample.character,&renderer);
            check(label+" drawing restores original HDC font",GetCurrentObject(dc,OBJ_FONT)==sparse);
            SelectObject(dc,bundled);bool present=Index(dc,sample.character)!=0xffff&&Index(dc,sample.character)!=0;
            auto expected=Draw(dc,sample.character);SelectObject(dc,sparse);
            check(label+" fallback has real nonempty glyph pixels",present&&translated.size!=GDI_ERROR&&!translated.pixels.empty()&&std::any_of(translated.pixels.begin(),translated.pixels.end(),[](BYTE b){return b>0;}));
            check(label+" fallback matches exact bundled font pixels and metrics",Equal(expected,translated));
            check(label+" disabling again restores unchanged original pixels",Equal(original,Draw(dc,sample.character,&renderer,false)));
        }
        SelectObject(dc,bundled);
        for(wchar_t ch:std::wstring(L"A\x4e2d\x0416\x03a9\x00e9")){
            unsigned before=renderer.Count();auto original=Draw(dc,ch);auto translated=Draw(dc,ch,&renderer);
            check("Existing glyph U+"+std::to_string((int)ch)+" remains byte exact",Equal(original,translated)&&renderer.Count()==before&&GetCurrentObject(dc,OBJ_FONT)==bundled);
        }
        SelectObject(dc,sparse);
        for(UINT ch:{0x0141u,0x0378u,0xd800u,0xdc00u,0xffffu,0x10000u}){
            unsigned before=renderer.Count();auto original=Draw(dc,ch);auto translated=Draw(dc,ch,&renderer);
            check("Unavailable or unsupported U+"+std::to_string(ch)+" keeps original result",Equal(original,translated)&&renderer.Count()==before&&GetCurrentObject(dc,OBJ_FONT)==sparse);
        }
        unsigned before=renderer.Count();auto glyph=Index(dc,L'A');
        check("Native glyph-index requests are not interpreted as Unicode",Equal(Draw(dc,glyph,nullptr,true,GGO_GRAY8_BITMAP|GGO_GLYPH_INDEX),Draw(dc,glyph,&renderer,true,GGO_GRAY8_BITMAP|GGO_GLYPH_INDEX))&&renderer.Count()==before);
        GLYPHMETRICS metrics={};auto original=Draw(dc,L'\x0416');
        DWORD returned=renderer.Draw(FailFallback,true,dc,L'\x0416',GGO_GRAY8_BITMAP,&metrics,0,nullptr,&identity);
        check("Injected fallback GDI error retries original font",injectedCalls==1&&restoredCalls==1&&returned==original.size,true);
        check("Injected error restores exact original HDC selection",GetCurrentObject(dc,OBJ_FONT)==sparse,true);
        check("Injected error never counts as applied",renderer.Count()==before,true);
        check("Normal drawing recovers after injected font error",Equal(Draw(dc,L'\x0416',&renderer),[&](){SelectObject(dc,bundled);auto r=Draw(dc,L'\x0416');SelectObject(dc,sparse);return r;}()),true);
    }
    check("Renderer destruction leaves original font selected",GetCurrentObject(dc,OBJ_FONT)==sparse);
    SelectObject(dc,previous);DeleteObject(sparse);DeleteObject(bundled);DeleteDC(dc);
    if(registered)RemoveFontResourceExW(argv[1],FR_PRIVATE,nullptr);
    json result={{"scope","Actual Windows GDI memory-DC rendering using production WolfFont.h; no game or WOLF runtime launched. Failure injection separately classified."},{"realGdiChecks",real},{"controlledFailureChecks",controlled},{"failures",failed}};
    std::ofstream(std::filesystem::path(argv[2]),std::ios::binary)<<result.dump(2);
    std::cout<<"real-gdi-checks="<<real.size()<<" controlled-checks="<<controlled.size()<<" failures="<<failed<<"\n";return failed?1:0;
}

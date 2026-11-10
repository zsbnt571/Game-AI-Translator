#include <windows.h>
#include <fstream>
#include <iostream>
#include <filesystem>
#include <vector>
#include "json.hpp"
#include "../WolfFont.h"
using nlohmann::json;
static MAT2 identity={{0,1},{0,0},{0,0},{0,1}};
struct Raster{DWORD size=GDI_ERROR;GLYPHMETRICS metrics={};std::vector<BYTE> pixels;};
static Raster Draw(HDC dc,UINT ch,WolfFont::Renderer* renderer=nullptr,bool translated=false,UINT format=GGO_GRAY8_BITMAP){
    Raster result;auto call=[&](DWORD bytes,void* buffer){return renderer?renderer->Draw(GetGlyphOutlineW,translated,dc,ch,format,&result.metrics,bytes,buffer,&identity):GetGlyphOutlineW(dc,ch,format,&result.metrics,bytes,buffer,&identity);};
    result.size=call(0,nullptr);if(result.size!=GDI_ERROR&&result.size<1024*1024){result.pixels.resize(result.size);if(result.size)result.size=call(result.size,result.pixels.data());}return result;
}
static bool Equal(const Raster& a,const Raster& b){return a.size==b.size&&memcmp(&a.metrics,&b.metrics,sizeof(a.metrics))==0&&a.pixels==b.pixels;}
static HFONT originalFont=nullptr;static int injected=0,retried=0;
static DWORD WINAPI FailCustom(HDC dc,UINT ch,UINT format,LPGLYPHMETRICS metrics,DWORD count,LPVOID buffer,const MAT2* matrix){
    if(GetCurrentObject(dc,OBJ_FONT)!=originalFont){injected++;return GDI_ERROR;}retried++;return GetGlyphOutlineW(dc,ch,format,metrics,count,buffer,matrix);
}
int wmain(int argc,wchar_t** argv){
    if(argc!=5)return 2;std::wstring path=argv[1],family=argv[2],hash=argv[3];json checks=json::array();int failures=0,controlled=0;
    auto check=[&](std::string name,bool success,bool injection=false){checks.push_back({{"name",name},{"passed",success},{"controlled",injection}});if(!success){failures++;std::cerr<<"FAIL "<<name<<"\n";}if(injection)controlled++;};
    HDC dc=CreateCompatibleDC(nullptr);originalFont=CreateFontW(28,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,ANTIALIASED_QUALITY,DEFAULT_PITCH,L"Arial");auto old=SelectObject(dc,originalFont);
    check("Neutral memory DC available",dc&&originalFont&&old&&old!=HGDI_ERROR);
    auto original=Draw(dc,L'W');
    {
        WolfFont::Renderer renderer;check("Default does not load a font",renderer.Configure(L"",L"",L"")&&!renderer.CustomActive()&&!renderer.CustomFailed());
        check("Default original pixels unchanged",Equal(original,Draw(dc,L'W',&renderer)));
        check("Default cannot be changed after startup configuration",!renderer.Configure(path,family,hash)&&!renderer.CustomActive());
    }
    {
        WolfFont::Renderer renderer;check("Private selected file loads with exact SHA256 and face bytes",renderer.Configure(path,family,hash)&&renderer.CustomActive()&&!renderer.CustomFailed());
        LOGFONTW description={};WolfFont::Description(dc,description);wcscpy_s(description.lfFaceName,family.c_str());description.lfCharSet=DEFAULT_CHARSET;HFONT expectedFont=CreateFontIndirectW(&description);
        for(auto ch:std::wstring(L"AWm\x0416\x00e9\x00df\x00f1")){
            std::string label="U+"+std::to_string((unsigned)ch);SelectObject(dc,expectedFont);auto expected=Draw(dc,ch);SelectObject(dc,originalFont);
            auto fontOnly=Draw(dc,ch,&renderer,false);check(label+" custom pixels before translation",expected.size!=GDI_ERROR&&!expected.pixels.empty()&&Equal(expected,fontOnly));
            check(label+" translation toggle preserves selected font",Equal(fontOnly,Draw(dc,ch,&renderer,true)));
            check(label+" original DC font always restored",GetCurrentObject(dc,OBJ_FONT)==originalFont);
        }
        check("Custom glyph counter records actual successful drawings",renderer.CustomCount()>=14);
        GLYPHMETRICS metrics={};unsigned before=renderer.CustomCount();auto returned=renderer.Draw(FailCustom,false,dc,L'W',GGO_GRAY8_BITMAP,&metrics,0,nullptr,&identity);
        check("Injected custom drawing failure retries original face",injected==1&&retried==1&&returned==original.size,true);
        check("Failed drawing restores exact original HDC",GetCurrentObject(dc,OBJ_FONT)==originalFont,true);
        check("Failed drawing is not counted as custom success",renderer.CustomCount()==before&&renderer.CustomFailures()>0,true);
        for(UINT ch:{0x0378u,0xd800u,0xdc00u,0xffffu,0x10000u})check("Unavailable or unsupported scalar "+std::to_string(ch)+" preserves original",Equal(Draw(dc,ch),Draw(dc,ch,&renderer)));
        wchar_t ch=L'W';WORD glyph=0;GetGlyphIndicesW(dc,&ch,1,&glyph,GGI_MARK_NONEXISTING_GLYPHS);
        check("Glyph indices preserve original font interpretation",Equal(Draw(dc,glyph,nullptr,false,GGO_GRAY8_BITMAP|GGO_GLYPH_INDEX),Draw(dc,glyph,&renderer,false,GGO_GRAY8_BITMAP|GGO_GLYPH_INDEX)));
        check("Live reconfiguration is rejected to preserve WOLF cache",!renderer.Configure(L"",L"",L"")&&renderer.CustomActive());
        DeleteObject(expectedFont);
    }
    check("After custom lifetime original face remains usable",Equal(original,Draw(dc,L'W')));
    {
        WolfFont::Renderer restored;check("Next startup default restores exact original glyph",restored.Configure(L"",L"",L"")&&Equal(original,Draw(dc,L'W',&restored)));
    }
    for(auto failure:std::vector<std::pair<std::wstring,std::wstring>>{{path,std::wstring(64,L'0')},{path+L".missing",hash}}){
        WolfFont::Renderer renderer;check("Rejected file or hash leaves original pixels",!renderer.Configure(failure.first,family,failure.second)&&renderer.CustomFailed()&&!renderer.CustomActive()&&Equal(original,Draw(dc,L'W',&renderer)));
    }
    {
        WolfFont::Renderer renderer;check("Wrong face cannot silently choose a system fallback",!renderer.Configure(path,L"FusionMissingFace132",hash)&&renderer.CustomFailed()&&Equal(original,Draw(dc,L'W',&renderer)));
    }
    {
        WolfFont::Renderer renderer;check("Host validation failure preserves default",!renderer.Configure(L"",L"",L"",true)&&renderer.CustomFailed()&&Equal(original,Draw(dc,L'W',&renderer)));
    }
    SelectObject(dc,old);DeleteObject(originalFont);DeleteDC(dc);
    json result={{"realGdiChecks",checks.size()-controlled},{"controlledFailureChecks",controlled},{"failures",failures},{"originalGames",0},{"apiCalls",0},{"engine","Windows GDI memory DC, not WOLF runtime"},{"checks",checks}};
    std::ofstream(std::filesystem::path(argv[4]))<<result.dump(2);std::cout<<"real-gdi="<<checks.size()-controlled<<" controlled="<<controlled<<" failures="<<failures<<"\n";return failures?1:0;
}

// Actual official DxLib raster/cache experiment. No WOLF/game process is used.
#include <windows.h>
#include <fstream>
#include <iostream>
#include <filesystem>
#include <vector>
#include <algorithm>
#include "json.hpp"
#include "MinHook.h"
#include "../WolfFont.h"
using nlohmann::json;
static WolfFont::Renderer renderer;
static decltype(&GetGlyphOutlineW) glyph;
static DWORD WINAPI Draw(HDC dc,UINT ch,UINT format,LPGLYPHMETRICS metrics,DWORD count,LPVOID buffer,const MAT2* matrix){
    return renderer.Draw(glyph,false,dc,ch,format,metrics,count,buffer,matrix);
}
template<class T>T Api(HMODULE module,const char* name){auto f=GetProcAddress(module,name);if(!f)throw std::runtime_error(name);return reinterpret_cast<T>(f);}
int wmain(int argc,wchar_t** argv){
    if(argc!=5)return 2;json checks=json::array();int failures=0;
    auto check=[&](const char* name,bool result){checks.push_back({{"name",name},{"passed",result}});if(!result){++failures;std::cerr<<"FAIL "<<name<<"\n";}};
    try{
        auto dx=LoadLibraryW(argv[1]);if(!dx)throw std::runtime_error("Official DxLib DLL failed to load");
        using Unary=int(WINAPI*)(int);using Empty=int(WINAPI*)();
        for(auto name:{"dx_SetNotSoundFlag","dx_SetNotInputFlag","dx_SetUseGDIFlag"})Api<Unary>(dx,name)(1);
        Api<Unary>(dx,"dx_SetWindowVisibleFlag")(0);Api<Unary>(dx,"dx_ChangeWindowMode")(1);
        check("Official DxLib initialization succeeds without sound, input or a visible window",Api<Empty>(dx,"dx_DxLib_Init")()==0);
        Api<Unary>(dx,"dx_SetFontCacheToTextureFlag")(1);
        using Create=int(WINAPI*)(const wchar_t*,int,int,int,int,int,int,int);
        auto create=Api<Create>(dx,"dx_CreateFontToHandle");auto clear=Api<Unary>(dx,"dx_ClearFontCacheToHandle");
        auto remove=Api<Unary>(dx,"dx_DeleteFontToHandle");
        int font=create(L"Arial",32,1,0,0,-1,0,-1);check("Native source font created",font>=0);
        using Make=int(WINAPI*)(int,int);using Blit=int(WINAPI*)(int,int,const wchar_t*,int,int,int,int);
        using Pixel=int(WINAPI*)(int,int,int,int*,int*,int*,int*);
        auto make=Api<Make>(dx,"dx_MakeARGB8ColorSoftImage");auto blit=Api<Blit>(dx,"dx_BltStringSoftImageToHandle");
        auto pixel=Api<Pixel>(dx,"dx_GetPixelSoftImage");auto removeImage=Api<Unary>(dx,"dx_DeleteSoftImage");
        auto raster=[&](){int image=make(160,64);if(image<0)throw std::runtime_error("Soft image unavailable");
            if(blit(3,3,L"AWm",image,-1,font,0)<0)throw std::runtime_error("Native font raster failed");
            std::vector<unsigned> result;result.reserve(160*64);for(int y=0;y<64;++y)for(int x=0;x<160;++x){int r=0,g=0,b=0,a=0;
                if(pixel(image,x,y,&r,&g,&b,&a)<0)throw std::runtime_error("Native pixels unavailable");result.push_back((unsigned)a<<24|(unsigned)r<<16|(unsigned)g<<8|b);}
            removeImage(image);return result;};
        renderer.Configure(L"",L"",L"");
        check("Production glyph hook installs",MH_Initialize()==MH_OK&&MH_CreateHook((void*)GetGlyphOutlineW,(void*)Draw,(void**)&glyph)==MH_OK&&MH_EnableHook((void*)GetGlyphOutlineW)==MH_OK);
        auto original=raster();check("Original native raster is nonempty",std::any_of(original.begin(),original.end(),[](auto p){return p!=0;}));
        check("Missing engine cache contract refuses live change",!renderer.ReplaceAfterCacheClear(argv[2],L"Consolas",argv[3],{}));
        check("Refused request preserves actual cached pixels",raster()==original);
        int clears=0;auto flush=[&](){++clears;return clear(font)==0;};
        check("Validated native clear commits selected face",renderer.ReplaceAfterCacheClear(argv[2],L"Consolas",argv[3],flush));
        auto custom=raster();check("Native cached pixels change after commit",custom!=original&&renderer.CustomCount()>0);
        check("Native custom cache stabilizes",custom==raster());
        check("Failed hash leaves the current face and cache",!renderer.ReplaceAfterCacheClear(argv[2],L"Consolas",std::wstring(64,L'0'),flush)&&custom==raster()&&clears==1);
        check("Rejected engine clear leaves current face and pixels",!renderer.ReplaceAfterCacheClear(L"",L"",L"",[]{return false;})&&renderer.CustomActive()&&custom==raster());
        check("Native clear commits restore",renderer.ReplaceAfterCacheClear(L"",L"",L"",flush));
        check("Restored cache pixels exactly equal original",raster()==original&&!renderer.CustomActive()&&clears==2);
        for(int i=0;i<4;++i){bool a=renderer.ReplaceAfterCacheClear(argv[2],L"Consolas",argv[3],flush)&&raster()==custom;
            bool b=renderer.ReplaceAfterCacheClear(L"",L"",L"",flush)&&raster()==original;check("Repeated actual cache switch and restore",a&&b);}
        remove(font);MH_DisableHook((void*)GetGlyphOutlineW);MH_Uninitialize();Api<Empty>(dx,"dx_DxLib_End")();
    }catch(const std::exception& e){check(e.what(),false);}
    json result={{"scope","Actual official DxLib 3.25a software font cache and pixels using production WolfFont.h. Not a WOLF runtime acceptance."},{"checks",checks},{"failures",failures},{"originalGames",0},{"apiCalls",0}};
    std::ofstream(std::filesystem::path(argv[4]))<<result.dump(2);std::cout<<checks.size()-failures<<"/"<<checks.size()<<" passed\n";return failures?1:0;
}

#pragma once
// Engineering-only capture of this marked test game's D3D9 backbuffer.
// Include only in a FUSION_TEST_CAPTURE build. Nothing reads the desktop or
// front buffer, shows a window, changes focus, or switches the input desktop.
#ifdef FUSION_TEST_CAPTURE
#include <windows.h>
#include <d3d9.h>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>
#include <memory>
#include <mutex>
#include <condition_variable>
#include <atomic>
#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include "MinHook.h"
#include "json.hpp"
#include "WolfSessionFiles.h"

namespace WolfTestScreen {
using Json=nlohmann::json;
namespace Path=std::filesystem;
using Present=HRESULT(WINAPI*)(IDirect3DDevice9*,const RECT*,const RECT*,HWND,const RGNDATA*);
static Present originalPresent=nullptr;
static std::wstring expectedDesktop,session;
static std::mutex captureGate,requestGate;
static std::condition_variable captured;
static std::atomic<unsigned long long> pending{0},presentCalls{0},ownedPresentCalls{0};
static std::atomic<bool> installed{false};
static unsigned long long serial=0,inFlight=0;
static std::string installError="not-installed";
static HRESULT installHr=E_PENDING;
struct Frame {unsigned long long ticket=0;Json info;std::vector<unsigned char> pixels;bool written=false;};
static std::shared_ptr<Frame> latest;
template<class T> struct Com {
    T* p=nullptr;~Com(){if(p)p->Release();}
    T** Out(){return &p;}T* operator->()const{return p;}operator bool()const{return p!=nullptr;}
};
static std::wstring Environment(const wchar_t* name){
    wchar_t value[32768];DWORD n=GetEnvironmentVariableW(name,value,32768);
    return n&&n<32768?std::wstring(value,n):std::wstring{};
}
static std::wstring ObjectName(HANDLE handle){
    DWORD bytes=0;GetUserObjectInformationW(handle,UOI_NAME,nullptr,0,&bytes);
    if(!bytes||bytes>32768)return {};std::wstring value(bytes/sizeof(wchar_t),L'\0');
    if(!GetUserObjectInformationW(handle,UOI_NAME,value.data(),bytes,&bytes))return {};
    value.resize(wcsnlen(value.c_str(),value.size()));return value;
}
static bool NoReparseChain(const Path::path& value){
    if(!value.is_absolute())return false;
    for(auto at=value;!at.empty();){
        std::wstring native;if(!WolfSessionFiles::NativePath(at,native))return false;
        DWORD flags=GetFileAttributesW(native.c_str());
        if(flags==INVALID_FILE_ATTRIBUTES||(flags&FILE_ATTRIBUTE_REPARSE_POINT))return false;
        auto parent=at.parent_path();if(parent==at)break;at=std::move(parent);
    }return true;
}
static bool PrivateDesktop(DWORD thread){
    if(expectedDesktop.rfind(L"FusionWolfTest_",0)!=0||expectedDesktop.size()>=80||
       expectedDesktop.find_first_not_of(L"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_")!=expectedDesktop.npos)return false;
    if(ObjectName(GetThreadDesktop(thread))!=expectedDesktop)return false;
    HDESK input=OpenInputDesktop(0,FALSE,DESKTOP_READOBJECTS);if(!input)return false;
    auto inputName=ObjectName(input);CloseDesktop(input);return !inputName.empty()&&inputName!=expectedDesktop;
}
static bool MarkedCopy(){
    wchar_t exe[32768];DWORD n=GetModuleFileNameW(nullptr,exe,32768);if(!n||n>=32768)return false;
    Path::path marker=Path::path(exe).parent_path()/L".fusion-test-copy";
    std::wstring native;if(!NoReparseChain(marker)||!WolfSessionFiles::NativePath(marker,native)||!Path::is_regular_file(Path::path(native)))return false;
    std::ifstream file(Path::path(native),std::ios::binary);std::string content(128,'\0');file.read(content.data(),content.size());content.resize((size_t)file.gcount());
    if(!file.eof())return false;while(!content.empty()&&(content.back()=='\r'||content.back()=='\n'||content.back()==' '))content.pop_back();
    return content=="FusionTranslationTestCopy/1";
}
static bool Allowed(){
    auto token=Environment(L"FUSION_WOLF_SECRET");
    return MarkedCopy()&&token.size()==64&&token.find_first_not_of(L"0123456789ABCDEF")==token.npos&&
        Environment(L"FUSION_WOLF_TEST_DESKTOP")==expectedDesktop&&PrivateDesktop(GetCurrentThreadId());
}
static bool OwnedWindow(HWND window){
    DWORD pid=0;DWORD thread=GetWindowThreadProcessId(window,&pid);
    return window&&thread&&pid==GetCurrentProcessId()&&PrivateDesktop(thread);
}
static bool OwnedDevice(IDirect3DDevice9* device,HWND overrideWindow,D3DDEVICE_CREATION_PARAMETERS& creation,HWND& window){
    if(!device||FAILED(device->GetCreationParameters(&creation))||!OwnedWindow(creation.hFocusWindow))return false;
    Com<IDirect3DSwapChain9> chain;D3DPRESENT_PARAMETERS parameters={};
    if(FAILED(device->GetSwapChain(0,chain.Out()))||FAILED(chain->GetPresentParameters(&parameters)))return false;
    window=overrideWindow?overrideWindow:(parameters.hDeviceWindow?parameters.hDeviceWindow:creation.hFocusWindow);
    return OwnedWindow(window);
}
static unsigned BytesPerPixel(D3DFORMAT format){
    switch(format){
    case D3DFMT_A8R8G8B8:case D3DFMT_X8R8G8B8:case D3DFMT_A8B8G8R8:case D3DFMT_X8B8G8R8:
    case D3DFMT_A2R10G10B10:case D3DFMT_A2B10G10R10:return 4;
    case D3DFMT_R8G8B8:return 3;
    case D3DFMT_R5G6B5:case D3DFMT_X1R5G5B5:case D3DFMT_A1R5G5B5:return 2;
    default:return 0;
    }
}
static void Fail(Frame& result,const char* stage,HRESULT hr){
    result.info["ok"]=false;result.info["stage"]=stage;result.info["hresult"]=(long)hr;
}
static void ReadBack(IDirect3DDevice9* device,HWND window,const D3DDEVICE_CREATION_PARAMETERS& creation,Frame& result){
    result.info={{"ok",false},{"method","owned-d3d9-device-backbuffer"},{"pid",GetCurrentProcessId()},
        {"window",(unsigned long long)(uintptr_t)window},{"device",(unsigned long long)(uintptr_t)device},
        {"adapterOrdinal",creation.AdapterOrdinal},{"deviceType",(unsigned)creation.DeviceType},
        {"privateDesktop",true},{"inputDesktopSwitched",false},{"screenCapture",false},{"ticket",result.ticket}};
    Com<IDirect3DSurface9> back,resolve,system;D3DSURFACE_DESC desc={};HRESULT hr=device->GetBackBuffer(0,0,D3DBACKBUFFER_TYPE_MONO,back.Out());
    if(FAILED(hr)){Fail(result,"GetBackBuffer",hr);return;}
    if(FAILED(hr=back->GetDesc(&desc))){Fail(result,"GetDesc",hr);return;}
    result.info["width"]=desc.Width;result.info["height"]=desc.Height;result.info["format"]=(unsigned)desc.Format;
    result.info["multisampleType"]=(unsigned)desc.MultiSampleType;result.info["multisampleQuality"]=desc.MultiSampleQuality;
    unsigned bpp=BytesPerPixel(desc.Format);
    if(!bpp||!desc.Width||!desc.Height||desc.Width>8192||desc.Height>8192||(unsigned long long)desc.Width*desc.Height>16*1024*1024){Fail(result,"unsupported-format-or-size",D3DERR_INVALIDCALL);return;}
    IDirect3DSurface9* source=back.p;
    if(desc.MultiSampleType!=D3DMULTISAMPLE_NONE){
        hr=device->CreateRenderTarget(desc.Width,desc.Height,desc.Format,D3DMULTISAMPLE_NONE,0,FALSE,resolve.Out(),nullptr);
        if(FAILED(hr)){Fail(result,"CreateResolveTarget",hr);return;}
        if(FAILED(hr=device->StretchRect(back.p,nullptr,resolve.p,nullptr,D3DTEXF_NONE))){Fail(result,"ResolveMultisample",hr);return;}
        source=resolve.p;
    }
    hr=device->CreateOffscreenPlainSurface(desc.Width,desc.Height,desc.Format,D3DPOOL_SYSTEMMEM,system.Out(),nullptr);
    if(FAILED(hr)){Fail(result,"CreateSystemSurface",hr);return;}
    if(FAILED(hr=device->GetRenderTargetData(source,system.p))){Fail(result,"GetRenderTargetData",hr);return;}
    D3DLOCKED_RECT locked={};if(FAILED(hr=system->LockRect(&locked,nullptr,D3DLOCK_READONLY))){Fail(result,"LockRect",hr);return;}
    if(!locked.pBits||locked.Pitch<(int)(desc.Width*bpp)||locked.Pitch>32*1024*1024){system->UnlockRect();Fail(result,"invalid-pitch",D3DERR_INVALIDCALL);return;}
    // Allocate before locking in future extensions; this guard also ensures an
    // allocation failure cannot leave the borrowed surface locked.
    try{result.pixels.resize((size_t)desc.Width*desc.Height*4);}catch(...){system->UnlockRect();throw;}
    unsigned long long nonBlack=0;unsigned minimum=255,maximum=0;
    for(unsigned y=0;y<desc.Height;y++){
        auto line=(const unsigned char*)locked.pBits+(size_t)y*locked.Pitch;auto out=result.pixels.data()+(size_t)y*desc.Width*4;
        for(unsigned x=0;x<desc.Width;x++){
            unsigned r=0,g=0,b=0;auto in=line+(size_t)x*bpp;
            switch(desc.Format){
            case D3DFMT_A8R8G8B8:case D3DFMT_X8R8G8B8:case D3DFMT_R8G8B8:b=in[0];g=in[1];r=in[2];break;
            case D3DFMT_A8B8G8R8:case D3DFMT_X8B8G8R8:r=in[0];g=in[1];b=in[2];break;
            case D3DFMT_R5G6B5:{unsigned v=in[0]|(in[1]<<8);r=((v>>11)&31)*255/31;g=((v>>5)&63)*255/63;b=(v&31)*255/31;break;}
            case D3DFMT_X1R5G5B5:case D3DFMT_A1R5G5B5:{unsigned v=in[0]|(in[1]<<8);r=((v>>10)&31)*255/31;g=((v>>5)&31)*255/31;b=(v&31)*255/31;break;}
            case D3DFMT_A2R10G10B10:case D3DFMT_A2B10G10R10:{unsigned v=0;memcpy(&v,in,4);unsigned a=(v&1023)*255/1023,c=((v>>20)&1023)*255/1023;g=((v>>10)&1023)*255/1023;
                if(desc.Format==D3DFMT_A2R10G10B10){b=a;r=c;}else{r=a;b=c;}break;}
            default:break;
            }
            out[x*4]=(unsigned char)b;out[x*4+1]=(unsigned char)g;out[x*4+2]=(unsigned char)r;out[x*4+3]=255;
            if(r|g|b)nonBlack++;minimum=(std::min)(minimum,(std::min)(r,(std::min)(g,b)));maximum=(std::max)(maximum,(std::max)(r,(std::max)(g,b)));
        }
    }
    system->UnlockRect();result.info["ok"]=true;result.info["stage"]="readback-complete";
    result.info["nonBlackPixels"]=nonBlack;result.info["minimumChannel"]=minimum;result.info["maximumChannel"]=maximum;
}
static HRESULT WINAPI PresentHook(IDirect3DDevice9* device,const RECT* source,const RECT* dest,HWND window,const RGNDATA* dirty){
    presentCalls.fetch_add(1,std::memory_order_relaxed);
    auto ticket=pending.load(std::memory_order_acquire);
    if(ticket){
        try{
            D3DDEVICE_CREATION_PARAMETERS creation={};HWND owned=nullptr;
            if(Allowed()&&OwnedDevice(device,window,creation,owned)){
                ownedPresentCalls.fetch_add(1,std::memory_order_relaxed);
                bool claim=false;{std::lock_guard<std::mutex> lock(captureGate);
                    if(!inFlight&&pending.load()==ticket){inFlight=ticket;pending.store(0,std::memory_order_release);claim=true;}
                }
                if(claim){
                    auto result=std::make_shared<Frame>();result->ticket=ticket;
                    try{ReadBack(device,owned,creation,*result);}catch(...){Fail(*result,"readback-exception",E_FAIL);}
                    {std::lock_guard<std::mutex> lock(captureGate);latest=std::move(result);inFlight=0;}captured.notify_all();
                }
            }
        }catch(...){
            // A failed test capture must neither wedge future requests nor
            // prevent the game's original Present from running.
            {std::lock_guard<std::mutex> lock(captureGate);if(inFlight==ticket)inFlight=0;}captured.notify_all();
        }
    }
    return originalPresent(device,source,dest,window,dirty);
}
static bool Install(const std::wstring& sessionDirectory){
    std::lock_guard<std::mutex> lock(requestGate);if(installed.load())return true;
    expectedDesktop=Environment(L"FUSION_WOLF_TEST_DESKTOP");session=sessionDirectory;
    std::wstring nativeSession;
    if(!Allowed()||session.empty()||Environment(L"FUSION_WOLF_SESSION")!=session||!NoReparseChain(Path::path(session))||!WolfSessionFiles::NativePath(Path::path(session),nativeSession)||!Path::is_directory(Path::path(nativeSession))){installError="isolated-copy-or-private-desktop-check-failed";return false;}
    // Dynamic loading avoids adding graphics dependencies to the normal bridge.
    HMODULE library=LoadLibraryExW(L"d3d9.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
    if(!library){installError="system-d3d9-unavailable";installHr=HRESULT_FROM_WIN32(GetLastError());return false;}
    using Create=IDirect3D9*(WINAPI*)(UINT);auto create=(Create)GetProcAddress(library,"Direct3DCreate9");
    Com<IDirect3D9> api;if(create)api.p=create(D3D_SDK_VERSION);
    if(!api){installError="Direct3DCreate9-failed";installHr=E_FAIL;return false;}
    std::wstring className=L"FusionWolfTestCapture_"+std::to_wstring(GetCurrentProcessId());HMODULE executable=GetModuleHandleW(nullptr);
    WNDCLASSW type={};type.hInstance=executable;type.lpfnWndProc=DefWindowProcW;type.lpszClassName=className.c_str();
    ATOM atom=RegisterClassW(&type);if(!atom){installError="dummy-window-class-failed";installHr=HRESULT_FROM_WIN32(GetLastError());return false;}
    HWND dummy=CreateWindowExW(0,className.c_str(),L"",WS_POPUP,0,0,16,16,nullptr,nullptr,executable,nullptr);
    if(!dummy){installError="hidden-dummy-window-failed";installHr=HRESULT_FROM_WIN32(GetLastError());UnregisterClassW(className.c_str(),executable);return false;}
    D3DPRESENT_PARAMETERS params={};params.Windowed=TRUE;params.SwapEffect=D3DSWAPEFFECT_DISCARD;params.hDeviceWindow=dummy;
    params.BackBufferWidth=params.BackBufferHeight=16;params.BackBufferFormat=D3DFMT_UNKNOWN;params.PresentationInterval=D3DPRESENT_INTERVAL_IMMEDIATE;
    IDirect3DDevice9* probe=nullptr;installHr=api->CreateDevice(D3DADAPTER_DEFAULT,D3DDEVTYPE_HAL,dummy,
        D3DCREATE_SOFTWARE_VERTEXPROCESSING|D3DCREATE_MULTITHREADED|D3DCREATE_FPU_PRESERVE,&params,&probe);
    void* target=SUCCEEDED(installHr)&&probe?(*(void***)probe)[17]:nullptr;
    if(probe)probe->Release();DestroyWindow(dummy);UnregisterClassW(className.c_str(),executable);
    if(!target){installError="private-desktop-dummy-device-failed";return false;}
    // IDirect3DDevice9's public COM layout defines Present as slot 17. No byte
    // pattern, game-code address, foreign process or global OS hook is used.
    auto mh=MH_Initialize();if(mh!=MH_OK&&mh!=MH_ERROR_ALREADY_INITIALIZED){installError="minhook-initialize-failed";installHr=E_FAIL;return false;}
    mh=MH_CreateHook(target,(void*)PresentHook,(void**)&originalPresent);
    if(mh!=MH_OK){installError="present-hook-create-failed";installHr=E_FAIL;return false;}
    mh=MH_EnableHook(target);if(mh!=MH_OK){MH_RemoveHook(target);originalPresent=nullptr;installError="present-hook-enable-failed";installHr=E_FAIL;return false;}
    installHr=S_OK;installError.clear();installed.store(true);return true;
}
static bool WriteBitmap(Frame& result){
    if(result.written)return true;if(result.pixels.empty()||!result.info.value("ok",false))return false;
    if(!Allowed()||Environment(L"FUSION_WOLF_SESSION")!=session||!NoReparseChain(Path::path(session)))return false;
    auto name=L"wolf-backbuffer-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(result.ticket)+L".bmp";
    Path::path final=Path::path(session)/name,temp=final;temp+=L".tmp";
    std::wstring nativeFinal,nativeTemp;if(!WolfSessionFiles::NativePath(final,nativeFinal)||!WolfSessionFiles::NativePath(temp,nativeTemp))return false;
    BITMAPFILEHEADER file={};BITMAPINFOHEADER bitmap={};file.bfType=0x4d42;file.bfOffBits=sizeof(file)+sizeof(bitmap);file.bfSize=file.bfOffBits+(DWORD)result.pixels.size();
    bitmap.biSize=sizeof(bitmap);bitmap.biWidth=result.info["width"].get<LONG>();bitmap.biHeight=-result.info["height"].get<LONG>();
    bitmap.biPlanes=1;bitmap.biBitCount=32;bitmap.biCompression=BI_RGB;bitmap.biSizeImage=(DWORD)result.pixels.size();
    HANDLE output=CreateFileW(nativeTemp.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_WRITE_THROUGH,nullptr);
    if(output==INVALID_HANDLE_VALUE)return false;
    auto write=[&](const void* data,DWORD size){DWORD count=0;return WriteFile(output,data,size,&count,nullptr)&&count==size;};
    bool ok=write(&file,sizeof(file))&&write(&bitmap,sizeof(bitmap))&&write(result.pixels.data(),(DWORD)result.pixels.size());CloseHandle(output);
    if(!ok){DeleteFileW(nativeTemp.c_str());return false;}
    if(!MoveFileExW(nativeTemp.c_str(),nativeFinal.c_str(),MOVEFILE_WRITE_THROUGH)){DeleteFileW(nativeTemp.c_str());return false;}
    result.written=true;result.info["path"]=final.u8string();result.info["fileBytes"]=file.bfSize;result.info["stage"]="bitmap-written";return true;
}
static Json Request(const Json& query=Json::object()){
    std::lock_guard<std::mutex> request(requestGate);
    if(!installed.load())return {{"ok",false},{"installed",false},{"stage",installError},{"hresult",(long)installHr}};
    if(!Allowed())return {{"ok",false},{"installed",true},{"stage","private-desktop-check-failed"}};
    int timeout=(std::max)(0,(std::min)(5000,query.value("timeoutMs",3000)));bool arm=query.value("arm",true);
    std::unique_lock<std::mutex> lock(captureGate);unsigned long long ticket=pending.load();if(!ticket)ticket=inFlight;
    if(arm&&!ticket){
        if(serial>=32)return {{"ok",false},{"stage","capture-budget-exhausted"}};
        ticket=++serial;pending.store(ticket,std::memory_order_release);
    }
    if(ticket&&timeout)captured.wait_for(lock,std::chrono::milliseconds(timeout),[&]{return latest&&latest->ticket==ticket;});
    auto result=latest;lock.unlock();
    if(!result||(ticket&&result->ticket!=ticket))return {{"ok",false},{"installed",true},{"stage","waiting-for-owned-present"},{"ticket",ticket},{"presentCalls",presentCalls.load()},{"ownedPresentCalls",ownedPresentCalls.load()}};
    if(result->info.value("ok",false)&&!WriteBitmap(*result))return {{"ok",false},{"installed",true},{"stage","session-bitmap-write-failed"},{"ticket",result->ticket}};
    auto info=result->info;info["installed"]=true;info["presentCalls"]=presentCalls.load();info["ownedPresentCalls"]=ownedPresentCalls.load();return info;
}
static Json GetScreenshot(unsigned timeoutMs=3000){return Request({{"timeoutMs",timeoutMs},{"arm",true}});}
}
#endif

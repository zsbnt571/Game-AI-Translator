#include <windows.h>
#include <mmsystem.h>
#include <atomic>
#include <algorithm>
#include <cmath>
#include "MinHook.h"
#include "json.hpp"
#include "../WolfSpeed.h"
#include "../WolfSpeedProtocol.h"

extern "C" __declspec(dllexport) BOOL __cdecl FixtureInitialize(){
    return MH_Initialize()==MH_OK&&WolfSpeed::Initialize();
}
extern "C" __declspec(dllexport) int __cdecl FixtureRequest(const char* source,char* result,unsigned capacity){
    try{bool ok=true;auto reply=WolfSpeed::Request(nlohmann::json::parse(source),ok);
        auto text=nlohmann::json({{"ok",ok},{"result",reply}}).dump();
        if(text.size()+1>capacity)return 0;memcpy(result,text.c_str(),text.size()+1);return 1;
    }catch(...){return 0;}
}
// These calls originate outside the main image and must remain unscaled.
extern "C" __declspec(dllexport) double __cdecl FixtureWallMs(){
    LARGE_INTEGER now{},hz{};QueryPerformanceCounter(&now);QueryPerformanceFrequency(&hz);return now.QuadPart*1000.0/hz.QuadPart;
}
extern "C" __declspec(dllexport) void __cdecl FixtureWallSleep(DWORD ms){Sleep(ms);}
extern "C" __declspec(dllexport) void __cdecl FixtureExternalClocks(double* out){
    out[0]=FixtureWallMs();out[1]=GetTickCount();out[2]=(double)GetTickCount64();out[3]=timeGetTime();
}

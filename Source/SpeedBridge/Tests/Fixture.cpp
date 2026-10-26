#include "FixtureProtocol.h"
#include <mmsystem.h>
#include <string>
using namespace FusionSpeed;
int wmain(int argc,wchar_t** argv){
 if(argc!=3)return 2;
 HANDLE map=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,argv[1]);if(!map)return 3;
 auto data=(Fixture::Control*)MapViewOfFile(map,FILE_MAP_ALL_ACCESS,0,0,sizeof(Fixture::Control));if(!data)return 4;
 HANDLE wake=OpenEventW(SYNCHRONIZE|EVENT_MODIFY_STATE,FALSE,argv[2]);if(!wake)return 5;
 wchar_t path[32768];GetModuleFileNameW(nullptr,path,32768);std::wstring root(path);root=root.substr(0,root.find_last_of(L'\\')+1);std::wstring arch=sizeof(void*)==8?L"x64":L"x86";
 auto allowedModule=LoadLibraryW((root+L"SDLFixture."+arch+L".dll").c_str());auto excludedModule=LoadLibraryW((root+L"UnrelatedFixture."+arch+L".dll").c_str());
  using Probe=LONGLONG(__cdecl*)();auto probe=[&](HMODULE module,const char* name="ClockRead"){auto fn=GetProcAddress(module,name);if(!fn)fn=GetProcAddress(module,(std::string("_")+name).c_str());return (Probe)fn;};
 Probe allowed=probe(allowedModule),excluded=probe(excludedModule);if(!allowed||!excluded)return 6;
  Probe allowedFile=probe(allowedModule,"FileTimeRead"),excludedFile=probe(excludedModule,"FileTimeRead"),allowedPrecise=probe(allowedModule,"PreciseFileTimeRead"),excludedPrecise=probe(excludedModule,"PreciseFileTimeRead");if(!allowedFile||!excludedFile||!allowedPrecise||!excludedPrecise)return 6;
 InterlockedExchange(&data->ready,1);LONG last=0;
 for(;;){if(WaitForSingleObject(wake,INFINITE)!=WAIT_OBJECT_0)return 7;LONG request=InterlockedCompareExchange(&data->request,0,0);if(request==last)continue;last=request;LONG cmd=data->command;
  if(cmd==1){LARGE_INTEGER before{},after{};QueryPerformanceCounter(&before);DWORD tick=GetTickCount(),mm=timeGetTime();ULONGLONG t64=GetTickCount64();auto a=allowed(),e=excluded();
   WaitForSingleObject(wake,data->duration);QueryPerformanceCounter(&after);
   Write64(&data->qpc,after.QuadPart-before.QuadPart);Write64(&data->tick,DWORD(GetTickCount()-tick));Write64(&data->tick64,GetTickCount64()-t64);Write64(&data->multimedia,DWORD(timeGetTime()-mm));Write64(&data->allowed,allowed()-a);Write64(&data->excluded,excluded()-e);
  }else if(cmd==6||cmd==7){
   // No QPC/tick reads here: emulate older Defold's FILETIME-only update clock.
   auto read=cmd==6?GetSystemTimeAsFileTime:GetSystemTimePreciseAsFileTime;auto moduleRead=cmd==6?allowedFile:allowedPrecise;auto otherRead=cmd==6?excludedFile:excludedPrecise;
   FILETIME before{},after{};read(&before);auto a=moduleRead(),e=otherRead();WaitForSingleObject(wake,data->duration);read(&after);
   auto ticks=[](const FILETIME& value){return (LONGLONG(value.dwHighDateTime)<<32)|value.dwLowDateTime;};
   Write64(&data->qpc,ticks(after)-ticks(before));Write64(&data->allowed,moduleRead()-a);Write64(&data->excluded,otherRead()-e);
  }else if(cmd==10){
   // Fixed-step logic waits on an unchanged event timer. Scaling QPC must not
   // pretend it makes these 20 updates execute more often in real time.
   LARGE_INTEGER before{},after{};QueryPerformanceCounter(&before);auto realBefore=excluded();LONG steps=0;
   for(int i=0;i<20;i++){WaitForSingleObject(wake,5);steps++;}
   QueryPerformanceCounter(&after);Write64(&data->qpc,after.QuadPart-before.QuadPart);Write64(&data->tick,steps);Write64(&data->excluded,excluded()-realBefore);
  }else if(cmd==8||cmd==9){auto module=GetModuleHandleW((L"FusionSpeed."+arch+L".dll").c_str());auto version=module?(DWORD*)GetProcAddress(module,"FusionSpeedProtocolVersion"):nullptr;data->error=version?0:1;if(version)*version=cmd==8?1:Version;
  }else if(cmd==2){Sleep(data->duration);}else if(cmd==5){SleepEx(data->duration,FALSE);}else if(cmd==4){auto module=LoadLibraryW((root+L"FusionWolf."+arch+L".dll").c_str());data->error=module?0:GetLastError();}
  InterlockedExchange(&data->ack,last);if(cmd==3)break;
 }
 InterlockedExchange(&data->done,1);CloseHandle(wake);UnmapViewOfFile(data);CloseHandle(map);return 0;
}

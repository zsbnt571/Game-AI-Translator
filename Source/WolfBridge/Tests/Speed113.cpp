#include <windows.h>
#include <mmsystem.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <cmath>
#include "json.hpp"
using nlohmann::json;namespace fs=std::filesystem;
using Initialize=BOOL(__cdecl*)();using Request=int(__cdecl*)(const char*,char*,unsigned);
using Wall=double(__cdecl*)();using Wait=void(__cdecl*)(DWORD);using Clocks=void(__cdecl*)(double*);
int wmain(int argc,wchar_t** argv){
    if(argc!=3)return 2;fs::path folder=argv[1];std::wstring mode=argv[2];fs::create_directories(folder);
    wchar_t exe[32768]{};GetModuleFileNameW(nullptr,exe,32768);
    auto payload=LoadLibraryW((fs::path(exe).parent_path()/L"Speed113Payload.dll").c_str());if(!payload)return 3;
    auto init=(Initialize)GetProcAddress(payload,"FixtureInitialize");auto request=(Request)GetProcAddress(payload,"FixtureRequest");
    auto wall=(Wall)GetProcAddress(payload,"FixtureWallMs");auto wait=(Wait)GetProcAddress(payload,"FixtureWallSleep");
    auto external=(Clocks)GetProcAddress(payload,"FixtureExternalClocks");if(!init||!request||!wall||!wait||!external)return 4;
    json checks=json::array(),measurements=json::array();int failed=0;
    auto check=[&](const char* name,bool passed,json detail=nullptr){checks.push_back({{"name",name},{"passed",passed},{"detail",detail}});if(!passed)failed++;};
    auto ask=[&](json q){char result[8192]{};if(!request(q.dump().c_str(),result,sizeof(result)))throw std::runtime_error("Protocol returned no JSON");return json::parse(result);};
    auto status=[&](){return ask({{"op","gameSpeed"}})["result"];};
    auto set=[&](double rate){return ask({{"op","gameSpeed"},{"rate",rate}});};
    auto sample=[&](const char* phase,double rate){
        double a[4]{},b[4]{},outsideA[4]{},outsideB[4]{};LARGE_INTEGER v{},hz{};QueryPerformanceFrequency(&hz);
        auto read=[&](double* values){QueryPerformanceCounter(&v);values[0]=v.QuadPart*1000.0/hz.QuadPart;values[1]=GetTickCount();values[2]=(double)GetTickCount64();values[3]=timeGetTime();};
        auto before=status();external(outsideA);read(a);double start=wall();wait(240);double real=wall()-start;read(b);external(outsideB);auto after=status();
        json ratios=json::array(),externalRatios=json::array();
        for(unsigned n=0;n<4;n++){double ratio=(b[n]-a[n])/real,out=(outsideB[n]-outsideA[n])/real;ratios.push_back(ratio);externalRatios.push_back(out);
            check("main-image clock follows requested rate",std::abs(ratio-rate)<(std::max)(0.12,rate*0.12),{{"phase",phase},{"clock",n},{"rate",rate},{"ratio",ratio}});
            check("external DLL clock stays on wall time",std::abs(out-1)<0.15,{{"phase",phase},{"clock",n},{"ratio",out}});
        }
        measurements.push_back({{"phase",phase},{"rate",rate},{"wallMs",real},{"mainClockRatios",ratios},{"externalClockRatios",externalRatios},{"status",after}});
        check("real-time DLL wait is not scaled",real>=210&&real<600,{{"phase",phase},{"wallMs",real}});
        if(after["installed"]==true)check("main-image clocks produce scoped evidence",after["clockCalls"].get<unsigned long long>()>before["clockCalls"].get<unsigned long long>());
    };
    auto publish=[&](const wchar_t* name,json value){auto temp=folder/(std::wstring(name)+L".tmp");{std::ofstream f(temp);f<<value.dump(2);}MoveFileExW(temp.c_str(),(folder/name).c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH);};
    try{
        check("production clock initialization succeeds",init()!=FALSE);
        auto initial=status();check("translation-only initialization has no speed hook or authorization",initial["installed"]==false&&initial["enabled"]==false&&initial["clockCalls"]==0);
        auto denied=set(4);check("disabled speed request cannot install or scale",denied["ok"]==false&&status()["installed"]==false&&status()["speed"]==1);
        check("restore before enabling does not install",set(1)["ok"]==true&&status()["installed"]==false);
        sample("translation-only",1);
        auto untouched=status();check("translation-only clocks did not traverse speed hooks",untouched["installed"]==false&&untouched["clockCalls"]==0&&untouched["sleepCalls"]==0);
        check("explicit enable succeeds without hook installation",ask({{"op","speedEnable"}})["result"]["enabled"]==true&&status()["installed"]==false);
        if(mode==L"single"){
            // A no-op test DLL carries only the reserved module name. This
            // checks the coexistence refusal, not a generic SpeedBridge run.
            auto marker=LoadLibraryW((fs::path(exe).parent_path()/L"FusionSpeed.x86.dll").c_str());
            check("owned coexistence marker loads",marker!=nullptr);
            check("generic component presence prevents second clock installation",set(4)["ok"]==false&&status()["installed"]==false&&status()["speed"]==1);
            check("normal restoration remains available with generic marker",set(1)["ok"]==true);
            if(marker)FreeLibrary(marker);
            for(double rate:{0.25,2.0,4.0,10.0}){
                auto result=set(rate);check("authorized rate is acknowledged",result["ok"]==true&&result["result"]["speed"]==rate);
                sample("active",rate);double start=wall();Sleep(100);double elapsed=wall()-start;
                check("main-image Sleep uses requested scaling",elapsed>=100.0/rate*0.75&&elapsed<100.0/rate*1.5+30,{{"rate",rate},{"wallMs",elapsed}});
            }
            LARGE_INTEGER before{},after{};QueryPerformanceCounter(&before);check("explicit disable restores and revokes",ask({{"op","speedDisable"}})["result"]["enabled"]==false&&status()["speed"]==1);QueryPerformanceCounter(&after);
            check("restoring normal rate keeps counter continuous",after.QuadPart>=before.QuadPart&&(after.QuadPart-before.QuadPart)*1000.0/([](){LARGE_INTEGER f;QueryPerformanceFrequency(&f);return(double)f.QuadPart;})()<500);
            check("late non-1 request is rejected after revoke",set(4)["ok"]==false&&status()["speed"]==1);sample("restored",1);
        }else{
            double rate=mode==L"A"?4:10;check("parallel session accepts own rate",set(rate)["ok"]==true);sample("parallel-active",rate);
            publish(L"ready.json",{{"pid",GetCurrentProcessId()},{"rate",rate},{"exe",fs::path(exe).u8string()}});
            const wchar_t* signal=mode==L"A"?L"stop.signal":L"peer-closed.signal";double start=wall();while(!fs::exists(folder/signal)&&wall()-start<15000)wait(20);
            check("parallel coordinator signal arrives",fs::exists(folder/signal));
            if(mode==L"B"){check("other instance exit preserves this rate",status()["speed"]==10&&status()["enabled"]==true);sample("peer-closed",10);}
            ask({{"op","speedDisable"}});check("parallel own close restores rate",status()["speed"]==1&&status()["enabled"]==false);
        }
    }catch(const std::exception& e){check("fixture completes without exception",false,e.what());}
    ask({{"op","speedDisable"}});
    json report={{"scope","Production WOLF clock/protocol headers in owned x86 neutral EXE and DLL; no WOLF engine/game/renderer or real save tested."},{"pid",GetCurrentProcessId()},{"exe",fs::path(exe).u8string()},{"checks",checks.size()},{"failures",failed},{"results",checks},{"measurements",measurements}};
    publish(L"results.json",report);std::cout<<"{\"checks\":"<<checks.size()<<",\"failures\":"<<failed<<"}\n";return failed?1:0;
}

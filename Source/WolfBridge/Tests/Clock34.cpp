#include <windows.h>
#include <iostream>
#include <thread>
#include <atomic>
#include "../WolfCache.h"
int main(){
    SetPriorityClass(GetCurrentProcess(),BELOW_NORMAL_PRIORITY_CLASS);int count=0,failed=0;
    auto check=[&](const char* name,bool ok){count++;std::cout<<(ok?"PASS ":"FAIL ")<<name<<"\n";if(!ok)failed++;};
    WolfCache::Cache cache;WolfCache::HitKind hit;
    cache.Put("AM 09:16",u8"上午 09:16");cache.Publish();
    check("new clock lookup without host roundtrip",cache.Find("AM 09:17",&hit)==u8"上午 09:17"&&hit==WolfCache::HitKind::Derived);
    check("meridians never mixed",cache.Find("PM 09:17").empty());
    cache.Put("PM 0:10",u8"下午 0:10");cache.Publish();check("zero hour retained",cache.Find("PM 0:11")==u8"下午 0:11");
    check("seconds retained",cache.Find("AM 01:02:03")==u8"上午 01:02:03");
    for(auto bad:{"AM 09:60","AM 24:00","AM 09:17 tomorrow","Room 09:17","v09:17","09:17","2026-10-19","AM09:17","AM 09:17\nfoo"})check("unrelated source declined",cache.Find(bad).empty());
    cache.Put("09:16 AM",u8"09:16 午前");cache.Publish();check("target clock order retained",cache.Find("10:21 AM")==u8"10:21 午前");
    for(int n=0;n<180;n++){char time[20];sprintf_s(time,"%02d:%02d",n/60,n%60);cache.Put("AM "+std::string(time),u8"上午 "+std::string(time));}cache.Publish();
    check("many earlier readings share one constant time lookup",cache.Find("AM 13:28")==u8"上午 13:28");
    cache.Put("AM 12:00",u8"早上 12:00");cache.Publish();check("conflicting pattern declines",cache.Find("AM 13:28").empty());
    cache.Put("AM 12:00",u8"上午 12:00");cache.Publish();check("corrected owner removes stale conflict",cache.Find("AM 13:28")==u8"上午 13:28");
    cache.Put("AM 13:28",u8"手工保留 13:28");cache.Publish();check("exact manual value wins",cache.Find("AM 13:28")==u8"手工保留 13:28");
    cache.Put("AM 13:28",u8"上午 13:28");cache.Publish();
    std::atomic<bool> done=false;std::atomic<int> read=0,bad=0;
    std::thread renderer([&]{while(!done){int n=read++;char time[20];sprintf_s(time,"%02d:%02d:%02d",n%24,n%60,(n/60)%60);
        if(cache.Find("AM "+std::string(time))!=u8"上午 "+std::string(time))bad++;}});
    auto started=GetTickCount64();while(read<10000&&GetTickCount64()-started<2000)std::this_thread::yield();
    for(int n=0;n<100;n++){cache.Put("Unrelated "+std::to_string(n),u8"其他文字");cache.Publish();}
    done=true;renderer.join();check("rapid ticking and cache publication never return original",read>=10000&&bad==0);
    cache.Clear();check("clear removes clock patterns",cache.Find("AM 09:17").empty());
    WolfClockIndex::Source s;WolfClockIndex::Target t;
    check("invalid numeric target rejected",WolfClockIndex::Parse("AM 09:16",s)&&!WolfClockIndex::Pair(s,u8"上午 09:17",t));
    check("repeated numeric target rejected",!WolfClockIndex::Pair(s,u8"上午 09:16 至 09:16",t));
    std::cout<<"{\"checks\":"<<count<<",\"failed\":"<<failed<<",\"renderLookups\":"<<read<<",\"originalFlashes\":"<<bad<<"}\n";return failed?1:0;
}

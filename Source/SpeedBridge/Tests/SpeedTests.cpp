#include "FixtureProtocol.h"
#include "../ProcessIdentity.h"
#include "../Clock.h"
#include "json.hpp"
#include <fstream>
#include <iostream>
#include <iomanip>
#include <sstream>
#include <stdexcept>
#include <limits>
using namespace FusionSpeed;
using Json=nlohmann::json;
namespace {
Json checks=Json::array();int failed=0,uniqueId=0;std::wstring root,arch;
void Check(const char* name,bool pass,double measured=0){checks.push_back({{"name",name},{"passed",pass},{"measured",measured}});if(!pass){failed++;std::cerr<<"FAIL: "<<name<<" ("<<measured<<")\n";}}
std::wstring Unique(const wchar_t* prefix){std::wostringstream text;text<<prefix<<std::hex<<std::setw(8)<<std::setfill(L'0')<<GetCurrentProcessId()<<std::setw(8)<<++uniqueId<<L"AABBCCDDEEFF0011";return text.str();}
std::wstring Quote(const std::wstring& value){return L"\""+value+L"\"";}
bool Wait(LONG volatile* value,LONG expected,DWORD timeout=3000){auto until=GetTickCount64()+timeout;while(GetTickCount64()<until){if(InterlockedCompareExchange(value,0,0)==expected)return true;Sleep(5);}return false;}
struct Child {
 HANDLE mapping=nullptr,event=nullptr,process=nullptr;Fixture::Control* data=nullptr;DWORD pid=0;uint64_t created=0;std::wstring path,name,eventName;
 Child(){name=Unique(L"Local\\FusionSpeedFixture_");eventName=name+L"_event";mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(Fixture::Control),name.c_str());data=(Fixture::Control*)MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Fixture::Control));if(!data)throw std::runtime_error("fixture mapping");ZeroMemory(data,sizeof(*data));event=CreateEventW(nullptr,FALSE,FALSE,eventName.c_str());path=root+L"SpeedFixture."+arch+L".exe";std::wstring command=Quote(path)+L" "+Quote(name)+L" "+Quote(eventName);STARTUPINFOW start{sizeof(start)};PROCESS_INFORMATION info{};if(!CreateProcessW(path.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,root.c_str(),&start,&info))throw std::runtime_error("fixture launch");process=info.hProcess;pid=info.dwProcessId;CloseHandle(info.hThread);created=Created(process);if(!Wait(&data->ready,1))throw std::runtime_error("fixture ready");}
 bool Command(LONG command,DWORD duration=200,DWORD timeout=3000){data->duration=duration;data->command=command;MemoryBarrier();auto seq=InterlockedIncrement(&data->request);SetEvent(event);return Wait(&data->ack,seq,timeout);}
 void Close(){if(process){Command(3);WaitForSingleObject(process,3000);CloseHandle(process);process=nullptr;}}
 ~Child(){Close();if(data)UnmapViewOfFile(data);if(mapping)CloseHandle(mapping);if(event)CloseHandle(event);}
};
struct Channel {
 HANDLE mapping=nullptr;Shared* data=nullptr;std::wstring name;std::string token=std::string(64,'A');Child& child;
 Channel(Child& target,HANDLE owner=GetCurrentProcess(),DWORD ownerPid=GetCurrentProcessId()):child(target){name=Unique(L"Local\\FusionSpeed_");mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,Size,name.c_str());data=(Shared*)MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,Size);if(!data)throw std::runtime_error("speed mapping");ZeroMemory(data,Size);data->magic=Magic;data->version=Version;data->size=Size;data->ownerPid=ownerPid;data->ownerCreated=Created(owner);data->targetPid=child.pid;data->targetCreated=child.created;memcpy(data->token,token.data(),64);data->desiredMode=1;Write64(&data->requestedRate,Bits(1));Write64(&data->actualRate,Bits(1));Beat();}
 void Beat(){Write64(&data->heartbeat,GetTickCount64());}
 DWORD Attach(const std::wstring& overrideExe=L"",uint64_t createdOverride=0,const std::wstring& overrideToken=L"",bool otherArchitecture=false){
  auto helper=root+L"FusionSpeedHost."+(otherArchitecture?(arch==L"x64"?L"x86":L"x64"):arch)+L".exe";auto exe=overrideExe.empty()?child.path:overrideExe;std::wstring tokenArg=overrideToken.empty()?std::wstring(token.begin(),token.end()):overrideToken;
  auto command=Quote(helper)+L" --pid "+std::to_wstring(child.pid)+L" --created "+std::to_wstring(createdOverride?createdOverride:child.created)+L" --exe "+Quote(exe)+L" --mapping "+Quote(name)+L" --token "+Quote(tokenArg);
  STARTUPINFOW start{sizeof(start)};PROCESS_INFORMATION info{};if(!CreateProcessW(helper.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,root.c_str(),&start,&info))return 999;CloseHandle(info.hThread);DWORD code=998;if(WaitForSingleObject(info.hProcess,20000)==WAIT_OBJECT_0)GetExitCodeProcess(info.hProcess,&code);CloseHandle(info.hProcess);return code;
 }
 bool Request(double rate,uint32_t mode=1){Beat();Write64(&data->requestedRate,Bits(rate));data->desiredMode=mode;MemoryBarrier();LONG seq=InterlockedIncrement(&data->requestSeq);return Wait(&data->ackSeq,seq);}
 double Actual(){return Double(Read64(&data->actualRate));}
 ~Channel(){if(data){if(data->state==1){Request(1,2);Wait(&data->state,2);}UnmapViewOfFile(data);}if(mapping)CloseHandle(mapping);}
};
void Measure(Child& child,Channel& channel,double rate,const std::string& prefix){channel.Beat();Check((prefix+" probe completes").c_str(),child.Command(1,250));LARGE_INTEGER hz{};QueryPerformanceFrequency(&hz);double q=double(Read64(&child.data->qpc))/hz.QuadPart/.25;double a=double(Read64(&child.data->allowed))/hz.QuadPart/.25;double e=double(Read64(&child.data->excluded))/hz.QuadPart/.25;Check((prefix+" target QPC rate").c_str(),q>rate*.85&&q<rate*1.3,q);Check((prefix+" runtime DLL QPC rate").c_str(),a>rate*.85&&a<rate*1.3,a);Check((prefix+" unrelated DLL unscaled").c_str(),e>.85&&e<1.3,e);for(auto pair:{std::make_pair("tick32",Read64(&child.data->tick)),std::make_pair("tick64",Read64(&child.data->tick64)),std::make_pair("multimedia",Read64(&child.data->multimedia))}){double v=pair.second/250.;Check((prefix+" "+pair.first).c_str(),v>rate*.7&&v<rate*1.5,v);}}
ULONGLONG ClockReads(Shared* data){ULONGLONG total=0;for(auto& count:data->clockCalls)total+=(ULONGLONG)Read64(&count);return total;}
void MeasureFileTime(Child& child,Channel& channel,double rate,const std::string& prefix){
 for(int cmd:{6,7}){
  channel.Beat();std::string name=prefix+(cmd==6?" FILETIME":" precise FILETIME");LARGE_INTEGER hz{},before{},after{};QueryPerformanceFrequency(&hz);QueryPerformanceCounter(&before);
  Check((name+" only probe completes").c_str(),child.Command(cmd,250));QueryPerformanceCounter(&after);double wall=double(after.QuadPart-before.QuadPart)/hz.QuadPart;
  for(auto pair:{std::make_pair("target",Read64(&child.data->qpc)),std::make_pair("runtime DLL",Read64(&child.data->allowed)),std::make_pair("unrelated DLL",Read64(&child.data->excluded))}){
   double actual=double(pair.second)/10000000.0/wall,expected=std::string(pair.first)=="unrelated DLL"?1:rate;
   Check((name+" "+pair.first+" measured against external real time").c_str(),actual>expected*.75&&actual<expected*1.3,actual);
  }
 }
}
void FileTimeUnits(){
 Clock clock;const ULONGLONG epoch=133000000000000000ull;clock.Initialize(1000,1000,0,0,0,epoch,epoch+1000);
 Check("FILETIME keeps Windows epoch and 100ns units",clock.Read(1100,FileTimeClock)==(long double)epoch+1000000);
 Check("precise FILETIME keeps independent epoch",clock.Read(1100,PreciseFileTimeClock)==(long double)epoch+1001000);
 clock.Set(10,1100);Check("FILETIME 10x rate preserves transition",clock.Read(1100,FileTimeClock)==(long double)epoch+1000000&&clock.Read(1200,FileTimeClock)==(long double)epoch+11000000);
 clock.Set(1,1200);Check("FILETIME restore does not rewind",clock.Read(1300,FileTimeClock)==(long double)epoch+12000000);
}
void Units(){Clock clock;clock.Initialize(1000,1000,200,400,600);Check("default rate",clock.Rate()==1);Check("counter epoch",clock.Read(1100,0)==1100);Check("tick32 epoch",clock.Read(1100,1)==300);Check("tick64 epoch",clock.Read(1100,2)==500);Check("multimedia epoch",clock.Read(1100,3)==700);clock.Set(4,1100);Check("rate change preserves point",clock.Read(1100,0)==1100);Check("4x counter",clock.Read(1200,0)==1500);clock.Set(.25,1200);Check("slow change preserves point",clock.Read(1200,0)==1500);Check("quarter speed",clock.Read(1600,0)==1600);clock.Set(1,1600);Check("restore preserves accumulated offset",clock.Read(1700,0)==1700);clock.Set(100,1700);Check("100x sleep",clock.Delay(100)==1);Check("yield sleep unchanged",clock.Delay(0)==0);Check("infinite sleep unchanged",clock.Delay(INFINITE)==INFINITE);clock.Set(.25,1700);Check("large sleep saturates finite",clock.Delay(INFINITE-1)==INFINITE-1);Check("rates bounds",ValidRate(.25)&&ValidRate(100)&&!ValidRate(.249)&&!ValidRate(100.001));Check("nonfinite rejected",!ValidRate(std::numeric_limits<double>::infinity())&&!ValidRate(std::numeric_limits<double>::quiet_NaN()));Check("mapping namespace",MappingName(L"Local\\FusionSpeed_0123456789abcdef0123456789abcdef")&&!MappingName(L"Global\\FusionSpeed_0123456789abcdef0123456789abcdef"));Check("engine module allowlist",EngineModule(L"unityplayer.dll")&&EngineModule(L"gameassembly.dll")&&EngineModule(L"python311.dll")&&EngineModule(L"sdl2.dll")&&!EngineModule(L"fusionwolf.dll")&&!EngineModule(L"unrelated.dll"));Check("path component boundary",Under(L"d:\\game\\engine.dll",L"d:\\game")&&!Under(L"d:\\game-other\\engine.dll",L"d:\\game"));}
void Integration(){
 Child child;
 {Channel c(child);Check("wrong helper architecture rejected",c.Attach(L"",0,L"",true)==WrongArchitecture);c.data->targetCreated++;Check("stale PID creation identity rejected",c.Attach(L"",child.created+1)==TargetMismatch);c.data->targetCreated=child.created;c.data->ownerCreated++;Check("owner creation identity rejected",c.Attach()==OwnerUnavailable);}
 {Channel c(child);Check("wrong executable rejected",c.Attach(root+L"FusionSpeedHost."+arch+L".exe")==TargetMismatch);Check("target creation mismatch rejected",c.Attach(L"",child.created+1)==BadMapping);Check("token mismatch rejected",c.Attach(L"",0,std::wstring(64,L'B'))==BadMapping);Check("Unicode token rejected",c.Attach(L"",0,std::wstring(64,wchar_t(0x141)))==BadArguments);c.data->desiredMode=2;Check("cancel before attach rejected",c.Attach()==BadCommand);c.data->desiredMode=1;Write64(&c.data->heartbeat,GetTickCount64()-LeaseMs-10);Check("expired lease before attach rejected",c.Attach()==OwnerUnavailable);Check("identity rejection never loaded speed DLL",!HasWolf(Modules(child.pid))&&[&](){for(auto m:Modules(child.pid))if(BaseName(m.szModule).rfind(L"fusionspeed.",0)==0)return false;return true;}());}
 {Channel c(child);Check("same-bitness attach ready",c.Attach()==Success&&c.data->state==1);if(c.data->state!=1)return;Check("both target and engine scoped",c.data->modules>=2);Check("set 4x acknowledged",c.Request(4)&&c.Actual()==4&&c.data->error==0);Measure(child,c,4,"4x");MeasureFileTime(child,c,4,"4x");Check("scoped clock calls counted",Read64(&c.data->calls)>0);
  Check("10x request acknowledged",c.Request(10)&&c.Actual()==10);auto baseline=Read64(&c.data->commandClockBaseline);auto legacy=Read64(&c.data->calls);Check("sleep-only probe completes",child.Command(2,100));Sleep(80);
  Check("Sleep calls are not time-read evidence",ClockReads(c.data)==(ULONGLONG)baseline&&Read64(&c.data->commandClockMask)==0&&Read64(&c.data->calls)>legacy);
  auto qpcBefore=Read64(&c.data->clockCalls[CounterClock]);MeasureFileTime(child,c,10,"10x");Sleep(50);
  Check("FILETIME-only confirms without QPC reads",Read64(&c.data->clockCalls[CounterClock])==qpcBefore&&ClockReads(c.data)>=(ULONGLONG)baseline+4&&((ULONGLONG)Read64(&c.data->commandClockMask)&((1ull<<FileTimeClock)|(1ull<<PreciseFileTimeClock)))!=0);
  {Child other;Channel separate(other);MeasureFileTime(other,separate,1,"untouched second process");Check("second process attaches independently",separate.Attach()==Success&&separate.Request(.5));MeasureFileTime(other,separate,.5,"second process half speed");MeasureFileTime(child,c,10,"first process remains 10x");}
  Check("repeated request resets current evidence",c.Request(10)&&Read64(&c.data->commandClockMask)==0&&ClockReads(c.data)==(ULONGLONG)Read64(&c.data->commandClockBaseline));
  Check("resume 4x for delay tests",c.Request(4)&&c.Actual()==4);
  auto before=GetTickCount64();Check("accelerated Sleep returns",child.Command(2,800));auto elapsed=GetTickCount64()-before;Check("Sleep scales only target",elapsed>=150&&elapsed<600,double(elapsed));
  before=GetTickCount64();Check("accelerated SleepEx returns",child.Command(5,800));elapsed=GetTickCount64()-before;Check("SleepEx target timing",elapsed>=150&&elapsed<600,double(elapsed));
  Check("NaN acknowledged but not applied",c.Request(std::numeric_limits<double>::quiet_NaN())&&c.data->error==InvalidRate&&c.Actual()==4);Check("out of range not clamped",c.Request(101)&&c.data->error==InvalidRate&&c.Actual()==4);Check("invalid mode not applied",c.Request(2,99)&&c.data->error==BadCommand&&c.Actual()==4);
  Check("infinity rejected",c.Request(std::numeric_limits<double>::infinity())&&c.data->error==InvalidRate&&c.Actual()==4);Check("zero rejected",c.Request(0)&&c.data->error==InvalidRate&&c.Actual()==4);Check("negative rejected",c.Request(-1)&&c.data->error==InvalidRate&&c.Actual()==4);
  Check("fractional rate accepted",c.Request(.25)&&c.Actual()==.25);Measure(child,c,.25,"quarter");MeasureFileTime(child,c,.25,"quarter");Check("100x accepted",c.Request(100)&&c.Actual()==100);Measure(child,c,100,"100x");
  {Channel other(child);Check("second active session rejected",other.Attach()==SessionBusy);}
  Check("stop acknowledged",c.Request(1,2)&&Wait(&c.data->state,2)&&c.Actual()==1);Measure(child,c,1,"restored");MeasureFileTime(child,c,1,"restored");Check("stop leaves game alive",WaitForSingleObject(child.process,0)==WAIT_TIMEOUT);
 }
 {Check("synthetic resident protocol downgraded",child.Command(8)&&child.data->error==0);Channel c(child);Check("old resident protocol explicitly rejected",c.Attach()==OldBridge&&c.data->state==0);Check("synthetic resident protocol restored",child.Command(9)&&child.data->error==0);}
 {Channel c(child);Check("new mapping reconnects same loaded DLL",c.Attach()==Success);Check("reconnected rate applies",c.Request(3)&&c.Actual()==3);Write64(&c.data->heartbeat,GetTickCount64()-LeaseMs-20);Check("heartbeat loss restores 1x",Wait(&c.data->state,2)&&c.Actual()==1);Measure(child,c,1,"lease restored");}
 {Channel c(child);Check("real lease test reconnect",c.Attach()==Success);Check("real lease starts at 100x",c.Request(100));auto start=GetTickCount64();Sleep(1000);Check("100x does not accelerate owner lease",c.data->state==1&&c.Actual()==100);bool restored=Wait(&c.data->state,2,5000);auto elapsed=GetTickCount64()-start;Check("real wall-clock lease restores",restored&&c.Actual()==1&&elapsed>=4500&&elapsed<6000,double(elapsed));}
 {Child owner;Channel c(child,owner.process,owner.pid);Check("separate owner attach",c.Attach()==Success);Check("separate owner speed",c.Request(2)&&c.Actual()==2);owner.Close();Check("owner exit restores without killing game",Wait(&c.data->state,2)&&c.Actual()==1&&WaitForSingleObject(child.process,0)==WAIT_TIMEOUT);}
 {Channel c(child);Check("identity test reconnect",c.Attach()==Success);Check("identity test speed",c.Request(2));c.data->token[0]='B';Check("identity tamper restores 1x",Wait(&c.data->state,3)&&c.Actual()==1&&c.data->error==IdentityChanged);}
 {Channel c(child);Check("late Wolf test attach",c.Attach()==Success);Check("late Wolf speed",c.Request(2));Check("synthetic late Wolf loads",child.Command(4)&&child.data->error==0);Check("late Wolf restores and refuses coexistence",Wait(&c.data->state,3)&&c.Actual()==1&&c.data->error==WolfConflict);}
 {Channel c(child);Check("existing Wolf rejected before attach",c.Attach()==WolfConflict);}
 child.Close();Check("fixture exits cooperatively",child.data->done==1);
}
}
int wmain(int argc,wchar_t** argv){wchar_t exe[32768];GetModuleFileNameW(nullptr,exe,32768);root=exe;root=root.substr(0,root.find_last_of(L'\\')+1);arch=sizeof(void*)==8?L"x64":L"x86";try{Units();FileTimeUnits();Integration();}catch(const std::exception& error){Check("test harness completed",false);std::cerr<<error.what()<<"\n";}Json result={{"architecture",sizeof(void*)==8?"x64":"x86"},{"passed",checks.size()-failed},{"failed",failed},{"checks",checks},{"scope","Synthetic child processes only; no real games, user files or APIs"}};std::wstring output=argc>1?argv[1]:root+L"native-speed-"+arch+L".json";std::ofstream file(output);file<<result.dump(2)<<"\n";std::cout<<"Passed "<<checks.size()-failed<<" / "<<checks.size()<<"\n";return failed?1:0;}

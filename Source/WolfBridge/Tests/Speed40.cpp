#include <windows.h>
#include <atomic>
#include <algorithm>
#include <cmath>
#include <iostream>
#include <limits>
#include "MinHook.h"
#include "../WolfSpeed.h"
#include "../WolfData.h"
#include "../WolfSpeedProtocol.h"
// No injected game and no test payload. Exercise the production command and
// virtual-clock implementation against a deterministic counter in this process.
static long long nowCounter=1000;
static BOOL WINAPI CounterFixture(LARGE_INTEGER* value){value->QuadPart=nowCounter;return TRUE;}
int main(){
 int checks=0,failed=0;auto check=[&](const char* name,bool ok){++checks;if(!ok)++failed;std::cout<<(ok?"PASS ":"FAIL ")<<name<<"\n";};
 WolfSpeed::counter=CounterFixture;WolfSpeed::frequency=1000;WolfSpeed::anchor=nowCounter;WolfSpeed::virtualCounter=1000;WolfSpeed::virtualTicks=1000;
 // This deterministic test substitutes an already-installed clock. The separate
 // Speed113 process/DLL fixture exercises real MinHook installation and scope.
 WolfSpeed::initialized=true;WolfSpeed::installed=true;
 WolfData::Data data;bool ok=true;auto ask=[&](nlohmann::json q){ok=true;return data.Request(q,ok);};
 ask({{"op","dataSpeed"},{"rate",100}});check("disabled protocol rejects speed",!ok&&WolfSpeed::rate==1);
 ask({{"op","dataEnable"}});
 auto result=ask({{"op","dataSpeed"},{"rate",100}});check("production protocol accepts and confirms 100",ok&&result["speed"]==100&&WolfSpeed::rate==100);
 nowCounter+=10;check("100 scales elapsed clock",WolfSpeed::Read(false)==2000&&WolfSpeed::Read(true)==2000);
 result=ask({{"op","dataSpeed"},{"rate",1}});check("restore 1 is continuous",ok&&result["speed"]==1&&WolfSpeed::Read(false)==2000);
 nowCounter+=10;check("restored clock advances normally",WolfSpeed::Read(false)==2010);
 for(double rate:{0.0,-1.0,0.24,100.01,101.0,std::numeric_limits<double>::infinity(),std::numeric_limits<double>::quiet_NaN()}){
  ask({{"op","dataSpeed"},{"rate",rate}});check("protocol rejects nonfinite and out-of-range",!ok&&WolfSpeed::rate==1);
  WolfSpeed::Set(rate);check("clock setter also rejects invalid rate",WolfSpeed::rate==1);
 }
 for(auto value:{nlohmann::json("100"),nlohmann::json(nullptr),nlohmann::json(true)}){ask({{"op","dataSpeed"},{"rate",value}});check("protocol rejects wrong type",!ok&&WolfSpeed::rate==1);}
 ask({{"op","dataSpeed"}});check("protocol rejects missing rate",!ok&&WolfSpeed::rate==1);
 result=ask({{"op","dataSpeed"},{"rate",0.25}});check("lower boundary accepted",ok&&result["speed"]==0.25);
 ask({{"op","dataDisable"}});check("disable returns to normal clock",ok&&WolfSpeed::rate==1);
 auto speed=[&](nlohmann::json q){ok=true;return WolfSpeed::Request(q,ok);};
 speed({{"op","gameSpeed"},{"rate",10}});check("game speed also rejects disabled authorization",!ok&&WolfSpeed::rate==1);
 speed({{"op","gameSpeed"},{"rate",1}});check("normal-speed restoration needs no authorization",ok&&WolfSpeed::rate==1);
 speed({{"op","speedEnable"}});speed({{"op","gameSpeed"},{"rate",10}});
 check("explicit speed authorization accepts rate",ok&&WolfSpeed::rate==10);
 ask({{"op","dataEnable"}});ask({{"op","dataDisable"}});
 check("editing disable restores and revokes all speed",ok&&WolfSpeed::rate==1&&!WolfSpeed::authorized);
 speed({{"op","gameSpeed"},{"rate",4}});check("late speed request after data disable is rejected",!ok&&WolfSpeed::rate==1);
 ask({{"op","dataEnable"}});speed({{"op","gameSpeed"},{"rate",4}});
 check("data enable authorizes existing shared speed path",ok&&WolfSpeed::rate==4);
 data.Stop();check("stop restores speed and revokes authorization",WolfSpeed::rate==1&&!WolfSpeed::authorized);
 std::cout<<"{\"checks\":"<<checks<<",\"failed\":"<<failed<<"}\n";return failed?1:0;
}

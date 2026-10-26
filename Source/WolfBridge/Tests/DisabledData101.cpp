#define FUSION_TEST_WOLFDATA
#include "../WolfData.h"
#include <array>
#include <iostream>

// Only synthetic, owned memory in this executable. No game is launched.
int main(){
 int checks=0,failed=0;auto check=[&](const char* name,bool pass){++checks;if(!pass)++failed;std::cout<<(pass?"PASS ":"FAIL ")<<name<<"\n";};
 for(bool old:{false,true}){
  std::array<unsigned char,128> root{};std::array<int,2> values{10,20};std::array<uintptr_t,4> bank{};
  auto vec=[&](void* object,void* pointer,size_t bytes){auto at=(uintptr_t*)object+(old?1:0);at[0]=(uintptr_t)pointer;at[1]=at[2]=at[0]+bytes;};
  vec(bank.data(),values.data(),sizeof(values));vec(root.data()+(old?24:20),bank.data(),old?16:12);
  WolfData::Data data;data.FixtureRoot((uintptr_t)root.data(),old);data.FixtureDatabase(0);bool ok=true;
  auto ask=[&](nlohmann::json q){ok=true;return data.Request(q,ok);};
  unsigned disabledGeneration=0;
  for(const char* op:{"dataSnapshot","dataStatus","dataSet","dataLock","dataUnlock","dataUndo","dataUnknown"}){
   WolfData::fixtureReadCalls=0;auto result=ask({{"op",op},{"map",0},{"entry","0:0"},{"generation",0},{"expected",10},{"value",99}});
   check("initially disabled requests reject before any memory read",!ok&&WolfData::fixtureReadCalls==0&&values[0]==10);
   disabledGeneration=result["generation"];
  }
  WolfData::fixtureReadCalls=0;auto malformed=ask({{"op","dataSnapshot"},{"map","invalid"}});
  check("disabled map request cannot mutate selection or generation",!ok&&WolfData::fixtureReadCalls==0&&malformed["generation"]==disabledGeneration);
  WolfData::fixtureReadCalls=0;data.Tick();check("disabled frame does not read",WolfData::fixtureReadCalls==0);
  auto enabled=ask({{"op","dataEnable"}});check("explicit enable discovers data",ok&&enabled["available"]==true&&WolfData::fixtureReadCalls>0);
  auto snapshot=ask({{"op","dataSnapshot"}});unsigned generation=snapshot["generation"];
  check("disabled requests did not select a map",ok&&snapshot["map"]==-1&&snapshot["rows"].size()==2);
  ask({{"op","dataLock"},{"entry","0:0"},{"generation",generation},{"expected",10},{"value",50}});values[0]=7;data.Tick();
  check("enabled lock still works",ok&&values[0]==50);
  WolfData::fixtureReadCalls=0;ask({{"op","dataDisable"}});check("disable performs no data read",ok&&WolfData::fixtureReadCalls==0);
  values[0]=8;data.Tick();check("disable releases locks without reading",values[0]==8&&WolfData::fixtureReadCalls==0);
  for(const char* op:{"dataSnapshot","dataSet","dataLock","dataUndo"}){
   WolfData::fixtureReadCalls=0;ask({{"op",op},{"entry","0:0"},{"generation",generation},{"expected",8},{"value",99}});
   check("late request after disable cannot read or write",!ok&&WolfData::fixtureReadCalls==0&&values[0]==8);
  }
  ask({{"op","dataEnable"}});ask({{"op","dataSet"},{"entry","0:0"},{"generation",generation},{"expected",8},{"value",99}});
  check("re-enable invalidates old writes",!ok&&values[0]==8);
  snapshot=ask({{"op","dataSnapshot"}});check("re-enable reports current unlocked value",ok&&snapshot["rows"][0]["value"]==8&&snapshot["rows"][0]["locked"]==false);
  WolfData::Data other;other.FixtureRoot((uintptr_t)root.data(),old);other.FixtureDatabase(0);bool otherOk=true;
  other.Request({{"op","dataEnable"}},otherOk);ask({{"op","dataDisable"}});
  WolfData::fixtureReadCalls=0;auto otherSnapshot=other.Request({{"op","dataSnapshot"}},otherOk);
  check("disabling one session leaves independent session enabled",otherOk&&WolfData::fixtureReadCalls>0&&otherSnapshot["rows"].size()==2);
 }
 std::cout<<"{\"checks\":"<<checks<<",\"failures\":"<<failed<<",\"scope\":\"synthetic in-process memory only\"}\n";return failed?1:0;
}

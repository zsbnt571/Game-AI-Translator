#define FUSION_TEST_WOLFDATA
#include "../WolfData.h"
#include <array>
#include <iostream>
int main(){
 int checks=0,failed=0;auto check=[&](const char* name,bool pass){checks++;if(!pass){failed++;std::cout<<"FAIL "<<name<<"\n";}};
 for(bool old:{false,true}){
  std::array<unsigned char,128> root{},db{},type{};std::array<uintptr_t,4> bank{};
  std::array<int,2> normal{7,11};std::array<int,1> schema{1000};
  unsigned stride=old?16:12,recordStride=old?32:36;
  std::vector<unsigned char> records(21005*recordStride);std::vector<int> values(21005,13);values.back()=91;
  auto vec=[&](void* object,void* data,size_t bytes){auto at=(uintptr_t*)object+(old?1:0);at[0]=(uintptr_t)data;at[1]=at[2]=at[0]+bytes;};
  vec(bank.data(),normal.data(),sizeof(normal));vec(root.data()+(old?24:20),bank.data(),stride);
  *(int*)(db.data()+4)=1;vec(db.data()+20,type.data(),old?40:32);vec(type.data(),schema.data(),sizeof(schema));
  vec(type.data()+(old?16:12),records.data(),records.size());
  for(size_t i=0;i<values.size();i++)vec(records.data()+i*recordStride,&values[i],4);
  WolfData::Data data;data.FixtureRoot((uintptr_t)root.data(),old);data.FixtureDatabase((uintptr_t)db.data());bool ok=true;
  auto ask=[&](nlohmann::json q){ok=true;return data.Request(q,ok);};
  ask({{"op","dataEnable"}});
  auto all=ask({{"op","dataSnapshot"}});check("legacy all keeps bounded page",ok&&all["rows"].size()==20000&&all["truncated"]==true);
  auto selected=ask({{"op","dataSnapshot"},{"entries",{"db:0:21004:0","0:1"}},{"query","999"}});
  check("target beyond 20000 and ignore numeric query",ok&&selected["rows"].size()==2&&selected["rows"][0]["value"]==91&&selected["matchedEntries"]==2&&selected["truncated"]==false);
  unsigned generation=selected["generation"];
  ask({{"op","dataSet"},{"entry","db:0:21004:0"},{"generation",generation},{"expected",91},{"value",93}});
  check("target selected memory write",ok&&values.back()==93&&normal[1]==11);
  ask({{"op","dataSet"},{"entry","db:0:21004:0"},{"generation",generation},{"expected",93},{"value",95}});
  check("repeat write with returned current value",ok&&values.back()==95);
  ask({{"op","dataSet"},{"entry","db:0:21004:0"},{"generation",generation},{"expected",93},{"value",99}});
  check("stale write still rejected",!ok&&values.back()==95);
  auto empty=ask({{"op","dataSnapshot"},{"entries",nlohmann::json::array()}});
  check("empty exact targets never expand to all",ok&&empty["rows"].empty()&&empty["targeted"]==true);
  selected=ask({{"op","dataSnapshot"},{"entries",{"db:0:21004:0"}},{"includeVariables",true}});
  check("targeted database plus normal variables",ok&&selected["rows"].size()==3&&selected["matchedEntries"]==1);
  selected=ask({{"op","dataSnapshot"},{"entries",{"db:0:21004:0","db:0:21004:0","missing"}},{"scope","db"}});
  check("duplicates dedup and unmatched target counted",ok&&selected["requestedEntries"]==2&&selected["matchedEntries"]==1&&selected["rows"].size()==1);
  selected=ask({{"op","dataSnapshot"},{"entries",{"db:0:21004:0","0:1"}},{"scope","variables"}});
  check("target respects scope",ok&&selected["rows"].size()==1&&selected["rows"][0]["id"]=="0:1");
  ask({{"op","dataSnapshot"},{"entries","not an array"}});check("reject malformed target list",!ok);
  ask({{"op","dataSnapshot"},{"entries",{12}}});check("reject numeric address target",!ok);
  ask({{"op","dataSnapshot"},{"entries",{"0:1"}},{"includeVariables","true"}});check("reject malformed flag",!ok);
  nlohmann::json tooMany=nlohmann::json::array();for(int i=0;i<20001;i++)tooMany.push_back("0:1");
  ask({{"op","dataSnapshot"},{"entries",tooMany}});check("bound request size",!ok);
 }
 std::cout<<"{\"checks\":"<<checks<<",\"failures\":"<<failed<<",\"scope\":\"synthetic in-process memory; no game\"}\n";return failed?1:0;
}

#define FUSION_TEST_WOLFDATA
#include "../WolfData.h"
#include <array>
#include <iostream>
int main(){
 int failures=0,checks=0;auto check=[&](const char* name,bool pass){++checks;if(!pass)++failures;std::cout<<(pass?"PASS ":"FAIL ")<<name<<"\n";};
 for(bool old:{false,true}){
  std::array<unsigned char,128> root{};std::array<int,10> normal{},self{};normal[3]=17;self[4]=29;
  std::array<uintptr_t,4> bank{},event{},map{};auto vec=[&](void* object,void* data,size_t bytes){auto at=(uintptr_t*)object+(old?1:0);at[0]=(uintptr_t)data;at[1]=at[2]=at[0]+bytes;};
  unsigned stride=old?16:12;vec(bank.data(),normal.data(),sizeof(normal));vec(root.data()+(old?24:20),bank.data(),stride);
  vec(event.data(),self.data(),sizeof(self));vec(map.data(),event.data(),stride);vec(root.data()+8,map.data(),stride);
  WolfData::Data data;data.FixtureRoot((uintptr_t)root.data(),old);data.FixtureDatabase(0);bool ok=true;
  auto ask=[&](nlohmann::json q){ok=true;return data.Request(q,ok);};
  check("enable vectors",ask({{"op","dataEnable"}})["available"]==true);
  auto snapshot=ask({{"op","dataSnapshot"},{"map",0}});unsigned generation=snapshot["generation"];
  check("map and normal banks separately validated",ok&&snapshot["mapAvailable"]==true&&snapshot["rows"].size()==20);
  ask({{"op","dataSet"},{"generation",generation},{"entry","map:0:0:4"},{"expected",29},{"value",31}});
  check("set selected self without touching normal",ok&&self[4]==31&&normal[3]==17);
  ask({{"op","dataUndo"},{"generation",generation}});check("undo restores self",ok&&self[4]==29);
  ask({{"op","dataLock"},{"generation",generation},{"entry","0:3"},{"expected",17},{"value",21}});normal[3]=9;data.Tick();check("normal lock while map selected",ok&&normal[3]==21);
  ask({{"op","dataSet"},{"generation",generation},{"entry","map:0:0:4"},{"expected",28},{"value",99}});check("reject stale expected value",!ok&&self[4]==29);
  ask({{"op","dataSnapshot"},{"map",-1}});normal[3]=9;data.Tick();check("map change releases stale locks",normal[3]==9);
  ask({{"op","dataSet"},{"generation",generation},{"entry","map:0:0:4"},{"expected",29},{"value",99}});check("old generation cannot change self",!ok&&self[4]==29);
  snapshot=ask({{"op","dataSnapshot"},{"map",50000}});check("unknown map exposes no self addresses",ok&&snapshot["mapAvailable"]==false&&snapshot["rows"].size()==10);
  data.Stop();normal[3]=8;data.Tick();check("disable releases locks",normal[3]==8);
  std::array<unsigned char,128> db{},type{},record{};std::array<int,2> schema{1000,2000};std::array<int,1> money{148};std::array<char,28> stringSlot{};
  *(int*)(db.data()+4)=1;vec(db.data()+20,type.data(),old?40:32);vec(type.data(),schema.data(),sizeof(schema));vec(type.data()+(old?16:12),record.data(),old?32:36);
  vec(record.data(),money.data(),sizeof(money));vec(record.data()+(old?16:12),stringSlot.data(),old?28:24);
  data.FixtureDatabase((uintptr_t)db.data());ask({{"op","dataEnable"}});snapshot=ask({{"op","dataSnapshot"},{"map",-1}});generation=snapshot["generation"];
  check("mutable database exposes numeric field only",snapshot["databaseAvailable"]==true&&snapshot["rows"].size()==11&&snapshot["rows"][0]["id"]=="db:0:0:0");
  ask({{"op","dataSet"},{"generation",generation},{"entry","db:0:0:0"},{"expected",148},{"value",168}});check("database write leaves ordinary bank untouched",ok&&money[0]==168&&normal[3]==8);
  ask({{"op","dataUndo"},{"generation",generation}});check("database undo",ok&&money[0]==148);
  ask({{"op","dataLock"},{"generation",generation},{"entry","db:0:0:0"},{"expected",148},{"value",168}});money[0]=100;data.Tick();check("database numeric lock",money[0]==168);
  snapshot=ask({{"op","dataSnapshot"},{"scope","variables"}});check("ordinary scope not hidden by database rows",snapshot["rows"].size()==10);
  money[0]=100;data.Tick();check("switching category retains lock",money[0]==168);
  snapshot=ask({{"op","dataSnapshot"},{"scope","locked"}});check("locked scope includes only locked data",snapshot["rows"].size()==1&&snapshot["rows"][0]["id"]=="db:0:0:0");
  snapshot=ask({{"op","dataSnapshot"},{"query","168"}});check("numeric search covers ordinary and database values",snapshot["rows"].size()==1);
  std::array<int,1> replacement{93};vec(record.data(),replacement.data(),sizeof(replacement));data.Tick();check("database reallocation releases lock",replacement[0]==93);
  ask({{"op","dataSet"},{"generation",generation},{"entry","db:0:0:0"},{"expected",93},{"value",999}});check("stale database generation rejected",!ok&&replacement[0]==93);
  if(!old){
   vec(db.data()+20,type.data(),84);data.FixtureDatabase((uintptr_t)db.data());snapshot=ask({{"op","dataSnapshot"},{"scope","db"}});generation=snapshot["generation"];
   check("extended type metadata keeps numeric schema",ok&&snapshot["databaseAvailable"]==true&&snapshot["rows"].size()==1&&snapshot["rows"][0]["value"]==93);
   ask({{"op","dataSet"},{"generation",generation},{"entry","db:0:0:0"},{"expected",93},{"value",101}});check("extended type numeric write",ok&&replacement[0]==101);
   ask({{"op","dataUndo"},{"generation",generation}});check("extended type undo",ok&&replacement[0]==93);
  }
  *(int*)(db.data()+4)=0;snapshot=ask({{"op","dataSnapshot"}});check("read-only database rejected",snapshot["databaseAvailable"]==false&&snapshot["rows"].size()==10);
 }
 std::cout<<"{\"checks\":"<<checks<<",\"failures\":"<<failures<<"}\n";return failures?1:0;
}

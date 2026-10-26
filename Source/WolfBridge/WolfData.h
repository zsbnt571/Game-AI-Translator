#pragma once
#include <windows.h>
#include <vector>
#include <string>
#include <map>
#include <unordered_set>
#include <cmath>
#include "json.hpp"
// WOLF numeric variable banks. Locate the engine's initializer, then validate
// every vector and memory region. Never search for a player's numeric value or
// accept an address from IPC. Strings, DB objects and saves are not guessed.
namespace WolfData {
using nlohmann::json;
#ifdef FUSION_TEST_WOLFDATA
inline size_t fixtureReadCalls=0;
#endif
inline bool Region(uintptr_t address,size_t count,bool write=false){
 if(!address||count>4*1024*1024||address+count<address)return false;
 MEMORY_BASIC_INFORMATION m{};if(!VirtualQuery((void*)address,&m,sizeof(m))||m.State!=MEM_COMMIT||m.Protect&(PAGE_GUARD|PAGE_NOACCESS))return false;
 if(address+count>(uintptr_t)m.BaseAddress+m.RegionSize)return false;
 if(write&&!(m.Protect&(PAGE_READWRITE|PAGE_WRITECOPY)))return false;
 return true;
}
inline bool Read(uintptr_t address,void* out,size_t n){
#ifdef FUSION_TEST_WOLFDATA
 ++fixtureReadCalls;
#endif
 if(!Region(address,n))return false;__try{memcpy(out,(void*)address,n);return true;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}}
inline bool Exchange(uintptr_t address,int expected,int value){if(address%4||!Region(address,4,true))return false;__try{return InterlockedCompareExchange((volatile LONG*)address,value,expected)==expected;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}}
struct Bank {uintptr_t pointer=0;unsigned size=0;bool operator==(const Bank& b)const{return pointer==b.pointer&&size==b.size;}};
struct Entry {uintptr_t address;int expected;};
struct Undo {std::string id;int before,after;};
#include "WolfDatabaseData.h"
class Data {
 uintptr_t root=0;bool legacy=false,enabled=false;unsigned generation=0;std::vector<Bank> banks,mapBanks;int selectedMap=-1;bool mapAvailable=false;
 std::map<std::string,Entry> rows;std::map<std::string,int> locks;std::vector<Undo> history;
 uintptr_t databaseRoot=0;unsigned databaseTypeStride=0;bool databaseLocated=false,databaseAvailable=false;std::vector<DatabaseRow> databaseRows;std::vector<Bank> databaseShape;
 bool Locate(){
  auto base=(unsigned char*)GetModuleHandleW(nullptr);auto dos=(IMAGE_DOS_HEADER*)base;
  auto nt=(IMAGE_NT_HEADERS32*)(base+dos->e_lfanew);auto sections=IMAGE_FIRST_SECTION(nt);uintptr_t result=0;
  if(nt->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||nt->FileHeader.NumberOfSections>96)return false;
  for(unsigned n=0;n<nt->FileHeader.NumberOfSections;n++){
   auto s=sections[n];if(!(s.Characteristics&IMAGE_SCN_MEM_EXECUTE)||s.Misc.VirtualSize<38||s.VirtualAddress+s.Misc.VirtualSize>nt->OptionalHeader.SizeOfImage)continue;
   for(size_t i=0;i+38<=s.Misc.VirtualSize;i++){
    auto p=base+s.VirtualAddress+i;
    // 3.1 compiler emits add-id / mov-this / push-id without the explicit
    // flag pushes used by 3.7. Validate the resolver's prologue and ID domain,
    // then validate the live vectors; never use a game-specific address.
    if(!legacy&&p[0]==0x05&&!memcmp(p+1,"\x80\x84\x1e\x00",4)&&p[5]==0xb9&&p[10]==0x50&&p[11]==0xe8){
     uintptr_t object=0;memcpy(&object,p+6,4);int displacement=0;memcpy(&displacement,p+12,4);auto call=(uintptr_t)(p+16)+displacement;
     if(object>=(uintptr_t)base&&object<=(uintptr_t)base+nt->OptionalHeader.SizeOfImage-64&&Region(object,64,true)&&
        call>=(uintptr_t)base&&call<(uintptr_t)base+nt->OptionalHeader.SizeOfImage-128&&Region(call,128)&&
        !memcmp((void*)call,"\x55\x8b\xec\x6a\xff\x68",6)){
      auto code=(unsigned char*)call;bool domain=false;for(unsigned k=0;k+6<=128;k++)if(!memcmp(code+k,"\x81\xff\x00\xca\x9a\x3b",6))domain=true;
      if(domain){if(result&&result!=object)return false;result=object;}
     }
    }
    // Newer optimized runtimes no longer share the old initializer sequence.
    // This call site resolves a normal variable (2,000,000 + index), with the
    // same two explicit resolver flags. Require every matching site to agree
    // on one writable global object; Observe still validates all its vectors.
    if(!legacy&&p[0]==0xb9&&p[5]==0x6a&&p[6]==0&&p[7]==0x6a&&p[8]==1&&p[9]==0x05&&
       p[10]==0x80&&p[11]==0x84&&p[12]==0x1e&&p[13]==0&&p[14]==0x50&&p[15]==0xe8){
     uintptr_t object=0;memcpy(&object,p+1,4);
     int displacement=0;memcpy(&displacement,p+16,4);uintptr_t call=(uintptr_t)(p+20)+displacement;
     if(object>=(uintptr_t)base&&object<=(uintptr_t)base+nt->OptionalHeader.SizeOfImage-64&&Region(object,64,true)&&
        call>=(uintptr_t)base&&call<(uintptr_t)base+nt->OptionalHeader.SizeOfImage&&Region(call,16)&&
        !memcmp((void*)call,"\x53\x8b\xdc\x83\xec\x08\x83\xe4\xf8",9)){
       if(result&&result!=object)return false;result=object;
     }
    }
    if(p[0]!=0xb9||p[5]!=0xe8||p[10]!=0xb9||p[15]!=0xe8||p[20]!=0xb9||p[25]!=0xe8||p[30]!=0x68||p[35]!=0x8b)continue;
    bool valid=true;for(unsigned j:{0u,10u,20u}){
     uintptr_t object=0;memcpy(&object,p+j+1,4);if(object<(uintptr_t)base||object>(uintptr_t)base+nt->OptionalHeader.SizeOfImage-64||!Region(object,64,true)){valid=false;break;}
     int displacement=0;memcpy(&displacement,p+j+6,4);auto call=(uintptr_t)(p+j+10)+displacement;
     MEMORY_BASIC_INFORMATION m{};if(!VirtualQuery((void*)call,&m,sizeof(m))||!(m.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE))){valid=false;break;}
    }
    if(!valid)continue;uintptr_t value=0;memcpy(&value,p+21,4);if(result&&result!=value)return false;result=value;
   }
  }
  root=result;return root!=0;
 }
 bool Observe(std::vector<Bank>& result){
  if(!root&&!Locate())return false;
  uintptr_t outer[3]{};if(!Read(root+(legacy?0x1c:0x14),outer,sizeof(outer)))return false;
  unsigned stride=legacy?16:12;if(outer[1]<outer[0]||outer[2]<outer[1]||(outer[1]-outer[0])%stride)return false;
  auto count=(outer[1]-outer[0])/stride;if(!count||count>64||!Region(outer[0],outer[2]-outer[0]))return false;
  size_t total=0;
  for(unsigned i=0;i<count;i++){
   uintptr_t vector[3]{};if(!Read(outer[0]+i*stride+(legacy?4:0),vector,sizeof(vector)))return false;
   if(vector[1]<vector[0]||vector[2]<vector[1]||(vector[1]-vector[0])%4)return false;
   unsigned size=(unsigned)((vector[1]-vector[0])/4);if(size>100000||(total+=size)>250000)return false;
   if(size&&(vector[0]%4||!Region(vector[0],vector[2]-vector[0],true)))return false;
   result.push_back({vector[0],size});
  }return total>0;
 }
 bool Vector(uintptr_t object,unsigned stride,unsigned maximum,Bank& value){
  uintptr_t v[3]{};if(!Read(object+(legacy?4:0),v,sizeof(v))||v[1]<v[0]||v[2]<v[1]||(v[1]-v[0])%stride)return false;
  auto count=(v[1]-v[0])/stride;if(count>maximum||count&&(!Region(v[0],v[2]-v[0],true)||v[0]%4))return false;
  value={v[0],(unsigned)count};return true;
 }
 bool ObserveMap(std::vector<Bank>& result){
  if(selectedMap<0)return true;unsigned stride=legacy?16:12;Bank maps{},events{};
  if(!Vector(root+8,stride,50000,maps)||(unsigned)selectedMap>=maps.size||!Vector(maps.pointer+selectedMap*stride,stride,50000,events))return false;
  size_t total=0;for(unsigned e=0;e<events.size;e++){
   Bank values{};if(!Vector(events.pointer+e*stride,4,10,values)||(total+=values.size)>250000)return false;result.push_back(values);
  }return true;
 }
 bool ObserveDatabaseLayout(std::vector<DatabaseRow>& result,std::vector<Bank>& shape,unsigned typeStride){
  Bank types{};unsigned rowStride=legacy?32:36;
  if(!Vector(databaseRoot+0x14,typeStride,512,types)||!types.size)return false;shape.push_back(types);
  size_t cells=0;
  for(unsigned t=0;t<types.size;t++){
   auto type=types.pointer+t*typeStride;Bank schema{},records{};
   if(!Vector(type,4,4096,schema)||!Vector(type+(legacy?16:12),rowStride,100000,records))return false;
   shape.push_back(schema);shape.push_back(records);std::vector<unsigned> fields;
   unsigned strings=0;
   for(unsigned f=0;f<schema.size;f++){
    int slot=0;if(!Read(schema.pointer+f*4,&slot,4))return false;
    if(slot>=1000&&slot<2000){unsigned index=slot-1000;if(index!=fields.size())return false;fields.push_back(f);}
    else if(slot>=2000&&slot<3000){if(unsigned(slot-2000)!=strings++)return false;}
    else return false;
   }
   for(unsigned r=0;r<records.size;r++){
    Bank values{},texts{};auto row=records.pointer+r*rowStride;
    if(!Vector(row,4,4096,values)||values.size!=fields.size()||!Vector(row+(legacy?16:12),legacy?28:24,4096,texts)||texts.size!=strings)return false;
    if((cells+=values.size)>250000)return false;
    result.push_back({t,r,values,fields});
   }
  }return true;
 }
 bool ObserveDatabase(std::vector<DatabaseRow>& result,std::vector<Bank>& shape){
  if(!databaseLocated){databaseRoot=DatabaseRoot();databaseLocated=true;}
  if(!databaseRoot)return false;int kind=-1;if(!Read(databaseRoot+4,&kind,4)||kind!=1)return false;
  if(databaseTypeStride)return ObserveDatabaseLayout(result,shape,databaseTypeStride);
  // WOLF's newer 3.x type object retains the same typed numeric/string
  // vectors but appends metadata (84 bytes instead of 32). Validate the
  // complete schema and every row for each known layout; never infer a
  // currency address from a value or from a game's title/version.
  unsigned matches=0,chosen=0;
  for(unsigned stride:legacy?std::vector<unsigned>{40}:std::vector<unsigned>{32,84}){
   std::vector<DatabaseRow> candidate;std::vector<Bank> candidateShape;
   if(!ObserveDatabaseLayout(candidate,candidateShape,stride))continue;
   ++matches;chosen=stride;result=std::move(candidate);shape=std::move(candidateShape);
  }
  if(matches!=1){result.clear();shape.clear();return false;}
  databaseTypeStride=chosen;return true;
 }
 bool Refresh(){
  // Never locate or dereference variable/database banks before an explicit
  // enable, including a late request that arrives after dataDisable.
  if(!enabled)return false;
  std::vector<Bank> current,currentMap;bool normalAvailable=Observe(current);if(!normalAvailable)current.clear();
  mapAvailable=normalAvailable&&ObserveMap(currentMap);if(!mapAvailable)currentMap.clear();
  std::vector<DatabaseRow> currentDatabase;std::vector<Bank> currentShape;
  databaseAvailable=ObserveDatabase(currentDatabase,currentShape);if(!databaseAvailable){currentDatabase.clear();currentShape.clear();}
  if(current!=banks||currentMap!=mapBanks||currentDatabase!=databaseRows||currentShape!=databaseShape){banks=std::move(current);mapBanks=std::move(currentMap);databaseRows=std::move(currentDatabase);databaseShape=std::move(currentShape);Reset();}return normalAvailable||databaseAvailable;
 }
 void Reset(){++generation;rows.clear();locks.clear();history.clear();}
 static bool Number(const json& q,const char* field,int& result){if(!q.contains(field)||!q[field].is_number())return false;double x=q[field].get<double>();if(!std::isfinite(x)||std::floor(x)!=x||x<INT_MIN||x>INT_MAX)return false;result=(int)x;return true;}
 json Failure(bool& ok,const char* message){ok=false;return {{"message",message},{"generation",generation}};}
public:
#ifdef FUSION_TEST_WOLFDATA
 void FixtureRoot(uintptr_t value,bool old){root=value;legacy=old;}
 void FixtureDatabase(uintptr_t value){databaseRoot=value;databaseLocated=true;databaseTypeStride=0;}
#endif
 void Initialize(bool old){legacy=old;}
 void Stop(){enabled=false;Reset();
#ifndef FUSION_TEST_WOLFDATA
  WolfSpeed::Revoke();
#endif
 }
 void Tick(){
  if(!enabled||locks.empty())return;if(!Refresh())return;
  for(auto i=locks.begin();i!=locks.end();){auto row=rows.find(i->first);int value=0;
   if(row==rows.end()||!Read(row->second.address,&value,4)||!Exchange(row->second.address,value,i->second)){i=locks.erase(i);continue;}
   row->second.expected=i->second;++i;
  }
 }
 json Request(const json& q,bool& ok){
  auto op=q.value("op","");
  if(op=="dataDisable"){Stop();return {{"enabled",false}};}
  if(op=="dataEnable"){
   enabled=true;
#ifndef FUSION_TEST_WOLFDATA
   WolfSpeed::Authorize();
#endif
   bool available=Refresh();return {{"enabled",true},{"available",available},{"generation",generation}};
  }
  if(!enabled)return Failure(ok,u8"请先开启游戏修改。");
#ifndef FUSION_TEST_WOLFDATA
  if(op=="dataSpeed"){
   if(!q.contains("rate")||!q["rate"].is_number())return Failure(ok,u8"请填写有效的游戏速度。");
   double value=q["rate"].get<double>();if(!WolfSpeed::Valid(value))return Failure(ok,u8"变速范围为 0.25 到 100 倍。");
   const char* error=nullptr;if(!WolfSpeed::Apply(value,error))return Failure(ok,error);return {{"speed",WolfSpeed::rate.load()}};
  }
#endif
  if(op=="dataSnapshot"&&q.contains("map")){
   int value=-1;if(!Number(q,"map",value)||value< -1||value>50000)return Failure(ok,u8"地图编号无效。");
   if(value!=selectedMap){selectedMap=value;mapBanks.clear();Reset();}
  }
  if(!Refresh())return Failure(ok,u8"尚未识别当前数值数据。若已进入游戏，此引擎版本可能尚未支持；请保留诊断报告。");
#ifdef FUSION_TEST_CAPTURE
  if(op=="dataInspect"){
   Bank maps{},events{};unsigned stride=legacy?16:12;bool a=Vector(root+8,stride,50000,maps),b=a&&selectedMap>=0&&(unsigned)selectedMap<maps.size&&Vector(maps.pointer+selectedMap*stride,stride,50000,events);
   json sizes=json::array();if(b)for(unsigned i=0;i<(std::min)(events.size,16u);i++){Bank s{};bool valid=Vector(events.pointer+i*stride,4,100000,s);sizes.push_back({{"valid",valid},{"size",s.size}});}
   return {{"mapsValid",a},{"maps",maps.size},{"eventsValid",b},{"events",events.size},{"banks",sizes}};
  }
#endif
  if(op=="dataSnapshot"){
   bool targeted=q.contains("entries");std::unordered_set<std::string> requested;
   if(q.contains("includeVariables")&&!q["includeVariables"].is_boolean())return Failure(ok,u8"请求的变量列表无效。");
   bool includeVariables=targeted&&q.value("includeVariables",false);
   if(targeted){
    const auto& values=q["entries"];
    if(!values.is_array()||values.size()>20000)return Failure(ok,u8"请求的变量列表无效。");
    for(const auto& value:values){
     if(!value.is_string())return Failure(ok,u8"请求的变量列表无效。");
     auto id=value.get<std::string>();
     if(id.empty()||id.size()>96)return Failure(ok,u8"请求的变量列表无效。");
     requested.insert(std::move(id));
    }
   }
   json output=json::array();for(auto i=rows.begin();i!=rows.end();)if(!locks.count(i->first))i=rows.erase(i);else ++i;bool truncated=false;
   auto scope=q.value("scope","all");
   auto query=q.value("query","");char* end=nullptr;long long number=query.empty()?0:strtoll(query.c_str(),&end,10);
   bool numericQuery=!query.empty()&&end==query.c_str()+query.size()&&number>=INT_MIN&&number<=INT_MAX;
   auto include=[&](const std::string& id,int value){return (scope!="locked"||locks.count(id))&&(targeted?(requested.count(id)!=0||(includeVariables&&id[0]>='0'&&id[0]<='9')):!numericQuery||value==number);};
   // Database first, so large normal banks cannot hide currency or game stats.
   if(scope!="variables")for(const auto& record:databaseRows)for(unsigned i=0;i<record.values.size;i++){
    auto address=record.values.pointer+i*4;int value=0;if(!Read(address,&value,4))continue;
    std::string id="db:"+std::to_string(record.type)+":"+std::to_string(record.row)+":"+std::to_string(record.fields[i]);if(!include(id,value))continue;if(output.size()>=20000){truncated=true;break;}rows[id]={address,value};
    output.push_back({{"id",id},{"name",u8"可变数据库 / "+std::to_string(record.type)+" / "+std::to_string(record.row)+" / "+std::to_string(record.fields[i])},{"type","number"},{"value",value},{"locked",locks.count(id)!=0}});
   }
   if(scope!="db"&&selectedMap>=0&&mapAvailable)for(unsigned e=0;e<mapBanks.size();e++)for(unsigned i=0;i<mapBanks[e].size;i++){
    auto address=mapBanks[e].pointer+i*4;int value=0;if(!Read(address,&value,4))continue;
    std::string id="map:"+std::to_string(selectedMap)+":"+std::to_string(e)+":"+std::to_string(i);if(!include(id,value))continue;if(output.size()>=20000){truncated=true;break;}rows[id]={address,value};
    output.push_back({{"id",id},{"name",u8"地图 "+std::to_string(selectedMap)+u8" / 事件 "+std::to_string(e)+u8" / Self "+std::to_string(i)},{"type","number"},{"value",value},{"locked",locks.count(id)!=0}});
   }
   if(scope!="db")for(unsigned b=0;b<banks.size();b++)for(unsigned i=0;i<banks[b].size;i++){
    auto address=banks[b].pointer+i*4;int value=0;if(!Read(address,&value,4))continue;
    std::string id=std::to_string(b)+":"+std::to_string(i);if(!include(id,value))continue;if(output.size()>=20000){truncated=true;break;}rows[id]={address,value};
    output.push_back({{"id",id},{"name",u8"变量组 "+std::to_string(b)+" / "+std::to_string(i)},{"type","number"},{"value",value},{"locked",locks.count(id)!=0}});
   }
   size_t matched=0;if(targeted)for(const auto& row:output)if(requested.count(row["id"].get<std::string>()))matched++;
   return {{"generation",generation},{"rows",output},{"truncated",truncated},{"targeted",targeted},{"requestedEntries",requested.size()},{"matchedEntries",matched},{"scope","numeric-variable-banks"},{"map",selectedMap},{"mapAvailable",mapAvailable},{"databaseAvailable",databaseAvailable}};
  }
  if(q.value("generation",0u)!=generation)return Failure(ok,u8"游戏数据已变化，请重新刷新后修改。");
  if(op=="dataUndo"){
   if(history.empty())return Failure(ok,u8"没有可撤销的修改。");auto u=history.back();auto i=rows.find(u.id);
   if(i==rows.end()||!Exchange(i->second.address,u.after,u.before))return Failure(ok,u8"数值已被游戏改变，未覆盖当前值。");
   locks.erase(u.id);i->second.expected=u.before;history.pop_back();return {{"ok",true}};
  }
  auto id=q.value("entry","");auto i=rows.find(id);int expected=0,value=0;
  if(i==rows.end()||!Number(q,"expected",expected))return Failure(ok,u8"变量已失效，请重新刷新。");
  if(op=="dataUnlock"){locks.erase(id);return {{"ok",true}};}
  if(op!="dataSet"&&op!="dataLock")return Failure(ok,u8"此修改操作尚未支持。");
  if(!Number(q,"value",value))return Failure(ok,u8"WOLF 数值变量需要填写 32 位整数。");
  if(!Exchange(i->second.address,expected,value))return Failure(ok,u8"数值已变化，请刷新后重试。");
  if(history.size()>=100)history.erase(history.begin());history.push_back({id,expected,value});i->second.expected=value;
  if(op=="dataLock")locks[id]=value;else locks.erase(id);return {{"ok",true},{"value",value}};
 }
};
}

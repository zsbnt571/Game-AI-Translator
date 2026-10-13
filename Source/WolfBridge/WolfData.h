#pragma once
#include <windows.h>
#include <vector>
#include <string>
#include <map>
#include <cmath>
#include "json.hpp"
// WOLF numeric variable banks. Locate the engine's initializer, then validate
// every vector and memory region. Never search for a player's numeric value or
// accept an address from IPC. Strings, DB objects and saves are not guessed.
namespace WolfData {
using nlohmann::json;
inline bool Region(uintptr_t address,size_t count,bool write=false){
 if(!address||count>4*1024*1024||address+count<address)return false;
 MEMORY_BASIC_INFORMATION m{};if(!VirtualQuery((void*)address,&m,sizeof(m))||m.State!=MEM_COMMIT||m.Protect&(PAGE_GUARD|PAGE_NOACCESS))return false;
 if(address+count>(uintptr_t)m.BaseAddress+m.RegionSize)return false;
 if(write&&!(m.Protect&(PAGE_READWRITE|PAGE_WRITECOPY)))return false;
 return true;
}
inline bool Read(uintptr_t address,void* out,size_t n){if(!Region(address,n))return false;__try{memcpy(out,(void*)address,n);return true;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}}
inline bool Exchange(uintptr_t address,int expected,int value){if(address%4||!Region(address,4,true))return false;__try{return InterlockedCompareExchange((volatile LONG*)address,value,expected)==expected;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}}
struct Bank {uintptr_t pointer=0;unsigned size=0;bool operator==(const Bank& b)const{return pointer==b.pointer&&size==b.size;}};
struct Entry {uintptr_t address;int expected;};
struct Undo {std::string id;int before,after;};
class Data {
 uintptr_t root=0;bool legacy=false,enabled=false;unsigned generation=0;std::vector<Bank> banks;
 std::map<std::string,Entry> rows;std::map<std::string,int> locks;std::vector<Undo> history;
 bool Locate(){
  auto base=(unsigned char*)GetModuleHandleW(nullptr);auto dos=(IMAGE_DOS_HEADER*)base;
  auto nt=(IMAGE_NT_HEADERS32*)(base+dos->e_lfanew);auto sections=IMAGE_FIRST_SECTION(nt);uintptr_t result=0;
  if(nt->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||nt->FileHeader.NumberOfSections>96)return false;
  for(unsigned n=0;n<nt->FileHeader.NumberOfSections;n++){
   auto s=sections[n];if(!(s.Characteristics&IMAGE_SCN_MEM_EXECUTE)||s.Misc.VirtualSize<38||s.VirtualAddress+s.Misc.VirtualSize>nt->OptionalHeader.SizeOfImage)continue;
   for(size_t i=0;i+38<=s.Misc.VirtualSize;i++){
    auto p=base+s.VirtualAddress+i;
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
 bool Refresh(){std::vector<Bank> current;if(!Observe(current)){if(!banks.empty()){banks.clear();Reset();}return false;}if(current!=banks){banks=std::move(current);Reset();}return true;}
 void Reset(){++generation;rows.clear();locks.clear();history.clear();}
 static bool Number(const json& q,const char* field,int& result){if(!q.contains(field)||!q[field].is_number())return false;double x=q[field].get<double>();if(!std::isfinite(x)||std::floor(x)!=x||x<INT_MIN||x>INT_MAX)return false;result=(int)x;return true;}
 json Failure(bool& ok,const char* message){ok=false;return {{"message",message},{"generation",generation}};}
public:
#ifdef FUSION_TEST_WOLFDATA
 void FixtureRoot(uintptr_t value,bool old){root=value;legacy=old;}
#endif
 void Initialize(bool old){legacy=old;}
 void Stop(){enabled=false;Reset();}
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
  if(!Refresh())return Failure(ok,u8"尚未找到此引擎版本的数值变量；请进入游戏后重新刷新。");
  if(op=="dataEnable"){enabled=true;return {{"enabled",true},{"generation",generation}};}
  if(!enabled)return Failure(ok,u8"请先开启游戏修改。");
  if(op=="dataSnapshot"){
   json output=json::array();rows.clear();bool truncated=false;
   for(unsigned b=0;b<banks.size();b++)for(unsigned i=0;i<banks[b].size;i++){
    if(output.size()>=20000){truncated=true;break;}auto address=banks[b].pointer+i*4;int value=0;if(!Read(address,&value,4))continue;
    std::string id=std::to_string(b)+":"+std::to_string(i);rows[id]={address,value};
    output.push_back({{"id",id},{"name",u8"变量组 "+std::to_string(b)+" / "+std::to_string(i)},{"type","number"},{"value",value},{"locked",locks.count(id)!=0}});
   }
   return {{"generation",generation},{"rows",output},{"truncated",truncated},{"scope","numeric-variable-banks"}};
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

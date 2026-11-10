#pragma once
// DxLib's native InitFontCacheToHandle contract, matched by the complete cache
// reset routine rather than an executable identity or a game-specific address.
// The masks below allow only relative CALL operands to vary. Layout constants,
// loop bounds and x86 calling convention remain part of the contract.
namespace WolfFontCache {
using Cdecl=int(__cdecl*)(void*,int);
using Fast=int(__fastcall*)(void*);
inline void* original=nullptr;
inline bool fast=false;
inline unsigned maxOffset=0,indexOffset=0,lengthOffset=0,minimumSize=0;
inline std::atomic<uintptr_t> manager{0};
inline std::atomic<DWORD> renderingThread{0};
inline std::atomic<unsigned> observations{0},clears{0},failures{0};
inline std::atomic<unsigned> lastStage{0},lastCacheCount{0},lastCacheLength{0};
inline const char* contract="unavailable";
inline bool Read(uintptr_t p,void* out,size_t size) noexcept {
 __try{memcpy(out,(void*)p,size);return true;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
inline bool Region(uintptr_t p,size_t size,bool write=false){
 if(!p||size>2*1024*1024||p+size<p)return false;
 for(uintptr_t end=p+size;p<end;){MEMORY_BASIC_INFORMATION m={};if(!VirtualQuery((void*)p,&m,sizeof(m))||m.State!=MEM_COMMIT||(m.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
  DWORD mode=m.Protect&0xff;if(write&&mode!=PAGE_READWRITE&&mode!=PAGE_WRITECOPY&&mode!=PAGE_EXECUTE_READWRITE&&mode!=PAGE_EXECUTE_WRITECOPY)return false;
  auto next=(uintptr_t)m.BaseAddress+m.RegionSize;if(next<=p)return false;p=next;}
 return true;
}
inline bool MatchManager(uintptr_t p,uintptr_t font,const unsigned* header){
 unsigned d[17]={};if(!Read(p,d,sizeof(d))||d[0]!=1||d[10]!=(header[1]&0x7c000000)||d[11]!=header[2]||d[12]==0||d[12]>65536||d[13]==0||d[13]>256)return false;
 unsigned index=header[1]&0x3ffff;if(index>=d[12]||!Region(d[1],d[12]*4))return false;
 uintptr_t current=0;return Read(d[1]+index*4,&current,4)&&current==font;
}
inline void Observe(void* value){
 observations++;if(manager.load())return;
 auto font=(uintptr_t)value;unsigned h[5]={};
 if(!Read(font,h,sizeof(h))||h[2]<minimumSize||h[2]>2*1024*1024||h[0]!=(h[1]&0x03fc0000)>>18)return;
 auto base=(unsigned char*)GetModuleHandleW(nullptr);auto dos=(IMAGE_DOS_HEADER*)base;auto nt=(IMAGE_NT_HEADERS32*)(base+dos->e_lfanew);auto sections=IMAGE_FIRST_SECTION(nt);
 uintptr_t found=0;
 for(unsigned s=0;s<nt->FileHeader.NumberOfSections;++s){auto section=sections[s];if(!(section.Characteristics&IMAGE_SCN_MEM_WRITE)||(section.Characteristics&IMAGE_SCN_MEM_EXECUTE))continue;
  if(section.VirtualAddress>=nt->OptionalHeader.SizeOfImage||section.Misc.VirtualSize>nt->OptionalHeader.SizeOfImage-section.VirtualAddress)return;
  for(size_t offset=0;offset+92<section.Misc.VirtualSize;offset+=4){auto p=(uintptr_t)base+section.VirtualAddress+offset;
   unsigned fields[12]={};if(!Read(p,fields,sizeof(fields)))continue;
   if(fields[0]==1&&fields[10]==(h[1]&0x7c000000)&&fields[11]==h[2]&&MatchManager(p,font,h)){if(found)return;found=p;}}
 }
 if(found)manager=found;
}
inline int __cdecl CdeclInit(void* data,int async){int result=((Cdecl)original)(data,async);if(result==0)Observe(data);return result;}
inline int __fastcall FastInit(void* data){int result=((Fast)original)(data);if(result==0)Observe(data);return result;}
inline void* Find(const char* pattern){
 auto wanted=WolfText::ParsePattern(pattern);auto base=(unsigned char*)GetModuleHandleW(nullptr);auto dos=(IMAGE_DOS_HEADER*)base;
 if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<64||dos->e_lfanew>1048576)return nullptr;
 auto nt=(IMAGE_NT_HEADERS32*)(base+dos->e_lfanew);if(nt->Signature!=IMAGE_NT_SIGNATURE||nt->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||nt->FileHeader.NumberOfSections>96)return nullptr;
 auto sections=IMAGE_FIRST_SECTION(nt);void* hit=nullptr;
 for(unsigned s=0;s<nt->FileHeader.NumberOfSections;++s){auto section=sections[s];if(!(section.Characteristics&IMAGE_SCN_MEM_EXECUTE))continue;
  auto size=section.Misc.VirtualSize;if(size<wanted.size()||section.VirtualAddress>=nt->OptionalHeader.SizeOfImage||size>nt->OptionalHeader.SizeOfImage-section.VirtualAddress)return nullptr;
  for(size_t i=0;i+wanted.size()<=size;++i)if(WolfText::Match(base+section.VirtualAddress+i,wanted)){if(hit)return nullptr;hit=base+section.VirtualAddress+i;}}
 return hit;
}
inline bool Install(){
 auto address=Find("56 57 8B F9 32 D2 68 00 00 08 00 8D 8F 88 5C 01 00 E8 ?? ?? ?? ?? 8D 77 7C 32 D2 68 0C 5C 01 00 8B CE E8 ?? ?? ?? ?? 68 00 10 00 00 8D 8F 8C 5C 09 00 32 D2 E8 ?? ?? ?? ?? 33 C9 C7 87 88 5C 09 00 00 00 00 00 83 C4 0C 39 8F 90 6C 09 00 7E 27 89 4E 14 8B C1 99 F7 BF 0C A1 09 00 89 46 18 8B C1 99 41 F7 BF 0C A1 09 00 89 56 1C 83 C6 2C 3B 8F 90 6C 09 00 7C D9 C7 46 14 FF FF FF FF 33 C0 C7 87 8C 6C 09 00 00 00 00 00 5F 5E C3");
 if(address){fast=true;maxOffset=0x96c90;indexOffset=0x96c8c;lengthOffset=0x9a10c;minimumSize=0x9a110;contract="dxlib-cache44-fastcall";}
 else{
  address=Find("56 8B 74 24 08 57 68 00 00 08 00 8D 86 74 5C 01 00 6A 00 50 E8 ?? ?? ?? ?? 68 0C 5C 01 00 8D 7E 68 6A 00 57 E8 ?? ?? ?? ?? 68 00 10 00 00 8D 8E 78 5C 09 00 6A 00 51 E8 ?? ?? ?? ?? 33 C9 83 C4 24 39 8E 7C 6C 09 00 C7 86 74 5C 09 00 00 00 00 00 7E 29 8B C1 99 89 4F 14 F7 BE B8 9C 09 00 83 C7 2C 89 47 EC 8B C1 99 F7 BE B8 9C 09 00 83 C1 01 89 57 F0 3B 8E 7C 6C 09 00 7C D7 C7 47 14 FF FF FF FF 5F C7 86 78 6C 09 00 00 00 00 00 33 C0 5E C3");
  if(address){maxOffset=0x96c7c;indexOffset=0x96c78;lengthOffset=0x99cb8;minimumSize=0x99cbc;contract="dxlib-cache44-cdecl";}
  else{
   address=Find("55 8B EC 56 8B 75 08 57 68 00 00 08 00 6A 00 8D 86 74 5C 01 00 50 E8 ?? ?? ?? ?? 68 0C 5C 01 00 8D 7E 68 6A 00 57 E8 ?? ?? ?? ?? 68 00 10 00 00 8D 86 78 5C 09 00 6A 00 50 E8 ?? ?? ?? ?? 33 C9 C7 86 74 5C 09 00 00 00 00 00 83 C4 24 39 8E 7C 6C 09 00 7E 27 89 4F 14 8B C1 99 F7 BE F8 A0 09 00 89 47 18 8B C1 99 41 F7 BE F8 A0 09 00 89 57 1C 83 C7 2C 3B 8E 7C 6C 09 00 7C D9 C7 47 14 FF FF FF FF 33 C0 5F C7 86 78 6C 09 00 00 00 00 00 5E 5D C3");
   if(address){maxOffset=0x96c7c;indexOffset=0x96c78;lengthOffset=0x9a0f8;minimumSize=0x9a0fc;contract="dxlib-cache44-cdecl-frame";}
  }
 }
 if(!address){
  // Older DxLib cache44 has the same native reset contract and handle table,
  // but FONTMANAGE's cache fields precede the newer cdecl layout by 8 bytes.
  // Match the complete routine, including all clear lengths and loop fields;
  // executable versions, game titles and relocated addresses are not evidence.
  address=Find("56 8B 74 24 08 57 68 00 00 08 00 8D 86 6C 5C 01 00 6A 00 50 E8 ?? ?? ?? ?? 68 0C 5C 01 00 8D 7E 60 6A 00 57 E8 ?? ?? ?? ?? 68 00 10 00 00 8D 8E 70 5C 09 00 6A 00 51 E8 ?? ?? ?? ?? 33 C9 83 C4 24 39 8E 74 6C 09 00 C7 86 6C 5C 09 00 00 00 00 00 7E 29 8B C1 99 89 4F 14 F7 BE B0 9C 09 00 83 C7 2C 89 47 EC 8B C1 99 F7 BE B0 9C 09 00 83 C1 01 89 57 F0 3B 8E 74 6C 09 00 7C D7 C7 47 14 FF FF FF FF 5F C7 86 70 6C 09 00 00 00 00 00 33 C0 5E C3");
  if(address){maxOffset=0x96c74;indexOffset=0x96c70;lengthOffset=0x99cb0;minimumSize=0x99cb4;contract="dxlib-cache44-cdecl-legacy";}
 }
 if(!address)return false;
 if(MH_CreateHook(address,fast?(void*)FastInit:(void*)CdeclInit,&original)!=MH_OK){original=nullptr;contract="hook-unavailable";return false;}
 return true;
}
inline void RenderThread(){DWORD unset=0;renderingThread.compare_exchange_strong(unset,GetCurrentThreadId());}
inline bool Available(){return original&&manager.load()!=0&&renderingThread.load()!=0;}
inline bool Clear(){
 lastStage=1;auto root=manager.load();if(!original||!root||GetCurrentThreadId()!=renderingThread.load())return false;
 // HANDLEMANAGE's stable prefix is anchored by an actual native initialized
 // FONTMANAGE and its owning table. Hold the engine's own critical section;
 // never follow a retired pointer or race an async handle deletion.
 lastStage=2;auto critical=(CRITICAL_SECTION*)(root+68);if(!Region((uintptr_t)critical,sizeof(*critical),true)||!TryEnterCriticalSection(critical))return false;
 struct Unlock{CRITICAL_SECTION* value;~Unlock(){LeaveCriticalSection(value);}} unlock{critical};
 lastStage=3;unsigned d[17]={};if(!Read(root,d,sizeof(d))||d[0]!=1||d[11]<minimumSize||d[11]>2*1024*1024||d[12]>65536||d[13]>256||!Region(d[1],d[12]*4))return false;
 std::vector<uintptr_t> fonts;fonts.reserve(d[13]);
 for(unsigned i=0;i<d[12];++i){lastStage=4;uintptr_t p=0;if(!Read(d[1]+i*4,&p,4))return false;if(!p)continue;
  lastStage=5;unsigned h[5]={},count=0,length=0;if(!Read(p,h,sizeof(h))||h[2]!=d[11]||h[4]!=0||(h[1]&0x3ffff)!=i||(h[1]&0x7c000000)!=d[10]||h[0]!=(h[1]&0x03fc0000)>>18)return false;
  lastStage=6;if(!Region(p,h[2],true))return false;
  lastStage=7;if(!Read(p+maxOffset,&count,4)||!Read(p+lengthOffset,&length,4))return false;lastCacheCount=count;lastCacheLength=length;
  lastStage=8;if(count>2024||length==0||length>2024)return false;
  fonts.push_back(p);if(fonts.size()>256)return false;
 }
 lastStage=9;if(fonts.size()!=d[13])return false;
 for(auto p:fonts){int result=fast?((Fast)original)((void*)p):((Cdecl)original)((void*)p,0);unsigned index=1;
  if(result!=0||!Read(p+indexOffset,&index,4)||index!=0){failures++;return false;}}
 clears+=(unsigned)fonts.size();lastStage=0;return true;
}
inline nlohmann::json State(){return {{"contract",contract},{"available",Available()},{"nativeInitializations",observations.load()},{"nativeCachesCleared",clears.load()},{"nativeClearFailures",failures.load()},{"lastValidationStage",lastStage.load()},{"lastCacheCount",lastCacheCount.load()},{"lastCacheLength",lastCacheLength.load()}};}
}

#pragma once
// Included inside WolfData. CDB only: the engine's mutable numeric database.
// Read-only/user/system databases and string slots are never exposed as writes.
struct DatabaseRow {
 unsigned type,row;Bank values;std::vector<unsigned> fields;
 bool operator==(const DatabaseRow& b)const{return type==b.type&&row==b.row&&values==b.values&&fields==b.fields;}
};
inline uintptr_t DatabaseRoot(){
 auto base=(unsigned char*)GetModuleHandleW(nullptr);auto dos=(IMAGE_DOS_HEADER*)base;
 auto nt=(IMAGE_NT_HEADERS32*)(base+dos->e_lfanew);auto sections=IMAGE_FIRST_SECTION(nt);uintptr_t result=0;
 if(nt->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||nt->FileHeader.NumberOfSections>96)return 0;
 for(unsigned n=0;n<nt->FileHeader.NumberOfSections;n++){
  auto s=sections[n];if(!(s.Characteristics&IMAGE_SCN_MEM_EXECUTE)||s.VirtualAddress+s.Misc.VirtualSize>nt->OptionalHeader.SizeOfImage)continue;
  for(unsigned i=0;i+320<s.Misc.VirtualSize;i++){
   auto p=base+s.VirtualAddress+i;unsigned start=0;
   // cmp variable, 1,200,000,000 (the end of the mutable DB domain).
   if(!memcmp(p,"\x81\xff\x00\x8c\x86\x47",6))start=6;
   else if(!memcmp(p,"\x81\x7d\x08\x00\x8c\x86\x47",7))start=7;
   if(!start)continue;
   bool domain=false,typeScale=false;std::map<uintptr_t,unsigned> objects;
   for(unsigned k=start;k<260;k++){
    if(!memcmp(p+k,"\x00\xab\x90\x41",4))domain=true;
    if(!memcmp(p+k,"\x40\x42\x0f\x00",4))typeScale=true;
    if(p[k]!=0xb9)continue;uintptr_t object=0;memcpy(&object,p+k+1,4);int kind=-1;
    if(object<(uintptr_t)base||object>(uintptr_t)base+nt->OptionalHeader.SizeOfImage-64||!Region(object,64,true)||!Read(object+4,&kind,4)||kind!=1)continue;
    objects[object]++;
   }
   if(!domain||!typeScale)continue;
   for(auto& entry:objects)if(entry.second>=3){if(result&&result!=entry.first)return 0;result=entry.first;}
  }
 }
 return result;
}

#include "Protocol.h"
#include "ProcessIdentity.h"
#include <iostream>
#include <map>
#include <vector>
#include <limits>
#include <functional>

using namespace FusionSpeed;
namespace {
struct Handle {HANDLE value=nullptr;explicit Handle(HANDLE h=nullptr):value(h){}~Handle(){if(value&&value!=INVALID_HANDLE_VALUE)CloseHandle(value);}operator HANDLE()const{return value;}};
struct View {Shared* data=nullptr;~View(){if(data)UnmapViewOfFile(data);}};
uint64_t Parse(const std::wstring& value){if(value.empty()||value.find_first_not_of(L"0123456789")!=value.npos)throw DWORD(BadArguments);size_t end=0;uint64_t n;try{n=std::stoull(value,&end);}catch(...){throw DWORD(BadArguments);}if(!n||end!=value.size())throw DWORD(BadArguments);return n;}
void Remote(HANDLE process,void* entry,const void* bytes,size_t size,bool checkResult,const std::function<void()>& verify){
 void* memory=VirtualAllocEx(process,nullptr,size,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);if(!memory)throw DWORD(LoaderFailure);bool canFree=true;
 try{SIZE_T written=0;if(!WriteProcessMemory(process,memory,bytes,size,&written)||written!=size)throw DWORD(LoaderFailure);
  verify();Handle thread(CreateRemoteThread(process,nullptr,0,(LPTHREAD_START_ROUTINE)entry,memory,0,nullptr));if(!thread.value)throw DWORD(LoaderFailure);canFree=false;
  if(WaitForSingleObject(thread,15000)!=WAIT_OBJECT_0)throw DWORD(RemoteTimeout);canFree=true;DWORD code=0;if(!GetExitCodeThread(thread,&code))throw DWORD(LoaderFailure);if(checkResult&&code!=0)throw code;
 }catch(...){if(canFree)VirtualFreeEx(process,memory,0,MEM_RELEASE);throw;}
 if(canFree)VirtualFreeEx(process,memory,0,MEM_RELEASE);
 // A timed-out remote thread still owns its argument buffer. Never free it,
 // retry automatically, or terminate the already-running target process.
}
void* RemoteLoader(const std::vector<MODULEENTRY32W>& modules){
 MEMORY_BASIC_INFORMATION memory{};if(!VirtualQuery((void*)LoadLibraryW,&memory,sizeof(memory)))return nullptr;
 wchar_t owner[32768];DWORD n=GetModuleFileNameW((HMODULE)memory.AllocationBase,owner,32768);if(!n||n>=32768)return nullptr;
 auto name=BaseName(owner);uintptr_t offset=(uintptr_t)LoadLibraryW-(uintptr_t)memory.AllocationBase;
 for(const auto& module:modules)if(BaseName(module.szModule)==name&&offset<module.modBaseSize)return module.modBaseAddr+offset;return nullptr;
}
// Inspect the already loaded module, not a newer on-disk DLL at the same path.
// A stale component may have a different export RVA and must never be called.
DWORD RemoteExport(HANDLE process,const MODULEENTRY32W& module,const char* wanted){
 auto read=[&](DWORD rva,void* target,size_t size){if(rva>=module.modBaseSize||size>module.modBaseSize-rva)return false;SIZE_T copied=0;return ReadProcessMemory(process,module.modBaseAddr+rva,target,size,&copied)&&copied==size;};
 IMAGE_DOS_HEADER dos{};if(!read(0,&dos,sizeof(dos))||dos.e_magic!=IMAGE_DOS_SIGNATURE||dos.e_lfanew<0)return 0;
 IMAGE_NT_HEADERS nt{};if(!read((DWORD)dos.e_lfanew,&nt,sizeof(nt))||nt.Signature!=IMAGE_NT_SIGNATURE||nt.OptionalHeader.Magic!=(sizeof(void*)==8?IMAGE_NT_OPTIONAL_HDR64_MAGIC:IMAGE_NT_OPTIONAL_HDR32_MAGIC)||nt.OptionalHeader.NumberOfRvaAndSizes<=IMAGE_DIRECTORY_ENTRY_EXPORT)return 0;
 auto directory=nt.OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];IMAGE_EXPORT_DIRECTORY exports{};
 if(!directory.VirtualAddress||!read(directory.VirtualAddress,&exports,sizeof(exports))||!exports.NumberOfNames||exports.NumberOfNames>4096||!exports.NumberOfFunctions||exports.NumberOfFunctions>4096)return 0;
 std::vector<DWORD> names(exports.NumberOfNames),functions(exports.NumberOfFunctions);std::vector<WORD> ordinals(exports.NumberOfNames);
 if(!read(exports.AddressOfNames,names.data(),names.size()*sizeof(DWORD))||!read(exports.AddressOfFunctions,functions.data(),functions.size()*sizeof(DWORD))||!read(exports.AddressOfNameOrdinals,ordinals.data(),ordinals.size()*sizeof(WORD)))return 0;
 for(unsigned i=0;i<names.size();i++){
  char name[96]{};bool valid=false;for(DWORD j=0;j<sizeof(name);j++){if(names[i]>MAXDWORD-j||!read(names[i]+j,&name[j],1))break;if(!name[j]){valid=true;break;}}
  if(!valid||strcmp(name,wanted)!=0||ordinals[i]>=functions.size())continue;DWORD rva=functions[ordinals[i]];
  if(!rva||rva>=module.modBaseSize||(rva>=directory.VirtualAddress&&ULONGLONG(rva)<ULONGLONG(directory.VirtualAddress)+directory.Size))return 0;return rva;
 }
 return 0;
}
}
int wmain(int argc,wchar_t** argv){DWORD error=Success,pid=0;try{
 std::map<std::wstring,std::wstring> args;for(int i=1;i<argc;i+=2){if(i+1>=argc||args.count(argv[i]))throw DWORD(BadArguments);args.emplace(argv[i],argv[i+1]);}
 if(args.size()!=5||!args.count(L"--pid")||!args.count(L"--created")||!args.count(L"--exe")||!args.count(L"--mapping")||!args.count(L"--token"))throw DWORD(BadArguments);
 auto wideToken=args[L"--token"];if(wideToken.size()!=64||wideToken.find_first_not_of(L"0123456789ABCDEF")!=wideToken.npos)throw DWORD(BadArguments);std::string token;for(auto c:wideToken)token.push_back((char)c);auto mapping=args[L"--mapping"];
 uint64_t targetId=Parse(args[L"--pid"]),created=Parse(args[L"--created"]);if(targetId>MAXDWORD||!MappingName(mapping)||!Hex(token))throw DWORD(BadArguments);pid=(DWORD)targetId;
 Handle map(OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,mapping.c_str()));if(!map.value)throw DWORD(BadMapping);View view;view.data=(Shared*)MapViewOfFile(map,FILE_MAP_ALL_ACCESS,0,0,Size);auto data=view.data;
 if(!Header(data)||memcmp(data->token,token.data(),64)!=0||data->targetPid!=pid||data->targetCreated!=created||InterlockedCompareExchange(&data->state,0,0)!=0)throw DWORD(BadMapping);
 Handle owner(OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,data->ownerPid));if(!owner.value||Created(owner)!=data->ownerCreated||WaitForSingleObject(owner,0)!=WAIT_TIMEOUT)throw DWORD(OwnerUnavailable);
 auto now=GetTickCount64(),heartbeat=(ULONGLONG)Read64(&data->heartbeat);if(heartbeat>now+1000||now>heartbeat+LeaseMs)throw DWORD(OwnerUnavailable);
 Handle process(OpenProcess(PROCESS_CREATE_THREAD|PROCESS_QUERY_INFORMATION|PROCESS_VM_OPERATION|PROCESS_VM_WRITE|PROCESS_VM_READ|SYNCHRONIZE,FALSE,pid));if(!process.value)throw DWORD(LoaderFailure);
 auto path=ProcessPath(process),expected=FinalPath(args[L"--exe"]);if(path.empty()||path!=expected||Created(process)!=created||WaitForSingleObject(process,0)!=WAIT_TIMEOUT)throw DWORD(TargetMismatch);
 if(Forbidden(process,pid,data->ownerPid,path))throw DWORD(ForbiddenTarget);if(!SameBitness(process))throw DWORD(WrongArchitecture);
 const uint32_t ownerId=data->ownerPid;const uint64_t ownerCreated=data->ownerCreated;
 auto verify=[&](){auto tick=GetTickCount64(),beat=(ULONGLONG)Read64(&data->heartbeat);
  if(!Header(data)||data->targetPid!=pid||data->targetCreated!=created||data->ownerPid!=ownerId||data->ownerCreated!=ownerCreated||memcmp(data->token,token.data(),64)!=0)throw DWORD(IdentityChanged);
  if(data->desiredMode!=1)throw DWORD(BadCommand);
  if(WaitForSingleObject(owner,0)!=WAIT_TIMEOUT||beat>tick+1000||tick>beat+LeaseMs)throw DWORD(OwnerUnavailable);
  if(Created(process)!=created||WaitForSingleObject(process,0)!=WAIT_TIMEOUT||ProcessPath(process)!=path)throw DWORD(TargetMismatch);
 };
 auto modules=Modules(pid);if(modules.empty())throw DWORD(LoaderFailure);if(HasWolf(modules))throw DWORD(WolfConflict);
 wchar_t host[32768];DWORD length=GetModuleFileNameW(nullptr,host,32768);if(!length||length>=32768)throw DWORD(LoaderFailure);
 std::wstring dll=std::wstring(host,length);dll=dll.substr(0,dll.find_last_of(L'\\')+1)+(sizeof(void*)==8?L"FusionSpeed.x64.dll":L"FusionSpeed.x86.dll");dll=FinalPath(dll);if(dll.empty())throw DWORD(LoaderFailure);
 MODULEENTRY32W loaded{};
 for(const auto& module:modules)if(BaseName(module.szModule).rfind(L"fusionspeed.",0)==0){if(FinalPath(module.szExePath)!=dll)throw DWORD(OtherBridge);loaded=module;}
 if(!loaded.modBaseAddr){void* loader=RemoteLoader(modules);if(!loader)throw DWORD(LoaderFailure);Remote(process,loader,dll.c_str(),(dll.size()+1)*sizeof(wchar_t),false,verify);
  modules=Modules(pid);for(const auto& module:modules)if(FinalPath(module.szExePath)==dll){loaded=module;break;}if(!loaded.modBaseAddr)throw DWORD(LoaderFailure);}
  DWORD versionRva=RemoteExport(process,loaded,"FusionSpeedProtocolVersion"),remoteVersion=0;SIZE_T versionRead=0;
  if(!versionRva||!ReadProcessMemory(process,loaded.modBaseAddr+versionRva,&remoteVersion,sizeof(remoteVersion),&versionRead)||versionRead!=sizeof(remoteVersion)||remoteVersion!=Version)throw DWORD(OldBridge);
  DWORD rva=RemoteExport(process,loaded,"FusionSpeedStart");if(!rva)throw DWORD(OldBridge);
 if(Created(process)!=created||WaitForSingleObject(owner,0)!=WAIT_TIMEOUT||data->targetPid!=pid||data->targetCreated!=created)throw DWORD(TargetMismatch);
 Bootstrap bootstrap{};bootstrap.size=sizeof(bootstrap);bootstrap.version=Version;wcscpy_s(bootstrap.mapping,mapping.c_str());memcpy(bootstrap.token,token.data(),64);
 Remote(process,loaded.modBaseAddr+rva,&bootstrap,sizeof(bootstrap),true,verify);
 for(int i=0;i<100&&InterlockedCompareExchange(&data->state,0,0)==0;i++)Sleep(10);
 LONG state=InterlockedCompareExchange(&data->state,0,0);if(state!=1)throw DWORD(state==3?InterlockedCompareExchange(&data->error,0,0):NativeTimeout);
 }catch(DWORD code){error=code;}catch(...){error=BadArguments;}
 std::cout<<"{\"ok\":"<<(error?"false":"true")<<",\"errorCode\":"<<error<<",\"message\":\""<<Message(error)<<"\",\"pid\":"<<pid<<"}\n";return (int)error;
}

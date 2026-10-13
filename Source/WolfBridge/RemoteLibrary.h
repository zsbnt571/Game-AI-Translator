#pragma once
#include <windows.h>
#include <tlhelp32.h>
#include <string>
// System DLL ASLR addresses are not assumed to be equal in two processes.
inline void* RemoteLoadLibraryAddress(DWORD pid){
    MEMORY_BASIC_INFORMATION memory={};if(!VirtualQuery((void*)LoadLibraryW,&memory,sizeof(memory)))return nullptr;
    wchar_t path[32768];DWORD n=GetModuleFileNameW((HMODULE)memory.AllocationBase,path,32768);if(!n||n>=32768)return nullptr;
    const wchar_t* name=wcsrchr(path,L'\\');name=name?name+1:path;
    uintptr_t offset=(uintptr_t)LoadLibraryW-(uintptr_t)memory.AllocationBase;
    for(int retry=0;retry<8;retry++){
        HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE|TH32CS_SNAPMODULE32,pid);
        if(snapshot==INVALID_HANDLE_VALUE){if(GetLastError()!=ERROR_BAD_LENGTH)return nullptr;Sleep(10);continue;}
        MODULEENTRY32W entry={sizeof(entry)};void* result=nullptr;
        if(Module32FirstW(snapshot,&entry))do{if(_wcsicmp(entry.szModule,name)==0&&offset<entry.modBaseSize){result=entry.modBaseAddr+offset;break;}}while(Module32NextW(snapshot,&entry));
        CloseHandle(snapshot);if(result)return result;Sleep(10);
    }
    return nullptr;
}
// In a newly created suspended process the loader may not yet have populated
// its module list. A no-op thread lets Windows perform normal DLL/thread
// initialization while the game's primary thread remains suspended. Its only
// instructions are `return 1`; no game or system function address is guessed.
inline bool InitializeSuspendedLoader(HANDLE process){
    const unsigned char code[]={0xb8,1,0,0,0,0xc2,4,0};
    void* memory=VirtualAllocEx(process,nullptr,sizeof(code),MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);if(!memory)return false;
    SIZE_T written=0;DWORD protection=0;
    if(!WriteProcessMemory(process,memory,code,sizeof(code),&written)||written!=sizeof(code)||!VirtualProtectEx(process,memory,sizeof(code),PAGE_EXECUTE_READ,&protection)){
        VirtualFreeEx(process,memory,0,MEM_RELEASE);return false;
    }
    FlushInstructionCache(process,memory,sizeof(code));
    HANDLE thread=CreateRemoteThread(process,nullptr,0,(LPTHREAD_START_ROUTINE)memory,nullptr,0,nullptr);
    if(!thread){VirtualFreeEx(process,memory,0,MEM_RELEASE);return false;}
    DWORD exit=0;bool finished=WaitForSingleObject(thread,10000)==WAIT_OBJECT_0;
    if(finished)GetExitCodeThread(thread,&exit);CloseHandle(thread);
    if(finished)VirtualFreeEx(process,memory,0,MEM_RELEASE);
    // On timeout the owning launcher terminates only this suspended child;
    // leave its code mapped until that process is gone.
    return finished&&exit==1;
}

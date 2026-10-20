#include <windows.h>
#include <filesystem>
#include <string>
#include <fstream>
#include "json.hpp"
#include "RemoteLibrary.h"
using nlohmann::json;
namespace fs=std::filesystem;
static std::wstring Env(const wchar_t* key){wchar_t b[32768];DWORD n=GetEnvironmentVariableW(key,b,32768);return n>0&&n<32768?std::wstring(b,n):std::wstring();}
static std::string Ascii(const std::wstring& value){std::string result;for(wchar_t c:value)result.push_back((char)c);return result;}
static bool Publish(const fs::path& path,const json& value){auto tmp=path;tmp+=L".tmp";auto text=value.dump();{std::ofstream f(tmp,std::ios::binary);if(!f.write(text.data(),text.size()))return false;}return MoveFileExW(tmp.c_str(),path.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)!=FALSE;}
static DWORD Remote(HANDLE process,void* entry,void* param){HANDLE thread=CreateRemoteThread(process,nullptr,0,(LPTHREAD_START_ROUTINE)entry,param,0,nullptr);if(!thread)return 0;DWORD code=0;if(WaitForSingleObject(thread,15000)==WAIT_OBJECT_0)GetExitCodeThread(thread,&code);CloseHandle(thread);return code;}
int wmain(int argc,wchar_t** argv){
    if(argc!=3)return 2;
    auto session=Env(L"FUSION_WOLF_SESSION"),token=Env(L"FUSION_WOLF_SECRET");
    if(session.empty()||token.size()!=64||token.find_first_not_of(L"0123456789ABCDEF")!=token.npos)return 2;
    fs::path exe=fs::absolute(argv[1]),dll=fs::absolute(argv[2]);
    PROCESS_INFORMATION pi={};HANDLE job=nullptr;void* remote=nullptr;int error=0;
    try{
        if(!fs::is_regular_file(exe)||!fs::is_regular_file(dll))throw 3;
        std::wstring command=L"\""+exe.wstring()+L"\"";
        STARTUPINFOW si={sizeof(si)};
        // Background acceptance runs use a new, invisible Windows desktop.
        // Never switch the user's input desktop or foreground window. This is
        // enabled only for an explicitly marked, isolated test copy.
        auto testDesktop=Env(L"FUSION_WOLF_TEST_DESKTOP");
        if(fs::exists(exe.parent_path()/L".fusion-test-copy")&&testDesktop.rfind(L"FusionWolfTest_",0)==0&&testDesktop.size()<80&&
           testDesktop.find_first_not_of(L"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_")==testDesktop.npos){
            si.lpDesktop=testDesktop.data();
        }
        if(Env(L"FUSION_WOLF_TEST_HIDDEN")==L"1"&&fs::exists(exe.parent_path()/L".fusion-test-copy")){
            si.dwFlags=STARTF_USESHOWWINDOW;si.wShowWindow=SW_HIDE;
        }
        if(!CreateProcessW(exe.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_SUSPENDED,nullptr,exe.parent_path().c_str(),&si,&pi))throw 4;
        job=CreateJobObjectW(nullptr,nullptr);if(!job)throw 5;
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits={};limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if(!SetInformationJobObject(job,JobObjectExtendedLimitInformation,&limits,sizeof(limits))||!AssignProcessToJobObject(job,pi.hProcess))throw 5;
        size_t bytes=(dll.wstring().size()+1)*sizeof(wchar_t);SIZE_T written=0;
        remote=VirtualAllocEx(pi.hProcess,nullptr,bytes,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);
        if(!remote||!WriteProcessMemory(pi.hProcess,remote,dll.c_str(),bytes,&written)||written!=bytes)throw 6;
        void* loader=RemoteLoadLibraryAddress(pi.dwProcessId);
        if(!loader&&InitializeSuspendedLoader(pi.hProcess))loader=RemoteLoadLibraryAddress(pi.dwProcessId);
        if(!loader)throw 7;
        DWORD remoteModule=Remote(pi.hProcess,loader,remote);if(!remoteModule)throw 7;
        VirtualFreeEx(pi.hProcess,remote,0,MEM_RELEASE);remote=nullptr;
        // Resolve our own export by RVA; never call a local pointer in the peer.
        HMODULE local=LoadLibraryExW(dll.c_str(),nullptr,DONT_RESOLVE_DLL_REFERENCES);if(!local)throw 8;
        auto symbol=GetProcAddress(local,"FusionWolfStart");uintptr_t offset=symbol?(uintptr_t)symbol-(uintptr_t)local:0;FreeLibrary(local);
        if(!offset||Remote(pi.hProcess,(void*)(remoteModule+offset),nullptr)!=1)throw 9;
        if(ResumeThread(pi.hThread)==(DWORD)-1)throw 10;
        if(!Publish(fs::path(session)/L"launch.json",{{"secret",Ascii(token)},{"pid",pi.dwProcessId},{"ok",true}}))throw 11;
        WaitForSingleObject(pi.hProcess,INFINITE);DWORD code=0;GetExitCodeProcess(pi.hProcess,&code);
        CloseHandle(pi.hThread);CloseHandle(pi.hProcess);CloseHandle(job);return 0;
    }catch(int code){error=code;}catch(...){error=12;}
    if(pi.hProcess){TerminateProcess(pi.hProcess,error);WaitForSingleObject(pi.hProcess,5000);if(remote)VirtualFreeEx(pi.hProcess,remote,0,MEM_RELEASE);CloseHandle(pi.hProcess);}
    if(pi.hThread)CloseHandle(pi.hThread);if(job)CloseHandle(job);
    Publish(fs::path(session)/L"launch.json",{{"secret",Ascii(token)},{"ok",false},{"error",error}});return error;
}

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>

// Independent RGSS API shim. The original runtime, scripts and archive remain
// in use. Only the owned Ruby display bridge is evaluated before GameMain.
static HMODULE selfModule=nullptr,engine=nullptr;
static INIT_ONCE once=INIT_ONCE_STATIC_INIT;
static FARPROC functions[32]{};
static const char* names[]={"RGSSAddRTPPath","RGSSAudioFinalize","RGSSAudioInitialize","RGSSClearRTPPath","RGSSErrorMessage","RGSSErrorType","RGSSEval","RGSSFinalize","RGSSGC","RGSSGameMain","RGSSGetBool","RGSSGetDouble","RGSSGetInt","RGSSGetPathWithRTP","RGSSGetRTPPath","RGSSGetStringACP","RGSSGetStringUTF16","RGSSGetStringUTF8","RGSSGetSymbol","RGSSGetTable","RGSSInitialize","RGSSInitialize2","RGSSInitialize3","RGSSSetString","RGSSSetStringACP","RGSSSetStringUTF16","RGSSSetStringUTF8","RGSSSetupFonts","RGSSSetupRTP"};
static BOOL CALLBACK Initialize(PINIT_ONCE,PVOID,PVOID*) {
 wchar_t module[32768]{};DWORD length=GetModuleFileNameW(selfModule,module,32768);if(!length||length>=32768)return FALSE;
 std::wstring own(module,length);auto slash=own.find_last_of(L"\\/");if(slash==std::wstring::npos)return FALSE;std::wstring folder=own.substr(0,slash),ini=folder+L"\\Original.ini";
 slash=folder.find_last_of(L"\\/");if(slash==std::wstring::npos)return FALSE;std::wstring root=folder.substr(0,slash+1);
 wchar_t library[4096]{};if(!GetPrivateProfileStringW(L"Game",L"Library",L"",library,4096,ini.c_str()))return FALSE;
 std::wstring relative(library);if(relative.empty()||relative[0]==L'/'||relative[0]==L'\\'||relative.find(L':')!=std::wstring::npos)return FALSE;
 wchar_t full[32768]{};DWORD n=GetFullPathNameW((root+relative).c_str(),32768,full,nullptr);if(!n||n>=32768||_wcsnicmp(full,root.c_str(),root.size())||!_wcsicmp(full,own.c_str()))return FALSE;
 engine=LoadLibraryExW(full,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);if(!engine)return FALSE;
 for(unsigned i=0;i<sizeof(names)/sizeof(names[0]);++i)functions[i]=GetProcAddress(engine,names[i]);
 return functions[6]&&functions[9]&&functions[28];
}
static void __cdecl Ensure(unsigned index) {
 if(!InitOnceExecuteOnce(&once,Initialize,nullptr,nullptr)||!functions[index]) {
  MessageBoxW(nullptr,L"The original RGSS runtime could not be loaded. Restore the Fusion component and check the game files.",L"Fusion RGSS",MB_OK|MB_ICONERROR);
  ExitProcess(3);
 }
}
#define FORWARD(name,index) extern "C" __declspec(naked) void name() { __asm { pushfd } __asm { pushad } __asm { push index } __asm { call Ensure } __asm { add esp,4 } __asm { popad } __asm { popfd } __asm { jmp dword ptr [functions+index*4] } }
FORWARD(RGSSAddRTPPath,0) FORWARD(RGSSAudioFinalize,1) FORWARD(RGSSAudioInitialize,2) FORWARD(RGSSClearRTPPath,3)
FORWARD(RGSSErrorMessage,4) FORWARD(RGSSErrorType,5) FORWARD(RGSSEval,6) FORWARD(RGSSFinalize,7) FORWARD(RGSSGC,8)
FORWARD(RGSSGetBool,10) FORWARD(RGSSGetDouble,11) FORWARD(RGSSGetInt,12) FORWARD(RGSSGetPathWithRTP,13) FORWARD(RGSSGetRTPPath,14)
FORWARD(RGSSGetStringACP,15) FORWARD(RGSSGetStringUTF16,16) FORWARD(RGSSGetStringUTF8,17) FORWARD(RGSSGetSymbol,18) FORWARD(RGSSGetTable,19)
FORWARD(RGSSSetString,23) FORWARD(RGSSSetStringACP,24) FORWARD(RGSSSetStringUTF16,25) FORWARD(RGSSSetStringUTF8,26) FORWARD(RGSSSetupFonts,27) FORWARD(RGSSSetupRTP,28)
extern "C" void RGSSInitialize(HMODULE){Ensure(20);reinterpret_cast<void(__cdecl*)(HMODULE)>(functions[20])(engine);}
extern "C" void RGSSInitialize2(HMODULE){Ensure(21);reinterpret_cast<void(__cdecl*)(HMODULE)>(functions[21])(engine);}
extern "C" void RGSSInitialize3(HMODULE){Ensure(22);reinterpret_cast<void(__cdecl*)(HMODULE)>(functions[22])(engine);}
static bool ReadOwnedBridge(std::string& source) {
 wchar_t module[32768]{};DWORD length=GetModuleFileNameW(selfModule,module,32768);
 if(!length||length>=32768)return false;
 std::wstring path(module,length);auto slash=path.find_last_of(L"\\/");if(slash==std::wstring::npos)return false;
 path=path.substr(0,slash+1)+L"bridge.rb";
 HANDLE input=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
 if(input==INVALID_HANDLE_VALUE)return false;
 LARGE_INTEGER size{};bool ok=GetFileSizeEx(input,&size)&&size.QuadPart>0&&size.QuadPart<=256*1024;
 if(ok){source.resize(static_cast<size_t>(size.QuadPart));DWORD read=0;ok=ReadFile(input,&source[0],static_cast<DWORD>(source.size()),&read,nullptr)&&read==source.size();}
 CloseHandle(input);
 // This file is the installer's owned, hash-checked ASCII bridge. Never accept
 // command text or an environment-provided script path as executable source.
 return ok&&source.find('\0')==std::string::npos;
}
static void BootstrapFailure(const wchar_t* session,const char* text) {
 std::wstring path(session);if(path.empty()||path.size()>32000)return;
 path+=L"\\bridge-error.txt";
 HANDLE output=CreateFileW(path.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
 if(output==INVALID_HANDLE_VALUE)return;
 DWORD written=0;WriteFile(output,text,static_cast<DWORD>(strlen(text)),&written,nullptr);CloseHandle(output);
}
extern "C" void RGSSGameMain(HWND window,const void* scripts,void** archives) {
 Ensure(9);
 wchar_t session[32768]{},secret[65]{};
 if(GetEnvironmentVariableW(L"FUSION_RGSS_SESSION",session,32768)>0&&GetEnvironmentVariableW(L"FUSION_RGSS_SECRET",secret,65)==64) {
  // Native RGSS overrides Ruby's load with its resource reader, which is not
  // ready before GameMain. Read the installer's fixed sibling file directly;
  // RGSSEval remains the same engine-owned entrypoint used for bootstrapping.
  std::string source;
  if(!ReadOwnedBridge(source))BootstrapFailure(session,"RGSS owned bridge source could not be read safely.");
  else if(reinterpret_cast<int(__cdecl*)(const char*)>(functions[6])(source.c_str())!=0)
    BootstrapFailure(session,"RGSS owned bridge source could not be initialized.");
 }
 reinterpret_cast<void(__cdecl*)(HWND,const void*,void**)>(functions[9])(window,scripts,archives);
}
BOOL WINAPI DllMain(HMODULE module,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH){selfModule=module;DisableThreadLibraryCalls(module);}return TRUE;}

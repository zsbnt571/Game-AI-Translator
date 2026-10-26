#pragma once
#include "Protocol.h"
#include <tlhelp32.h>
#include <vector>
#include <algorithm>
#include <cwctype>

namespace FusionSpeed {
inline std::wstring Lower(std::wstring text){std::transform(text.begin(),text.end(),text.begin(),[](wchar_t c){return (wchar_t)towlower(c);});return text;}
inline std::wstring FinalPath(const std::wstring& path){HANDLE file=CreateFileW(path.c_str(),FILE_READ_ATTRIBUTES,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_FLAG_BACKUP_SEMANTICS,nullptr);if(file==INVALID_HANDLE_VALUE)return {};std::vector<wchar_t> buffer(32768);DWORD length=GetFinalPathNameByHandleW(file,buffer.data(),(DWORD)buffer.size(),FILE_NAME_NORMALIZED|VOLUME_NAME_DOS);CloseHandle(file);if(!length||length>=buffer.size())return {};std::wstring result(buffer.data(),length);if(result.rfind(L"\\\\?\\UNC\\",0)==0)result=L"\\\\"+result.substr(8);else if(result.rfind(L"\\\\?\\",0)==0)result=result.substr(4);return Lower(result);}
inline std::wstring ProcessPath(HANDLE process){std::vector<wchar_t> path(32768);DWORD size=(DWORD)path.size();return QueryFullProcessImageNameW(process,0,path.data(),&size)?FinalPath(std::wstring(path.data(),size)):std::wstring();}
inline std::wstring BaseName(const std::wstring& path){auto at=path.find_last_of(L"\\/");return Lower(at==path.npos?path:path.substr(at+1));}
inline bool Under(const std::wstring& path,const std::wstring& root){return !root.empty()&&path.size()>root.size()&&path.compare(0,root.size(),root)==0&&path[root.size()]==L'\\';}
inline std::vector<MODULEENTRY32W> Modules(DWORD pid){for(int retry=0;retry<12;retry++){HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPMODULE|TH32CS_SNAPMODULE32,pid);if(snapshot==INVALID_HANDLE_VALUE){if(GetLastError()!=ERROR_BAD_LENGTH)return {};Sleep(10);continue;}std::vector<MODULEENTRY32W> result;MODULEENTRY32W entry={sizeof(entry)};if(Module32FirstW(snapshot,&entry))do{result.push_back(entry);}while(result.size()<4096&&Module32NextW(snapshot,&entry));CloseHandle(snapshot);return result;}return {};}
inline bool HasWolf(const std::vector<MODULEENTRY32W>& modules){for(const auto& module:modules){auto name=BaseName(module.szModule);if(name==L"fusionwolf.dll"||name.rfind(L"fusionwolf.",0)==0)return true;}return false;}
inline bool SameBitness(HANDLE target){using Probe=BOOL(WINAPI*)(HANDLE,USHORT*,USHORT*);auto probe=(Probe)GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"IsWow64Process2");if(probe){USHORT machine=0,native=0;if(!probe(target,&machine,&native))return false;return (machine?machine:native)==(sizeof(void*)==8?IMAGE_FILE_MACHINE_AMD64:IMAGE_FILE_MACHINE_I386);}BOOL selfWow=FALSE,targetWow=FALSE;SYSTEM_INFO info{};GetNativeSystemInfo(&info);return (info.wProcessorArchitecture==PROCESSOR_ARCHITECTURE_AMD64||info.wProcessorArchitecture==PROCESSOR_ARCHITECTURE_INTEL)&&IsWow64Process(GetCurrentProcess(),&selfWow)&&IsWow64Process(target,&targetWow)&&selfWow==targetWow;}
inline bool Forbidden(HANDLE process,DWORD pid,DWORD owner,const std::wstring& path,bool currentIsTarget=false){if(pid<=4||pid==owner||(!currentIsTarget&&pid==GetCurrentProcessId())||path.empty())return true;wchar_t windows[32768];UINT n=GetWindowsDirectoryW(windows,32768);auto root=n&&n<32768?FinalPath(windows):L"";if(Under(path,root))return true;auto name=BaseName(path);return name==L"gametranslator.exe"||name==L"codex.exe"||name==L"explorer.exe"||name==L"cmd.exe"||name==L"powershell.exe"||name==L"pwsh.exe"||name.rfind(L"fusionspeedhost.",0)==0;}
inline bool EngineModule(const std::wstring& name){
 if(name.rfind(L"fusion",0)==0)return false;
 if(name==L"unityplayer.dll"||name==L"gameassembly.dll"||name==L"libcef.dll"||name==L"nw.dll"||name==L"node.dll"||name==L"libgodot.dll"||name==L"godot.dll")return true;
 for(auto prefix:{L"rgss",L"python",L"sdl",L"libpython",L"libcocos",L"cocos2d",L"love",L"gameengine",L"ue4-",L"unrealengine-"})if(name.rfind(prefix,0)==0&&name.size()>4&&name.substr(name.size()-4)==L".dll")return true;
 return name==L"engine.dll"||name==L"core.dll"||name==L"coreuobject.dll";
}
}

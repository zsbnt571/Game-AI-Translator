#pragma once
// Engineering-only input driver for an explicitly marked game copy on a private
// desktop. It changes only what this game process reads; it never submits OS
// input, moves the pointer, switches desktops, or changes actual foreground.
// Production builds contain none of these declarations or hooks.
#ifdef FUSION_TEST_CAPTURE
#include <windows.h>
#include <array>
#include <atomic>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include "MinHook.h"
#include "json.hpp"

namespace WolfTestInput {
using nlohmann::json;
static std::wstring desktopName;
static std::atomic<bool> installed{false};
static std::array<std::atomic<ULONGLONG>,256> expires{};
static std::array<std::atomic<bool>,256> pressed{};
static std::atomic<unsigned long long> keyMessages{0},keyboardReads{0},asyncReads{0},focusReads{0};
static std::atomic<unsigned long long> directReads{0},activeDirectReads{0};
static std::atomic<unsigned long long> pointerReads{0},mouseReads{0},pointerMessages{0};
static std::atomic<unsigned long long> mouseDirectReads{0},activeMouseDirectReads{0};
static std::atomic<int> pointerX{0},pointerY{0};
static std::atomic<bool> pointerSet{false};
static std::atomic<ULONGLONG> mouseExpires{0};
static void** keyboardSlot=nullptr;
static void* keyboardDevice=nullptr;
static void* keyboardStateTarget=nullptr;
static void* mouseDevice=nullptr;
using DeviceState=HRESULT(WINAPI*)(void*,DWORD,LPVOID);
static DeviceState originalDeviceState=nullptr;
static DeviceState originalMouseState=nullptr;
static decltype(&GetKeyboardState) originalKeyboard=nullptr;
static decltype(&GetAsyncKeyState) originalAsync=nullptr;
static decltype(&GetForegroundWindow) originalForeground=nullptr;
static decltype(&GetActiveWindow) originalActive=nullptr;
static decltype(&GetFocus) originalFocus=nullptr;
static decltype(&GetCursorPos) originalCursor=nullptr;
static decltype(&GetKeyState) originalKeyState=nullptr;

static std::wstring Environment(const wchar_t* name){
    wchar_t text[32768];DWORD size=GetEnvironmentVariableW(name,text,32768);
    return size>0&&size<32768?std::wstring(text,size):std::wstring();
}
static bool CurrentDesktop(){
    if(desktopName.empty())return false;
    wchar_t name[128];DWORD needed=0;HDESK desktop=GetThreadDesktop(GetCurrentThreadId());
    return desktop&&GetUserObjectInformationW(desktop,UOI_NAME,name,sizeof(name),&needed)&&desktopName==name;
}
static bool Allowed(unsigned vk){
    switch(vk){case VK_RETURN:case VK_SPACE:case 'Z':case 'X':case VK_ESCAPE:case VK_UP:case VK_DOWN:case VK_LEFT:case VK_RIGHT:case VK_SNAPSHOT:return true;default:return false;}
}
struct WindowSearch {HWND window=nullptr;long long area=-1;};
static BOOL CALLBACK CollectWindow(HWND window,LPARAM parameter){
    auto& found=*(WindowSearch*)parameter;DWORD pid=0;GetWindowThreadProcessId(window,&pid);
    RECT bounds{};if(pid!=GetCurrentProcessId()||!IsWindowVisible(window)||!GetClientRect(window,&bounds))return TRUE;
    long long area=(long long)(bounds.right-bounds.left)*(bounds.bottom-bounds.top);
    if(area>found.area){found.window=window;found.area=area;}return TRUE;
}
static HWND OwnedWindow(){
    WindowSearch result;
    if(!CurrentDesktop())return nullptr;
    EnumDesktopWindows(GetThreadDesktop(GetCurrentThreadId()),CollectWindow,(LPARAM)&result);
    return result.window;
}
static bool Active(unsigned vk){return vk<256&&Allowed(vk)&&GetTickCount64()<expires[vk].load(std::memory_order_relaxed);}
static bool MouseActive(){return GetTickCount64()<mouseExpires.load(std::memory_order_relaxed);}
static unsigned ScanCode(unsigned vk){
    switch(vk){case VK_RETURN:return 0x1c;case VK_SPACE:return 0x39;case 'Z':return 0x2c;case 'X':return 0x2d;case VK_ESCAPE:return 1;
    case VK_UP:return 0xc8;case VK_DOWN:return 0xd0;case VK_LEFT:return 0xcb;case VK_RIGHT:return 0xcd;case VK_SNAPSHOT:return 0xb7;default:return 0;}
}
static bool ReadDevice(void*** slot,void** device,void** target){
    __try{
        if(!*slot)return false;void* value=**slot;if(!value)return false;
        void** table=*(void***)value;if(!table||!table[9])return false;
        *device=value;*target=table[9];return true;
    }__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
static bool MouseResult(void* self,DWORD size,LPVOID buffer){
    if(installed.load()&&CurrentDesktop()&&mouseDevice&&self==mouseDevice&&size==20&&buffer){
        std::memset(buffer,0,20);bool active=MouseActive();if(active)((BYTE*)buffer)[12]=0x80;
        mouseDirectReads.fetch_add(1,std::memory_order_relaxed);if(active)activeMouseDirectReads.fetch_add(1,std::memory_order_relaxed);return true;
    }
    return false;
}
static HRESULT WINAPI DirectMouseState(void* self,DWORD size,LPVOID buffer){
    if(MouseResult(self,size,buffer))return S_OK;
    return originalMouseState(self,size,buffer);
}
static HRESULT WINAPI DirectState(void* self,DWORD size,LPVOID buffer){
    if(MouseResult(self,size,buffer))return S_OK;
    if(installed.load()&&CurrentDesktop()&&self==keyboardDevice&&size==256&&buffer){
        std::memset(buffer,0,256);bool active=false;
        for(unsigned vk=0;vk<256;vk++)if(Active(vk)){((BYTE*)buffer)[ScanCode(vk)]=0x80;active=true;}
        directReads.fetch_add(1,std::memory_order_relaxed);if(active)activeDirectReads.fetch_add(1,std::memory_order_relaxed);
        return S_OK;
    }
    return originalDeviceState(self,size,buffer);
}
static bool ResolveDevice(const int* pattern,size_t length,void*** outSlot,void** outDevice,void** outTarget,unsigned slotOffset=1){
    auto base=(BYTE*)GetModuleHandleW(nullptr);auto dos=(IMAGE_DOS_HEADER*)base;
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<64||dos->e_lfanew>1024*1024)return false;
    auto nt=(IMAGE_NT_HEADERS32*)(base+dos->e_lfanew);
    if(nt->Signature!=IMAGE_NT_SIGNATURE||nt->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||nt->OptionalHeader.SizeOfImage>128*1024*1024||nt->FileHeader.NumberOfSections>96)return false;
    auto sections=IMAGE_FIRST_SECTION(nt);BYTE* found=nullptr;
    for(unsigned section=0;section<nt->FileHeader.NumberOfSections;section++){
        auto& s=sections[section];size_t begin=s.VirtualAddress,size=s.Misc.VirtualSize;
        if(!(s.Characteristics&IMAGE_SCN_MEM_EXECUTE)||begin>=nt->OptionalHeader.SizeOfImage||size>nt->OptionalHeader.SizeOfImage-begin||size<length)continue;
        for(size_t offset=0;offset+length<=size;offset++){
            BYTE* at=base+begin+offset;if(*at!=pattern[0])continue;bool match=true;
            for(size_t i=1;i<length;i++)if(pattern[i]>=0&&at[i]!=(BYTE)pattern[i]){match=false;break;}
            if(match){if(found)return false;found=at;}
        }
    }
    if(!found)return false;auto slot=*(void***)(found+slotOffset);uintptr_t address=(uintptr_t)slot;
    if(address<(uintptr_t)base||address>(uintptr_t)base+nt->OptionalHeader.SizeOfImage-sizeof(void*))return false;
    bool writable=false;for(unsigned i=0;i<nt->FileHeader.NumberOfSections;i++){auto& s=sections[i];if((s.Characteristics&IMAGE_SCN_MEM_WRITE)&&address>=(uintptr_t)base+s.VirtualAddress&&address+sizeof(void*)<=(uintptr_t)base+s.VirtualAddress+s.Misc.VirtualSize)writable=true;}
    if(!writable)return false;void* device=nullptr;void* target=nullptr;
    if(!ReadDevice(&slot,&device,&target))return false;
    MEMORY_BASIC_INFORMATION memory{};if(!VirtualQuery(target,&memory,sizeof(memory))||memory.State!=MEM_COMMIT||
       !(memory.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY)))return false;
    HMODULE library=nullptr;wchar_t libraryPath[32768];if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,(LPCWSTR)target,&library)||!GetModuleFileNameW(library,libraryPath,32768))return false;
    auto filename=std::filesystem::path(libraryPath).filename().wstring();
    if(_wcsicmp(filename.c_str(),L"dinput8.dll")&&_wcsicmp(filename.c_str(),L"dinput.dll"))return false;
    *outSlot=slot;*outDevice=device;*outTarget=target;return true;
}
static bool EnsureDirectInput(){
    if(originalDeviceState)return true;
    // Unique verified keyboard poll: 256-byte DIK state and vtable +0x24.
    const int pattern[]={0xa1,-1,-1,-1,-1,0x8b,0x10,0x68,-1,-1,-1,-1,0x68,0x00,0x01,0x00,0x00,0x50,0x8b,0x42,0x24,0xff,0xd0,0x85,0xc0};
    // WOLF 3.100 / newer compiler uses call [ecx+0x24] directly. The final
    // 74 30 distinguishes the first poll from the retry, and was checked in
    // the isolated Rignetta binary (slot 0xB12298, first call 0x54F525).
    const int pattern31[]={0xa1,-1,-1,-1,-1,0x68,-1,-1,-1,-1,0x68,0x00,0x01,0x00,0x00,0x50,0x8b,0x08,0xff,0x51,0x24,0x85,0xc0,0x74,0x30};
    const int pattern37[]={0x8b,0x0d,-1,-1,-1,-1,0x68,-1,-1,-1,-1,0x68,0,1,0,0,0x51,0x8b,0x01,0xff,0x50,0x24,0x85,0xc0,0x74,0x31};
    void** slot=nullptr;void* device=nullptr;void* target=nullptr;
    if(!ResolveDevice(pattern,sizeof(pattern)/sizeof(pattern[0]),&slot,&device,&target)&&
       !ResolveDevice(pattern31,sizeof(pattern31)/sizeof(pattern31[0]),&slot,&device,&target)&&
       !ResolveDevice(pattern37,sizeof(pattern37)/sizeof(pattern37[0]),&slot,&device,&target,2))return false;
    if(MH_CreateHook(target,(void*)DirectState,(void**)&originalDeviceState)!=MH_OK)return false;
    keyboardSlot=slot;keyboardDevice=device;keyboardStateTarget=target;
    if(MH_EnableHook(target)!=MH_OK){originalDeviceState=nullptr;keyboardDevice=nullptr;return false;}
    return true;
}
static bool EnsureMouseInput(){
    if(mouseDevice)return true;
    // Unique first mouse poll (retry has a different final branch): the
    // separately verified device returns a 20-byte DIMOUSESTATE2, button[0]
    // at offset 12. Never reinterpret the keyboard object as a mouse.
    const int pattern[]={0xa1,-1,-1,-1,-1,0x8b,0x08,0x8d,0x14,0x24,0x52,0x6a,0x14,0x50,0x8b,0x41,0x24,0xff,0xd0,0x85,0xc0,0x74,0x31};
    void** slot=nullptr;void* device=nullptr;void* target=nullptr;
    if(!ResolveDevice(pattern,sizeof(pattern)/sizeof(pattern[0]),&slot,&device,&target))return false;
    if(originalDeviceState&&target==keyboardStateTarget){mouseDevice=device;return true;}
    if(MH_CreateHook(target,(void*)DirectMouseState,(void**)&originalMouseState)!=MH_OK)return false;
    mouseDevice=device;
    if(MH_EnableHook(target)!=MH_OK){originalMouseState=nullptr;mouseDevice=nullptr;return false;}
    return true;
}
static BOOL WINAPI Keyboard(PBYTE state){
    if(!installed.load()||!CurrentDesktop())return originalKeyboard(state);
    if(!state)return FALSE;
    keyboardReads.fetch_add(1,std::memory_order_relaxed);
    // Suppress physical keyboard state in this test process. All non-fixture
    // keys stay released so the user's normal typing cannot advance the copy.
    std::memset(state,0,256);
    for(unsigned vk=0;vk<256;vk++)if(Active(vk))state[vk]=0x80;
    if(MouseActive()){state[VK_LBUTTON]=0x80;mouseReads.fetch_add(1,std::memory_order_relaxed);}
    return TRUE;
}
static SHORT WINAPI Async(int vk){
    if(!installed.load()||!CurrentDesktop())return originalAsync(vk);
    asyncReads.fetch_add(1,std::memory_order_relaxed);
    if(vk==VK_LBUTTON){mouseReads.fetch_add(1,std::memory_order_relaxed);return MouseActive()?(SHORT)0x8000:0;}
    if(vk<0||vk>=256||!Active((unsigned)vk))return 0;
    return (SHORT)(0x8000|(pressed[(unsigned)vk].exchange(false)?1:0));
}
static SHORT WINAPI KeyState(int vk){
    if(!installed.load()||!CurrentDesktop())return originalKeyState(vk);
    if(vk==VK_LBUTTON){mouseReads.fetch_add(1,std::memory_order_relaxed);return MouseActive()?(SHORT)0x8000:0;}
    return vk>=0&&vk<256&&Active((unsigned)vk)?(SHORT)0x8000:0;
}
static BOOL WINAPI Cursor(LPPOINT point){
    if(installed.load()&&CurrentDesktop()&&pointerSet.load()&&point){
        HWND window=OwnedWindow();if(window){POINT owned={pointerX.load(),pointerY.load()};if(ClientToScreen(window,&owned)){*point=owned;pointerReads.fetch_add(1,std::memory_order_relaxed);return TRUE;}}
    }
    return originalCursor(point);
}
static HWND WINAPI Foreground(){
    if(installed.load()&&CurrentDesktop()){auto own=OwnedWindow();if(own){focusReads.fetch_add(1,std::memory_order_relaxed);return own;}}
    return originalForeground();
}
static HWND WINAPI ActiveWindow(){
    if(installed.load()&&CurrentDesktop()){auto own=OwnedWindow();if(own){focusReads.fetch_add(1,std::memory_order_relaxed);return own;}}
    return originalActive();
}
static HWND WINAPI Focus(){
    if(installed.load()&&CurrentDesktop()){auto own=OwnedWindow();if(own){focusReads.fetch_add(1,std::memory_order_relaxed);return own;}}
    return originalFocus();
}
static bool Install(){
    if(Environment(L"FUSION_WOLF_TEST_INPUT")!=L"1")return false;
    desktopName=Environment(L"FUSION_WOLF_TEST_DESKTOP");
    if(desktopName.rfind(L"FusionWolfTest_",0)!=0||desktopName.size()>=80||
       desktopName.find_first_not_of(L"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_")!=desktopName.npos||!CurrentDesktop())return false;
    wchar_t path[32768];DWORD length=GetModuleFileNameW(nullptr,path,32768);if(!length||length>=32768)return false;
    auto marker=std::filesystem::path(path).parent_path()/L".fusion-test-copy";
    DWORD attributes=GetFileAttributesW(marker.c_str());if(attributes==INVALID_FILE_ATTRIBUTES||(attributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT)))return false;
    if(std::filesystem::file_size(marker)>128)return false;
    std::ifstream input(marker,std::ios::binary);std::string text((std::istreambuf_iterator<char>(input)),std::istreambuf_iterator<char>());
    if(text!="FusionTranslationTestCopy/1"&&text!="FusionTranslationTestCopy/1\n"&&text!="FusionTranslationTestCopy/1\r\n")return false;
    if(MH_CreateHook((void*)GetKeyboardState,(void*)Keyboard,(void**)&originalKeyboard)!=MH_OK||
       MH_CreateHook((void*)GetAsyncKeyState,(void*)Async,(void**)&originalAsync)!=MH_OK||
       MH_CreateHook((void*)GetForegroundWindow,(void*)Foreground,(void**)&originalForeground)!=MH_OK||
       MH_CreateHook((void*)GetActiveWindow,(void*)ActiveWindow,(void**)&originalActive)!=MH_OK||
       MH_CreateHook((void*)GetFocus,(void*)Focus,(void**)&originalFocus)!=MH_OK||
       MH_CreateHook((void*)GetCursorPos,(void*)Cursor,(void**)&originalCursor)!=MH_OK||
       MH_CreateHook((void*)GetKeyState,(void*)KeyState,(void**)&originalKeyState)!=MH_OK)return false;
    // The caller enables all hooks only after the normal render hooks succeed.
    installed.store(true);return true;
}
static json Diagnostics(){
    unsigned active=0;for(unsigned vk=0;vk<256;vk++)if(Active(vk))active++;
    return {{"installed",installed.load()},{"privateDesktop",CurrentDesktop()},{"ownedWindow",(uintptr_t)OwnedWindow()},
        {"activeKeys",active},{"scheduledKeys",keyMessages.load()},{"keyboardReads",keyboardReads.load()},
        {"asyncReads",asyncReads.load()},{"focusReads",focusReads.load()},
        {"directInputHook",originalDeviceState!=nullptr},{"directInputReads",directReads.load()},{"activeDirectInputReads",activeDirectReads.load()},
        {"pointerSet",pointerSet.load()},{"pointerX",pointerX.load()},{"pointerY",pointerY.load()},{"pointerReads",pointerReads.load()},
        {"mouseReads",mouseReads.load()},{"pointerMessages",pointerMessages.load()},{"leftButtonActive",MouseActive()},
        {"mouseDirectInputHook",mouseDevice!=nullptr},{"mouseDirectReads",mouseDirectReads.load()},{"activeMouseDirectReads",activeMouseDirectReads.load()},
        {"physicalInputUsed",false},{"inputDesktopSwitched",false},{"actualForegroundChanged",false},
        {"inputSemantics","test-only process keyboard-state fixture"}};
}
static json Request(const json& request,bool& ok){
    if(!installed.load()||!CurrentDesktop()||!OwnedWindow()){ok=false;return {{"message","Private owned test window is unavailable."}};}
    if(request.value("releaseAll",false)){
        for(unsigned vk=0;vk<256;vk++){expires[vk].store(0);pressed[vk].store(false);}
        mouseExpires.store(0);
        return Diagnostics();
    }
    if(request.value("op","")=="testPointer"){
        if(!request.contains("x")||!request["x"].is_number_integer()||!request.contains("y")||!request["y"].is_number_integer()||!request.contains("holdMs")||!request["holdMs"].is_number_integer()){
            ok=false;return {{"message","Expected owned-client integer x, y, and bounded holdMs."}};
        }
        int x=request["x"].get<int>(),y=request["y"].get<int>(),hold=request["holdMs"].get<int>();RECT client{};HWND window=OwnedWindow();
        if(!window||!GetClientRect(window,&client)||x<0||y<0||x>=client.right||y>=client.bottom||hold<20||hold>1000){ok=false;return {{"message","Pointer fixture must stay inside the owned game client for 20 to 1000 milliseconds."}};}
        EnsureDirectInput();bool mouse=EnsureMouseInput();
        pointerX.store(x);pointerY.store(y);pointerSet.store(true);mouseExpires.store(GetTickCount64()+(ULONGLONG)hold);pointerMessages.fetch_add(1,std::memory_order_relaxed);
        auto result=Diagnostics();result["holdMs"]=hold;result["osPointerMoved"]=false;result["mouseDirectInputHookAttempt"]=mouse;return result;
    }
    if(!request.contains("vk")||!request["vk"].is_number_integer()||!request.contains("holdMs")||!request["holdMs"].is_number_integer()){
        ok=false;return {{"message","Expected a permitted integer vk and bounded holdMs."}};
    }
    int vk=request["vk"].get<int>(),hold=request["holdMs"].get<int>();
    if(vk<0||vk>=256||!Allowed((unsigned)vk)||hold<20||hold>1000){ok=false;return {{"message","Only navigation keys held for 20 to 1000 milliseconds are permitted."}};}
    bool direct=EnsureDirectInput();
    pressed[(unsigned)vk].store(true);expires[(unsigned)vk].store(GetTickCount64()+(ULONGLONG)hold);
    keyMessages.fetch_add(1,std::memory_order_relaxed);auto result=Diagnostics();result["vk"]=vk;result["holdMs"]=hold;result["directInputHookAttempt"]=direct;return result;
}
}
#endif

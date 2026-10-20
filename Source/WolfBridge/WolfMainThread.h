#pragma once
// Dispatch through the game's message pump, independent of text redraw and
// translation state. Never read variable vectors on the transport thread.
namespace WolfMainThread {
inline std::atomic<DWORD> thread{0};
inline decltype(&PeekMessageA) peekA=nullptr;
inline decltype(&PeekMessageW) peekW=nullptr;
inline decltype(&GetMessageA) getA=nullptr;
inline decltype(&GetMessageW) getW=nullptr;
inline void Service(){
    static thread_local bool inside=false;
    if(inside||depth||GetCurrentThreadId()!=thread.load())return;
    inside=true;DataFrame();inside=false;
}
inline BOOL WINAPI PeekA(LPMSG msg,HWND window,UINT first,UINT last,UINT remove){Service();return peekA(msg,window,first,last,remove);}
inline BOOL WINAPI PeekW(LPMSG msg,HWND window,UINT first,UINT last,UINT remove){Service();return peekW(msg,window,first,last,remove);}
inline BOOL WINAPI GetA(LPMSG msg,HWND window,UINT first,UINT last){Service();BOOL result=getA(msg,window,first,last);Service();return result;}
inline BOOL WINAPI GetW(LPMSG msg,HWND window,UINT first,UINT last){Service();BOOL result=getW(msg,window,first,last);Service();return result;}
inline BOOL CALLBACK Find(HWND window,LPARAM){
    DWORD pid=0;DWORD id=GetWindowThreadProcessId(window,&pid);
    if(pid==GetCurrentProcessId()&&!GetWindow(window,GW_OWNER)){thread=id;return FALSE;}return TRUE;
}
inline void Wake(){if(!thread.load())EnumWindows(Find,0);auto id=thread.load();if(id)PostThreadMessageW(id,WM_NULL,0,0);}
inline bool Install(){
    return MH_CreateHook((void*)PeekMessageA,(void*)PeekA,(void**)&peekA)==MH_OK&&
        MH_CreateHook((void*)PeekMessageW,(void*)PeekW,(void**)&peekW)==MH_OK&&
        MH_CreateHook((void*)GetMessageA,(void*)GetA,(void**)&getA)==MH_OK&&
        MH_CreateHook((void*)GetMessageW,(void*)GetW,(void**)&getW)==MH_OK;
}
}

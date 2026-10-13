#pragma once

// A bounded wake-up for players whose compiled UI never enters runtime_invoke.
// This hook is confined to this process's Unity main thread. It never consumes
// input, posts input, changes focus, or calls Unity from the worker thread.
static std::atomic<HHOOK> wakeHook=nullptr;
static UINT wakeMessage=0;
static std::atomic<bool> wakePending=false,runtimeStopping=false,wakeFinished=false;
static std::atomic<ULONGLONG> lastPumpAt=0;
static uint64_t wakeTicks=0,renderTicks=0;
static void Pump();
static LRESULT CALLBACK MainMessageHook(int code,WPARAM removed,LPARAM data){
 if(code==HC_ACTION&&removed==PM_REMOVE&&data){
  auto msg=reinterpret_cast<MSG*>(data);
  if(msg->message==WM_QUIT)runtimeStopping=true;
  if(wakeMessage&&msg->message==wakeMessage&&msg->hwnd==nullptr){
   wakePending=false;
   // The registered message belongs to us; other hooks still see WM_NULL.
   msg->message=WM_NULL;msg->wParam=0;msg->lParam=0;
   if(ready&&!runtimeStopping&&!inside&&GetTickCount64()-lastPumpAt.load()>=16){wakeTicks++;Pump();}
  }
 }
 return CallNextHookEx(wakeHook,code,removed,data);
}
static DWORD WINAPI WakeWorker(void*){
 HANDLE main=OpenThread(SYNCHRONIZE,FALSE,config.mainThread);
 while(!runtimeStopping&&!wakeFinished&&main&&WaitForSingleObject(main,16)==WAIT_TIMEOUT){
  if(ready&&GetTickCount64()-lastPumpAt.load()>=16&&!wakePending.exchange(true)){
   if(!PostThreadMessageW(config.mainThread,wakeMessage,0,0))wakePending=false;
  }
 }
 if(main)CloseHandle(main);
 if(wakeHook){UnhookWindowsHookEx(wakeHook);wakeHook=nullptr;}
 return 0;
}
static void StartWake(){
 wakeMessage=RegisterWindowMessageW(L"Fusion.Unity.TextRefresh.129");
 if(!wakeMessage)return;
 wakeHook=SetWindowsHookExW(WH_GETMESSAGE,MainMessageHook,nullptr,config.mainThread);
 if(!wakeHook){Log("main-thread wake unavailable");return;}
 auto thread=CreateThread(nullptr,0,WakeWorker,nullptr,0,nullptr);
 if(thread)CloseHandle(thread);else{UnhookWindowsHookEx(wakeHook);wakeHook=nullptr;}
}

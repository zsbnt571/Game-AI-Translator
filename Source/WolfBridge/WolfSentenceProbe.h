#pragma once
#ifdef FUSION_TEST_CAPTURE
#include <intrin.h>
namespace WolfSentenceProbe {
using Copy=void*(__cdecl*)(void*,const void*,size_t);
static Copy original=nullptr;
static void* setterOriginal=nullptr;
static std::atomic<bool> armed{false};
static std::mutex mutex;
static json records=json::array();
static std::unordered_set<std::string> unique;
static std::string match;
static thread_local bool busy=false;
static size_t ReadCopy(const void* source,size_t size,char* out);
static bool ReadMemory(const void* source,void* target,size_t n){__try{if(!source)return false;memcpy(target,source,n);return true;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}}
static void __cdecl SetterRecord(uintptr_t* frame) noexcept {
    if(!armed.load()||busy)return;busy=true;
    try{
        uintptr_t object[6]{};char raw[24001];
        if(ReadMemory(frame+11,object,24)&&object[4]>5&&object[4]<24000&&object[5]>=object[4]){
            auto ptr=object[5]>15?(void*)object[0]:(void*)(frame+11);size_t n=ReadCopy(ptr,object[4],raw);
            std::string text(raw,n);std::lock_guard<std::mutex> lock(mutex);
            if(records.size()<1500&&(match.empty()||text.find(match)!=text.npos)){
                uintptr_t context[12]{},command=0;unsigned char bytes[128]{};
                ReadMemory((void*)frame[6],context,sizeof(context));ReadMemory((void*)(frame[6]+0x884),&command,4);
                bool readable=ReadMemory((void*)command,bytes,sizeof(bytes));
                json values=json::array();for(auto v:context)values.push_back(v);
                std::string hex;const char* digits="0123456789abcdef";if(readable)for(auto b:bytes){hex+=digits[b>>4];hex+=digits[b&15];}
                records.push_back({{"kind","setter"},{"text",text},{"interpreter",frame[6]},{"variable",frame[10]},{"callerRva",frame[9]-(uintptr_t)GetModuleHandleW(nullptr)},
                    {"contextDwords",values},{"commandPointer",command},{"commandHex",hex}});
            }
        }
    }catch(...){}busy=false;
}
static __declspec(naked) void SetterThunk(){__asm{
    pushfd
    pushad
    mov ebp,esp
    sub esp,528
    and esp,-16
    fxsave [esp]
    push ebp
    call SetterRecord
    add esp,4
    fxrstor [esp]
    mov esp,ebp
    popad
    popfd
    jmp setterOriginal
}}
static size_t ReadCopy(const void* source,size_t size,char* out){
    __try{if(!source||size<6||size>24000)return 0;size_t n=0;for(;n<size;n++){char c=((const char*)source)[n];if(!c)break;out[n]=c;}out[n]=0;return n;}
    __except(EXCEPTION_EXECUTE_HANDLER){return 0;}
}
static void* __cdecl Observe(void* dst,const void* source,size_t size){
    if(armed.load(std::memory_order_relaxed)&&!busy){
        busy=true;
        try{
            char raw[24001];size_t n=ReadCopy(source,size,raw);
            if(n>=6&&MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,raw,(int)n,nullptr,0)>0){
                std::string text(raw,n);bool prose=false;for(unsigned char c:text)if(c>=0x80){prose=true;break;}
                if(prose){std::lock_guard<std::mutex> lock(mutex);
                    if(records.size()<1500&&(match.empty()||text.find(match)!=text.npos)){
                        uintptr_t caller=(uintptr_t)_ReturnAddress();auto key=std::to_string(caller)+"|"+text;
                        if(unique.insert(key).second){
                            void* stack[20]{};USHORT count=CaptureStackBackTrace(1,20,stack,nullptr);json frames=json::array();
                            auto base=(uintptr_t)GetModuleHandleW(nullptr);
                            for(USHORT i=0;i<count;i++)frames.push_back((uintptr_t)stack[i]-(uintptr_t)base);
                            records.push_back({{"text",text},{"size",size},{"source",(uintptr_t)source},{"destination",(uintptr_t)dst},{"callerRva",caller-base},{"thread",GetCurrentThreadId()},{"framesRva",frames}});
                        }
                    }
                }
            }
        }catch(...){}
        busy=false;
    }
    return original(dst,source,size);
}
static bool Install(void* address){
    if(!WolfTestInput::installed.load()||!WolfTestInput::CurrentDesktop()||!address)return false;
    auto target=(unsigned char*)GetModuleHandleW(nullptr)+0xa68b0;
    return MH_CreateHook(address,(void*)Observe,(void**)&original)==MH_OK&&MH_CreateHook(target,(void*)SetterThunk,&setterOriginal)==MH_OK;
}
static json Request(const json& q){
    std::lock_guard<std::mutex> lock(mutex);
    if(q.value("reset",false)){records=json::array();unique.clear();}
    if(q.contains("match")&&q["match"].is_string())match=q["match"].get<std::string>().substr(0,200);
    if(q.contains("armed")&&q["armed"].is_boolean())armed=q["armed"].get<bool>();
    return {{"installed",original!=nullptr},{"armed",armed.load()},{"records",records},{"testOnly",true},{"stringValuesModified",false}};
}
}
#endif

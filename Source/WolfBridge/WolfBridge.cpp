// WOLF display boundaries. No event, save, decision or input data is
// translated. The original raw string is borrowed only for one layout call.
#include <windows.h>
#include <array>
#include <string>
#include <vector>
#include <deque>
#include <unordered_map>
#include <unordered_set>
#include <mutex>
#include <atomic>
#include <fstream>
#include <filesystem>
#include <memory>
#include <algorithm>
#include <condition_variable>
#include <cmath>
#include "MinHook.h"
#include "json.hpp"
#include "WolfText.h"
#include "WolfLegacyText.h"
#include "WolfCache.h"
#include "WolfFont.h"
#include "WolfData.h"
using nlohmann::json;
namespace fs=std::filesystem;
static HMODULE module;
static void* layout;
static bool legacy=false;
using ConvertString=int(__cdecl*)(const char*,int,int,void*,int,int);
static ConvertString originalConvert;
static std::atomic<unsigned> legacyResolved{0};
static std::wstring folder,fontPath;
static std::string secret;
static HANDLE owner;
static std::atomic<bool> enabled{false},started{false};
static std::mutex gate;
static WolfCache::Cache cache;
static bool Store(const std::string& source,const std::string& text){return cache.Put(source,text);}
static std::unordered_set<std::string> seen,displayed;
static std::deque<std::string> pending;
struct Binding { ULONGLONG time;bool translated; };
static std::unordered_map<uintptr_t,Binding> bindings;
static int epoch=0,lastId=0;
static std::atomic<unsigned> unsupported{0},failures{0};
static WolfFont::Renderer glyphRenderer;
static WolfData::Data gameData;
struct DataJob {json request,result;bool ok=true,done=false,cancelled=false;ULONGLONG expires=0;};
static std::mutex dataGate;static std::condition_variable dataChanged;static std::shared_ptr<DataJob> dataJob;
static std::atomic<bool> dataStop{false};
static void DataFrame(){
    // The renderer is called by the game's main thread. Variable vectors must
    // not be dereferenced by the IPC worker while the game can resize them.
    std::unique_lock<std::mutex> l(dataGate,std::try_to_lock);if(!l)return;
    if(dataStop.exchange(false)){gameData.Stop();if(dataJob)dataJob->cancelled=true;}
    if(dataJob&&!dataJob->done&&!dataJob->cancelled&&GetTickCount64()<dataJob->expires){
        try{dataJob->result=gameData.Request(dataJob->request,dataJob->ok);}catch(...){dataJob->ok=false;dataJob->result={{"message",u8"读取游戏变量失败，未继续修改。"}};}
        dataJob->done=true;dataChanged.notify_one();
    }
    static ULONGLONG next=0;if(GetTickCount64()>=next){next=GetTickCount64()+500;gameData.Tick();}
}
static json DataRequest(const json& request,bool& ok){
    std::unique_lock<std::mutex> l(dataGate);auto job=std::make_shared<DataJob>();job->request=request;job->expires=GetTickCount64()+5000;dataJob=job;
    if(!dataChanged.wait_for(l,std::chrono::seconds(5),[&]{return job->done;})){job->cancelled=true;ok=false;return {{"message",u8"游戏当前未刷新界面，修改未执行；请返回游戏后重试。"}};}
    ok=job->ok;return job->result;
}
#ifdef FUSION_TEST_CAPTURE
static std::mutex traceGate;static json rawTrace=json::array(),layoutTrace=json::array();
static std::unordered_set<std::string> traced;
static std::string fixtureMatch,fixtureRaw;static uintptr_t fixtureObject=0;static std::atomic<bool> fixtureNoRepair{false};
#endif
static std::atomic<unsigned> completedLayouts{0};
static std::atomic<unsigned> remeasuredLayouts{0};

static std::wstring Env(const wchar_t* key) {
    DWORD n=GetEnvironmentVariableW(key,nullptr,0);if(n<2||n>32768)return {};
    std::wstring s(n,L'\0');DWORD count=GetEnvironmentVariableW(key,s.data(),n);
    if(count==0||count>=n)return {};s.resize(count);return s;
}
static std::string Narrow(const std::wstring& value) {
    int n=WideCharToMultiByte(CP_UTF8,0,value.data(),(int)value.size(),nullptr,0,nullptr,nullptr);
    std::string s(n,0);if(n)WideCharToMultiByte(CP_UTF8,0,value.data(),(int)value.size(),s.data(),n,nullptr,nullptr);return s;
}
static json Read(const wchar_t* name,size_t limit) {
    std::error_code error;fs::path path=fs::path(folder)/name;
    auto size=fs::file_size(path,error);if(error||size==0||size>limit)return {};
    std::ifstream f(path,std::ios::binary);std::string s((size_t)size,0);if(!f.read(s.data(),s.size()))return {};
    return json::parse(s,nullptr,false);
}
static bool Write(const std::wstring& name,const json& value) {
    auto path=fs::path(folder)/name,temp=path;temp+=L".tmp";
    auto data=value.dump(-1,' ',false,json::error_handler_t::replace);
    {std::ofstream f(temp,std::ios::binary|std::ios::trunc);if(!f.write(data.data(),data.size()))return false;}
    return MoveFileExW(temp.c_str(),path.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)!=FALSE;
}
static bool ReadRaw(void* object,char* dst,size_t* len) {
    __try {
        auto p=(uintptr_t*)object;size_t n=p[4],capacity=p[5];
        if(n==0||n>24000||capacity<n||capacity>1024*1024)return false;
        auto s=capacity>15?(const char*)p[0]:(const char*)object;
        if(!s||s[n]!=0)return false;memcpy(dst,s,n);dst[n]=0;*len=n;return true;
    } __except(EXCEPTION_EXECUTE_HANDLER) {return false;}
}
static bool CopyRaw(void* dst,const void* src) {
    __try {memcpy(dst,src,24);return true;} __except(EXCEPTION_EXECUTE_HANDLER) {return false;}
}
struct LayoutState {
    void* object=nullptr;std::array<unsigned char,24> original{};
    uintptr_t returnAddress=0;std::string replacement;
#ifdef FUSION_TEST_CAPTURE
    bool fixture=false,draw=false;unsigned measureBefore=0,measureAfter=0;bool measured=false;
#endif
};
static thread_local std::array<LayoutState,16> states;
static thread_local unsigned depth=0;
static void LayoutAfter();

// In the verified WOLF 2 ABI this same routine has a measure-only mode. A new
// source clears the line metrics; draw mode consumes them without rebuilding
// them. A late translation therefore needs one measurement of the exact bytes
// that the pending draw will use. Never call the network or alter event data.
static bool MeasureLegacy(uintptr_t* frame,void* object,const std::string& current){
    if(!legacy||!(frame[10]&0xff))return false;
    char prepared[24001];size_t n=0;
    if(ReadRaw((char*)object+28,prepared,&n)&&n==current.size()&&!memcmp(prepared,current.data(),n))return false;
    double x,y,angle;memcpy(&x,frame+11,8);memcpy(&y,frame+13,8);memcpy(&angle,frame+15,8);
    if(!std::isfinite(x)||!std::isfinite(y)||!std::isfinite(angle)||fabs(x)>10000||fabs(y)>10000||fabs(angle)>10000)return false;
#ifdef FUSION_TEST_CAPTURE
    if(fixtureNoRepair)return false;
#endif
    using Measure=void(__thiscall*)(void*,bool,double,double,double);
    ((Measure)layout)((char*)object-32,false,x,y,angle);
    remeasuredLayouts.fetch_add(1,std::memory_order_relaxed);return true;
}

static void __cdecl LayoutBefore(uintptr_t* frame) noexcept {
    try {
        if(depth==0)DataFrame();
        if(depth>=states.size())return;
        auto& state=states[depth++];state.object=nullptr;state.returnAddress=frame[9];frame[9]=(uintptr_t)LayoutAfter;
#ifdef FUSION_TEST_CAPTURE
        state.fixture=false;state.draw=(frame[10]&0xff)!=0;state.measured=false;
#endif
        // Legacy debug strings keep an iterator proxy ahead of the same 24-byte
        // payload. Preserve that proxy; it still belongs to the game object.
        auto object=(unsigned char*)frame[6]+(legacy?32:24);
        char raw[24001];size_t length=0;
        if(!ReadRaw(object,raw,&length))return;
        std::string original(raw,length),prefix,source;
#ifdef FUSION_TEST_CAPTURE
        {std::lock_guard<std::mutex> l(traceGate);
        if(!fixtureRaw.empty()&&(fixtureObject==(uintptr_t)object||(!fixtureObject&&original.find(fixtureMatch)!=original.npos))){
            fixtureObject=(uintptr_t)object;state.fixture=true;state.measureBefore=*(unsigned*)((char*)object-32+0xf4);
            original=fixtureRaw;
        }
        if(rawTrace.size()<512&&traced.insert(original).second){std::string hex;const char* digits="0123456789abcdef";for(unsigned char c:original){hex+=digits[c>>4];hex+=digits[c&15];}rawTrace.push_back(hex);}}
#endif
        std::vector<WolfText::Run> runs;
        if(!WolfText::Runs(original,runs,legacy)){
            runs.clear();
            std::string decoded=original;if(legacy&&!WolfLegacyText::Decode(original,decoded))return;
            if(!WolfText::Split(decoded,prefix,source)){unsupported.fetch_add(1,std::memory_order_relaxed);return;}
            // Existing single-prefix compatibility, encoded back before the
            // common per-span path so raw command bytes are preserved.
            if(legacy){std::string encoded;if(!WolfLegacyText::Encode(prefix,encoded))return;prefix=std::move(encoded);if(!WolfLegacyText::Encode(source,encoded))return;source=std::move(encoded);}
            if(!prefix.empty())runs.push_back({prefix,false});runs.push_back({source,true});
        }
        std::string replacement;bool changed=false;std::vector<std::string> renderedSources;
        for(const auto& run:runs){
        if(!run.display){replacement+=run.bytes;continue;}
        source=run.bytes;if(legacy&&!WolfLegacyText::Decode(run.bytes,source)){replacement+=run.bytes;continue;}
        if(!WolfText::Plain(source)){replacement+=run.bytes;continue;}
        auto first=source.find_first_not_of(" \r\n\t"),last=source.find_last_not_of(" \r\n\t");
        if(first==source.npos){replacement+=run.bytes;continue;}
        std::string before=source.substr(0,first),after=source.substr(last+1);source=source.substr(first,last-first+1);
        std::string translated;
        if(enabled.load(std::memory_order_relaxed))translated=cache.Find(source);
        {
            // Cache snapshots remain readable even while a request is handled.
            // A missed telemetry lock must never flash the original text.
            std::unique_lock<std::mutex> lock(gate,std::try_to_lock);
            if(lock&&enabled.load(std::memory_order_relaxed)) {
                if(seen.size()<100000&&pending.size()<4096&&seen.insert(source).second)pending.push_back(source);
            }
            if(lock){auto found=bindings.find((uintptr_t)object);
                if(found!=bindings.end()||bindings.size()<4096)bindings[(uintptr_t)object]={GetTickCount64(),false};}
        }
        translated=WolfCache::Cache::Key(translated);
        if(translated.empty()||translated==source){replacement+=run.bytes;continue;}
        if(legacy){std::string encoded;if(!WolfLegacyText::Encode(translated,encoded)){unsupported.fetch_add(1,std::memory_order_relaxed);replacement+=run.bytes;continue;}translated=std::move(encoded);}
        replacement+=before+translated+after;changed=true;
        renderedSources.push_back(source);
        }
        if(replacement.size()>24000)return;
#ifdef FUSION_TEST_CAPTURE
        changed|=state.fixture;
#endif
        if(!changed){MeasureLegacy(frame,object,original);return;}
        state.replacement=std::move(replacement);
        if(!CopyRaw(state.original.data(),object))return;
        auto substitute=WolfText::StringView(state.replacement);
        if(!CopyRaw(object,substitute.data()))return;
        state.object=object;
        bool measured=MeasureLegacy(frame,object,state.replacement);
#ifdef FUSION_TEST_CAPTURE
        state.measured=measured;state.measureAfter=*(unsigned*)((char*)object-32+0xf4);
#endif
        {std::unique_lock<std::mutex> lock(gate,std::try_to_lock);if(lock){auto found=bindings.find((uintptr_t)object);if(found!=bindings.end())found->second.translated=true;for(const auto& s:renderedSources)if(displayed.size()<100000)displayed.insert(s);}}
    }catch(...){failures.fetch_add(1,std::memory_order_relaxed);}
}
static uintptr_t __cdecl LayoutFinish() noexcept {
    // The entry changes the return address only after this TLS record exists.
    auto& state=states[--depth];
#ifdef FUSION_TEST_CAPTURE
    if(state.fixture&&state.object){std::lock_guard<std::mutex> l(traceGate);if(layoutTrace.size()<512)layoutTrace.push_back({{"draw",state.draw},{"before",state.measureBefore},{"measured",state.measured},{"afterMeasure",state.measureAfter},{"after",*(unsigned*)((char*)state.object-32+0xf4)},{"replacement",state.replacement}});}
#endif
    if(state.object){if(!CopyRaw(state.object,state.original.data()))failures.fetch_add(1,std::memory_order_relaxed);completedLayouts.fetch_add(1,std::memory_order_relaxed);}
    return state.returnAddress;
}
static int __cdecl LegacyConvert(const char* source,int length,int from,void* destination,int capacity,int to) {
    int result=originalConvert(source,length,from,destination,capacity,to);
    if(depth&&states[depth-1].object&&from==932&&to==1200&&destination&&result>0)
        legacyResolved.fetch_add(WolfLegacyText::Resolve(destination,(std::min)(result,capacity)),std::memory_order_relaxed);
    return result;
}
static __declspec(naked) void LayoutBeforeThunk(){__asm{
    pushfd
    pushad
    mov ebp,esp
    sub esp,528
    and esp,-16
    fxsave [esp]
    push ebp
    call LayoutBefore
    add esp,4
    fxrstor [esp]
    mov esp,ebp
    popad
    popfd
    jmp layout
}}
static __declspec(naked) void LayoutAfter(){__asm{
    push 0
    pushfd
    pushad
    mov ebp,esp
    sub esp,528
    and esp,-16
    fxsave [esp]
    call LayoutFinish
    mov [ebp+36],eax
    fxrstor [esp]
    mov esp,ebp
    popad
    popfd
    ret
}}

// Preserve original glyphs and match missing CJK glyphs to the actual em size.
static decltype(&GetGlyphOutlineW) originalGlyph;
static DWORD WINAPI Glyph(HDC dc,UINT ch,UINT format,LPGLYPHMETRICS metrics,DWORD count,LPVOID buffer,const MAT2* matrix) {
    return glyphRenderer.Draw(originalGlyph,depth!=0&&states[depth-1].object,dc,ch,format,metrics,count,buffer,matrix);
}
static void* FindPattern(int id) {
    HRSRC resource=FindResourceW(module,MAKEINTRESOURCEW(id),MAKEINTRESOURCEW(10));if(!resource)return nullptr;
    auto h=LoadResource(module,resource);auto bytes=(const char*)LockResource(h);DWORD length=SizeofResource(module,resource);
    if(!bytes||length>4096)return nullptr;
    auto pattern=WolfText::ParsePattern(std::string(bytes,length));if(pattern.empty())return nullptr;
    auto base=(unsigned char*)GetModuleHandleW(nullptr);
    auto dos=(IMAGE_DOS_HEADER*)base;if(dos->e_magic!=IMAGE_DOS_SIGNATURE||dos->e_lfanew<64||dos->e_lfanew>1024*1024)return nullptr;
    auto nt=(IMAGE_NT_HEADERS32*)(base+dos->e_lfanew);
    if(nt->Signature!=IMAGE_NT_SIGNATURE||nt->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386||nt->OptionalHeader.Magic!=IMAGE_NT_OPTIONAL_HDR32_MAGIC||nt->FileHeader.NumberOfSections>96)return nullptr;
    auto sections=IMAGE_FIRST_SECTION(nt);void* found=nullptr;size_t scanned=0;
    for(unsigned n=0;n<nt->FileHeader.NumberOfSections;n++) {
        auto s=sections[n];if(!(s.Characteristics&IMAGE_SCN_MEM_EXECUTE))continue;
        size_t begin=s.VirtualAddress,len=s.Misc.VirtualSize;
        if(begin>=nt->OptionalHeader.SizeOfImage||len>nt->OptionalHeader.SizeOfImage-begin||len<pattern.size()||(scanned+=len)>128*1024*1024)return nullptr;
        for(size_t i=0;i<=len-pattern.size();i++)if(base[begin+i]==pattern[0]&&WolfText::Match(base+begin+i,pattern)){
            if(found)return nullptr;found=base+begin+i;
        }
    }
    return found;
}
static bool Pair(const std::string& source,const std::string& text) {
    return source.size()<=24000&&text.size()<=48000&&text.find_first_not_of(" \r\n\t")!=text.npos&&WolfText::Plain(source)&&WolfText::Plain(text);
}
static json State(bool poll) {
    json result={{"epoch",epoch},{"enabled",enabled.load()},{"entries",cache.Size()},{"rawCaptureCount",seen.size()},
        {"rawCaptureCountSemantics","unique-source"},{"nativePreprocessorHits",displayed.size()},
        {"completedLayoutCalls",completedLayouts.load()},{"glyphFallbacks",glyphRenderer.Count()},
        {"remeasuredLayoutCalls",remeasuredLayouts.load()},
        {"legacyUnicodeCharacters",legacyResolved.load()},{"textEncoding",legacy?"shift-jis":"utf-8"},
        {"writebacksFailed",failures.load()}};
    unsigned rendered=0;ULONGLONG now=GetTickCount64();
    for(auto i=bindings.begin();i!=bindings.end();)if(now-i->second.time>2000)i=bindings.erase(i);else{if(i->second.translated)rendered++;++i;}
    result["activeRenderedBindings"]=rendered;result["bindings"]=bindings.size();
    if(poll){json texts=json::array();while(!pending.empty()&&texts.size()<128){texts.push_back(std::move(pending.front()));pending.pop_front();}result["texts"]=std::move(texts);result["ahead"]=json::array();}
    if(unsupported.load())result["runtimeWarning"]=u8"部分控制码或字符暂未替换，保留原显示。";
    return result;
}
static json Request(const json& q,bool& ok) {
    std::lock_guard<std::mutex> lock(gate);std::string op=q.value("op","");
    if(op.rfind("data",0)==0)return DataRequest(q,ok);
    if(op=="translationPrepare"){
        ++epoch;seen.clear();pending.clear();if(!q.value("preserveStartup",false)){enabled=false;cache.Clear();}return {{"epoch",epoch}};
    }
    if(op=="translationEnable"){enabled=true;return {{"epoch",epoch}};}
    if(op=="translationDisable"){enabled=false;++epoch;return {{"epoch",epoch}};}
    if(op=="translationApply"){
        if(q.value("epoch",-1)!=epoch)return {{"epoch",epoch},{"stored",0},{"stale",true}};
        int stored=0,checked=0;
        if(q.contains("entries")&&q["entries"].is_array())for(const auto& p:q["entries"]){if(++checked>128)break;if(!p.is_object())continue;
            auto s=p.value("source",""),t=p.value("text","");if(Pair(s,t)&&Store(s,t))stored++;}
        cache.Publish();
        return {{"epoch",epoch},{"stored",stored}};
    }
    if(op=="translationPoll")return State(true);
    if(op=="translationState")return State(false);
#ifdef FUSION_TEST_CAPTURE
    if(op=="testTrace"){std::lock_guard<std::mutex> l(traceGate);return {{"raw",rawTrace},{"layouts",layoutTrace},{"legacy",legacy}};}
    if(op=="testFixture"){std::lock_guard<std::mutex> l(traceGate);fixtureMatch=q.value("match","");fixtureRaw=q.value("raw","");if(legacy){std::string encoded;if(!WolfLegacyText::Encode(fixtureRaw,encoded)){ok=false;fixtureRaw.clear();}else fixtureRaw=std::move(encoded);}fixtureNoRepair=q.value("noRepair",false);fixtureObject=0;layoutTrace=json::array();return {{"ready",ok}};}
#endif
    ok=false;return {{"epoch",epoch}};
}
static DWORD WINAPI Worker(void*) {
    json reply;int replyId=0;
    for(;;) {
        try {
            if(WaitForSingleObject(owner,0)==WAIT_OBJECT_0){enabled=false;dataStop=true;break;}
            auto stop=Read(L"stop.json",1024);if(stop.is_object()&&stop.value("secret","")==secret){enabled=false;dataStop=true;break;}
            if(!reply.is_null()){
                if(Write(L"response-"+std::to_wstring(replyId)+L".json",reply)){lastId=replyId;reply=nullptr;}
            }else {
                auto q=Read(L"request.json",262144);
                if(q.is_object()&&q.value("secret","")==secret&&q.contains("id")&&q["id"].is_number_integer()){
                    int id=q["id"].get<int>();if(id>lastId&&id>0){bool ok=true;json result=Request(q,ok);reply={{"id",id},{"secret",secret},{"ok",ok},{"result",std::move(result)}};replyId=id;}
                }
            }
        }catch(...){failures.fetch_add(1,std::memory_order_relaxed);}
        Sleep(20);
    }
    CloseHandle(owner);owner=nullptr;return 0;
}
extern "C" DWORD WINAPI FusionWolfStart(void*) {
    try {
        if(started.exchange(true))return 0;
        folder=Env(L"FUSION_WOLF_SESSION");secret=Narrow(Env(L"FUSION_WOLF_SECRET"));
        if(folder.empty()||secret.size()!=64||secret.find_first_not_of("0123456789ABCDEF")!=secret.npos)return 0;
        wchar_t* end=nullptr;auto parent=Env(L"FUSION_WOLF_OWNER_PID");unsigned long pid=wcstoul(parent.c_str(),&end,10);if(!pid||!end||*end)return 0;
        owner=OpenProcess(SYNCHRONIZE,FALSE,pid);if(!owner)return 0;
        auto target=FindPattern(101);void* convertTarget=nullptr;
        if(!target){target=FindPattern(102);convertTarget=FindPattern(103);if(!target||!convertTarget)return 0;legacy=true;}
        gameData.Initialize(legacy);
        wchar_t path[32768];DWORD n=GetModuleFileNameW(module,path,32768);if(n==0||n>=32768)return 0;
        fontPath=(fs::path(path).parent_path()/L"bridge-font.otf").wstring();
        if(!AddFontResourceExW(fontPath.c_str(),FR_PRIVATE,nullptr))return 0;
        auto initial=Read(L"startup.json",32*1024*1024);
        if(initial.is_object()&&initial.value("secret","")==secret&&initial.contains("entries")&&initial["entries"].is_object())
            for(auto i=initial["entries"].begin();i!=initial["entries"].end()&&cache.Size()<100000;++i)
                if(i.value().is_string()){auto text=i.value().get<std::string>();if(Pair(i.key(),text))Store(i.key(),text);}
        cache.Publish();
        enabled=Env(L"FUSION_WOLF_TRANSLATION")==L"1";
        if(MH_Initialize()!=MH_OK||MH_CreateHook(target,(void*)LayoutBeforeThunk,&layout)!=MH_OK||
            MH_CreateHook((void*)GetGlyphOutlineW,(void*)Glyph,(void**)&originalGlyph)!=MH_OK)return 0;
        if(legacy&&MH_CreateHook(convertTarget,(void*)LegacyConvert,(void**)&originalConvert)!=MH_OK)return 0;
        if(MH_EnableHook(MH_ALL_HOOKS)!=MH_OK)return 0;
        if(!Write(L"hello.json",{{"secret",secret},{"protocol",133},{"engine","wolf"},{"pid",GetCurrentProcessId()}}))return 0;
        HANDLE worker=CreateThread(nullptr,0,Worker,nullptr,0,nullptr);if(!worker)return 0;CloseHandle(worker);return 1;
    }catch(...){return 0;}
}
BOOL APIENTRY DllMain(HMODULE value,DWORD reason,LPVOID) {
    if(reason==DLL_PROCESS_ATTACH){module=value;DisableThreadLibraryCalls(value);}return TRUE;
}

// WOLF display boundaries. Ordinary strings are borrowed for one layout call;
// audited custom typewriters may translate an owned display-only temporary
// before the game compiles controls. Source databases and saves stay original.
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
#include <sstream>
#include <filesystem>
#include <memory>
#include <algorithm>
#include <condition_variable>
#include <cmath>
#include "MinHook.h"
#include "json.hpp"
#include "WolfSessionFiles.h"
#include "WolfText.h"
#include "WolfLegacyText.h"
#include "WolfCache.h"
#include "WolfFont.h"
#include "WolfSpeed.h"
#include "WolfSpeedProtocol.h"
#include "WolfData.h"
#include "WolfTestInput.h"
#include "WolfTestScreen.h"
using nlohmann::json;
namespace fs=std::filesystem;
#include "WolfSentenceProbe.h"
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
static void WakeDataThread();
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
    WakeDataThread();
    if(!dataChanged.wait_for(l,std::chrono::seconds(5),[&]{return job->done;})){job->cancelled=true;ok=false;return {{"message",u8"游戏暂未响应修改请求，本次未执行。请稍后重试。"}};}
    ok=job->ok;return job->result;
}
#ifdef FUSION_TEST_CAPTURE
static std::mutex traceGate;static json rawTrace=json::array(),layoutTrace=json::array(),renderTrace=json::array();
static std::unordered_set<std::string> traced,renderTraced;
static std::string fixtureMatch,fixtureRaw;static uintptr_t fixtureObject=0;static std::atomic<bool> fixtureNoRepair{false};
#endif
static std::atomic<unsigned> completedLayouts{0};
static std::atomic<unsigned> remeasuredLayouts{0};
static std::atomic<unsigned long long> layoutRevision{0},refreshedPictureBounds{0};
// These counters last for this game process. They count calls/spans, while
// seen/displayed remain unique source counts within the current prepare epoch.
static std::atomic<unsigned long long> layoutEntries{0},layoutReturns{0},layoutDepthSkipped{0};
static std::atomic<unsigned long long> rawReadSuccess{0},rawReadEmpty{0},rawReadInvalid{0};
static std::atomic<unsigned long long> displaySpans{0},lookupCalls{0},lookupDisabled{0};
static std::atomic<unsigned long long> exactHits{0},derivedHits{0},cacheMisses{0};

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
    std::string s;if(!WolfSessionFiles::Read(fs::path(folder)/name,limit,s))return {};
    return json::parse(s,nullptr,false);
}
static bool Write(const std::wstring& name,const json& value) {
    return WolfSessionFiles::WriteAtomic(fs::path(folder)/name,value.dump(-1,' ',false,json::error_handler_t::replace));
}
enum class RawRead { Invalid, Empty, Ready };
static bool ReadRaw(void* object,char* dst,size_t* len,RawRead* status=nullptr) {
    if(status)*status=RawRead::Invalid;
    __try {
        auto p=(uintptr_t*)object;size_t n=p[4],capacity=p[5];
        if(n>24000||capacity<n||capacity>1024*1024)return false;
        auto s=capacity>15?(const char*)p[0]:(const char*)object;
        if(!s||s[n]!=0)return false;
        if(!n){if(status)*status=RawRead::Empty;return false;}
        memcpy(dst,s,n);dst[n]=0;*len=n;if(status)*status=RawRead::Ready;return true;
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
#include "WolfMainThread.h"
static void WakeDataThread(){WolfMainThread::Wake();}
static void LayoutAfter();

// In the verified WOLF 2 ABI this same routine has a measure-only mode. A new
// source clears the line metrics; draw mode consumes them without rebuilding
// them. A late translation therefore needs one measurement of the exact bytes
// that the pending draw will use. Never call the network or alter event data.
static void MeasureModern(void* object,const void* saved,double angle){
    // WOLF 3's verified renderer uses ECX + XMM2/XMM3 and three stack words,
    // unlike WOLF 2's all-stack floating-point arguments. Reuse the incoming
    // register snapshot, never call it through an incompatible C++ signature.
    __asm {
        mov eax,saved
        movsd xmm2,qword ptr [eax+192]
        movsd xmm3,qword ptr [eax+208]
        push dword ptr [angle+4]
        push dword ptr [angle]
        push 0
        mov ecx,object
        call layout
    }
}
static bool MeasureDisplay(uintptr_t* frame,void* object,const std::string& current,const void* saved){
    if(!(frame[10]&0xff))return false;
    char prepared[24001];size_t n=0;
    if(ReadRaw((char*)object+(legacy?28:24),prepared,&n)&&n==current.size()&&!memcmp(prepared,current.data(),n))return false;
    double x,y,angle;
    if(legacy){memcpy(&x,frame+11,8);memcpy(&y,frame+13,8);memcpy(&angle,frame+15,8);}
    else{memcpy(&x,(const char*)saved+192,8);memcpy(&y,(const char*)saved+208,8);memcpy(&angle,frame+11,8);}
    if(!std::isfinite(x)||!std::isfinite(y)||!std::isfinite(angle)||fabs(x)>10000||fabs(y)>10000||fabs(angle)>10000)return false;
#ifdef FUSION_TEST_CAPTURE
    if(fixtureNoRepair)return false;
#endif
    using Measure=void(__thiscall*)(void*,bool,double,double,double);
    if(legacy)((Measure)layout)((char*)object-32,false,x,y,angle);
    else MeasureModern((char*)object-24,saved,angle);
    remeasuredLayouts.fetch_add(1,std::memory_order_relaxed);return true;
}

static void __cdecl LayoutBefore(uintptr_t* frame,const void* saved) noexcept {
    layoutEntries.fetch_add(1,std::memory_order_relaxed);
    try {
        if(depth==0){if(!WolfMainThread::thread.load())WolfMainThread::thread=GetCurrentThreadId();DataFrame();}
        if(depth>=states.size()){layoutDepthSkipped.fetch_add(1,std::memory_order_relaxed);return;}
        auto& state=states[depth++];state.object=nullptr;state.returnAddress=frame[9];frame[9]=(uintptr_t)LayoutAfter;
#ifdef FUSION_TEST_CAPTURE
        state.fixture=false;state.draw=(frame[10]&0xff)!=0;state.measured=false;
#endif
        // Legacy debug strings keep an iterator proxy ahead of the same 24-byte
        // payload. Preserve that proxy; it still belongs to the game object.
        auto object=(unsigned char*)frame[6]+(legacy?32:24);
        char raw[24001];size_t length=0;RawRead rawStatus;
        if(!ReadRaw(object,raw,&length,&rawStatus)){
            (rawStatus==RawRead::Empty?rawReadEmpty:rawReadInvalid).fetch_add(1,std::memory_order_relaxed);return;
        }
        rawReadSuccess.fetch_add(1,std::memory_order_relaxed);
        std::string original(raw,length),prefix,source;
#ifdef FUSION_TEST_CAPTURE
        {std::lock_guard<std::mutex> l(traceGate);
        if(!fixtureRaw.empty()&&(fixtureObject==(uintptr_t)object||(!fixtureObject&&original.find(fixtureMatch)!=original.npos))){
            fixtureObject=(uintptr_t)object;state.fixture=true;state.measureBefore=legacy?*(unsigned*)((char*)object-32+0xf4):0;
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
#ifdef FUSION_TEST_CAPTURE
        json renderedPairs=json::array();
#endif
        for(const auto& run:runs){
        if(!run.display){replacement+=run.bytes;continue;}
        source=run.bytes;if(legacy&&!WolfLegacyText::Decode(run.bytes,source)){replacement+=run.bytes;continue;}
        if(!WolfText::Plain(source)){replacement+=run.bytes;continue;}
        auto first=source.find_first_not_of(" \r\n\t"),last=source.find_last_not_of(" \r\n\t");
        if(first==source.npos){replacement+=run.bytes;continue;}
        std::string before=source.substr(0,first),after=source.substr(last+1);source=source.substr(first,last-first+1);
        displaySpans.fetch_add(1,std::memory_order_relaxed);
        std::string translated;WolfCache::HitKind hit=WolfCache::HitKind::Miss;
        if(enabled.load(std::memory_order_relaxed)){
            lookupCalls.fetch_add(1,std::memory_order_relaxed);translated=cache.Find(source,&hit);
            if(hit==WolfCache::HitKind::Exact)exactHits.fetch_add(1,std::memory_order_relaxed);
            else if(hit==WolfCache::HitKind::Derived)derivedHits.fetch_add(1,std::memory_order_relaxed);
            else cacheMisses.fetch_add(1,std::memory_order_relaxed);
        }else lookupDisabled.fetch_add(1,std::memory_order_relaxed);
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
#ifdef FUSION_TEST_CAPTURE
        auto translatedText=translated;
#endif
        if(legacy){std::string encoded;if(!WolfLegacyText::Encode(translated,encoded)){unsupported.fetch_add(1,std::memory_order_relaxed);replacement+=run.bytes;continue;}translated=std::move(encoded);}
#ifdef FUSION_TEST_CAPTURE
        renderedPairs.push_back({{"source",source.substr(0,6000)},{"text",translatedText.substr(0,12000)},
            {"truncated",source.size()>6000||translatedText.size()>12000},{"kind",hit==WolfCache::HitKind::Exact?"exact":"derived"}});
#endif
        replacement+=before+translated+after;changed=true;
        renderedSources.push_back(source);
        }
        if(replacement.size()>24000)return;
#ifdef FUSION_TEST_CAPTURE
        changed|=state.fixture;
#endif
        if(!changed){MeasureDisplay(frame,object,original,saved);return;}
        state.replacement=std::move(replacement);
        if(!CopyRaw(state.original.data(),object))return;
        auto substitute=WolfText::StringView(state.replacement);
        if(!CopyRaw(object,substitute.data()))return;
        state.object=object;
#ifdef FUSION_TEST_CAPTURE
        // Test builds alone expose bounded text evidence. Normal user sessions
        // retain counters without copying dialogue into diagnostic reports.
        {std::lock_guard<std::mutex> l(traceGate);for(auto& pair:renderedPairs){
            if(renderTrace.size()>=32)break;auto key=pair["source"].get<std::string>();key.push_back('\0');key+=pair["text"].get<std::string>();key.push_back(state.draw?'1':'0');
            if(renderTraced.insert(key).second){pair["draw"]=state.draw;renderTrace.push_back(std::move(pair));}
        }}
#endif
        bool measured=MeasureDisplay(frame,object,state.replacement,saved);
#ifdef FUSION_TEST_CAPTURE
        state.measured=measured;state.measureAfter=legacy?*(unsigned*)((char*)object-32+0xf4):0;
#endif
        {std::unique_lock<std::mutex> lock(gate,std::try_to_lock);if(lock){auto found=bindings.find((uintptr_t)object);if(found!=bindings.end())found->second.translated=true;for(const auto& s:renderedSources)if(displayed.size()<100000)displayed.insert(s);}}
    }catch(...){failures.fetch_add(1,std::memory_order_relaxed);}
}
static uintptr_t __cdecl LayoutFinish() noexcept {
    layoutReturns.fetch_add(1,std::memory_order_relaxed);
    // The entry changes the return address only after this TLS record exists.
    auto& state=states[--depth];
#ifdef FUSION_TEST_CAPTURE
    if(state.fixture&&state.object){std::lock_guard<std::mutex> l(traceGate);if(layoutTrace.size()<512)layoutTrace.push_back({{"draw",state.draw},{"legacyMetrics",legacy},{"before",state.measureBefore},{"measured",state.measured},{"afterMeasure",state.measureAfter},{"after",legacy?*(unsigned*)((char*)state.object-32+0xf4):0},{"replacement",state.replacement}});}
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
    mov eax,esp
    push eax
    push ebp
    call LayoutBefore
    add esp,8
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

// In the WOLF 2 picture ABI, text layout is embedded at +8 and its measured
// dimensions are copied into the enclosing picture. Center/right anchors are
// calculated from that copy BEFORE LayoutBefore is called. Refresh the copy
// when a cached translation or the enable state changes, so late enabling has
// exactly the same anchor calculation as translated startup.
static void* pictureDraw=nullptr;
using PictureMeasure=void(__thiscall*)(void*,int*,int*);
static PictureMeasure pictureMeasure=nullptr;
struct PictureStamp {std::string source,renderKey;unsigned long long revision;bool enabled;int width,height;};
static thread_local std::unordered_map<uintptr_t,PictureStamp> pictureStamps;
static bool PictureSize(void* object,int& width,int& height){
    __try{auto p=(unsigned char*)object;if(p[0x264]!=1)return false;width=*(int*)(p+0x1c0);height=*(int*)(p+0x1c8);return width>=0&&height>=0&&width<=16384&&height<=16384;}
    __except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
static void ApplyPictureSize(void* object,int oldWidth,int oldHeight,int width,int height){
    auto p=(unsigned char*)object;
    if(*(int*)(p+0x1c4)==oldWidth)*(int*)(p+0x1c4)=width;
    if(*(int*)(p+0x1cc)==oldHeight)*(int*)(p+0x1cc)=height;
    *(int*)(p+0x1c0)=width;*(int*)(p+0x1c8)=height;
}
static std::string PictureTranslationKey(const std::string& raw,bool active){
    if(!active)return {};std::vector<WolfText::Run> runs;if(!WolfText::Runs(raw,runs,true))return {};
    std::string key;
    for(auto& run:runs)if(run.display){
        std::string source;if(!WolfLegacyText::Decode(run.bytes,source))continue;
        source=WolfCache::Cache::Key(source);auto text=cache.Find(source);
        if(!text.empty()&&text!=source){key+=source;key.push_back('\0');key+=text;key.push_back('\0');}
    }return key;
}
static void __fastcall DrawPicture(void* object,void*,bool draw){
    try{
        int oldWidth=0,oldHeight=0;char raw[24001];size_t size=0;
        if(depth==0&&PictureSize(object,oldWidth,oldHeight)&&ReadRaw((char*)object+40,raw,&size)){
            auto key=(uintptr_t)object;auto found=pictureStamps.find(key);
            auto revision=layoutRevision.load(std::memory_order_relaxed);bool active=enabled.load(std::memory_order_relaxed);
            if(found==pictureStamps.end()||found->second.revision!=revision||found->second.enabled!=active||
                found->second.width!=oldWidth||found->second.height!=oldHeight||found->second.source!=std::string(raw,size)){
                std::string source(raw,size),renderKey=PictureTranslationKey(source,active);
                bool needsMeasure=found==pictureStamps.end()?!renderKey.empty():found->second.renderKey!=renderKey||
                    (!renderKey.empty()&&(found->second.source!=source||found->second.width!=oldWidth||found->second.height!=oldHeight));
                int width=oldWidth,height=oldHeight;
                if(needsMeasure)pictureMeasure((char*)object+8,&width,&height);
                if(width>=0&&height>=0&&width<=16384&&height<=16384){
                    if(needsMeasure){ApplyPictureSize(object,oldWidth,oldHeight,width,height);refreshedPictureBounds.fetch_add(1,std::memory_order_relaxed);}
                    if(pictureStamps.size()>=4096)pictureStamps.clear();
                    pictureStamps[key]={std::move(source),std::move(renderKey),revision,active,width,height};
                }
            }
        }
    }catch(...){failures.fetch_add(1,std::memory_order_relaxed);}
    ((void(__thiscall*)(void*,bool))pictureDraw)(object,draw);
}
namespace WolfCollect {static void* UniquePattern(const std::string& text);static void* Call(void* hit,unsigned displacement);}
static void InstallLegacyPictureBounds(void* layoutTarget){
    auto draw=WolfCollect::UniquePattern("55 8B EC 81 EC 14 0B 00 00 56 57 51 8D BD EC F4 FF FF B9 C5 02 00 00 B8 CC CC CC CC F3 AB 59 89 4D FC C6 85 E7 FC FF FF 00 C6 85 E6 FC FF FF 00 8B 45 FC 0F B6 88 79 02 00 00 85 C9 74 05");
    auto measure=WolfCollect::UniquePattern("55 8B EC 83 EC 08 56 C7 45 F8 CC CC CC CC C7 45 FC CC CC CC CC 89 4D FC DD 05 ?? ?? ?? ?? 83 EC 08 DD 1C 24 DD 05 ?? ?? ?? ?? 83 EC 08 DD 1C 24 DD 05 ?? ?? ?? ?? 83 EC 08 DD 1C 24 6A 00 8B 4D FC E8 ?? ?? ?? ??");
    if(!draw||!measure||WolfCollect::Call(measure,66)!=layoutTarget)return;
    pictureMeasure=(PictureMeasure)measure;
    if(MH_CreateHook(draw,(void*)DrawPicture,&pictureDraw)!=MH_OK){pictureMeasure=nullptr;pictureDraw=nullptr;}
}

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
#include "WolfSentence.h"
static json State(bool poll) {
    json result={{"epoch",epoch},{"enabled",enabled.load()},{"entries",cache.Size()},{"rawCaptureCount",seen.size()},
        {"rawCaptureCountSemantics","unique-source"},{"nativePreprocessorHits",displayed.size()},
        {"completedLayoutCalls",completedLayouts.load()},{"glyphFallbacks",glyphRenderer.Count()},
        {"completedLayoutCallsSemantics","translated-layout-calls-completed"},
        {"layoutHookCalls",layoutEntries.load()},{"layoutHookReturns",layoutReturns.load()},
        {"layoutDepthSkipped",layoutDepthSkipped.load()},
        {"layoutRawReadSuccess",rawReadSuccess.load()},{"layoutEmptyStrings",rawReadEmpty.load()},
        {"layoutInvalidStrings",rawReadInvalid.load()},{"displaySourceSpans",displaySpans.load()},
        {"cacheLookupCalls",lookupCalls.load()},{"cacheLookupDisabled",lookupDisabled.load()},
        {"cacheExactHits",exactHits.load()},{"cacheDerivedHits",derivedHits.load()},{"cacheMisses",cacheMisses.load()},
        {"callCounterScope","process-lifetime"},{"uniqueSourceCounterScope","current-prepare-epoch"},
        {"cacheCounterSemantics","enabled-display-span-lookups; disabled-spans-are-not-misses"},
        {"remeasuredLayoutCalls",remeasuredLayouts.load()},
        {"pictureBoundsSupported",pictureMeasure!=nullptr},{"refreshedPictureBounds",refreshedPictureBounds.load()},
        {"legacyUnicodeCharacters",legacyResolved.load()},{"textEncoding",legacy?"shift-jis":"utf-8"},
        {"writebacksFailed",failures.load()},{"sentenceBoundary",WolfSentence::State()}};
    unsigned rendered=0;ULONGLONG now=GetTickCount64();
    for(auto i=bindings.begin();i!=bindings.end();)if(now-i->second.time>2000)i=bindings.erase(i);else{if(i->second.translated)rendered++;++i;}
    result["activeRenderedBindings"]=rendered;result["bindings"]=bindings.size();
    if(poll){json texts=json::array();while(!pending.empty()&&texts.size()<128){texts.push_back(std::move(pending.front()));pending.pop_front();}result["texts"]=std::move(texts);result["ahead"]=json::array();}
    if(unsupported.load())result["runtimeWarning"]=u8"部分控制码或字符暂未替换，保留原显示。";
    return result;
}
static json Request(const json& q,bool& ok) {
    std::string op=q.value("op","");
    // Speed belongs to the process session, not the editable-data lifecycle.
    // It needs neither a variable scan nor the game's main-thread data queue.
    if(op=="gameSpeed"||op=="speedEnable"||op=="speedDisable")return WolfSpeed::Request(q,ok);
    // Modification may wait for the main thread; do not hold the translation
    // telemetry/cache lock while waiting for it.
    // Revoke speed before that wait: a paused/busy main thread may time out its
    // dataDisable, but must not keep a previously authorized speed writable.
    if(op=="dataDisable")WolfSpeed::Revoke();
    if(op.rfind("data",0)==0)return DataRequest(q,ok);
    std::lock_guard<std::mutex> lock(gate);
    if(op=="translationPrepare"){
        ++epoch;seen.clear();pending.clear();if(!q.value("preserveStartup",false)){enabled=false;cache.Clear();layoutRevision++;}return {{"epoch",epoch}};
    }
    if(op=="translationEnable"){enabled=true;return {{"epoch",epoch}};}
    if(op=="translationDisable"){enabled=false;++epoch;return {{"epoch",epoch}};}
    if(op=="translationApply"){
        if(q.value("epoch",-1)!=epoch)return {{"epoch",epoch},{"stored",0},{"stale",true}};
        int stored=0,checked=0;
        if(q.contains("entries")&&q["entries"].is_array())for(const auto& p:q["entries"]){if(++checked>128)break;if(!p.is_object())continue;
            auto s=p.value("source",""),t=p.value("text","");if(Pair(s,t)&&Store(s,t))stored++;}
        cache.Publish();if(stored)layoutRevision++;
        return {{"epoch",epoch},{"stored",stored}};
    }
    if(op=="translationPoll")return State(true);
    if(op=="translationState")return State(false);
#ifdef FUSION_TEST_CAPTURE
    if(op=="testSentenceState")return WolfSentence::TestState();
    if(op=="testSentenceProbe")return WolfSentenceProbe::Request(q);
    if(op=="testInput"||op=="testPointer")return WolfTestInput::Request(q,ok);
    if(op=="testTrace"){std::lock_guard<std::mutex> l(traceGate);if(q.value("reset",false)){rawTrace=json::array();layoutTrace=json::array();renderTrace=json::array();traced.clear();renderTraced.clear();}return {{"raw",rawTrace},{"layouts",layoutTrace},{"rendered",renderTrace},{"legacy",legacy}};}
    if(op=="testFixture"){std::lock_guard<std::mutex> l(traceGate);fixtureMatch=q.value("match","");fixtureRaw=q.value("raw","");if(legacy){std::string encoded;if(!WolfLegacyText::Encode(fixtureRaw,encoded)){ok=false;fixtureRaw.clear();}else fixtureRaw=std::move(encoded);}fixtureNoRepair=q.value("noRepair",false);fixtureObject=0;layoutTrace=json::array();return {{"ready",ok}};}
    if(op=="testScreen")return WolfTestScreen::Request(q);
    if(op=="testClocks"){LARGE_INTEGER wall,frequency;QueryPerformanceCounter(&wall);QueryPerformanceFrequency(&frequency);return {{"rate",WolfSpeed::rate.load()},{"virtualMs",WolfSpeed::Read(true)},{"wallMs",(double)wall.QuadPart*1000.0/frequency.QuadPart},{"gameCalls",WolfSpeed::gameClockCalls.load()}};}
#endif
    ok=false;return {{"epoch",epoch}};
}
static DWORD WINAPI Worker(void*) {
    json reply;int replyId=0;ULONGLONG nextWake=0;
    for(;;) {
        try {
            if(GetTickCount64()>=nextWake){WolfMainThread::Wake();nextWake=GetTickCount64()+500;}
            if(WaitForSingleObject(owner,0)==WAIT_OBJECT_0){enabled=false;WolfSpeed::Revoke();dataStop=true;WakeDataThread();break;}
            auto stop=Read(L"stop.json",1024);if(stop.is_object()&&stop.value("secret","")==secret){enabled=false;WolfSpeed::Revoke();dataStop=true;WakeDataThread();break;}
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
#include "WolfCollect.h"
extern "C" DWORD WINAPI FusionWolfStart(void*) {
    try {
        if(started.exchange(true))return 0;
        folder=Env(L"FUSION_WOLF_SESSION");secret=Narrow(Env(L"FUSION_WOLF_SECRET"));
        if(folder.empty()||secret.size()!=64||secret.find_first_not_of("0123456789ABCDEF")!=secret.npos)return 0;
        wchar_t* end=nullptr;auto parent=Env(L"FUSION_WOLF_OWNER_PID");unsigned long pid=wcstoul(parent.c_str(),&end,10);if(!pid||!end||*end)return 0;
        owner=OpenProcess(SYNCHRONIZE,FALSE,pid);if(!owner)return 0;
        if(Env(L"FUSION_WOLF_COLLECT")==L"1")return WolfCollect::Start()?1:0;
        auto target=FindPattern(101);void* convertTarget=nullptr;
        if(!target)target=FindPattern(104);
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
        if(legacy)InstallLegacyPictureBounds(target);
        if(!WolfMainThread::Install())return 0;
        if(!WolfSpeed::Initialize())return 0;
        if(!legacy)WolfSentence::Install(Read(L"sentence-profile.json",4*1024*1024),
            WolfCollect::UniquePattern("55 8B EC 51 56 8B 75 0C 57 8B F9 8D 86 00 96 E7 FF 3D 9F 86 01 00 77 22 B8 1F 85 EB 51 F7 EE 8B 47 18 C1 FA 05 8B CA C1 E9 1F 03 CA 2B C1 05 F0 49 02 00 6B C0 64 03 F0 EB 2F 8D 86 90 C5 73 FF 3D 0F 27 00 00 77 22"),
            WolfCollect::UniquePattern("55 8B EC 53 8B 5D 0C 57 8B F9 3B 5F 14 77 32 56 E8 ?? ?? ?? ?? 53 FF 75 08 8B F0 89 5F 10 56 E8 ?? ?? ?? ?? 8D 45 0B C6 45 0B 00 50 8D 04 1E 50 E8 ?? ?? ?? ?? 83 C4 14 8B C7 5E 5F 5B 5D C2 08 00"));
#ifdef FUSION_TEST_CAPTURE
        if(Env(L"FUSION_WOLF_TEST_INPUT")==L"1"&&!WolfTestInput::Install())return 0;
        if(Env(L"FUSION_WOLF_SENTENCE_PROBE")==L"1"&&!WolfSentenceProbe::Install(WolfCollect::UniquePattern("57 56 8b 74 24 10 8b 4c 24 14 8b 7c 24 0c")))return 0;
#endif
        if(MH_EnableHook(MH_ALL_HOOKS)!=MH_OK)return 0;
#ifdef FUSION_TEST_CAPTURE
        if(Env(L"FUSION_WOLF_TEST_INPUT")==L"1")WolfTestScreen::Install(folder);
#endif
        if(!Write(L"hello.json",{{"secret",secret},{"protocol",133},{"engine","wolf"},{"pid",GetCurrentProcessId()}}))return 0;
        HANDLE worker=CreateThread(nullptr,0,Worker,nullptr,0,nullptr);if(!worker)return 0;CloseHandle(worker);return 1;
    }catch(...){return 0;}
}
BOOL APIENTRY DllMain(HMODULE value,DWORD reason,LPVOID) {
    if(reason==DLL_PROCESS_ATTACH){module=value;DisableThreadLibraryCalls(value);}return TRUE;
}

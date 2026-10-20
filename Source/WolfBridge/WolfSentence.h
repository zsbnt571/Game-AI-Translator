#pragma once
#include <intrin.h>
#include <bcrypt.h>
#pragma comment(lib,"bcrypt.lib")
#include "WolfSentenceRules.h"

namespace WolfSentence {
using Getter=void*(__thiscall*)(void*,void*,int,int);
using Assign=void*(__thiscall*)(void*,const char*,unsigned);
static Getter original=nullptr;
static Assign assign=nullptr;
static std::unordered_map<int,std::string> sources;
static std::string status="not-profiled";
static std::atomic<unsigned long long> eligible{0},applied{0},exact{0},derived{0},misses{0},rejected{0},disabled{0},failed{0};
static std::atomic<unsigned long long> paragraphs{0},deferred{0};
#ifdef FUSION_TEST_CAPTURE
static std::mutex traceMutex;
static std::deque<json> observations;
static unsigned long long observationSequence=0;
#endif
static bool Memory(const void* p,void* out,size_t n){
    __try{if(!p)return false;memcpy(out,p,n);return true;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
static bool InvokeAssign(void* out,const char* data,unsigned n){
    __try{assign(out,data,n);return true;}__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
struct Context {int common=0,index=0,id=0,line=0,character=0;bool accepted=false;};
static bool Position(Context& c){
    // This exact EXE's common-local numeric bank. Inspect only bounded vector
    // headers; do not call the numeric getter (which mutates engine scratch).
    auto base=(unsigned char*)GetModuleHandleW(nullptr);uintptr_t outer[3]{},inner[3]{};
    if(!Memory(base+0xda964c,outer,12)||!outer[0]||(outer[0]&3)||outer[1]<outer[0]||outer[2]<outer[1]||
       (outer[1]-outer[0])%12||outer[2]-outer[0]>600000||outer[1]-outer[0]<(c.common+1)*12u)return false;
    if(!Memory((void*)(outer[0]+c.common*12),inner,12)||!inner[0]||(inner[0]&3)||inner[1]<inner[0]||inner[2]<inner[1]||
       (inner[1]-inner[0])%4||inner[2]-inner[0]>40000||inner[1]-inner[0]<128)return false;
    if(!Memory((void*)(inner[0]+30*4),&c.character,4)||!Memory((void*)(inner[0]+31*4),&c.line,4))return false;
    return c.line>=1&&c.line<=1000&&c.character>=1&&c.character<=10000;
}
static Context Inspect(void* interpreter,uintptr_t caller,int id){
    Context c;c.id=id;int type,row,field;
    if(!original||!WolfSentenceRules::SourceId(id,type,row,field)||sources.find(id)==sources.end())return c;
    uintptr_t cmd=0,vector[3]{};int data[6]{};
    auto p=(unsigned char*)interpreter;
    if(!Memory(p+8,&c.index,4)||!Memory(p+0x18,&c.common,4)||!Memory(p+0x884,&cmd,4)||!Memory((void*)cmd,vector,12))return c;
    if(!vector[0]||(vector[0]&3)||vector[1]<vector[0]||vector[1]-vector[0]!=24||vector[2]<vector[1]||vector[2]-vector[0]>4096||!Memory((void*)vector[0],data,24))return c;
    c.accepted=WolfSentenceRules::Site(caller-(uintptr_t)GetModuleHandleW(nullptr),c.common,c.index,data)&&Position(c);return c;
}
struct Paragraph {
    WolfSentenceRules::ParagraphCursor cursor;
    std::string replacement;
    WolfCache::HitKind kind=WolfCache::HitKind::Miss;
    bool selectedEnabled=false,replace=false;
    unsigned long long generation=0;
};
static thread_local Paragraph paragraph;
static void Finished(const Context& c,void* out){
    char raw[24001];size_t n=0;
    if(!ReadRaw(out,raw,&n)){rejected++;return;}
    std::string source(raw,n);auto expected=sources.find(c.id);
    // The interpreter identity and source address alone do not confer write
    // authority: the freshly copied bytes must also equal the audited asset.
    if(expected==sources.end()||expected->second!=source){rejected++;return;}
    eligible++;
    bool on=enabled.load(std::memory_order_relaxed),changed=false;
    if(paragraph.cursor.Advance(c.id,c.common,c.line,c.character)){
        paragraphs++;paragraph.generation++;paragraph.replacement.clear();paragraph.replace=false;paragraph.selectedEnabled=on;paragraph.kind=WolfCache::HitKind::Miss;
        if(on){
            paragraph.replacement=cache.Find(source,&paragraph.kind);
            if(paragraph.kind==WolfCache::HitKind::Exact)exact++;else if(paragraph.kind==WolfCache::HitKind::Derived)derived++;else misses++;
            if(!paragraph.replacement.empty()&&paragraph.replacement!=source){
                paragraph.replace=WolfText::Plain(paragraph.replacement)&&WolfSentenceRules::Pair(source,paragraph.replacement);
                if(!paragraph.replace)rejected++;
            }
        }
    }
    if(on!=paragraph.selectedEnabled)deferred++;
    if(on){
        // Feed complete original paragraphs into the same normal scheduler.
        // There is no provider, variable lookup or database write on this path.
        {std::unique_lock<std::mutex> lock(gate,std::try_to_lock);if(lock&&seen.size()<100000&&pending.size()<4096&&seen.insert(source).second)pending.push_back(source);}
    }else disabled++;
    if(paragraph.replace){
                // out is an independent game-owned copy. Its original caller
                // will compile controls and later destroy it with the game CRT.
                changed=InvokeAssign(out,paragraph.replacement.data(),(unsigned)paragraph.replacement.size());
                if(changed){applied++;std::unique_lock<std::mutex> lock(gate,std::try_to_lock);if(lock&&displayed.size()<100000)displayed.insert(source);}else failed++;
    }
#ifdef FUSION_TEST_CAPTURE
    {std::lock_guard<std::mutex> lock(traceMutex);
        observations.push_back({{"sequence",++observationSequence},{"source",source},{"effective",changed?paragraph.replacement:source},{"sourceId",c.id},{"commonId",c.common},{"commandIndex",c.index},{"enabled",on},{"selectionEnabled",paragraph.selectedEnabled},{"paragraphGeneration",paragraph.generation},{"line",c.line},{"character",c.character},{"hit",paragraph.kind==WolfCache::HitKind::Exact?"exact":paragraph.kind==WolfCache::HitKind::Derived?"derived":"miss"},{"replaced",changed}});
        while(observations.size()>64)observations.pop_front();
    }
#endif
}
static void* __fastcall Hook(void* interpreter,void*,void* out,int id,int flags){
    auto c=Inspect(interpreter,(uintptr_t)_ReturnAddress(),id);
    auto result=original(interpreter,out,id,flags);
    if(c.accepted)try{Finished(c,out);}catch(...){failed++;}
    return result;
}
static std::string ExeHash(){
    wchar_t path[32768];DWORD n=GetModuleFileNameW(nullptr,path,32768);if(!n||n>=32768)return {};
    std::error_code error;auto size=fs::file_size(fs::path(path),error);if(error||size>64*1024*1024)return {};
    BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;std::string result;
    if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return {};
    if(BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)>=0){
        std::ifstream file(fs::path(path),std::ios::binary);unsigned char bytes[65536],digest[32];bool ok=true;
        while(file){file.read((char*)bytes,sizeof(bytes));auto count=file.gcount();if(count>0&&BCryptHashData(hash,bytes,(ULONG)count,0)<0){ok=false;break;}}
        if(!file.eof())ok=false;
        if(ok&&BCryptFinishHash(hash,digest,32,0)>=0){const char* digits="0123456789abcdef";for(auto v:digest){result+=digits[v>>4];result+=digits[v&15];}}
        BCryptDestroyHash(hash);
    }
    BCryptCloseAlgorithmProvider(algorithm,0);return result;
}
static json State(){return {{"installed",original!=nullptr},{"profile",original?"rignetta-3.100-display-v1":""},{"status",status},
    {"eligibleCalls",eligible.load()},{"appliedCalls",applied.load()},{"exactHits",exact.load()},{"derivedHits",derived.load()},
    {"misses",misses.load()},{"rejected",rejected.load()},{"disabledCalls",disabled.load()},{"assignmentFailed",failed.load()},
    {"sourceFields",sources.size()},{"paragraphs",paragraphs.load()},{"deferredSwitchCalls",deferred.load()},
    {"updatePolicy","next-paragraph-or-game-redisplay; persistent-character-pictures-are-not-rebuilt"},
    {"boundary","owned-raw-display-copy-before-control-compilation"}};}
#ifdef FUSION_TEST_CAPTURE
static json TestState(){auto value=State();std::lock_guard<std::mutex> lock(traceMutex);value["observations"]=observations;value["observationSequence"]=observationSequence;return value;}
#endif
// Caller supplies the unique executable-section pattern resolver after the
// ordinary WOLF hooks have been found. A mismatch leaves normal rendering on.
static bool Install(const json& profile,void* getter,void* assignment){
    static const std::string exeHash="87f7406acd7cb7df133c4e71a5a4556c2c393c86117457d2c083ea47d6c47d5e";
    try{
        if(!profile.is_object())return false;
        status="profile-rejected";
        if(profile.value("version",0)!=1||profile.value("profileId","")!="rignetta-3.100-display-v1"||profile.value("secret","")!=secret||profile.value("exeSha256","")!=exeHash||ExeHash()!=exeHash)return false;
        auto hashes=profile.at("resourceHashes");
        if(hashes.value("Data/BasicData/CommonEvent.dat","")!="f660ed58a9f2cfc5ded72d870914cc11e964dab8d1d7d531fd04c20c4804d9ef"||
           hashes.value("Data/BasicData/DataBase.dat","")!="3fffe3db34cbb21df6f161ff06439ff9c75f272053a509cbcbccabaef87ef3d4"||
           hashes.value("Data/BasicData/DataBase.project","")!="558cdc8a3756f8648cf3d76bffda1a9d05acfbf18dff98355d5c4a492fc86018")return false;
        if(!profile.contains("sources")||!profile["sources"].is_array()||profile["sources"].size()>4000)return false;
        std::unordered_map<int,std::string> accepted;
        for(const auto& s:profile["sources"]){
            int id=s.at("encodedSourceId").get<int>(),type,row,field;auto text=s.at("source").get<std::string>();
            if(!WolfSentenceRules::SourceId(id,type,row,field)||s.value("type",-1)!=type||s.value("row",-1)!=row||s.value("field",-1)!=field||!WolfText::Plain(text)||text.size()>24000||!accepted.emplace(id,text).second)return false;
        }
        auto base=(unsigned char*)GetModuleHandleW(nullptr);unsigned char call[5];
        if(getter!=base+0xa6c70||assignment!=base+0xc8ce0||!Memory(base+0xb742f,call,5)||call[0]!=0xe8)return false;
        int32_t distance;memcpy(&distance,call+1,4);if(base+0xb7434+distance!=getter)return false;
        if(accepted.empty())return false;
        assign=(Assign)assignment;sources=std::move(accepted);
        if(MH_CreateHook(getter,(void*)Hook,(void**)&original)!=MH_OK){sources.clear();original=nullptr;return false;}
        status="ready";return true;
    }catch(...){status="profile-rejected";sources.clear();return false;}
}
}

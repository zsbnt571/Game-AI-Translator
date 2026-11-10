#pragma once
// Optional UE4.27 public FSlateFontInfo filename constructor. Engine symbols
// and ABI are checked by SlateBridge before this path can receive a request.
// No reflected UFontFace fields, private FontFilename hash, or live widget
// FontInfo is written. Existing style/size/material/outline stays in place.
#include "../WolfBridge/WolfFontSelection.h"
#include <ctime>
#include <array>
namespace SlateFont {
using Constructor=void(__fastcall*)(void*,const wchar_t*,int,unsigned char);
using Composite=const void*(__fastcall*)(const void*);
using KeyConstructor=void(__fastcall*)(void*,const void*,const void*,float);
inline Constructor construct=nullptr;
inline Composite original=nullptr;
inline KeyConstructor originalKey=nullptr;
inline unsigned identityOffsets[3]={};
inline std::atomic<bool> installed{false};
inline std::atomic<DWORD> gameThread{0};
inline std::atomic<unsigned long long> revision{0},substitutions{0};
struct Generation {
    alignas(16) unsigned char fontInfo[88]={};
    std::wstring path,hash;
    WolfFont::StartupSelection validation;
    HANDLE file=INVALID_HANDLE_VALUE;
    HFONT coverageFont=nullptr;
    size_t fileBytes=0;
    std::mutex coverageGate;
    std::unordered_map<std::wstring,bool> coverage;
    const void* composite=nullptr;
    ~Generation(){if(coverageFont)DeleteObject(coverageFont);if(file!=INVALID_HANDLE_VALUE)CloseHandle(file);}
};
// Unreal can keep pointers into a CompositeFont in its glyph caches. Retain
// constructed generations until process teardown; never call an inline or
// guessed destructor. Reusing a hash reuses that same native generation.
inline std::vector<std::shared_ptr<Generation>> retained;
inline size_t retainedFileBytes=0;
inline std::shared_ptr<Generation> current;
struct CoverageScope {std::shared_ptr<Generation> selected;std::wstring text;bool eligible=false,frozen=false;};
inline thread_local std::array<CoverageScope,32> coverageScopes;
inline thread_local unsigned coverageDepth=0,coverageOverflow=0;
inline std::atomic<unsigned long long> coverageRejected{0},coverageUnknown{0};
inline void BeginScope(){if(coverageDepth>=coverageScopes.size()){++coverageOverflow;return;}auto& scope=coverageScopes[coverageDepth++];scope.selected=std::atomic_load(&current);scope.text.clear();scope.eligible=false;scope.frozen=false;}
inline void EndScope(){if(coverageOverflow){--coverageOverflow;return;}if(coverageDepth){auto& scope=coverageScopes[--coverageDepth];scope.selected.reset();scope.text.clear();}}
inline bool HasGlyphs(Generation& selected,const std::wstring& text){
    if(text.empty()||text.size()>6000||!selected.coverageFont)return false;
    std::lock_guard<std::mutex> lock(selected.coverageGate);
    auto cached=selected.coverage.find(text);if(cached!=selected.coverage.end())return cached->second;
    bool covered=true;std::wstring printable;
    for(wchar_t c:text){
        if(c==L'\r'||c==L'\n'||c==L'\t')continue;
        // GDI maps UTF-16 code units here. Do not treat a surrogate half as
        // evidence of a complete supplementary glyph or shaping sequence.
        if(c<32||(c>=0xd800&&c<=0xdfff)){covered=false;break;}printable+=c;
    }
    HDC dc=covered?CreateCompatibleDC(nullptr):nullptr;HGDIOBJ previous=dc?SelectObject(dc,selected.coverageFont):nullptr;
    if(covered){
        // The immutable HFONT's exact file identity was checked once on the
        // request worker. Do not read an entire CJK font on every display tick.
        if(!dc||!previous||previous==HGDI_ERROR)covered=false;
        else if(!printable.empty()){
            std::vector<WORD> glyphs(printable.size());
            if(GetGlyphIndicesW(dc,printable.data(),(int)printable.size(),glyphs.data(),GGI_MARK_NONEXISTING_GLYPHS)==GDI_ERROR)covered=false;
            else for(WORD index:glyphs)if(index==0||index==0xffff){covered=false;break;}
        }
    }
    if(previous&&previous!=HGDI_ERROR)SelectObject(dc,previous);if(dc)DeleteDC(dc);
    if(selected.coverage.size()<128)selected.coverage.emplace(text,covered);
    return covered;
}
inline void ConsiderText(const std::wstring& text) noexcept{
    if(!coverageDepth||coverageOverflow)return;auto& scope=coverageScopes[coverageDepth-1];if(!scope.selected)return;
    if(scope.frozen){if(scope.text!=text)scope.eligible=false;return;}
    // Conflicting complete strings in one widget scope do not establish a
    // single complete display. Retain the engine face conservatively.
    if(!scope.text.empty()&&scope.text!=text){scope.eligible=false;scope.frozen=true;++coverageRejected;return;}
    try{scope.text=text;scope.eligible=HasGlyphs(*scope.selected,text);}catch(...){scope.eligible=false;scope.frozen=true;}
    if(!scope.eligible)++coverageRejected;
}
inline std::shared_ptr<Generation> ForScope(){
    if(!depth||!coverageDepth||coverageOverflow)return {};auto& scope=coverageScopes[coverageDepth-1];
    if(!scope.frozen&&scope.selected&&scope.text.empty())++coverageUnknown;
    scope.frozen=true;return scope.eligible?scope.selected:nullptr;
}
struct Command {unsigned id=0;ULONGLONG expires=0;std::shared_ptr<Generation> value;};
inline std::mutex commandGate;
inline Command pending;
inline unsigned completed=0,lastSeen=0;
inline bool accepted=false;
inline std::string reason="native-font-construction-unavailable";
inline bool Available(){return installed.load()&&gameThread.load()!=0;}
inline bool Construct(Generation* value) noexcept {
    __try{
        // The constructor owns its internal FString/TSharedPtr allocations.
        // 16 is only a seed: consumers keep the original FontInfo's actual size.
        construct(value->fontInfo,value->path.c_str(),16,0);
        value->composite=original(value->fontInfo);
        MEMORY_BASIC_INFORMATION m={};
        return value->composite&&VirtualQuery(value->composite,&m,sizeof(m))&&m.State==MEM_COMMIT&&!(m.Protect&(PAGE_NOACCESS|PAGE_GUARD));
    }__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
inline void Service(){
    if(!installed.load())return;
    DWORD unset=0;gameThread.compare_exchange_strong(unset,GetCurrentThreadId());
    if(gameThread.load()!=GetCurrentThreadId())return;
    std::unique_lock<std::mutex> lock(commandGate,std::try_to_lock);if(!lock||!pending.id)return;
    auto command=std::move(pending);pending={};completed=command.id;accepted=false;
    if(GetTickCount64()>=command.expires){reason="font-request-expired";return;}
    if(command.value&&!command.value->composite){
        // Reserve lifetime before calling engine code, even if construction
        // fails after allocating part of its internal native data.
        if(retained.size()>=32||retainedFileBytes+command.value->fileBytes>128*1024*1024){reason="font-generation-budget";return;}
        retainedFileBytes+=command.value->fileBytes;
        retained.push_back(command.value);
        if(!Construct(command.value.get())){reason="native-font-construction-failed";return;}
    }
    std::atomic_store(&current,std::move(command.value));++revision;
    accepted=true;reason="accepted";
}
inline const void* __fastcall GetComposite(const void* font){
    // The parent's depth covers native text paint/measure only. A font is
    // independent of localization and stays selected when text is disabled.
    if(depth){auto selected=ForScope();if(selected&&selected->composite){++substitutions;return selected->composite;}}
    return original(font);
}
inline bool Borrow(const void* source,const Generation* selected,unsigned char* target) noexcept {
    __try{
        memcpy(target,source,88);
        // Borrow TSharedPtr storage only during a const native call. The
        // constructed generation remains pinned; the native FSlateFontKey
        // constructor performs its own ordinary reference-counted copy.
        const unsigned sizes[]={8,16,8};
        for(unsigned i=0;i<3;i++)memcpy(target+identityOffsets[i],selected->fontInfo+identityOffsets[i],sizes[i]);
        return true;
    }__except(EXCEPTION_EXECUTE_HANDLER){return false;}
}
inline void __fastcall MakeKey(void* self,const void* font,const void* outline,float scale){
    auto selected=ForScope();
    alignas(16) unsigned char borrowed[88];
    if(selected&&Borrow(font,selected.get(),borrowed)){originalKey(self,borrowed,outline,scale);return;}
    originalKey(self,font,outline,scale);
}
inline void Submit(const json& request){
    unsigned id=request.value("id",0u);std::lock_guard<std::mutex> lock(commandGate);
    if(!id||id<=lastSeen)return;lastSeen=id;
    auto fail=[&](const char* value){completed=id;accepted=false;reason=value;};
    auto seconds=request.value("expiresUnix",0ll)-(long long)std::time(nullptr);
    if(seconds<=0||seconds>5){fail("font-request-expired");return;}
    if(!Available()){fail("native-font-construction-unavailable");return;}
    if(pending.id){fail("font-request-busy");return;}
    std::string mode=request.value("mode","");std::shared_ptr<Generation> value;
    if(mode=="custom"){
        auto path=Wide(request.value("path","")),family=Wide(request.value("family","")),hash=Wide(request.value("sha256",""));
        if(path.empty()||path.size()>240||hash.size()!=64||family.empty()){fail("font-file-identity-invalid");return;}
        // File locking precedes validation and remains in force while Unreal
        // can perform a deferred font load. No write/delete sharing is granted.
        value=std::make_shared<Generation>();value->path=path;value->hash=hash;
        value->file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
        if(value->file==INVALID_HANDLE_VALUE||!value->validation.Configure(path,family,hash)){fail("font-file-validation-failed");return;}
        LARGE_INTEGER fileSize={};if(!GetFileSizeEx(value->file,&fileSize)||fileSize.QuadPart<12||fileSize.QuadPart>32*1024*1024){fail("font-file-validation-failed");return;}value->fileBytes=(size_t)fileSize.QuadPart;
        value->coverageFont=CreateFontW(-32,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,DEFAULT_QUALITY,DEFAULT_PITCH,family.c_str());
        if(!value->coverageFont){fail("font-coverage-validation-unavailable");return;}
        HDC proof=CreateCompatibleDC(nullptr);HGDIOBJ previous=proof?SelectObject(proof,value->coverageFont):nullptr;
        bool exact=previous&&previous!=HGDI_ERROR&&value->validation.Matches(proof);
        if(previous&&previous!=HGDI_ERROR)SelectObject(proof,previous);if(proof)DeleteDC(proof);
        if(!exact){fail("font-coverage-validation-unavailable");return;}
        for(const auto& previous:retained)if(previous->composite&&previous->path==path&&previous->hash==hash){value=previous;break;}
    }else if(mode!="default"){fail("font-mode-invalid");return;}
    pending={id,GetTickCount64()+(ULONGLONG)seconds*1000,std::move(value)};reason="pending-native-text-frame";
}
inline json State(){
    std::lock_guard<std::mutex> lock(commandGate);
    if(pending.id&&GetTickCount64()>=pending.expires){completed=pending.id;pending={};accepted=false;reason="font-request-expired";}
    return {{"customFontSupported",Available()},{"customFontActive",(bool)std::atomic_load(&current)},
        {"independent",true},{"independentOfTranslation",true},{"restartRequired",false},
        {"scope","native-slate-local-pdb-4.27"},{"cacheIdentity","native-FSlateFontKey-constructor"},{"id",completed},{"pendingId",pending.id},{"accepted",accepted},
        {"reason",reason},{"revision",revision.load()},{"substitutions",substitutions.load()},{"retainedGenerations",retained.size()},
        {"coveragePolicy","whole-observed-display-or-original"},{"coverageRejected",coverageRejected.load()},{"coverageUnknown",coverageUnknown.load()},{"retainedFileBytes",retainedFileBytes}};
}
}

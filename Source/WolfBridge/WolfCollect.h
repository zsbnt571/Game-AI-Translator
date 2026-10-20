// Engine-assisted collection is enabled only in an isolated, disposable game
// copy. The parser runs on its original calling thread; no event interpreter,
// save command or game-modification operation is invoked by this collector.
namespace WolfCollect {
static void* mapOriginal=nullptr;static void* copyOriginal=nullptr;
using Map=bool(__thiscall*)(void*,const char*,int);
using Copy=void*(__cdecl*)(void*,const void*,size_t);
using First=int(__cdecl*)(const wchar_t*,void*);
using Next=int(__cdecl*)(int,void*);
using Close=int(__cdecl*)(int);
static First first=nullptr;static Next next=nullptr;static Close closeFind=nullptr;
static uintptr_t* vfsTable=nullptr;
static bool cp932=false;
static bool Invoke(void* self,const char* path,int mode){return cp932?((bool(__thiscall*)(void*,const char*))mapOriginal)(self,path):((Map)mapOriginal)(self,path,mode);}
static thread_local bool collecting=false,busy=false;
static thread_local std::string current;
static std::atomic<bool> ran{false};static std::mutex collectGate;
static std::atomic<DWORD> captureThread{0};static std::atomic<bool> captureEnabled{true};
static std::atomic<bool> warmup{false};static bool mapComplete=false;static std::string inputPath;static int inputMode=0;
static void* drawOriginal=nullptr;static thread_local bool displaySource=false;
static json records=json::array(),maps=json::array();
static std::unordered_set<std::string> unique;static unsigned long long total=0;
static unsigned rejects=0;static bool limited=false;
static bool InImage(uintptr_t p,bool code=false){
    auto b=(unsigned char*)GetModuleHandleW(nullptr);auto d=(IMAGE_DOS_HEADER*)b;auto n=(IMAGE_NT_HEADERS32*)(b+d->e_lfanew);
    if(p<(uintptr_t)b||p>=(uintptr_t)b+n->OptionalHeader.SizeOfImage)return false;
    if(!code)return true;auto s=IMAGE_FIRST_SECTION(n);for(unsigned i=0;i<n->FileHeader.NumberOfSections;i++)if((s[i].Characteristics&IMAGE_SCN_MEM_EXECUTE)&&p>=(uintptr_t)b+s[i].VirtualAddress&&p<(uintptr_t)b+s[i].VirtualAddress+s[i].Misc.VirtualSize)return true;return false;
}
static void* UniquePattern(const std::string& text){
    std::vector<int> pattern;std::istringstream input(text);std::string item;while(input>>item)pattern.push_back(item=="??"?-1:std::stoi(item,nullptr,16));if(pattern.size()<13||pattern.size()>256)return nullptr;
    auto b=(unsigned char*)GetModuleHandleW(nullptr);auto d=(IMAGE_DOS_HEADER*)b;auto n=(IMAGE_NT_HEADERS32*)(b+d->e_lfanew);auto s=IMAGE_FIRST_SECTION(n);void* found=nullptr;
    for(unsigned j=0;j<n->FileHeader.NumberOfSections;j++)if(s[j].Characteristics&IMAGE_SCN_MEM_EXECUTE){
        size_t size=s[j].Misc.VirtualSize;if(size<pattern.size()||s[j].VirtualAddress+size>n->OptionalHeader.SizeOfImage)return nullptr;
        for(size_t i=0;i<=size-pattern.size();i++)if(WolfText::Match(b+s[j].VirtualAddress+i,pattern)){if(found)return nullptr;found=b+s[j].VirtualAddress+i;}
    }return found;
}
static void* Call(void* hit,unsigned displacement){
    if(!hit)return nullptr;auto p=(unsigned char*)hit+displacement;
    if(p[-1]!=0xe8)return nullptr;auto result=p+4+*(int32_t*)p;return InImage((uintptr_t)result,true)?result:nullptr;
}
static size_t Snapshot(const char* bytes,size_t n,char* out){
    __try{if(!bytes||n<2)return 0;size_t limit=(std::min)(n+1,(size_t)24001);
        size_t length=0;for(;length<limit;length++){char c=bytes[length];out[length]=c;if(!c)break;}
        return length>=2&&length<limit?length:0;
    }__except(EXCEPTION_EXECUTE_HANDLER){return 0;}
}
static void Observe(const void* src,size_t n){
    DWORD thread=GetCurrentThreadId(),empty=0;captureThread.compare_exchange_strong(empty,thread);
    if(captureEnabled.load(std::memory_order_relaxed)&&captureThread.load(std::memory_order_relaxed)==thread&&!busy&&n>=2&&(!ran.load()||collecting||warmup.load())){
        busy=true;char buffer[24002];
        size_t length=Snapshot((const char*)src,n,buffer);
        int wideLength=length?MultiByteToWideChar(cp932?932:CP_UTF8,MB_ERR_INVALID_CHARS,buffer,(int)length,nullptr,0):0;
        if(wideLength>0){
            try{std::lock_guard<std::mutex> guard(collectGate);std::string s(buffer,length);
            if(cp932){std::wstring wide(wideLength,0);MultiByteToWideChar(932,MB_ERR_INVALID_CHARS,buffer,(int)length,wide.data(),wideLength);s=Narrow(wide);}
            if(s.find_first_of("\x01\x02\x03\x04\x05\x06\x07\x08")!=s.npos)rejects++;
            else if(unique.size()<100000&&total+length<24*1024*1024&&records.size()<200000){
                std::string file=displaySource?"engine-display":collecting?current:"engine-initialization";
                std::string key=file+"\n"+s;if(unique.insert(key).second){total+=length;records.push_back({{"file",file},{"text",s}});}
            }else limited=true;}catch(...){limited=true;}
        }busy=false;
    }
}
static void* __cdecl Capture(void* dst,const void* src,size_t n){Observe(src,n);return ((Copy)copyOriginal)(dst,src,n);}
static size_t __cdecl Length(const char* src){size_t n=((size_t(__cdecl*)(const char*))copyOriginal)(src);Observe(src,n);return n;
}
static void __cdecl DrawCollect(uintptr_t* frame) noexcept {
    if(!warmup.load()||busy)return;
    try{
        char raw[24001];size_t n=0;void* object=(unsigned char*)frame[6]+(cp932?32:24);if(!ReadRaw(object,raw,&n))return;
        std::vector<WolfText::Run> runs;if(!WolfText::Runs(std::string(raw,n),runs,cp932))return;
        displaySource=true;for(const auto& run:runs)if(run.display)Observe(run.bytes.c_str(),run.bytes.size());displaySource=false;
    }catch(...){displaySource=false;}
}
static __declspec(naked) void DrawThunk(){__asm{
    pushfd
    pushad
    mov ebp,esp
    sub esp,528
    and esp,-16
    fxsave [esp]
    push ebp
    call DrawCollect
    add esp,4
    fxrstor [esp]
    mov esp,ebp
    popad
    popfd
    jmp drawOriginal
}}
static bool SafeName(const std::wstring& name){return !name.empty()&&name!=L"."&&name!=L".."&&name.find_first_of(L"/\\:")==name.npos&&name.size()<256;}
static void Enumerate(const std::wstring& directory,std::vector<std::wstring>& result,unsigned depth=0){
    if(depth>16||result.size()>=5000){limited=true;return;}
    alignas(8) unsigned char info[1024]={};int handle=first((directory+L"/*").c_str(),info);if(handle==-1)return;
    unsigned count=0;try{
        do{if(++count>100000){limited=true;break;}auto name=(wchar_t*)info;size_t length=0;while(length<260&&name[length])length++;
            if(length>=260){limited=true;break;}std::wstring part(name,length);if(!SafeName(part))continue;
            std::wstring path=directory+L"/"+part;if(*(int*)(info+520))Enumerate(path,result,depth+1);
            else if(part.size()>4&&_wcsicmp(part.c_str()+part.size()-4,L".mps")==0)result.push_back(path);
            if(result.size()>=5000){limited=true;break;}
        }while(next(handle,info)==0);
    }catch(...){closeFind(handle);throw;}closeFind(handle);
}
static DWORD WINAPI Finish(void*){
    Sleep(2000);captureEnabled=false;std::lock_guard<std::mutex> guard(collectGate);
    Write(L"collection.json",{{"secret",secret},{"version",2},{"complete",mapComplete&&!limited},
        {"maps",maps},{"records",records},{"limited",limited},{"rejected",rejects},
        {"thread",captureThread.load()},{"inputPath",inputPath},{"mode",inputMode},{"initializationWarmup",true},{"method","engine-map-loader"}});
    ExitProcess(0);
}
static bool __fastcall Load(void* self,void*,const char* path,int mode){
    if(ran.exchange(true))return Invoke(self,path,mode);
    captureThread=GetCurrentThreadId();
    try{
        inputPath=path?path:"";inputMode=mode;
        if(!vfsTable)throw std::runtime_error("missing-vfs");for(int i=0;i<14;i++)if(!InImage(vfsTable[i],true))throw std::runtime_error("invalid-vfs");
        first=(First)vfsTable[11];next=(Next)vfsTable[12];closeFind=(Close)vfsTable[13];
        Write(L"collection-progress.json",{{"phase","enumerating"},{"secret",secret}});
        std::vector<std::wstring> paths;Enumerate(L"Data",paths);std::sort(paths.begin(),paths.end());paths.erase(std::unique(paths.begin(),paths.end()),paths.end());
        unsigned complete=0;ULONGLONG start=GetTickCount64();
        for(const auto& p:paths){
            if(GetTickCount64()-start>120000){limited=true;break;}
            auto stop=Read(L"stop.json",1024);if(stop.is_object()&&stop.value("secret","")==secret){limited=true;break;}
            current=Narrow(p);Write(L"collection-progress.json",{{"phase","loading"},{"secret",secret},{"file",current},{"loaded",complete},{"total",paths.size()}});
            std::string relative=current.rfind("Data/",0)==0?current.substr(4):current;
            if(cp932){std::wstring wide=p.rfind(L"Data/",0)==0?p.substr(4):p;BOOL fallback=FALSE;int n=WideCharToMultiByte(932,WC_NO_BEST_FIT_CHARS,wide.data(),(int)wide.size(),nullptr,0,nullptr,&fallback);if(n<=0||fallback){limited=true;continue;}relative.resize(n);WideCharToMultiByte(932,WC_NO_BEST_FIT_CHARS,wide.data(),(int)wide.size(),relative.data(),n,nullptr,&fallback);if(fallback){limited=true;continue;}}
            collecting=true;bool ok=Invoke(self,relative.c_str(),mode);collecting=false;
            maps.push_back({{"file",current},{"ok",ok}});if(ok)complete++;
        }
        mapComplete=!limited&&complete==paths.size()&&!paths.empty();
        // Restore the requested map only in the disposable process, then allow
        // a bounded first-screen warmup. This catches initialization-time text
        // concatenation without sending any input or affecting a live session.
        warmup=true;bool result=Invoke(self,inputPath.c_str(),mode);
        Write(L"collection-progress.json",{{"secret",secret},{"phase","initialization-warmup"},{"loaded",complete},{"total",paths.size()}});
        HANDLE worker=CreateThread(nullptr,0,Finish,nullptr,0,nullptr);if(!worker)throw std::runtime_error("warmup-worker");CloseHandle(worker);return result;
    }catch(...){collecting=false;Write(L"collection.json",{{"secret",secret},{"version",2},{"complete",false},{"error","collection-exception"}});ExitProcess(10);}
}
static bool __fastcall Load2(void* self,void*,const char* path){return Load(self,nullptr,path,0);}
static bool Start(){
    wchar_t exe[32768];DWORD len=GetModuleFileNameW(nullptr,exe,32768);if(!len||len>=32768)return false;
    if(!fs::is_regular_file(fs::path(exe).parent_path()/L".fusion-test-copy"))return false;
    void* map=Call(UniquePattern("8d 4d ?? e8 ?? ?? ?? ?? 50 8d 8e ?? ?? ?? ?? e8 ?? ?? ?? ?? 84 c0 75 ??"),16);
    void* second=Call(UniquePattern("6a 01 8d 4d ?? c6 45 ?? 01 e8 ?? ?? ?? ?? 50 8d 8d ?? ?? ?? ?? e8 ?? ?? ?? ?? 84 c0 75 0c"),22);
    if(!map&&!second&&Env(L"FUSION_WOLF_COLLECTION_PROFILE")==L"3.714")map=second=Call(UniquePattern("c6 45 fc 01 8d 45 ?? 83 7d ?? 0f 8d 8d ?? ?? ?? ff 6a 01"),25);
    if(!map&&!second&&Env(L"FUSION_WOLF_COLLECTION_PROFILE")==L"2.271"){
        map=second=UniquePattern("55 8b ec 6a ff 68 ?? ?? ?? ?? 64 a1 00 00 00 00 50 64 89 25 00 00 00 00 81 ec f0 04 00 00");cp932=true;
    }
    void* copy=UniquePattern("57 56 8b 74 24 10 8b 4c 24 14 8b 7c 24 0c");
    if(cp932)copy=UniquePattern("8B 4C 24 04 F7 C1 03 00 00 00 74 24 8A 01 83 C1 01 84 C0 74 4E F7 C1 03 00 00 00 75 EF 05 00 00 00 00 8D A4 24 00 00 00 00 8D A4 24 00 00 00 00 8B 01");
    auto tableHit=(unsigned char*)UniquePattern("6a 00 6a 01 6a 00 ?? ff 15 ?? ?? ?? ?? 83 c4 10 8b d8 85 f6 74 ??");
    if(!map||map!=second||!copy||memcmp(map,"\x55\x8b\xec\x6a\xff",5))return false;
    if(tableHit)vfsTable=*(uintptr_t**)(tableHit+9);
    else {tableHit=(unsigned char*)UniquePattern("33 c0 83 ?? ?? ?? ?? ?? 01 0f ?? ?? ?? 00 00 c7 05 ?? ?? ?? ?? ?? ?? ?? ?? c7 05");if(tableHit)vfsTable=*(uintptr_t**)(tableHit+17);}
    if(!vfsTable&&cp932){tableHit=(unsigned char*)UniquePattern("6a 00 6a 01 6a 00 ?? ff 15 ?? ?? ?? ?? 83 c4 10 85 ?? 8b f0 74 09");if(tableHit&&tableHit[6]==0x56)vfsTable=*(uintptr_t**)(tableHit+9);}
    if(!vfsTable||!InImage((uintptr_t)vfsTable)||!InImage((uintptr_t)(vfsTable+14)))return false;
    Write(L"collection-progress.json",{{"secret",secret},{"phase","waiting-for-map"},{"mapRva",(uintptr_t)map-(uintptr_t)GetModuleHandleW(nullptr)},
        {"copyRva",(uintptr_t)copy-(uintptr_t)GetModuleHandleW(nullptr)},{"tableRva",(uintptr_t)vfsTable-(uintptr_t)GetModuleHandleW(nullptr)}});
    auto draw=cp932?FindPattern(102):FindPattern(101);if(!draw&&!cp932)draw=FindPattern(104);
    if(!draw)return false;
    return MH_Initialize()==MH_OK&&MH_CreateHook(map,cp932?(void*)Load2:(void*)Load,&mapOriginal)==MH_OK&&MH_CreateHook(copy,cp932?(void*)Length:(void*)Capture,&copyOriginal)==MH_OK&&MH_CreateHook(draw,(void*)DrawThunk,&drawOriginal)==MH_OK&&MH_EnableHook(MH_ALL_HOOKS)==MH_OK;
}
}

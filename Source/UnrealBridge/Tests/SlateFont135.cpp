// Controlled ABI/PDB and transaction check. This is not Unreal Engine and
// cannot establish real Slate glyph/layout/pixel compatibility.
#include "../SlateBridge.cpp"
#include <thread>
#include <iostream>
enum class EFontHinting : unsigned char {Default};
static volatile bool fixtureFail=false;
static unsigned char fixtureComposite[64]={};
struct FFontOutlineSettings {unsigned char bytes[32]={};};
struct FixtureShared {const void* data=nullptr;void* counter=nullptr;};
struct FSlateFontInfo {
 const void* FontObject=nullptr;void* FontMaterial=nullptr;FFontOutlineSettings OutlineSettings;
 FixtureShared CompositeFont;unsigned long long TypefaceFontName=0;int Size=0,LetterSpacing=0;unsigned char tail[8]={};
 __declspec(noinline) FSlateFontInfo(const wchar_t* path,int size,EFontHinting hint){
  CompositeFont.data=fixtureFail?nullptr:fixtureComposite;Size=size;
  tail[0]=(unsigned char)hint;if(path&&*path)tail[1]=(unsigned char)*path;
 }
 __declspec(noinline) FSlateFontInfo(const char* path,int size,EFontHinting hint){
  CompositeFont.data=fixtureComposite;Size=size;tail[0]=(unsigned char)hint;if(path)tail[1]=(unsigned char)*path;
 }
 __declspec(noinline) const void* GetCompositeFont()const{
  if(fixtureFail&&Size==0xff)return nullptr;
  return CompositeFont.data;
 }
};
struct FSlateFontKey {
 FSlateFontInfo FontInfo;FFontOutlineSettings OutlineSettings;float Scale;
 __declspec(noinline) FSlateFontKey(const FSlateFontInfo& info,const FFontOutlineSettings& outline,float scale)
  :FontInfo(info),OutlineSettings(outline),Scale(scale){if(fixtureFail)Scale=scale+1;}
};
static_assert(sizeof(FSlateFontInfo)==88);
int wmain(int argc,wchar_t** argv){
 if(argc!=4)return 2;json checks=json::array();
 auto check=[&](const char* name,bool value){checks.push_back({{"name",name},{"passed",value}});};
 // Keep both overloads in this executable's real MSVC PDB.
 FSlateFontInfo fixture(L"seed",16,EFontHinting::Default),ansi("seed",16,EFontHinting::Default);
 auto unused=fixture.GetCompositeFont();if(!unused||!ansi.GetCompositeFont())return 3;
 FFontOutlineSettings outline;FSlateFontKey key(fixture,outline,1.0f);
 wchar_t exe[32768];GetModuleFileNameW(nullptr,exe,32768);
 HANDLE symbols=CreateEventW(nullptr,TRUE,FALSE,nullptr);
 SymSetOptions(SYMOPT_UNDNAME|SYMOPT_EXACT_SYMBOLS|SYMOPT_FAIL_CRITICAL_ERRORS|SYMOPT_NO_PROMPTS|SYMOPT_IGNORE_NT_SYMPATH);
 bool initialized=SymInitializeW(symbols,fs::path(exe).parent_path().c_str(),FALSE)!=FALSE;
 auto base=initialized?SymLoadModuleExW(symbols,nullptr,exe,nullptr,(DWORD64)GetModuleHandleW(nullptr),0,nullptr,0):0;
 void* ctor=nullptr;void* composite=nullptr;void* keyCtor=nullptr;
 bool found=base&&FontConstructor(symbols,base,&ctor)&&Function(symbols,base,"FSlateFontInfo::GetCompositeFont",1,SymTagPointerType,8,&composite);
 check("matching-local-PDB-selects-wchar-int32-uint8-constructor",found);
 bool identity=base&&FontIdentityFields(symbols,base)&&FontKeyConstructor(symbols,base,&keyCtor);
 check("matching-local-PDB-validates-cache-key-ABI-and-identity-fields",identity);
 SymCleanup(symbols);CloseHandle(symbols);
 if(found&&identity){
  SlateFont::construct=(SlateFont::Constructor)ctor;SlateFont::original=(SlateFont::Composite)composite;SlateFont::installed=true;
  SlateFont::originalKey=(SlateFont::KeyConstructor)keyCtor;
  check("no-UI-thread-yet-is-unavailable",!SlateFont::Available());SlateFont::Service();
  check("observed-native-scope-enables-request-gate",SlateFont::Available());
  unsigned serial=0;
  auto request=[&](std::string mode,std::string hash){return json{{"id",++serial},{"expiresUnix",(long long)std::time(nullptr)+5},{"mode",mode},{"path",Utf8(argv[1])},{"family","Consolas"},{"sha256",hash}};};
  auto sha=Utf8(argv[2]);auto originalBytes=std::vector<unsigned char>((unsigned char*)&fixture,(unsigned char*)&fixture+sizeof(fixture));
  SlateFont::Submit(request("custom",sha));check("worker-preparation-does-not-change-font",!std::atomic_load(&SlateFont::current));
  std::thread wrong([]{SlateFont::Service();});wrong.join();check("foreign-thread-cannot-commit",!std::atomic_load(&SlateFont::current));
  SlateFont::Service();auto first=std::atomic_load(&SlateFont::current);
  check("validated-real-font-and-native-ABI-commit",first&&first->composite==fixtureComposite&&SlateFont::State()["accepted"]==true);
  unsigned char originalComposite[64]={};fixture.CompositeFont.data=originalComposite;
  check("outside-text-scope-preserves-engine-composite",SlateFont::GetComposite(&fixture)==originalComposite);
  depth=1;SlateFont::BeginScope();SlateFont::ConsiderText(L"AWm");check("inside-covered-text-scope-selects-custom-composite",SlateFont::GetComposite(&fixture)==fixtureComposite);SlateFont::EndScope();depth=0;
  depth=1;SlateFont::BeginScope();check("scope-without-complete-text-keeps-original",SlateFont::GetComposite(&fixture)==originalComposite);
  SlateFont::ConsiderText(L"AWm");check("late-text-cannot-switch-font-after-measurement-started",SlateFont::GetComposite(&fixture)==originalComposite);SlateFont::EndScope();
  SlateFont::BeginScope();SlateFont::ConsiderText(L"AWm 中文");check("actual-GDI-missing-glyph-keeps-whole-scope-original",SlateFont::GetComposite(&fixture)==originalComposite);SlateFont::EndScope();
  SlateFont::BeginScope();SlateFont::ConsiderText(std::wstring(1,0xd83d)+std::wstring(1,0xde00));check("unproved-surrogate-shaping-keeps-original",SlateFont::GetComposite(&fixture)==originalComposite);SlateFont::EndScope();depth=0;
  fixture.CompositeFont.data=fixtureComposite;
  check("size-material-outline-storage-untouched",memcmp(&fixture,originalBytes.data(),sizeof(fixture))==0);
  fixture.FontObject=originalComposite;fixture.FontMaterial=originalComposite;fixture.Size=37;fixture.LetterSpacing=29;fixture.TypefaceFontName=51;fixture.OutlineSettings.bytes[3]=17;
  depth=1;SlateFont::BeginScope();SlateFont::ConsiderText(L"AWm");SlateFont::MakeKey(&key,&fixture,&outline,1.25f);SlateFont::EndScope();depth=0;
  check("native-key-has-new-font-identity-and-preserved-style",key.FontInfo.FontObject==nullptr&&key.FontInfo.TypefaceFontName==0&&
   key.FontInfo.CompositeFont.data==fixtureComposite&&key.FontInfo.FontMaterial==originalComposite&&key.FontInfo.Size==37&&key.FontInfo.LetterSpacing==29&&key.FontInfo.OutlineSettings.bytes[3]==17&&key.Scale==1.25f);
  SlateFont::MakeKey(&key,&fixture,&outline,1.0f);
  check("outside-scope-native-key-keeps-original-identity",key.FontInfo.FontObject==originalComposite&&key.FontInfo.TypefaceFontName==51);
  SlateFont::Submit(request("custom",std::string(64,'0')));SlateFont::Service();
  check("wrong-hash-retains-valid-current-generation",std::atomic_load(&SlateFont::current)==first&&!SlateFont::State()["accepted"].get<bool>());
  auto expired=request("default","");expired["expiresUnix"]=(long long)std::time(nullptr)-1;
  SlateFont::Submit(expired);SlateFont::Service();check("expired-transport-command-never-applies-later",std::atomic_load(&SlateFont::current)==first);
  SlateFont::Submit(request("custom",sha));SlateFont::Service();check("same-file-reuses-cached-native-generation",std::atomic_load(&SlateFont::current)==first&&SlateFont::retained.size()==1);
  for(int i=0;i<36;i++){SlateFont::Submit(request("default",""));SlateFont::Service();SlateFont::Submit(request("custom",sha));SlateFont::Service();}
  check("repeated-switches-have-bounded-native-lifetimes",SlateFont::retained.size()==1&&std::atomic_load(&SlateFont::current)==first);
  while(SlateFont::retained.size()<32)SlateFont::retained.push_back(std::make_shared<SlateFont::Generation>());
  SlateFont::Submit(request("default",""));SlateFont::Service();
  fixture.CompositeFont.data=originalComposite;depth=1;
  check("default-restore-works-even-at-retention-limit",!std::atomic_load(&SlateFont::current)&&SlateFont::GetComposite(&fixture)==originalComposite&&SlateFont::State()["accepted"]==true);depth=0;
 }
 bool passed=true;for(auto& c:checks)passed&=c["passed"].get<bool>();
 std::ofstream output(fs::path(argv[3]),std::ios::binary);output<<json{{"scope","controlled-MSVC-PDB-ABI-and-font-transaction; NOT actual Unreal Engine or pixels"},{"passed",passed},{"checks",checks}}.dump(2);
 std::cout<<checks.size()<<" checks; passed="<<passed<<"\n";return passed?0:1;
}

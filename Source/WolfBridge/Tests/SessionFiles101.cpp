#include <windows.h>
#include <filesystem>
#include <iostream>
#include <string>
#include "../WolfSessionFiles.h"
#define FUSION_TEST_CAPTURE
#include "../WolfTestScreen.h"
namespace fs=std::filesystem;
int wmain(int argc,wchar_t** argv) {
    if(argc!=2)return 2;int checks=0,failures=0;
    auto check=[&](const char* name,bool ok){++checks;if(!ok){++failures;std::cout<<"FAIL "<<name<<"\n";}};
    const fs::path root=argv[1];std::wstring native;
    if(!WolfSessionFiles::NativePath(root,native)||fs::exists(root))return 2;
    check("create isolated root",fs::create_directories(fs::path(native)));
    check("unmarked test executable remains rejected",!WolfTestScreen::MarkedCopy());
    for(const auto& invalid:std::initializer_list<std::wstring>{
        L"relative.json",L"C:relative.json",L"\\rooted.json",L"\\\\server\\share\\file.json",
        L"\\\\?\\C:\\file.json",L"\\\\.\\C:\\file.json",L"C:\\folder\\..\\file.json",
        L"C:\\folder\\.\\file.json",L"C:\\folder\\\\file.json",L"C:\\folder.\\file.json",
        L"C:\\folder \\file.json",L"C:\\folder\\file.json:stream",L"C:\\folder\\bad?.json",
        L"C:\\folder\\NUL.json",L"C:\\CON\\file.json",L"C:/folder/file.json",
        std::wstring(L"C:\\nul\0file.json",16),L"C:\\folder\\"}) {
        native=L"stale";check("reject non-normal local path",!WolfSessionFiles::NativePath(fs::path(invalid),native)&&native.empty());
    }
    check("reject overlong component",!WolfSessionFiles::NativePath(fs::path(L"C:\\"+std::wstring(256,L'p')+L"\\file.json"),native));
    check("reject namespace length overflow",!WolfSessionFiles::NativePath(fs::path(L"C:\\"+std::wstring(32763,L'p')),native));
    for(size_t length: {size_t(0),size_t(264),size_t(500),size_t(2000)}) {
        auto folder=root/(L"unicode-\u8fc1\u79fb-\u041f\u0443\u0442\u044c-"+std::to_wstring(length));
        while(length&&folder.native().size()<length) {
            size_t count=(std::min)(size_t(48),length-folder.native().size()-1);
            if(count==0){folder+=L"p";break;}
            folder/=std::wstring(count,L'\u8def');
        }
        const auto original=folder.native();
        check("exact requested session length",length==0||original.size()==length);
        check("normal path conversion",WolfSessionFiles::NativePath(folder,native)&&native==L"\\\\?\\"+original);
        check("create session directory",fs::create_directories(fs::path(native)));
        check("test capture validates original long session",WolfTestScreen::NoReparseChain(folder));
        check("test capture rejects absent session",!WolfTestScreen::NoReparseChain(folder/L"absent"));
        const auto path=folder/L"\u56de\u590d-\u0434\u0430\u043d\u043d\u044b\u0435.json";
        const std::string first=u8"{\"text\":\"\u4f60\u597d \u041f\u0440\u0438\u0432\u0435\u0442\",\"generation\":1}";
        const std::string second=u8"{\"text\":\"\u66f4\u65b0\",\"generation\":2}";
        std::string data="stale";
        check("publish Unicode content",WolfSessionFiles::WriteAtomic(path,first));
        check("read exact published content",WolfSessionFiles::Read(path,4096,data)&&data==first);
        check("read upper bound",!WolfSessionFiles::Read(path,first.size()-1,data)&&data.empty());
        check("read exact size bound",WolfSessionFiles::Read(path,first.size(),data)&&data==first);
        check("atomically replace existing file",WolfSessionFiles::WriteAtomic(path,second));
        check("read replacement content",WolfSessionFiles::Read(path,4096,data)&&data==second);
        auto temporary=path;temporary+=L".tmp";
        check("successful publication removes temporary",WolfSessionFiles::NativePath(temporary,native)&&!fs::exists(fs::path(native)));
        check("missing read is bounded failure",!WolfSessionFiles::Read(folder/L"missing.json",4096,data)&&data.empty());
        check("missing parent not created implicitly",!WolfSessionFiles::WriteAtomic(folder/L"missing-parent"/L"reply.json",first));
        check("session identity unchanged",folder.native()==original);
    }
    std::cout<<"checks="<<checks<<" failures="<<failures<<"\n";return failures?1:0;
}

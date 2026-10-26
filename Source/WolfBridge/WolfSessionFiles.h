#pragma once
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <string>
#include <limits>

// Keep the original session string for ownership/test-copy checks. Only file
// I/O receives an extended Win32 path: an injected DLL cannot opt its host game
// into long paths by supplying a manifest of its own.
namespace WolfSessionFiles {
inline bool ReservedComponent(const std::wstring& value,size_t start,size_t end) {
    auto dot=value.find(L'.',start);if(dot<end)end=dot;
    std::wstring name=value.substr(start,end-start);
    for(auto& c:name)if(c>=L'a'&&c<=L'z')c-=L'a'-L'A';
    return name==L"CON"||name==L"PRN"||name==L"AUX"||name==L"NUL"||name==L"CONIN$"||name==L"CONOUT$"||
        (name.size()==4&&(name.compare(0,3,L"COM")==0||name.compare(0,3,L"LPT")==0)&&
         ((name[3]>=L'1'&&name[3]<=L'9')||name[3]==L'\u00b9'||name[3]==L'\u00b2'||name[3]==L'\u00b3'));
}
inline bool NativePath(const std::filesystem::path& path,std::wstring& result) {
    result.clear();const auto& value=path.native();
    // Accept an ordinary, normalized, absolute local-drive path only. Never
    // reinterpret a UNC/device namespace, relative path, ADS or dot segment.
    if(value.size()<3||value.size()>32762||
       !((value[0]>=L'A'&&value[0]<=L'Z')||(value[0]>=L'a'&&value[0]<=L'z'))||
       value[1]!=L':'||value[2]!=L'\\')return false;
    if(value.size()==3){result=L"\\\\?\\"+value;return true;}
    size_t start=3;
    for(size_t i=3;i<=value.size();++i) {
        if(i<value.size()&&value[i]!=L'\\') {
            const wchar_t c=value[i];
            if(c<32||c==L'/'||c==L':'||c==L'<'||c==L'>'||c==L'"'||c==L'|'||c==L'?'||c==L'*')return false;
            continue;
        }
        if(i==start||i-start>255||value[i-1]==L'.'||value[i-1]==L' '||ReservedComponent(value,start,i))return false;
        start=i+1;
    }
    result=L"\\\\?\\"+value;return true;
}
inline bool Read(const std::filesystem::path& path,size_t limit,std::string& text) {
    text.clear();std::wstring native;if(!NativePath(path,native))return false;
    std::ifstream file(std::filesystem::path(native),std::ios::binary|std::ios::ate);
    if(!file)return false;const auto length=file.tellg();
    if(length<=0||static_cast<unsigned long long>(length)>limit||
       static_cast<unsigned long long>(length)>static_cast<unsigned long long>((std::numeric_limits<std::streamsize>::max)()))return false;
    file.seekg(0);text.resize(static_cast<size_t>(length));
    if(!file.read(text.data(),static_cast<std::streamsize>(length))){text.clear();return false;}return true;
}
inline bool WriteAtomic(const std::filesystem::path& path,const std::string& text) {
    auto temporary=path;temporary+=L".tmp";std::wstring native,nativeTemporary;
    if(!NativePath(path,native)||!NativePath(temporary,nativeTemporary)||
       text.size()>static_cast<size_t>((std::numeric_limits<std::streamsize>::max)()))return false;
    {
        std::ofstream file(std::filesystem::path(nativeTemporary),std::ios::binary|std::ios::trunc);
        if(!file.write(text.data(),static_cast<std::streamsize>(text.size())))return false;
        file.close();if(!file)return false;
    }
    return MoveFileExW(nativeTemporary.c_str(),native.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)!=FALSE;
}
}

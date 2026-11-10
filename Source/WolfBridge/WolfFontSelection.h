#pragma once
#include <windows.h>
#include <bcrypt.h>
#include <vector>
#include <string>
#pragma comment(lib,"bcrypt.lib")

namespace WolfFont {
// One immutable validated selection. Renderer can replace the whole selection
// only after a verified native cache reset; this object has no mutable setter.
class StartupSelection {
    std::vector<BYTE> bytes;
    HANDLE resource=nullptr;
    bool configured=false,requested=false,active=false;
    std::wstring family;
    static std::wstring Hash(const std::vector<BYTE>& value){
        BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;
        BYTE digest[32]={};bool ok=false;
        if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0){
            if(BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)>=0)
                ok=BCryptHashData(hash,const_cast<BYTE*>(value.data()),(ULONG)value.size(),0)>=0&&BCryptFinishHash(hash,digest,32,0)>=0;
        }
        if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);
        if(!ok)return L"";std::wstring result;const wchar_t* digits=L"0123456789ABCDEF";
        for(BYTE b:digest){result+=digits[b>>4];result+=digits[b&15];}return result;
    }
public:
    ~StartupSelection(){if(resource)RemoveFontMemResourceEx(resource);}
    bool Requested()const{return requested;}
    bool Active()const{return active;}
    const std::wstring& Family()const{return family;}
    bool Matches(HDC dc)const noexcept{
        try{
            if(bytes.empty())return false;
            DWORD size=GetFontData(dc,0,0,nullptr,0);if(size!=bytes.size())return false;
            std::vector<BYTE> actual(size);return GetFontData(dc,0,0,actual.data(),size)==size&&actual==bytes;
        }catch(...){return false;}
    }
    bool Configure(const std::wstring& path,const std::wstring& face,const std::wstring& expectedHash,bool hostFailed=false){
        if(configured)return false;configured=true;
        requested=hostFailed||!path.empty()||!face.empty();if(!requested)return true;
        if(hostFailed||path.empty()||face.empty()||face.size()>=LF_FACESIZE||expectedHash.size()!=64)return false;
        std::wstring filePath=path;
        if(filePath.size()>=248&&filePath.size()>2&&filePath[1]==L':')filePath=L"\\\\?\\"+filePath;
        HANDLE file=CreateFileW(filePath.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
        if(file==INVALID_HANDLE_VALUE)return false;LARGE_INTEGER size={};bool read=false;DWORD got=0;
        if(GetFileSizeEx(file,&size)&&size.QuadPart>=12&&size.QuadPart<=32*1024*1024){
            bytes.resize((size_t)size.QuadPart);read=ReadFile(file,bytes.data(),(DWORD)bytes.size(),&got,nullptr)&&got==bytes.size();
        }
        CloseHandle(file);
        if(!read||Hash(bytes)!=expectedHash||memcmp(bytes.data(),"ttcf",4)==0){bytes.clear();return false;}
        family=face;DWORD count=0;resource=AddFontMemResourceEx(bytes.data(),(DWORD)bytes.size(),nullptr,&count);
        // A single CJK face can register both horizontal and vertical names.
        // The selected face is proved by exact bytes below, not this count.
        if(!resource||count<1||count>256){if(resource)RemoveFontMemResourceEx(resource);resource=nullptr;bytes.clear();return false;}
        HDC dc=CreateCompatibleDC(nullptr);HFONT font=CreateFontW(-32,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,DEFAULT_QUALITY,DEFAULT_PITCH,family.c_str());
        if(dc&&font){auto old=SelectObject(dc,font);active=old&&old!=HGDI_ERROR&&Matches(dc);if(old&&old!=HGDI_ERROR)SelectObject(dc,old);}
        if(font)DeleteObject(font);if(dc)DeleteDC(dc);
        if(!active){RemoveFontMemResourceEx(resource);resource=nullptr;bytes.clear();family.clear();}
        return active;
    }
};
}

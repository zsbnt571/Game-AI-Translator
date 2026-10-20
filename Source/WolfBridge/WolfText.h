#pragma once
#include <windows.h>
#include <array>
#include <string>
#include <vector>

namespace WolfText {
struct Run {std::string bytes;bool display;};
inline bool KnownCommand(unsigned count,unsigned op,bool legacy){
    if((count==2&&(op==5||op==20))||(count==4&&op==17)||(count==5&&op==3)||(count==6&&op==2))return true;
    // The WOLF 3 renderer retains the same framed alignment, position, font
    // and icon controls. Their payload is opaque and copied byte-for-byte.
    // Verified against the legacy renderer's opcode dispatch and its cursor
    // advances. Icons (22), alignment (19/21), offsets and font switches are
    // opaque display commands, not a reason to discard adjacent prose.
    switch(op){
    case 1:case 6:case 11:case 12:case 14:case 15:case 16:
    case 19:case 21:case 26:case 27:case 32:return count==2;
    case 7:return count==3;
    case 18:return count==4;
    case 4:case 22:case 24:case 25:case 29:case 30:
    case 33:case 35:case 36:case 37:return count==5;
    default:return false;
    }
}
// The source display stream frames compiled commands as SOH, byte-count,
// opcode and payload. Counts include their own count byte. Only explicitly
// recognized command shapes are accepted here.
// Payload bytes can resemble ASCII letters; they are never natural-language
// candidates. Unknown framing falls back to the older conservative boundary.
inline bool Runs(const std::string& raw,std::vector<Run>& output,bool legacy=false){
    output.clear();if(raw.empty()||raw.size()>24000)return false;
    size_t at=0,commands=0;
    while(at<raw.size()){
        if((unsigned char)raw[at]==1){
            if(at+3>raw.size()||++commands>256)return false;
            unsigned count=(unsigned char)raw[at+1],op=(unsigned char)raw[at+2];
            bool known=KnownCommand(count,op,legacy);
            if(!known||at+count+1>raw.size())return false;
            output.push_back({raw.substr(at,count+1),false});at+=count+1;
        }else{
            size_t end=raw.find('\x01',at);if(end==raw.npos)end=raw.size();
            for(size_t i=at;i<end;i++){unsigned char c=raw[i];if(!c||(c<32&&c!=9&&c!=10&&c!=13))return false;}
            output.push_back({raw.substr(at,end-at),true});at=end;
        }
    }
    return true;
}
inline bool Utf8(const std::string& s) {
    return !s.empty() && s.size()<=48000 && s.find('\0')==s.npos &&
        MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.data(),(int)s.size(),nullptr,0)>0;
}
inline bool Plain(const std::string& s) {
    if(!Utf8(s))return false;
    for(unsigned char c:s)if(c<32&&c!='\n'&&c!='\r'&&c!='\t')return false;
    return true;
}
// The renderer receives an already-compiled format prefix. Keep those bytes
// verbatim. Only a plain suffix may be translated; never consume text embedded
// between control opcodes, or infer an unknown opcode's argument length.
inline bool Split(const std::string& raw,std::string& prefix,std::string& source) {
    size_t split=0;
    for(size_t i=0;i<raw.size();i++){unsigned char c=raw[i];if(c<32&&c!='\n'&&c!='\r'&&c!='\t')split=i+1;}
    if(split>256||(split>0&&(unsigned char)raw[0]!=1))return false;
    for(size_t i=0;i<split;i++){
        unsigned char c=raw[i];
        if(c>=128||(c>='A'&&c<='Z')||(c>='a'&&c<='z'))return false;
    }
    source=raw.substr(split);prefix=raw.substr(0,split);
    return source.size()<=24000&&Plain(source);
}
inline std::array<uintptr_t,6> StringView(const std::string& value){
    std::array<uintptr_t,6> view{};size_t n=value.size();
    if(n<=15){memcpy(view.data(),value.data(),n);view[5]=15;}
    else {view[0]=(uintptr_t)value.c_str();view[5]=n;}
    view[4]=n;return view;
}
inline std::vector<int> ParsePattern(const std::string& text) {
    std::vector<int> result;size_t pos=0;
    while(pos<text.size()){
        while(pos<text.size()&&isspace((unsigned char)text[pos]))pos++;
        if(pos==text.size())break;
        if(pos+2>text.size())return {};
        auto token=text.substr(pos,2);pos+=2;
        if(token=="??")result.push_back(-1);
        else {char* end=nullptr;long n=strtol(token.c_str(),&end,16);if(*end||n<0||n>255)return {};result.push_back((int)n);}
        if(pos<text.size()&&!isspace((unsigned char)text[pos]))return {};
    }
    return result.size()>=64&&result.size()<=256?result:std::vector<int>{};
}
inline bool Match(const unsigned char* data,const std::vector<int>& pattern) {
    for(size_t i=0;i<pattern.size();i++)if(pattern[i]>=0&&data[i]!=pattern[i])return false;
    return true;
}
}


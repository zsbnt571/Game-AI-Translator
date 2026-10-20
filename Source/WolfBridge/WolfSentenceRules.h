#pragma once
#include <string>
#include <vector>
#include <cstdint>

// Display-only policy for a verified custom typewriter. This is deliberately
// independent of the engine's generic string-variable APIs.
namespace WolfSentenceRules {
// A paragraph is rendered as persistent pictures, not repainted in full on each
// getter call. Its chosen original/translation must therefore remain frozen
// while the game advances through that paragraph. The caller still validates
// the executable, script, source identity and display-only read site.
class ParagraphCursor {
    bool initialized=false;
    int previousSource=0,previousLine=0,previousCharacter=0,previousMode=0;
public:
    bool Advance(int sourceId,int commonId,int line,int character){
        // Invalid observations must not replace the last trustworthy cursor.
        // In 606, character is the configured line capacity, not a position.
        if((commonId!=605&&commonId!=606)||line<1||(commonId==605&&character<1))return false;
        bool fresh=!initialized||sourceId!=previousSource;
        if(initialized&&!fresh){
            fresh=line<previousLine;
            if(!fresh&&commonId==605&&previousMode==605&&line==previousLine)
                fresh=character<previousCharacter;
        }
        initialized=true;previousSource=sourceId;previousLine=line;
        previousCharacter=character;previousMode=commonId;
        return fresh;
    }
    // Repeating the same source at the identical one-character (or one-line
    // instant) position is indistinguishable from a repeated getter. A mode
    // switch alone also gives no comparable character cursor. Do not guess a
    // new paragraph from elapsed time, provider completion or toggle changes.
};
inline bool SourceId(int64_t id,int& type,int& row,int& field){
    if(id<1000000000LL||id>=1100000000LL)return false;
    auto value=id-1000000000LL;type=(int)(value/1000000);row=(int)((value%1000000)/100);field=(int)(value%100);
    return ((type>=41&&type<=48)||type==51||type==52)&&field<4&&row>=0&&row<10000;
}
inline bool Site(uintptr_t caller,int common,int index,const int* command){
    static const int expected[6]={250,1600032,1600033,1600034,4608,1600005};
    if(caller!=0xb7434||!((common==605&&index==12)||(common==606&&index==11)))return false;
    for(int i=0;i<6;i++)if(command[i]!=expected[i])return false;
    return true;
}
inline bool Codes(const std::string& text,std::vector<std::string>& codes,unsigned& breaks){
    codes.clear();breaks=0;
    for(size_t i=0;i<text.size();){
        unsigned char c=text[i];if(!c||(c<32&&c!=9&&c!=10&&c!=13))return false;
        if(c=='\n')++breaks;
        if(c!='\\'){++i;continue;}
        size_t end=i;
        if(i+1==text.size()||text[i+1]=='\r'||text[i+1]=='\n')end=i+1;
        else if(std::string("EN\\!.|<>^").find(text[i+1])!=std::string::npos)end=i+2;
        else{
            size_t name=i+1;while(name<text.size()&&((text[name]>='a'&&text[name]<='z')||(text[name]>='A'&&text[name]<='Z')||(text[name]>='0'&&text[name]<='9')||text[name]=='+'))++name;
            if(name>i+1&&name<text.size()&&text[name]=='['){
                unsigned depth=0;
                for(size_t p=name;p<text.size()&&p-i<=2048;p++){
                    if(text[p]=='\r'||text[p]=='\n')break;
                    if(text[p]=='['&&++depth>100)break;
                    if(text[p]==']'&&--depth==0){end=p+1;break;}
                }
            }
        }
        // Unknown escape syntax is not a license to alter its neighbours.
        if(end==i||codes.size()>=256)return false;
        codes.push_back(text.substr(i,end-i));i=end;
    }
    return true;
}
inline bool Pair(const std::string& source,const std::string& text){
    if(source.empty()||text.empty()||source.size()>24000||text.size()>24000)return false;
    std::vector<std::string> a,b;unsigned x=0,y=0;
    return Codes(source,a,x)&&Codes(text,b,y)&&a==b&&x==y;
}
}

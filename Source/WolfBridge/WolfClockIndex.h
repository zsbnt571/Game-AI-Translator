#pragma once
#include <string>
#include <unordered_map>
#include <memory>
#include <atomic>

// Matches ClockDisplayTemplate.cs. Only clock displays with explicit AM/PM
// qualify. Keep the actual digits verbatim and learn wording from cache pairs.
namespace WolfClockIndex {
struct Source {std::string key,time;};
struct Target {std::string before,after;bool operator==(const Target& b)const{return before==b.before&&after==b.after;}};
inline bool Time(const std::string& text){
    size_t colon=text.find(':');if(colon<1||colon>2)return false;
    if(text.size()!=colon+3&&text.size()!=colon+6)return false;
    for(size_t n=0;n<text.size();n++)if(n==colon||n==colon+3){if(text[n]!=':')return false;}else if(text[n]<'0'||text[n]>'9')return false;
    int hour=0;for(size_t n=0;n<colon;n++)hour=hour*10+text[n]-'0';
    return hour<=23&&text[colon+1]<='5'&&(text.size()==colon+3||text[colon+4]<='5');
}
inline bool Parse(const std::string& text,Source& value){
    if(text.size()<7||text.size()>80)return false;
    auto meridian=[](const std::string& s){return s=="AM"||s=="PM";};
    auto space=[](char c){return c==' '||c=='\t';};
    if(meridian(text.substr(0,2))&&space(text[2])){
        size_t at=2;while(at<text.size()&&space(text[at]))at++;auto time=text.substr(at);if(!Time(time))return false;
        value={"before:"+text.substr(0,2)+":"+text.substr(2,at-2),std::move(time)};return true;
    }
    if(meridian(text.substr(text.size()-2))&&space(text[text.size()-3])){
        size_t at=text.size()-2;while(at&&space(text[at-1]))at--;auto time=text.substr(0,at);if(!Time(time))return false;
        value={"after:"+text.substr(text.size()-2)+":"+text.substr(at,text.size()-2-at),std::move(time)};return true;
    }
    return false;
}
inline bool Pair(const Source& source,const std::string& text,Target& value){
    if(text.size()>160)return false;auto at=text.find(source.time);
    if(at==text.npos||text.find(source.time,at+source.time.size())!=text.npos)return false;
    auto before=text.substr(0,at),after=text.substr(at+source.time.size());auto label=before+after;bool letters=false;
    for(unsigned char c:label){if((c>='0'&&c<='9')||c=='\r'||c=='\n'||c=='\\'||c=='<'||c=='>'||c=='['||c==']'||c==':'||!c)return false;
        letters|=(c>='A'&&c<='Z')||(c>='a'&&c<='z')||c>=0x80;}
    if(!letters)return false;value={std::move(before),std::move(after)};return true;
}
class Index {
    struct Entry {Target target;bool conflict=false;};
    using Snapshot=std::unordered_map<std::string,Entry>;
    std::unordered_map<std::string,std::unordered_map<std::string,Target>> owners;
    std::shared_ptr<const Snapshot> published;
    bool dirty=false;
public:
    void Put(const std::string& source,const std::string& text){
        Source parsed;if(!Parse(source,parsed))return;
        auto& group=owners[parsed.key];Target target;
        if(Pair(parsed,text,target)){if(group.size()<4096||group.count(source))group[source]=std::move(target);}
        else group.erase(source);
        dirty=true;
    }
    void Publish(){
        if(!dirty)return;auto next=std::make_shared<Snapshot>();
        for(auto& group:owners)for(auto& pair:group.second){auto found=next->find(group.first);
            if(found==next->end())next->emplace(group.first,Entry{pair.second,false});
            else if(!(found->second.target==pair.second))found->second.conflict=true;}
        std::atomic_store(&published,std::shared_ptr<const Snapshot>(std::move(next)));dirty=false;
    }
    std::string Find(const std::string& text)const{
        Source parsed;if(!Parse(text,parsed))return {};auto snapshot=std::atomic_load(&published);if(!snapshot)return {};
        auto found=snapshot->find(parsed.key);if(found==snapshot->end()||found->second.conflict)return {};
        return found->second.target.before+parsed.time+found->second.target.after;
    }
    void Clear(){owners.clear();std::atomic_store(&published,std::shared_ptr<const Snapshot>{});dirty=false;}
};
}

#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <bitset>
#include <cctype>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>
#include "WolfLanguageBranches.h"

// Resource templates and the renderer's expanded prose have different keys.
// This index accepts only exact literal anchors and named WOLF substitutions.
// It never edits game variables, interprets commands or guesses a missing name.
namespace WolfRenderIndex {
inline std::string Trim(std::string s){
    std::string out;out.reserve(s.size());
    for(size_t i=0;i<s.size();i++){if(s[i]=='\r'&&i+1<s.size()&&s[i+1]=='\n')continue;out+=s[i];}
    auto a=out.find_first_not_of(" \r\n\t"),b=out.find_last_not_of(" \r\n\t");return a==out.npos?std::string{}:out.substr(a,b-a+1);
}
struct Segment {std::string text,command;};
inline bool Segments(const std::string& raw,std::vector<Segment>& result){
    result.clear();std::string run;
    for(size_t i=0;i<raw.size();){
        if(raw[i]!='\\'){run+=raw[i++];continue;}
        if(i+1<raw.size()&&(raw[i+1]=='E'||raw[i+1]=='N')){result.push_back({Trim(run),raw.substr(i,2)});run.clear();i+=2;continue;}
        size_t end=i+1;while(end<raw.size()&&((raw[end]>='A'&&raw[end]<='Z')||(raw[end]>='a'&&raw[end]<='z')))end++;
        if(end==i+1||end>=raw.size()||raw[end]!='[')return false;
        auto name=raw.substr(i+1,end-i-1);
        size_t number=++end;while(end<raw.size()&&raw[end]>='0'&&raw[end]<='9')end++;
        if(end==number||end-number>9||end>=raw.size()||raw[end]!=']')return false;
        auto code=raw.substr(i,end-i+1);i=end+1;
        if(name=="c"||name=="f"||name=="i"){result.push_back({Trim(run),code});run.clear();}
        else if(name=="s"||name=="v"||name=="self"||name=="cself")run+=code;
        else return false;
    }
    result.push_back({Trim(run),""});return result.size()<=257;
}
struct Parts {std::vector<std::string> literals,variables;};
inline bool Parse(const std::string& text,Parts& parts){
    parts={};size_t start=0;
    for(size_t i=0;i<text.size();i++)if(text[i]=='\\'){
        size_t end=text.find(']',i);if(end==text.npos)return false;
        parts.literals.push_back(text.substr(start,i-start));parts.variables.push_back(text.substr(i,end-i+1));i=end;start=end+1;
    }
    parts.literals.push_back(text.substr(start));return parts.variables.size()<=4;
}
struct Rule {
    Parts source,target;std::string key;
    bool Match(const std::string& input,std::string& output)const{
        const auto& lit=source.literals;
        if(source.variables.empty()){if(input!=lit[0])return false;output=target.literals[0];return true;}
        if(input.compare(0,lit[0].size(),lit[0])!=0)return false;
        size_t at=lit[0].size();std::unordered_map<std::string,std::string> values;
        for(size_t n=0;n<source.variables.size();n++){
            const auto& next=lit[n+1];size_t end;
            if(n+1==source.variables.size()){
                if(input.size()<next.size()||input.compare(input.size()-next.size(),next.size(),next)!=0)return false;end=input.size()-next.size();
            }else{
                if(next.empty())return false;end=input.find(next,at);
                // Multiple positions cannot prove a unique variable boundary.
                if(end==input.npos||input.find(next,end+1)!=input.npos)return false;
            }
            if(end<at||end-at>1024)return false;auto value=input.substr(at,end-at);
            auto old=values.find(source.variables[n]);if(old!=values.end()&&old->second!=value)return false;
            values[source.variables[n]]=std::move(value);at=end+next.size();
        }
        if(at!=input.size())return false;output=target.literals[0];
        for(size_t n=0;n<target.variables.size();n++){
            auto found=values.find(target.variables[n]);if(found==values.end())return false;
            output+=found->second;output+=target.literals[n+1];
        }
        return !output.empty()&&output.size()<=48000;
    }
};
inline std::vector<Rule> Compile(const std::string& source,const std::string& text){
    auto alternatives=WolfLanguageBranches::Pairs(source,text);
    if(!alternatives.empty()){
        std::vector<Rule> rules;
        for(auto& pair:alternatives){
            if(pair.first.find('\\')!=pair.first.npos){auto more=Compile(pair.first,pair.second);for(auto& rule:more)rules.push_back(std::move(rule));}
            else {Rule r;r.source.literals={pair.first};r.target.literals={pair.second};r.key="="+pair.first;rules.push_back(std::move(r));}
        }return rules;
    }
    if(source.find('\\')==source.npos)return {};
    std::vector<Segment> a,b;if(!Segments(source,a)||!Segments(text,b)||a.size()!=b.size()||a.size()>32)return {};
    std::vector<Rule> rules;
    for(size_t n=0;n<a.size();n++){
        if(a[n].command!=b[n].command)return {};if(a[n].text.empty()&&b[n].text.empty())continue;
        Rule r;if(!Parse(a[n].text,r.source)||!Parse(b[n].text,r.target))return {};
        auto av=r.source.variables,bv=r.target.variables;std::sort(av.begin(),av.end());std::sort(bv.begin(),bv.end());if(av!=bv)return {};
        if(r.source.variables.empty()){
            if(r.source.literals[0].empty()||r.target.literals[0].empty())return {};
            r.key="="+r.source.literals[0];
        }else{
            size_t literal=0;for(auto& s:r.source.literals)literal+=s.size();if(literal<8)continue;
            if(r.source.literals[0].size()>=4)r.key="^"+r.source.literals[0].substr(0,4);
            else if(r.source.literals.back().size()>=4)r.key="$"+r.source.literals.back().substr(r.source.literals.back().size()-4);
            else continue;
            bool adjacent=false;for(size_t k=1;k+1<r.source.literals.size();k++)if(r.source.literals[k].empty())adjacent=true;if(adjacent)continue;
        }
        rules.push_back(std::move(r));
    }
    return rules;
}
class Index {
    using Owners=std::unordered_map<std::string,std::vector<Rule>>;
    using Bucket=std::unordered_map<std::string,Owners>;
    std::array<Bucket,64> working;
    std::array<std::shared_ptr<const Bucket>,64> published;
    std::unordered_map<std::string,std::vector<std::string>> ownerKeys;
    std::unordered_map<std::string,size_t> ownerCosts;
    size_t bytes=0;
    std::bitset<64> dirty;
    size_t Shard(const std::string& key)const{return std::hash<std::string>{}(key)%working.size();}
public:
    void Put(const std::string& source,const std::string& text){
        auto prior=ownerKeys.find(source);
        if(prior!=ownerKeys.end())for(auto& key:prior->second){size_t i=Shard(key);auto p=working[i].find(key);if(p!=working[i].end()){p->second.erase(source);if(p->second.empty())working[i].erase(p);}dirty.set(i);}
        ownerKeys.erase(source);auto oldCost=ownerCosts.find(source);if(oldCost!=ownerCosts.end()){bytes-=oldCost->second;ownerCosts.erase(oldCost);}
        auto rules=Compile(source,text);if(rules.empty())return;
        size_t cost=0;for(auto& rule:rules){cost+=512+source.size()+rule.key.size();for(auto* parts:{&rule.source,&rule.target}){cost+=64*(parts->literals.size()+parts->variables.size());for(auto& s:parts->literals)cost+=s.size();for(auto& s:parts->variables)cost+=s.size();}}
        if(bytes+cost>32*1024*1024||ownerKeys.size()>=100000)return;bytes+=cost;ownerCosts[source]=cost;
        auto& keys=ownerKeys[source];
        for(auto& rule:rules){auto key=rule.key;size_t i=Shard(key);working[i][key][source].push_back(std::move(rule));dirty.set(i);if(std::find(keys.begin(),keys.end(),key)==keys.end())keys.push_back(key);}
    }
    void Publish(){for(size_t i=0;i<working.size();i++)if(dirty.test(i)){std::atomic_store(&published[i],std::make_shared<const Bucket>(working[i]));dirty.reset(i);}}
    std::string Find(const std::string& source)const{
        if(source.empty()||source.size()>24000)return {};std::string answer;size_t examined=0;
        for(auto& key:std::array<std::string,3>{"="+source,"^"+source.substr(0,4),"$"+source.substr(source.size()>4?source.size()-4:0)}){
            auto shard=std::atomic_load(&published[Shard(key)]);if(!shard)continue;auto p=shard->find(key);if(p==shard->end())continue;
            for(auto& owner:p->second)for(auto& rule:owner.second){if(++examined>64)return {};std::string value;
                if(!rule.Match(source,value))continue;if(!answer.empty()&&answer!=value)return {};answer=std::move(value);}
        }
        return answer;
    }
    void Clear(){for(size_t i=0;i<working.size();i++){working[i].clear();std::atomic_store(&published[i],std::shared_ptr<const Bucket>{});}ownerKeys.clear();ownerCosts.clear();bytes=0;dirty.reset();}
};
}

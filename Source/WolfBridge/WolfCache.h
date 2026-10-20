#pragma once
#include <array>
#include <atomic>
#include <bitset>
#include <memory>
#include <string>
#include <unordered_map>
#include "WolfRenderIndex.h"
#include "WolfClockIndex.h"

namespace WolfCache {
enum class HitKind { Miss, Exact, Derived };
// One worker writes; rendering only reads immutable published shards. An apply
// copies touched shards, without walking every cached sentence for each batch.
class Cache {
    using Shard=std::unordered_map<std::string,std::string>;
    std::array<Shard,64> working;
    std::array<std::shared_ptr<const Shard>,64> published;
    std::bitset<64> dirty;
    size_t entries=0,bytes=0;
    WolfRenderIndex::Index render;
    WolfClockIndex::Index clocks;
    size_t Index(const std::string& key)const{return std::hash<std::string>{}(key)%working.size();}
public:
    static std::string Key(const std::string& text){
        std::string result;result.reserve(text.size());
        for(size_t i=0;i<text.size();i++){
            if(text[i]=='\r'&&i+1<text.size()&&text[i+1]=='\n')continue;
            result+=text[i];
        }
        // Layout removes only surrounding display whitespace before lookup.
        // Prime/apply must use the same identity, preserving interior breaks,
        // speaker prefixes, punctuation and variable names.
        auto first=result.find_first_not_of(" \r\n\t"),last=result.find_last_not_of(" \r\n\t");
        return first==result.npos?std::string{}:result.substr(first,last-first+1);
    }
    size_t Size()const{return entries;}
    size_t Bytes()const{return bytes;}
    bool Put(const std::string& source,const std::string& text){
        auto key=Key(source);if(key.empty())return false;size_t index=Index(key);auto& shard=working[index];auto found=shard.find(key);
        if(found!=shard.end()&&found->second==text)return true;
        size_t old=found==shard.end()?0:key.size()+found->second.size(),cost=key.size()+text.size();
        if((found==shard.end()&&entries>=100000)||bytes-old+cost>64*1024*1024)return false;
        if(found==shard.end())entries++;
        shard[key]=text;render.Put(key,text);clocks.Put(key,Key(text));bytes=bytes-old+cost;dirty.set(index);return true;
    }
    size_t Publish(){
        size_t copied=0;
        for(size_t i=0;i<working.size();i++)if(dirty.test(i)){
            auto next=std::make_shared<const Shard>(working[i]);copied+=working[i].size();
            std::atomic_store(&published[i],std::move(next));dirty.reset(i);
        }
        render.Publish();clocks.Publish();return copied;
    }
    std::string Single(const std::string& key,HitKind* kind=nullptr)const{
        if(kind)*kind=HitKind::Miss;
        auto shard=std::atomic_load(&published[Index(key)]);
        if(shard){auto value=shard->find(key);if(value!=shard->end()){if(kind)*kind=HitKind::Exact;return value->second;}}
        auto value=render.Find(key);if(value.empty())value=clocks.Find(key);if(!value.empty()&&kind)*kind=HitKind::Derived;return value;
    }
    std::string Columns(const std::string& key)const{
        // UI labels and state strings are often stored separately and joined
        // with an ideographic space or a tab. Require both complete entries;
        // ordinary word spaces, unknown states and ambiguous joins stay misses.
        auto separator=[&](size_t at)->size_t{return key[at]=='\t'?1:key.compare(at,3,"\xe3\x80\x80")==0?3:0;};
        auto trim=[](std::string value){
            value=Key(value);
            while(value.compare(0,3,"\xe3\x80\x80")==0)value.erase(0,3);
            while(value.size()>=3&&value.compare(value.size()-3,3,"\xe3\x80\x80")==0)value.resize(value.size()-3);
            return Key(value);
        };
        std::string answer;unsigned candidates=0;
        for(size_t at=1;at<key.size();at++){
            auto length=separator(at);if(!length)continue;if(++candidates>16)return {};
            size_t end=at+length;while(end<key.size()){auto next=separator(end);if(!next)break;end+=next;}
            auto left=Single(Key(key.substr(0,at))),right=Single(Key(key.substr(end)));
            if(right.empty())right=Single(Key(key.substr(at)));
            if(!left.empty()&&!right.empty()&&left.find_first_of("\r\n")==left.npos&&right.find_first_of("\r\n")==right.npos){
                auto value=trim(left)+key.substr(at,end-at)+trim(right);
                if(value.size()>48000||(!answer.empty()&&answer!=value))return {};answer=std::move(value);
            }
            at=end-1;
        }
        return answer;
    }
    std::string Find(const std::string& source,HitKind* kind=nullptr)const{
        auto key=Key(source),whole=Single(key,kind);if(!whole.empty())return whole;
        if(key.find('\n')==key.npos){auto value=Columns(key);if(!value.empty()&&kind)*kind=HitKind::Derived;return value;}
        // Choice arrays are extracted individually but rendered as one string.
        // Join only fully known single-line translations. Never partially
        // translate a paragraph or consume line separators used by its cursor.
        std::string joined;size_t at=0;unsigned lines=0;
        while(at<=key.size()){
            if(++lines>64)return {};auto end=key.find('\n',at);if(end==key.npos)end=key.size();
            auto line=key.substr(at,end-at),normalized=Key(line);
            if(normalized.empty())joined+=line;
            else{
                auto value=Single(normalized);if(value.empty())value=Columns(normalized);if(value.empty()||value.find_first_of("\r\n")!=value.npos)return {};
                auto start=line.find_first_not_of(" \t\r"),last=line.find_last_not_of(" \t\r");
                joined+=line.substr(0,start)+value+line.substr(last+1);
            }
            if(end==key.size())break;joined+='\n';at=end+1;
        }
        if(kind)*kind=HitKind::Derived;return joined;
    }
    void Clear(){
        for(size_t i=0;i<working.size();i++){working[i].clear();std::atomic_store(&published[i],std::shared_ptr<const Shard>{});}
        render.Clear();clocks.Clear();dirty.reset();entries=bytes=0;
    }
};
}

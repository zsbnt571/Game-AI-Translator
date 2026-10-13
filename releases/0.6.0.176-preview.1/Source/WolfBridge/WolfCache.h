#pragma once
#include <array>
#include <atomic>
#include <bitset>
#include <memory>
#include <string>
#include <unordered_map>

namespace WolfCache {
// One worker writes; rendering only reads immutable published shards. An apply
// copies touched shards, without walking every cached sentence for each batch.
class Cache {
    using Shard=std::unordered_map<std::string,std::string>;
    std::array<Shard,64> working;
    std::array<std::shared_ptr<const Shard>,64> published;
    std::bitset<64> dirty;
    size_t entries=0,bytes=0;
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
        shard[key]=text;bytes=bytes-old+cost;dirty.set(index);return true;
    }
    size_t Publish(){
        size_t copied=0;
        for(size_t i=0;i<working.size();i++)if(dirty.test(i)){
            auto next=std::make_shared<const Shard>(working[i]);copied+=working[i].size();
            std::atomic_store(&published[i],std::move(next));dirty.reset(i);
        }
        return copied;
    }
    std::string Find(const std::string& source)const{
        auto key=Key(source);auto shard=std::atomic_load(&published[Index(key)]);
        if(!shard)return {};auto value=shard->find(key);return value==shard->end()?std::string{}:value->second;
    }
    void Clear(){
        for(size_t i=0;i<working.size();i++){working[i].clear();std::atomic_store(&published[i],std::shared_ptr<const Shard>{});}
        dirty.reset();entries=bytes=0;
    }
};
}

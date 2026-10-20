#pragma once
#include <string>
#include <vector>
#include <utility>
namespace WolfLanguageBranches {
inline std::string Clean(std::string s){std::string v;for(size_t i=0;i<s.size();i++){if(s[i]=='\r'&&i+1<s.size()&&s[i+1]=='\n')continue;v+=s[i];}auto a=v.find_first_not_of(" \t\r\n"),b=v.find_last_not_of(" \t\r\n");return a==v.npos?std::string{}:v.substr(a,b-a+1);}
inline std::string Prefix(const std::string& s){if(s.empty()||s[0]!='@')return {};size_t i=1;while(i<s.size()&&s[i]>='0'&&s[i]<='9')i++;return i>1&&i<=4?s.substr(0,i):std::string{};}
inline bool Kana(const std::string& s){for(size_t i=0;i+2<s.size();i++)if((unsigned char)s[i]==0xe3&&(unsigned char)s[i+1]>=0x81&&(unsigned char)s[i+1]<=0x83)return true;return false;}
inline bool Latin(const std::string& s){int n=0;for(auto c:s)if((c>='a'&&c<='z')||(c>='A'&&c<='Z'))n++;return n>=4;}
inline std::vector<std::pair<std::string,std::string>> Pairs(const std::string& source,const std::string& target){
 const char* markers[]={"[ENG]","[EN]","[JPN]","[JP]","[CHS]","[CHT]","[KOR]"};
 for(auto tag:markers){std::string marker=tag;auto p=source.find(marker);if(p==source.npos||!p||source.find(marker,p+marker.size())!=source.npos)continue;
  std::string a[]={Clean(source.substr(0,p)),Clean(source.substr(p+marker.size()))};if(a[0].size()<2||a[1].size()<2)continue;
  bool nested=false;for(auto m:markers)for(auto& s:a)if(s.find(m)!=s.npos)nested=true;
  if(nested||(Kana(a[0])==Kana(a[1])&&Latin(a[0])==Latin(a[1])))continue;
  auto q=target.find(marker);if(q==target.npos||target.find(marker,q+marker.size())!=target.npos)return {};
  std::string b[]={Clean(target.substr(0,q)),Clean(target.substr(q+marker.size()))};if(b[0].empty()||b[1].empty())return {};
  auto prefix=Prefix(a[0]);bool paired=!prefix.empty()&&Prefix(a[1])==prefix;
  std::vector<std::pair<std::string,std::string>> result;
  for(int i=0;i<2;i++){result.emplace_back(a[i],b[i]);if(paired&&Prefix(b[i])==prefix&&b[i].size()>prefix.size())result.emplace_back(a[i].substr(prefix.size()),b[i].substr(prefix.size()));}
  return result;
 }return {};
}
}

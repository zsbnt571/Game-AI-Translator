#include <windows.h>
#include <iostream>
#include <thread>
#include <fstream>
#include <filesystem>
#include "json.hpp"
#include "../WolfCache.h"
#include "../WolfText.h"
int wmain(int argc,wchar_t** argv){
    SetPriorityClass(GetCurrentProcess(),BELOW_NORMAL_PRIORITY_CLASS);int count=0,failed=0;
    auto check=[&](const char* name,bool ok){count++;if(!ok){failed++;std::cout<<"FAIL "<<name<<"\n";}};
    WolfCache::Cache cache;
    cache.Put("Hello there.",u8"你好。");cache.Publish();
    check("Existing exact cache",cache.Find("Hello there.")==u8"你好。");
    cache.Put("\\c[2]Welcome, \\s[10]!\\c[0]",u8"\\c[2]欢迎，\\s[10]！\\c[0]");cache.Publish();
    check("Static color and expanded variable",cache.Find("Welcome, Alice!")==u8"欢迎，Alice！");
    check("Variable is not translated",cache.Find("Welcome, メアリー!")==u8"欢迎，メアリー！");
    check("Template keys remain stored",cache.Find("\\c[2]Welcome, \\s[10]!\\c[0]")==u8"\\c[2]欢迎，\\s[10]！\\c[0]");
    check("Aliases do not inflate native source receipts",cache.Size()==2);
    cache.Put("\\f[24]Start game\\f[16]Exit game",u8"\\f[24]开始游戏\\f[16]退出游戏");cache.Publish();
    check("First display run",cache.Find("Start game")==u8"开始游戏");check("Second display run",cache.Find("Exit game")==u8"退出游戏");
    cache.Put("You have \\v[4] coins.",u8"你有 \\v[4] 金币。");cache.Publish();
    check("Number expansion retains runtime value",cache.Find("You have 137 coins.")==u8"你有 137 金币。");
    check("Unrelated sentence is not a fuzzy hit",cache.Find("You lost 137 coins.").empty());
    cache.Put("\\s[1] entered the castle.",u8"\\s[1]进入了城堡。");cache.Publish();
    check("Suffix indexed template",cache.Find("Margaret entered the castle.")==u8"Margaret进入了城堡。");
    cache.Put("Hello \\s[1], meet \\s[2].",u8"你好，\\s[1]，来认识\\s[2]。");cache.Publish();
    check("Two distinct variables",cache.Find("Hello Alice, meet Bob.")==u8"你好，Alice，来认识Bob。");
    check("Ambiguous variable boundary declines",cache.Find("Hello Alice, meet Bob, meet Charlie.").empty());
    cache.Put("Hello \\s[1], meet \\s[1].",u8"你好，\\s[1]，来认识\\s[1]。");cache.Publish();
    check("Conflicting template outputs decline",cache.Find("Hello Alice, meet Bob.")==u8"你好，Alice，来认识Bob。");
    cache.Put("Hello \\s[3], meet \\s[4].",u8"另一种说法：\\s[3]和\\s[4]。");cache.Publish();
    check("Actually conflicting outputs decline",cache.Find("Hello Alice, meet Bob.").empty());
    cache.Put("\\c[2]Welcome, \\s[10]!\\c[0]",u8"\\c[2]你好呀，\\s[10]！\\c[0]");cache.Publish();
    check("Updating parent replaces derived result",cache.Find("Welcome, Alice!")==u8"你好呀，Alice！");
    cache.Put("Welcome, Alice!",u8"人工修正的欢迎语。");cache.Publish();
    check("Exact correction has priority",cache.Find("Welcome, Alice!")==u8"人工修正的欢迎语。");
    cache.Put("\\unknown[3]Secret sentence.",u8"\\unknown[3]秘密句子。");cache.Publish();
    check("Unknown control not stripped",cache.Find("Secret sentence.").empty());
    cache.Put("\\C[2]Incorrect case.",u8"\\C[2]大小写错误。");cache.Publish();check("Control grammar is case sensitive",cache.Find("Incorrect case.").empty());
    cache.Put("\\EAn outlined caption.\\N",u8"\\E带描边的文字。\\N");cache.Publish();check("Single letter edge switches",cache.Find("An outlined caption.")==u8"带描边的文字。");
    cache.Put("Hi \\s[1]",u8"好，\\s[1]");cache.Publish();check("Weak anchors not used",cache.Find("Hi Alice").empty());
    cache.Put("\\c[3]A frozen sentence.",u8"\\c[4]冻结的句子。");cache.Publish();check("Changed formatting does not gain aliases",cache.Find("A frozen sentence.").empty());
    cache.Put("Result: \\s[1]\\s[2] done.",u8"结果：\\s[1]\\s[2]完成。");cache.Publish();check("Adjacent variables declined",cache.Find("Result: AB done.").empty());
    std::string framed("\x01\x05\x03\x02\x00\x00",6);framed+="Start game";framed+=std::string("\x01\x02\x05",3);
    std::vector<WolfText::Run> runs;check("Native framing parsed",WolfText::Runs(framed,runs));
    check("Prepared key equals renderer run",runs.size()==3&&runs[1].display&&cache.Find(runs[1].bytes)==u8"开始游戏");
    check("Compiled commands preserved byte for byte",runs.size()==3&&runs[0].bytes==framed.substr(0,6)&&runs[2].bytes==framed.substr(framed.size()-3));
    std::atomic<bool> done=false;std::atomic<int> reads=0;
    std::thread reader([&]{while(!done){auto text=cache.Find("You have 25 coins.");if(text==u8"你有 25 金币。")reads++;}});
    auto began=GetTickCount64();while(reads==0&&GetTickCount64()-began<1000)std::this_thread::yield();
    for(int i=0;i<200;i++){cache.Put("Caption "+std::to_string(i),u8"已保存");cache.Publish();}done=true;reader.join();
    check("Render reads survive publication",reads>0);
    cache.Put("First option",u8"第一个选项");cache.Put("Second option",u8"第二个选项");cache.Publish();
    check("Pretranslated options compose with cursor line breaks",cache.Find("First option\nSecond option\n")==u8"第一个选项\n第二个选项");
    check("Unknown option does not partially translate the list",cache.Find("First option\nUnknown option").empty());
    cache.Put("First option\nSecond option",u8"人工整组选项");cache.Publish();check("Whole list correction has priority",cache.Find("First option\nSecond option")==u8"人工整组选项");
    cache.Put("Music",u8"音乐");cache.Put(u8"　OFF",u8"　关闭");cache.Put("ON",u8"开启");cache.Publish();
    check("Known UI label and separately stored suffix compose",cache.Find(u8"Music　OFF")==u8"音乐　关闭");
    check("Known tab columns compose",cache.Find("Music\tON")==u8"音乐\t开启");
    check("Ordinary prose spaces are not column boundaries",cache.Find("Music ON").empty());
    check("Unknown column state is not partially translated",cache.Find(u8"Music　UNKNOWN").empty());
    check("Unknown label is not partially translated",cache.Find(u8"Unknown　OFF").empty());
    cache.Put(u8"Music　OFF",u8"人工菜单修正");cache.Publish();
    check("Whole menu correction wins over composed columns",cache.Find(u8"Music　OFF")==u8"人工菜单修正");
    cache.Clear();check("Clear removes exact and template results",cache.Find("Welcome, Alice!").empty()&&cache.Find("You have 25 coins.").empty());
    for(int i=0;i<100;i++)cache.Put(u8"私は文章"+std::to_string(i)+"[ENG]A sentence "+std::to_string(i),u8"第"+std::to_string(i)+u8"句[ENG]另第"+std::to_string(i)+u8"句");cache.Publish();
    check("Many Japanese alternatives sharing a prefix use exact index",cache.Find(u8"私は文章87")==u8"第87句");
    cache.Put(u8"私は文章87[ENG]A different sentence",u8"不同内容[ENG]别的内容");cache.Publish();
    check("Ambiguous language branches decline",cache.Find(u8"私は文章87").empty());
    cache.Put(u8"私は文章87",u8"人工指定");cache.Publish();check("Exact manual correction overrides branch aliases",cache.Find(u8"私は文章87")==u8"人工指定");
    cache.Put("English discussion of [ENG] labels","讨论[ENG]标签");cache.Publish();check("Literal marker prose does not create aliases",cache.Find("English discussion of").empty());
    cache.Put(u8"@1私は門番です。[ENG]@1I am a guard.",u8"@10我是门卫。[ENG]@10我是门卫。");cache.Publish();check("Different speaker number cannot form shortened alias",cache.Find(u8"私は門番です。").empty());
    if(argc>1){std::ifstream input(std::filesystem::path(argv[1]),std::ios::binary);auto vectors=nlohmann::json::parse(input);for(auto& v:vectors){cache.Put(v["source"],v["text"]);cache.Publish();check("CSharp persisted preparation reaches native render lookup",cache.Find(v["rendered"])==v["expected"]);}}
    std::cout<<"{\"checks\":"<<count<<",\"failed\":"<<failed<<",\"scope\":\"native cache and compiled display frames; no game execution\"}\n";return failed?1:0;
}

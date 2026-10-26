#pragma once
// Cached status only: no memory scan, hook installation or authorization here.
namespace WolfSpeed {
inline nlohmann::json Status(){
    return {{"speed",rate.load()},{"minimum",0.25},{"maximum",100},{"integerOnly",false},
        {"enabled",authorized.load()},{"installed",installed.load()},{"backend","wolf-main-module-clocks"},
        {"clockCalls",gameClockCalls.load()},{"sleepCalls",gameSleepCalls.load()},{"actualGameplayVerified",false}};
}
inline nlohmann::json Request(const nlohmann::json& q,bool& ok){
    auto op=q.value("op","");
    if(op=="speedEnable"){Authorize();return Status();}
    if(op=="speedDisable"){Revoke();return Status();}
    if(q.contains("rate")){
        if(!q["rate"].is_number()){ok=false;return {{"message",u8"变速范围为 0.25 到 100 倍。"}};}
        const char* error=nullptr;if(!Apply(q["rate"].get<double>(),error)){ok=false;return {{"message",error}};}
    }
    return Status();
}
}

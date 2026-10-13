#pragma once
// Monotonic, bounded scalar measurements. Observation starts when the bridge
// first sees an active source, not when a game object or a frame is created.
struct DisplayLatencyMark {
 bool started=false,cached=false,done=false,fontBlocked=false;
 ULONGLONG observedAt=0,cacheAt=0,fontAt=0;
 void Begin(ULONGLONG now){*this={};started=true;observedAt=now;}
};
struct DisplayLatencyMeasure {
 uint64_t samples=0,lastMs=0,maxMs=0;
 void Add(ULONGLONG value){samples++;lastMs=value;maxMs=std::max(maxMs,(uint64_t)value);}
};
static DisplayLatencyMeasure observedToCache,cacheToReadback,glyphWait;
static void CacheReady(DisplayLatencyMark& mark,ULONGLONG now){
 if(!mark.started||mark.cached)return;mark.cached=true;mark.cacheAt=now;observedToCache.Add(now-mark.observedAt);
}
static void FontReady(DisplayLatencyMark& mark,bool ready,ULONGLONG now){
 if(!mark.started)return;
 if(!ready&&!mark.fontBlocked){if(mark.done){mark.Begin(now);CacheReady(mark,now);}mark.fontBlocked=true;mark.fontAt=now;}
 else if(ready&&mark.fontBlocked){glyphWait.Add(now-mark.fontAt);mark.fontBlocked=false;}
}
static void ReadbackReady(DisplayLatencyMark& mark,ULONGLONG now){
 if(!mark.started||!mark.cached||mark.done)return;mark.done=true;cacheToReadback.Add(now-mark.cacheAt);
}

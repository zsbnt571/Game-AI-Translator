#pragma once
#include "Protocol.h"
#include <algorithm>

namespace FusionSpeed {
// Preserve each original clock's epoch. Changing rate never rewinds a game
// clock; a stopped lease continues at rate 1 with its accumulated offset.
struct Clock {
 SRWLOCK lock=SRWLOCK_INIT;LONGLONG anchor=0,frequency=1;
 long double virtualCounter=0,tick32=0,tick64=0,multimedia=0,fileTime=0,preciseFileTime=0;double rate=1;
 void Initialize(LONGLONG now,LONGLONG hz,DWORD t32,ULONGLONG t64,DWORD mm,ULONGLONG ft=0,ULONGLONG precise=0){anchor=now;frequency=hz;virtualCounter=(long double)now;tick32=t32;tick64=(long double)t64;multimedia=mm;fileTime=(long double)ft;preciseFileTime=(long double)precise;rate=1;}
 void Set(double next,LONGLONG now){AcquireSRWLockExclusive(&lock);auto elapsed=(long double)(now-anchor)*rate;virtualCounter+=elapsed;tick32+=elapsed*1000/frequency;tick64+=elapsed*1000/frequency;multimedia+=elapsed*1000/frequency;fileTime+=elapsed*10000000/frequency;preciseFileTime+=elapsed*10000000/frequency;anchor=now;rate=next;ReleaseSRWLockExclusive(&lock);}
 double Rate(){AcquireSRWLockShared(&lock);double value=rate;ReleaseSRWLockShared(&lock);return value;}
 long double Read(LONGLONG now,int kind){AcquireSRWLockShared(&lock);auto elapsed=(long double)(std::max)(LONGLONG(0),now-anchor)*rate;auto result=kind==0?virtualCounter+elapsed:kind>=4?(kind==4?fileTime:preciseFileTime)+elapsed*10000000/frequency:(kind==1?tick32:kind==2?tick64:multimedia)+elapsed*1000/frequency;ReleaseSRWLockShared(&lock);return result;}
 DWORD Delay(DWORD delay){if(!delay||delay==INFINITE)return delay;return (DWORD)(std::min)(double(INFINITE-1),(std::max)(1.0,std::ceil(delay/Rate())));}
};
}

#pragma once
#include "../Protocol.h"
namespace Fixture {
struct alignas(8) Control {
 volatile LONG request,ack,command,ready;
 DWORD duration,error;
 alignas(8) volatile LONG64 qpc,tick,tick64,multimedia,allowed,excluded;
 volatile LONG done;
};
}

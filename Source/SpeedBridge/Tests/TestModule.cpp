#include <windows.h>
extern "C" __declspec(dllexport) LONGLONG __cdecl ClockRead(){LARGE_INTEGER v{};QueryPerformanceCounter(&v);return v.QuadPart;}
extern "C" __declspec(dllexport) LONGLONG __cdecl FileTimeRead(){FILETIME v{};GetSystemTimeAsFileTime(&v);return (LONGLONG(v.dwHighDateTime)<<32)|v.dwLowDateTime;}
extern "C" __declspec(dllexport) LONGLONG __cdecl PreciseFileTimeRead(){FILETIME v{};GetSystemTimePreciseAsFileTime(&v);return (LONGLONG(v.dwHighDateTime)<<32)|v.dwLowDateTime;}
BOOL WINAPI DllMain(HINSTANCE,DWORD,void*){return TRUE;}

#pragma once
#include <windows.h>
#include <cstdint>

// Voicemeeter Remote API output-insert ABI. Windows LONG is 32 bits on x64.
struct BusBuffer {
    LONG rate, frames, inputs, outputs;
    float* read[128];
    float* write[128];
};

extern "C" {
__declspec(dllexport) LONG __stdcall sb_callback(void*, LONG command, void* data, LONG synchronized);
__declspec(dllexport) void sb_set_clip(const int16_t* stereo, int frames, int rate, int sendB1, int monitor, float gain);
__declspec(dllexport) void sb_clear();
__declspec(dllexport) int sb_position();
__declspec(dllexport) void sb_gain(float value);
}

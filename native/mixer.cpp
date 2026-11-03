#include "mixer.h"
#include <algorithm>
#include <atomic>
#include <cstring>

namespace {
SRWLOCK gate = SRWLOCK_INIT;
struct Transport {
    const int16_t* samples;
    int frames, rate;
    bool sendB1;
    int monitor; // -1: none, 0: A1, 1: A2
    double cursor;
} clip{};
std::atomic<float> volume{0.7f};
std::atomic<int> progress{-1};
}

void sb_clear() {
    AcquireSRWLockExclusive(&gate);
    clip = {};
    progress.store(-1);
    ReleaseSRWLockExclusive(&gate);
}

void sb_set_clip(const int16_t* stereo, int frames, int rate, int sendB1, int monitor, float gain) {
    AcquireSRWLockExclusive(&gate);
    clip = {stereo, frames, rate, sendB1 != 0, monitor, 0};
    volume.store(std::clamp(gain, 0.0f, 1.0f));
    progress.store(0);
    ReleaseSRWLockExclusive(&gate);
}

int sb_position() { return progress.load(std::memory_order_relaxed); }
void sb_gain(float value) { volume.store(std::clamp(value, 0.0f, 1.0f)); }

LONG __stdcall sb_callback(void*, LONG command, void* data, LONG synchronized) {
    if (command != 11 || !data) return 0; // BUFFER_OUT
    auto& b = *static_cast<BusBuffer*>(data);
    if (b.frames <= 0 || b.inputs < 0 || b.outputs < 0 || b.outputs > 128 || b.inputs > 128) return 0;
    // An insert must pass every existing channel through, including when idle.
    for (LONG ch = 0; ch < (std::min)(b.inputs, b.outputs); ++ch) {
        if (b.read[ch] && b.write[ch] && b.read[ch] != b.write[ch])
            std::memmove(b.write[ch], b.read[ch], static_cast<size_t>(b.frames) * sizeof(float));
    }
    if (!synchronized || b.inputs != 64 || b.outputs != 64 || b.rate <= 0 ||
        !TryAcquireSRWLockExclusive(&gate)) return 0;
    if (clip.samples) {
        // Potato buses contain eight channels each. Only write the front L/R pair.
        const int monitor = clip.monitor == 0 || clip.monitor == 1 ? clip.monitor * 8 : -1;
        const double step = static_cast<double>(clip.rate) / b.rate;
        const float scale = volume.load(std::memory_order_relaxed) / 32768.0f;
        for (int i = 0; i < b.frames && clip.cursor < clip.frames; ++i, clip.cursor += step) {
            const int frame = static_cast<int>(clip.cursor);
            const int next = (std::min)(frame + 1, clip.frames - 1);
            const float fraction = static_cast<float>(clip.cursor - frame);
            for (int side = 0; side < 2; ++side) {
                const float a = clip.samples[frame * 2 + side];
                const float z = clip.samples[next * 2 + side];
                const float sample = (a + (z - a) * fraction) * scale;
                if (clip.sendB1 && b.write[40 + side]) b.write[40 + side][i] += sample;
                if (monitor >= 0 && b.write[monitor + side]) b.write[monitor + side][i] += sample;
            }
        }
        if (clip.cursor >= clip.frames) {
            clip.samples = nullptr;
            progress.store(-1, std::memory_order_relaxed);
        } else {
            progress.store(static_cast<int>(clip.cursor), std::memory_order_relaxed);
        }
    }
    ReleaseSRWLockExclusive(&gate);
    return 0;
}

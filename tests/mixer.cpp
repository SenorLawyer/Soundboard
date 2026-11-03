#include "../native/mixer.h"
#include <array>
#include <cassert>
#include <cmath>
#include <cstdio>

int main() {
    constexpr int frames = 8;
    BusBuffer b{48000, frames, 64, 64, {}, {}};
    std::array<std::array<float, frames>, 64> input{}, output{};
    for (int ch = 0; ch < 64; ++ch) {
        input[ch].fill(ch / 100.0f);
        b.read[ch] = input[ch].data();
        b.write[ch] = output[ch].data();
    }
    const int16_t wave[]{16384, -16384, 8192, -8192};
    auto check = [&](int sendB1, int monitor) {
        sb_set_clip(wave, 2, 24000, sendB1, monitor, 0.5f);
        sb_callback(nullptr, 11, &b, 1);
        const float expected[]{0.25f,0.1875f,0.125f,0.125f,0,0,0,0};
        for (int ch = 0; ch < 64; ++ch) {
            bool changed = (sendB1 && ch / 2 == 20) || (monitor >= 0 && ch / 2 == monitor * 4);
            for (int i = 0; i < frames; ++i) {
                float added = changed ? expected[i] * (ch % 2 == 0 ? 1.0f : -1.0f) : 0.0f;
                assert(std::abs(output[ch][i] - input[ch][i] - added) < 1e-6f);
            }
        }
        assert(sb_position() == -1);
    };
    check(1, -1); check(0, 0); check(0, 1); check(1, 0); check(1, 1);
    sb_set_clip(wave, 2, 48000, 1, -1, 1);
    sb_callback(nullptr, 11, &b, 0);
    assert(input == output && sb_position() == 0);
    b.inputs = b.outputs = 40;
    sb_callback(nullptr, 11, &b, 1);
    assert(sb_position() == 0);
    sb_clear();
    b.inputs = b.outputs = 64;
    sb_callback(nullptr, 11, &b, 1);
    assert(input == output && sb_position() == -1);
    std::puts("Mixer passed: every channel, B1, A1/A2 preview and monitoring, resampling, stop and sync loss.");
}

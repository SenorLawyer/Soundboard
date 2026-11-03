"""Rebuild the six original starter recordings with Python's standard library."""
import math
import random
import struct
import wave
from pathlib import Path

rate = 48000
dest = Path(__file__).resolve().parents[1] / "src/Soundboard/Assets/Sounds"
dest.mkdir(parents=True, exist_ok=True)
rng = random.Random(23)
tau = math.tau


def tone(hz, t):
    return math.sin(tau * hz * t)


def strike(t, duration, fall):
    return min(1, t / 0.006, (duration - t) / 0.025) * math.exp(-fall * t)


claps = [(rng.uniform(0, 1.3), rng.uniform(0.18, 0.65)) for _ in range(55)]
for ident, duration in enumerate([1.15, 1.60, 0.69, 0.92, 1.08, 0.78], 1):
    samples = bytearray()
    for i in range(round(rate * duration)):
        t = i / rate
        if ident == 1:
            # Two detuned reeds and a short brass attack.
            signal = sum(tone(hz * h, t) / h for hz in (311, 376) for h in (1, 2, 3, 5)) * 0.27
            signal *= min(1, t / 0.055, (duration - t) / 0.16)
        elif ident == 2:
            env = sum(amp * math.exp(-80 * (t - start)) for start, amp in claps if 0 <= t - start < 0.09)
            signal = rng.uniform(-1, 1) * min(env, 1.1) * 0.8
        elif ident == 3:
            age = t if t < 0.09 else t - 0.09
            hz = 988 if t < 0.09 else 1318.5
            signal = (tone(hz, age) + 0.25 * tone(hz * 2, age)) * math.exp(-7 * age) * min(1, age / 0.004)
        elif ident == 4:
            signal = (tone(1318.5, t) + 0.43 * tone(2093, t) + 0.15 * tone(3390, t)) * strike(t, duration, 5)
        elif ident == 5:
            phase = 450 * (1 - math.exp(-2.8 * t)) / 2.8
            signal = (tone(1, phase) + 0.15 * tone(2, phase)) * strike(t, duration, 2.8)
        else:
            age = t if t < 0.23 else t - 0.23
            signal = (tone(659 if t < 0.23 else 880, age)) * strike(age, duration, 9)
        value = round(20000 * math.tanh(signal) * min(1, (duration - t) / 0.02))
        samples.extend(struct.pack("<hh", value, value))
    with wave.open(str(dest / f"{ident:08x}{'0' * 24}.wav"), "wb") as wav:
        wav.setparams((2, 2, rate, 0, "NONE", "not compressed"))
        wav.writeframes(samples)

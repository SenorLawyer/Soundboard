"""Send the packaged B1 mix through two local WebRTC peers without opening an audio device."""
import argparse
import asyncio
import ctypes as ct
from fractions import Fraction
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import wave

import av
from aiortc import AudioStreamTrack, RTCConfiguration, RTCPeerConnection, RTCRtpSender
import numpy as np
import webrtcvad

RATE, BLOCK = 48000, 960  # 20 ms Opus packets
PFloat = ct.POINTER(ct.c_float)


class Buffer(ct.Structure):
    _fields_ = [(key, ct.c_int32) for key in ("rate", "frames", "inputs", "outputs")] + [
        ("read", PFloat * 128), ("write", PFloat * 128)]


def render(pack, sources):
    dll = ct.CDLL(str(pack / "Soundboard.Audio.dll"))
    dll.sb_set_clip.argtypes = [ct.c_void_p, ct.c_int, ct.c_int, ct.c_int, ct.c_int, ct.c_float]
    dll.sb_callback.argtypes = [ct.c_void_p, ct.c_int32, ct.POINTER(Buffer), ct.c_int32]
    silence = np.zeros(BLOCK, np.float32)
    buses = np.zeros((64, BLOCK), np.float32)
    buffer = Buffer(RATE, BLOCK, 64, 64)
    for i in range(64):
        buffer.read[i] = silence.ctypes.data_as(PFloat)
        buffer.write[i] = buses[i].ctypes.data_as(PFloat)
    pads = []
    for path in sources:
        with wave.open(str(path)) as wav:
            assert (wav.getsampwidth(), wav.getnchannels(), wav.getframerate()) == (2, 2, RATE)
            pcm = np.frombuffer(wav.readframes(wav.getnframes()), "<i2").reshape(-1, 2)
        for preview in (True, False):
            dll.sb_set_clip(pcm.ctypes.data, len(pcm), RATE, int(not preview), 0 if preview else -1, 0.7)
            chunks = []
            for _ in range(0, len(pcm), BLOCK):
                dll.sb_callback(None, 11, ct.byref(buffer), 1)
                if preview:
                    assert not buses[40:42].any(), "Private preview leaked onto B1"
                bus = 0 if preview else 40
                chunks.append(buses[bus:bus + 2].mean(axis=0))
            assert dll.sb_position() == -1
            rendered = np.concatenate(chunks)[:len(pcm)]
            np.testing.assert_allclose(rendered, pcm.mean(axis=1) * (0.7 / 32768), atol=1e-6)
        pads.append(rendered)
    return pads


class Track(AudioStreamTrack):
    def __init__(self, samples):
        super().__init__()
        self.samples, self.cursor, self.start = samples, 0, None

    async def recv(self):
        loop = asyncio.get_running_loop()
        if self.start is None:
            self.start = loop.time()
        await asyncio.sleep(max(0, self.start + self.cursor / RATE - loop.time()))
        data = self.samples[self.cursor:self.cursor + BLOCK]
        data = np.pad(data, (0, BLOCK - len(data)))
        frame = av.AudioFrame.from_ndarray(data.reshape(1, -1), format="s16", layout="mono")
        frame.sample_rate, frame.time_base, frame.pts = RATE, Fraction(1, RATE), self.cursor
        self.cursor += BLOCK
        return frame


async def receive(samples):
    config = RTCConfiguration(iceServers=[])
    sender, peer = RTCPeerConnection(config), RTCPeerConnection(config)
    try:
        transceiver = sender.addTransceiver(Track(samples), direction="sendonly")
        transceiver.setCodecPreferences([c for c in RTCRtpSender.getCapabilities("audio").codecs
                                         if c.mimeType.lower() == "audio/opus"])
        await sender.setLocalDescription(await sender.createOffer())
        await peer.setRemoteDescription(sender.localDescription)
        await peer.setLocalDescription(await peer.createAnswer())
        await sender.setRemoteDescription(peer.localDescription)
        track = peer.getReceivers()[0].track
        chunks, count = [], 0
        while count < len(samples):
            frame = await track.recv()
            mono = frame.to_ndarray().reshape(-1, len(frame.layout.channels)).mean(axis=1) / 32768
            chunks.append(mono)
            count += len(mono)
        return np.concatenate(chunks)
    finally:
        await asyncio.gather(sender.close(), peer.close())


def align(recording, reference, position):
    lo = max(0, position - RATE // 4)
    window = recording[lo:position + len(reference) + RATE // 4]
    n = 1 << (len(window) + len(reference) - 2).bit_length()
    scores = np.fft.irfft(np.fft.rfft(window, n) * np.fft.rfft(reference[::-1], n), n)
    offset = np.argmax(scores[len(reference) - 1:len(window)])
    return window[offset:offset + len(reference)]


def suppress(samples, model):
    # A simple relative filter argument also works with spaces and drive letters in model paths.
    with tempfile.TemporaryDirectory() as folder:
        shutil.copyfile(model, Path(folder) / "voice.rnnn")
        raw = subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-f", "s16le", "-ar", str(RATE),
                              "-ac", "1", "-i", "pipe:0", "-af", "arnndn=m=voice.rnnn", "-f", "s16le", "pipe:1"],
                             input=samples.tobytes(), stdout=subprocess.PIPE, cwd=folder, check=True).stdout
    return np.frombuffer(raw, "<i2").copy()


async def check(pack, speech_model=None, noise_model=None):
    names = ("Air horn", "Quick applause", "Coin drop", "Glass ping", "Power down", "Signal check")
    paths = [pack / "Assets" / "Sounds" / f"{i:08x}{'0' * 24}.wav" for i in range(1, 7)]
    pads = render(pack, paths)
    gap = np.zeros(RATE // 2)
    source = np.concatenate([gap, *(part for pad in pads for part in (pad, gap))])
    source = np.rint(source * 32767).astype("<i2")
    source = np.pad(source, (0, -len(source) % BLOCK))
    modes = ["Open mic", "Voice activity"]
    if noise_model:
        modes.append("Denoise + VAD")
    for mode in modes:
        filtered = mode == "Denoise + VAD"
        samples = suppress(source, noise_model)[:len(source)] if filtered else source.copy()
        if mode != "Open mic":
            detector = webrtcvad.Vad(2)
            for frame in samples.reshape(-1, BLOCK):
                if not detector.is_speech(frame.tobytes(), RATE):
                    frame.fill(0)
        received = await asyncio.wait_for(receive(samples), len(samples) / RATE + 15)
        pos = len(gap)
        for name, pad in zip(names, pads):
            audio = align(received, pad, pos)
            energy = np.linalg.norm(audio)
            gain = 20 * np.log10(max(energy, 1e-12) / np.linalg.norm(pad))
            match = np.dot(audio, pad) / max(energy * np.linalg.norm(pad), 1e-12)
            print(f"{mode:14} {name:15} {gain:+.1f} dB   match {match:.2f}", flush=True)
            assert (-10 if filtered else -6) < gain < 3 and match > (0.45 if filtered else 0.55), f"Missing or distorted {name}"
            pos += len(pad) + len(gap)
        if speech_model and mode != "Open mic":
            from vosk import KaldiRecognizer, Model, SetLogLevel
            SetLogLevel(-1)
            recognizer = KaldiRecognizer(Model(str(speech_model)), RATE)
            recognizer.AcceptWaveform(np.rint(audio * 32767).clip(-32768, 32767).astype("<i2").tobytes())
            words = json.loads(recognizer.FinalResult())["text"]
            print("Received Signal check:", words)
            assert words == "one two three"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path, help="extracted Windows application folder")
    parser.add_argument("--speech-model", type=Path, help="optional local English Vosk model")
    parser.add_argument("--noise-model", type=Path, help="optional RNNoise .rnnn model for FFmpeg")
    args = parser.parse_args()
    asyncio.run(check(args.package.resolve(), args.speech_model, args.noise_model))

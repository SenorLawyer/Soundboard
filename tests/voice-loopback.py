"""Send the packaged B1 mix through two local WebRTC peers without opening an audio device."""
import argparse
import asyncio
import ctypes as ct
from fractions import Fraction
import hashlib
import io
import json
from pathlib import Path
import tempfile
from urllib.request import urlopen
import wave

import av
from aiortc import AudioStreamTrack, RTCConfiguration, RTCPeerConnection, RTCRtpSender
import numpy as np
import webrtcvad

RATE, BLOCK = 48000, 960  # 20 ms Opus packets
PFloat = ct.POINTER(ct.c_float)
MODEL_URL = ("https://raw.githubusercontent.com/GregorR/rnnoise-models/3eee541a283fd3b8f81b85b1748e3b9ccbefa04d/"
             "conjoined-burgers-2018-08-28/cb.rnnn")
MODEL_SHA = "f1357c4e5be9dee8467bead486dfced2d75b640c26ad0b594fa7f102322371d9"


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
    graph = av.filter.Graph()
    src = graph.add_abuffer(sample_rate=RATE, format="s16", layout="mono", time_base=Fraction(1, RATE))
    denoiser = graph.add("arnndn", model=str(model.resolve()))
    pcm = graph.add("aformat", sample_fmts="s16", channel_layouts="mono")
    sink = graph.add("abuffersink")
    graph.link_nodes(src, denoiser, pcm, sink).configure()
    frame = av.AudioFrame.from_ndarray(samples.reshape(1, -1), format="s16", layout="mono")
    frame.sample_rate = RATE
    src.push(frame)
    src.push(None)
    blocks = []
    while True:
        try:
            blocks.append(sink.pull().to_ndarray().ravel())
        except av.error.EOFError:
            return np.concatenate(blocks)


def levels(samples):
    blob = io.BytesIO()
    with wave.open(blob, "wb") as out:
        out.setparams((1, 2, RATE, 0, "NONE", "not compressed"))
        out.writeframes(np.rint(samples * 32767).clip(-32768, 32767).astype("<i2").tobytes())
    blob.seek(0)
    with av.open(blob, format="wav") as src:
        stats = json.loads(av.filter.loudnorm.stats("i=-18:tp=-2:lra=7", src.streams.audio[0]))
    return float(stats["input_i"]), float(stats["input_tp"])


async def check(pack, noise_model, speech_model=None, import_proof=None):
    names = ["Air horn", "Quick applause", "Coin drop", "Glass ping", "Power down", "Signal check"]
    paths = [pack / "Assets" / "Sounds" / f"{i:08x}{'0' * 24}.wav" for i in range(1, 7)]
    if import_proof:
        paths.append(import_proof / "quiet-import.wav")
        names.append("Quiet import")
    pads = render(pack, paths)
    if import_proof:
        original = render(pack, [import_proof / "quiet-source.wav"])[0]
        before, after = levels(original)[0], levels(pads[-1])[0]
        print(f"Quiet import at 70%: {before:.1f} to {after:.1f} LUFS", flush=True)
        assert before <= -38 and after - before > 20 and abs(after - levels(pads[5])[0]) < 2
        assert len(original) == len(pads[-1]) and np.corrcoef(original, pads[-1])[0, 1] > 0.95
    for name, pad in zip(names, pads):
        loudness, peak = levels(pad)
        print(f"B1 at 70%      {name:15} {loudness:.1f} LUFS   {peak:.1f} dBTP", flush=True)
        assert -20 <= loudness <= -16 and peak <= -3, f"Check the mastering level for {name}"
    gap = np.zeros(RATE // 2)
    source = np.concatenate([gap, *(part for pad in pads for part in (pad, gap))])
    source = np.rint(source * 32767).astype("<i2")
    source = np.pad(source, (0, -len(source) % BLOCK))
    for mode in ("Open mic", "Voice activity", "Denoise + VAD"):
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
            if mode == "Open mic":
                assert levels(audio)[1] <= -1, f"Opus left too little peak headroom for {name}"
            if name == "Quiet import":
                received_loudness, received_peak = levels(audio)
                print(f"Received quiet import: {received_loudness:.1f} LUFS, {received_peak:.1f} dBTP", flush=True)
                assert -24 <= received_loudness <= -17 and received_peak <= -1
            if speech_model and name in ("Signal check", "Quiet import") and mode != "Open mic":
                from vosk import KaldiRecognizer, Model, SetLogLevel
                SetLogLevel(-1)
                recognizer = KaldiRecognizer(Model(str(speech_model)), RATE)
                recognizer.AcceptWaveform(np.rint(audio * 32767).clip(-32768, 32767).astype("<i2").tobytes())
                words = json.loads(recognizer.FinalResult())["text"]
                print(f"Received {name}:", words)
                assert words == "one two three"
            pos += len(pad) + len(gap)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path, help="extracted Windows application folder")
    parser.add_argument("--speech-model", type=Path, help="optional local English Vosk model")
    parser.add_argument("--noise-model", type=Path, help="use a local copy of the pinned cb.rnnn model")
    parser.add_argument("--import-proof", type=Path, help="quiet source and library copy from the import check")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory() as tmp:
        model_path = args.noise_model or Path(tmp) / "cb.rnnn"
        if not args.noise_model:
            with urlopen(MODEL_URL, timeout=30) as response:
                model_path.write_bytes(response.read(512 * 1024))
        if hashlib.sha256(model_path.read_bytes()).hexdigest() != MODEL_SHA:
            raise ValueError("Unexpected noise suppression model")
        asyncio.run(check(args.package.resolve(), model_path, args.speech_model, args.import_proof))
